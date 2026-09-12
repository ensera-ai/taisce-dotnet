// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Taisce.AgentFramework;

/// <summary>
/// Injects governed memory into an agent's turn, and records the turn afterwards.
/// </summary>
/// <remarks>
/// <para><b>The role rule.</b> The framework's own documentation says a provider may inject messages
/// with any role, including <c>system</c>, that it does not validate or filter what a provider
/// returns, and that a memory service is exactly the source through which an indirect prompt
/// injection would arrive. Every memory is something somebody said, so every memory is untrusted
/// input. This provider returns <b>exactly one message, with role <c>user</c>, marked untrusted</b>,
/// and never <c>system</c>, never <c>assistant</c>. The conformance suite holds it to that.</para>
/// <para><b>Failure policy is asymmetric.</b> A recall that cannot reach the deployment yields no
/// message and no exception: the agent runs without memory rather than not at all, and
/// <see cref="TaisceContextProviderOptions.OnError"/> is how an operator still finds out. A failed
/// store throws, because losing a turn silently is the failure that stays invisible until a
/// subject access request comes back missing a conversation.</para>
/// <para><b>Store only on success, and only what people said.</b> The framework calls the store
/// hook only when the invocation succeeded. What is stored is the person's messages, which the
/// framework's default request filter keeps, and the assistant's final text, which this provider's
/// response filter keeps; tool calls, tool results and anything this provider injected are not
/// memory. The filters are given to the base class rather than reimplemented in the hooks, so the
/// defaults every adapter inherits live in one place.</para>
/// <para><b>Built through the constructor with the framework's hooks</b>, never by reimplementing
/// the invocation wrapper: the wrapper applies the filters and the success check, and a copy of it
/// is how internal tool traffic silently becomes memory.</para>
/// </remarks>
public sealed class TaisceContextProvider : AIContextProvider
{
    /// <summary>The first line of every injected memory message; the conformance suite parses it.</summary>
    public const string MemoryMessagePrefix = "taisce-memory/v1 untrusted";

    /// <summary>The key under which the injected message is marked untrusted in its additional properties.</summary>
    public const string UntrustedProperty = "taisce.untrusted";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TaisceClient _client;
    private readonly TaisceContextProviderOptions _options;

