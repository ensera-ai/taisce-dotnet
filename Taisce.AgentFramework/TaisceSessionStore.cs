// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Agents.AI;

namespace Taisce.AgentFramework;

/// <summary>
/// Keeps an agent session where the memory it belongs to already lives.
/// </summary>
/// <remarks>
/// <para>
/// The framework serializes a session and hands it back; it ships no store, so every application
/// invents one. Inventing it in the application's own database is the expensive mistake, and the
/// reason is governance rather than convenience: a session and the memory formed from it are erased
/// by the same request from the same person. Split across two systems, a deletion has two places to
/// sweep, one of which can produce a counted residual and one of which cannot, and the honest answer
/// to "is it gone" becomes "it is gone from the part we can measure".
/// </para>
/// <para>
/// So a session is an opaque object under the person it belongs to. The deployment never reads it,
/// expires it with that person's retention, and removes it with that person's erasure.
/// </para>
/// <para>
/// <b>Whose session it is, is checked by the deployment.</b> One credential opens a project and a
/// project holds every end user's objects, so an application serving many people could hand one
/// person's session to another; nothing in the application is a boundary against that, only care.
/// Every call here names the subject, and the deployment answers another person's session exactly as
/// one that does not exist.
/// </para>
/// <para>
/// <b>Concurrency is the caller's to declare.</b> A save carries the version it expects to replace
/// and the deployment refuses a stale one, so two turns of the same session cannot silently lose one
/// of their writes. Passing no version is not last-write-wins: the deployment replays an identical
/// save and refuses a changed one with `artifact_conflict`, so a second writer who did not read
/// first is told rather than obeyed. Naming the version you expect to replace is still the
/// default here.
/// </para>
/// </remarks>
public sealed class TaisceSessionStore
{
    private readonly TaisceClient _client;
    private readonly string _kind;

    /// <summary>Builds a store over one deployment.</summary>
    /// <param name="client">The deployment.</param>
    /// <param name="kind">
    /// The object kind to store sessions under. The deployment treats it as an opaque label and
    /// lists by it, which is what lets an operator see sessions apart from other stored objects.
    /// </param>
    public TaisceSessionStore(TaisceClient client, string kind = "state")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _kind = string.IsNullOrWhiteSpace(kind) ? "state" : kind;
    }

    /// <summary>What a save returned: the version the next save should expect to replace.</summary>
    public sealed record Saved(string SessionId, string Version);

    /// <summary>What a load returned, or null when the person has no session by that id.</summary>
    public sealed record Loaded(AgentSession Session, string Version);

    /// <summary>Serializes a session and stores it under the person it belongs to.</summary>
    public async Task<Saved> SaveAsync(AIAgent agent, AgentSession session, string sessionId,
        string dataSubjectId, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        Require(sessionId, dataSubjectId);

        var state = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        var receipt = await _client.PutArtifactAsync(new ArtifactPut
        {
            Id = sessionId,
            ExpectedVersion = expectedVersion,
            DataSubjectId = dataSubjectId,
            Kind = _kind,
            Name = "session",
            Content = JsonSerializer.SerializeToUtf8Bytes(state),
        }, cancellationToken).ConfigureAwait(false);
        return new Saved(receipt.Id, receipt.Version);
    }

    /// <summary>
    /// Reads a person's session back, or null when there is none by that id for that person.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, because "this person has not been here before" is the ordinary
    /// first turn of every conversation and not an error anybody should have to catch.
    /// </remarks>
    public async Task<Loaded?> LoadAsync(AIAgent agent, string sessionId, string dataSubjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Require(sessionId, dataSubjectId);

        Artifact stored;
        try
        {
            stored = await _client.GetArtifactAsync(
                new ArtifactRef { Id = sessionId, DataSubjectId = dataSubjectId }, cancellationToken).ConfigureAwait(false);
        }
        catch (TaisceException e) when (e.Status == 404)
        {
            return null;
        }
        using var document = JsonDocument.Parse(stored.Content);
        var session = await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new Loaded(session, stored.Version);
    }

    /// <summary>Removes a person's session. A session that is not there is already removed.</summary>
    public async Task DeleteAsync(string sessionId, string dataSubjectId, string? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        Require(sessionId, dataSubjectId);
        try
        {
            await _client.DeleteArtifactAsync(new ArtifactRef
            {
                Id = sessionId,
                DataSubjectId = dataSubjectId,
                ExpectedVersion = expectedVersion,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (TaisceException e) when (e.Status == 404)
        {
        }
    }

    /// <summary>
    /// Both are required, and the subject most of all: without it the deployment cannot tell whose
    /// session this is, and the check that makes one person's state unreachable to another is the
    /// one thing this class exists to keep.
    /// </summary>
    private static void Require(string sessionId, string dataSubjectId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("a session needs an identity", nameof(sessionId));
        }
        if (string.IsNullOrWhiteSpace(dataSubjectId))
        {
            throw new ArgumentException(
                "a session belongs to a person: without one, another person's session is reachable", nameof(dataSubjectId));
        }
    }
}
