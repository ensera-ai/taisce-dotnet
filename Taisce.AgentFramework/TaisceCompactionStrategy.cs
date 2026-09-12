// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Taisce.AgentFramework;

/// <summary>
/// The framework's compaction seam, translated into one operation: when the trigger fires, the
/// history the model would read is replaced by the deployment's context.
/// </summary>
/// <remarks>
/// <para><b>What it does, and what it does not.</b> It asks <c>POST /v1/contexts</c> for the data
/// subject's history under the configured budget and hands the answer over unchanged, as one
/// <c>user</c> message marked untrusted, in place of every group before the person's current
/// message that is not a system group. It writes no summary of its own and decides nothing about
/// what the context holds: a summary written here would be prose the deployment cannot register,
/// erase or re-derive, entering the history as though somebody said it.</para>
/// <para><b>The trigger is the framework's.</b> <see cref="CompactionTrigger"/> says when; by default
/// after <see cref="DefaultAfterMessages"/> included messages. The target is never met on its own,
/// because the replacement is whole rather than incremental.</para>
/// <para><b>Failure policy.</b> A context that cannot be fetched changes nothing and reports through
/// <see cref="TaisceCompactionOptions.OnError"/>; the model reads more, not less, and the turn goes
/// on.</para>
/// <para><b>Where to register it.</b> Wrap it in a <see cref="CompactionProvider"/> on the chat client
/// builder, with <c>UseAIContextProviders</c>, so what it contributes is read by the model and not
/// written into the session's history; <see cref="TaisceContextProvider"/> recognises the message by
/// its first line and never observes it either way.</para>
/// </remarks>
public sealed class TaisceCompactionStrategy : CompactionStrategy
{
    /// <summary>The included message count past which compaction is due, when no trigger is given.</summary>
    public const int DefaultAfterMessages = 40;

    private readonly TaisceClient _client;
    private readonly TaisceCompactionOptions _options;

    public TaisceCompactionStrategy(TaisceClient client, TaisceCompactionOptions options)
        : base(options?.Trigger ?? CompactionTriggers.MessagesExceed(DefaultAfterMessages), CompactionTriggers.Never)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataSubjectId))
        {
            throw new ArgumentException("a data subject is required: a context is one subject's history", nameof(options));
        }
        _client = client;
        _options = options;
    }

    // The turn this strategy last replaced a history for, and the lock over it.
    //
    // The framework's trigger counts the messages it can see, and the message this strategy inserts
    // is one of them — so past the threshold the trigger stays tripped and fires on every model
    // call. Without this, each of those calls fetched the deployment's context again and inserted
    // another copy, and the history never shrank. D131 says once per trigger: a context is fetched
    // when there is a turn to compact that was not compacted already.
    private readonly object _gate = new();
    private int _compactedThrough = -1;

    protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        var groups = index.Groups;
        var current = -1;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i].Kind == CompactionGroupKind.User && !groups[i].IsExcluded && !groups[i].Messages.All(TaisceContextProvider.IsMemoryMessage))
            {
                current = i;
                break;
            }
        }
        if (current < 0)
        {
            return false;
        }
        // A group without a turn index cannot be compared against the last one compacted, so it is
        // compacted: refusing what cannot be checked would be a history that never shrinks at all.
        var turn = groups[current].TurnIndex;
        lock (_gate)
        {
            if (turn is int only && only <= _compactedThrough)
            {
                return false;
            }
        }
        AssembledContext assembled;
        try
        {
            assembled = await _client.ContextAsync(new ContextRequest
            {
                DataSubjectId = _options.DataSubjectId!,
                MaxCharacters = _options.MaxCharacters,
            }, cancellationToken).ConfigureAwait(false);
        }
        // A timeout is a cancellation nobody asked for — a slow deployment — and is reported and survived
        // like any other failure. Only the caller's own cancellation ends the turn.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Swallowed by design; see the class remarks.
            _options.OnError?.Invoke("context", ex);
            return false;
        }
        var insertAt = 0;
        for (var i = 0; i < current; i++)
        {
            var group = groups[i];
            if (group.Kind == CompactionGroupKind.System)
            {
                insertAt = i + 1;
                continue;
            }
            if (group.Kind == CompactionGroupKind.User && group.Messages.All(TaisceContextProvider.IsMemoryMessage))
            {
                // Memory another provider injected for this run: not history, and not replaced.
                continue;
            }
            group.IsExcluded = true;
            group.ExcludeReason = "replaced by the deployment's context";
        }
        var message = new ChatMessage(ChatRole.User, TaisceContextProvider.RenderContextMessage(assembled))
        {
            AuthorName = "taisce",
            AdditionalProperties = new AdditionalPropertiesDictionary { [TaisceContextProvider.UntrustedProperty] = true },
        };
        index.InsertGroup(insertAt, CompactionGroupKind.Summary, [message], groups[insertAt < groups.Count ? insertAt : current].TurnIndex);
        lock (_gate)
        {
            if (turn is int done && done > _compactedThrough)
            {
                _compactedThrough = done;
            }
        }
        return true;
    }
}

/// <summary>What a compaction strategy is configured with; the rest is the deployment's.</summary>
public sealed class TaisceCompactionOptions
{
    /// <summary>Whose history is assembled, as registered. Required: a context is one subject's.</summary>
    public string? DataSubjectId { get; set; }

    /// <summary>The context's content ceiling; null uses the deployment's.</summary>
    public int? MaxCharacters { get; set; }

    /// <summary>When compaction is due, in the framework's vocabulary; null means after <see cref="TaisceCompactionStrategy.DefaultAfterMessages"/> messages.</summary>
    public CompactionTrigger? Trigger { get; set; }

    /// <summary>How a context failure, which is swallowed, reaches an operator. The first argument is "context".</summary>
    public Action<string, Exception>? OnError { get; set; }
}