    public TaisceContextProvider(TaisceClient client, TaisceContextProviderOptions? options = null)
        : base(provideInputMessageFilter: null, storeInputRequestMessageFilter: null, storeInputResponseMessageFilter: ExternalOnly)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new TaisceContextProviderOptions();
    }

    /// <summary>
    /// Only the assistant's own words survive into memory: a message with function calls is the
    /// model talking to a tool, and a tool's result is the tool talking back. Neither is something
    /// a person said, and both are what an application opts into elsewhere, deliberately.
    /// </summary>
    public static IEnumerable<ChatMessage> ExternalOnly(IEnumerable<ChatMessage> messages)
        => messages.Where(m => m.Role == ChatRole.Assistant
            && !m.Contents.Any(c => c is FunctionCallContent || c is FunctionResultContent)
            && !string.IsNullOrWhiteSpace(m.Text));

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var question = LastUserText(context.AIContext?.Messages);
        if (string.IsNullOrWhiteSpace(question))
        {
            return new AIContext();
        }
        Freshness watermark;
        RecallBundle bundle;
        try
        {
            watermark = await _client.FreshnessAsync(cancellationToken).ConfigureAwait(false);
            bundle = await _client.RecallAsync(new RecallRequest
            {
                Question = question,
                DataSubjectId = _options.DataSubjectId,
                MaxCharacters = _options.MaxCharacters,
                SourceRoles = _options.SourceRoles,
                Hops = _options.Hops,
                Surfaces = _options.Surfaces,
                Themes = _options.Themes,
                AsOf = _options.AsOf,
                AsKnownAt = _options.AsKnownAt,
            }, cancellationToken).ConfigureAwait(false);
        }
        // A timeout is a cancellation nobody asked for — a slow deployment — and is reported and survived
        // like any other failure. Only the caller's own cancellation ends the turn.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Swallowed by design; see the class remarks.
            _options.OnError?.Invoke("recall", ex);
            return new AIContext();
        }
        if (bundle.IsEmpty)
        {
            // Nothing to say costs the model nothing to read.
            return new AIContext();
        }
        var message = new ChatMessage(ChatRole.User, RenderMemoryMessage(watermark, bundle))
        {
            AuthorName = "taisce",
            AdditionalProperties = new AdditionalPropertiesDictionary { [UntrustedProperty] = true },
        };
        return new AIContext { Messages = [message] };
    }

    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.InvokeException is not null)
        {
            // A failed turn is not a memory. The framework already skips this hook on failure; the
            // check stays because the property exists and the rule is worth stating where it holds.
            return;
        }
        var messages = new List<ObserveMessage>();
        foreach (var m in ThisTurnsInput(context.RequestMessages))
        {
            if (m.Role == ChatRole.User && !IsMemoryMessage(m) && !string.IsNullOrWhiteSpace(m.Text))
            {
                messages.Add(new ObserveMessage("user", m.Text));
            }
        }
        foreach (var m in context.ResponseMessages ?? [])
        {
            messages.Add(new ObserveMessage("assistant", m.Text ?? string.Empty));
        }
        if (messages.Count == 0)
        {
            return;
        }
        try
        {
            await _client.ObserveAsync(new ObserveRequest
            {
                IdempotencyKey = TurnKey(_options.DataSubjectId, _options.RunId, messages),
                DataSubjectId = _options.DataSubjectId,
                OccurredAt = _options.Clock?.GetUtcNow(),
                Messages = messages,
            }, cancellationToken).ConfigureAwait(false);
        }
        // A timeout is a cancellation nobody asked for — a slow deployment — and is reported and survived
        // like any other failure. Only the caller's own cancellation ends the turn.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _options.OnError?.Invoke("observe", ex);
            // Rethrown, unlike recall. See the class remarks.
            throw;
        }
    }

    /// <summary>
    /// The request messages are the whole of what the agent sent: the session's history, what
    /// providers added, and the turn's input. Earlier turns were observed when they happened, so
    /// this turn's input is what follows the last reply: everything after the last assistant
    /// message, and the whole request when there is none.
    /// </summary>
    public static IEnumerable<ChatMessage> ThisTurnsInput(IEnumerable<ChatMessage> request)
    {
        var all = request.ToList();
        var start = all.FindLastIndex(m => m.Role == ChatRole.Assistant) + 1;
        return all.Skip(start);
    }

    /// <summary>
    /// The rendering of a context, in the memory message's shape: the watermark, the assembly's
    /// cost as the plan, and the segments and turns unchanged. What replaces a history is the
    /// deployment's answer and nothing this adapter wrote.
    /// </summary>
    public static string RenderContextMessage(AssembledContext context)
    {
        var document = new
        {
            watermark = new { stored = context.Watermark?.Stored, formed = context.Watermark?.Formed, parked = context.Watermark?.Parked ?? 0 },
            plan = new { characters = context.Characters, truncated = context.Truncated },
            segments = context.Segments.ValueKind == JsonValueKind.Array ? context.Segments : JsonDocument.Parse("[]").RootElement,
            turns = context.Turns.ValueKind == JsonValueKind.Array ? context.Turns : JsonDocument.Parse("[]").RootElement,
        };
        return MemoryMessagePrefix + "\n" + JsonSerializer.Serialize(document, Json);
    }

    /// <summary>Whether a message is one this provider injected, by its first line.</summary>
    public static bool IsMemoryMessage(ChatMessage message)
        => message.Text is { } text && text.StartsWith(MemoryMessagePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The one rendering every adapter reproduces: the prefix line, then a JSON document with the
    /// watermark, the plan (what the recall did) and the recall's own arrays unchanged.
    /// </summary>
    public static string RenderMemoryMessage(Freshness watermark, RecallBundle bundle)
    {
        var document = new
        {
            watermark = new { stored = watermark.Stored, formed = watermark.Formed, parked = watermark.Parked },
            plan = new { controls = bundle.Controls, degraded = bundle.Degraded, reach = bundle.Reach },
            facts = bundle.Facts.ValueKind == JsonValueKind.Array ? bundle.Facts : JsonDocument.Parse("[]").RootElement,
            reports = bundle.Reports,
            passages = bundle.Passages,
        };
        return MemoryMessagePrefix + "\n" + JsonSerializer.Serialize(document, Json);
    }

    /// <summary>
    /// A turn's key: a version-5 UUID over the subject, the run and the messages, so a retried store
    /// is one observation and a different turn is another.
    /// </summary>
    public static string TurnKey(string? dataSubjectId, string? runId, IReadOnlyList<ObserveMessage> messages)
    {
        var builder = new StringBuilder().Append(dataSubjectId).Append('\n').Append(runId).Append('\n');
        foreach (var m in messages)
        {
            builder.Append(m.Role).Append('\n').Append(m.Content).Append('\n');
        }
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var bytes = digest[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    private static string? LastUserText(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null)
        {
            return null;
        }
        foreach (var m in messages.Reverse())
        {
            if (m.Role == ChatRole.User && !IsMemoryMessage(m) && !string.IsNullOrWhiteSpace(m.Text))
            {
                return m.Text;
            }
        }
        return null;
    }
}

/// <summary>What a provider is configured with; the rest is the deployment's.</summary>
public sealed class TaisceContextProviderOptions
{
    /// <summary>Who the turns are about, as registered. Null means project-wide memory.</summary>
    public string? DataSubjectId { get; set; }

    /// <summary>An identifier for this run, folded into every turn's idempotency key.</summary>
    public string? RunId { get; set; }

    /// <summary>The recall's content ceiling; null uses the deployment's.</summary>
    public int? MaxCharacters { get; set; }

    /// <summary>Which speakers' words may answer; null uses the deployment's default.</summary>
    public IReadOnlyList<string>? SourceRoles { get; set; }

    /// <summary>How far the walk may travel from an anchor; null uses the deployment's default.</summary>
    public int? Hops { get; set; }

    /// <summary>Which surfaces may answer, such as facts, reports or passages; null uses all of them.</summary>
    public IReadOnlyList<string>? Surfaces { get; set; }

    /// <summary>Whether thematic reports may answer; null uses the deployment's default.</summary>
    public bool? Themes { get; set; }

    /// <summary>Answer as the world was at this time, rather than as it is now.</summary>
    public DateTimeOffset? AsOf { get; set; }

    /// <summary>Answer with what was known at this time, rather than with everything learned since.</summary>
    public DateTimeOffset? AsKnownAt { get; set; }

    /// <summary>The clock that stamps a turn's time; null lets the deployment stamp ingestion time.</summary>
    public TimeProvider? Clock { get; set; }

    /// <summary>
    /// How a recall failure, which is swallowed, and a store failure, which is rethrown, reach an
    /// operator. The first argument is the stage, "recall" or "observe".
    /// </summary>
    public Action<string, Exception>? OnError { get; set; }
}
