// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taisce;

// The v1 wire shapes this client sends and reads, named as the contract names them. Every field a
// deployment may add later is tolerated by the deserialiser, which is what the frozen contract's
// "a field is only ever added" promise relies on from a client; a field removed would break here,
// and that is a new version by the same contract.

/// <summary>One message of a turn, with the role of whoever said it.</summary>
public sealed record ObserveMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content)
{
    /// <summary>Atomic group. A tool call and its result share one. Supplied for every message or none.</summary>
    [JsonPropertyName("group_ordinal")]
    public int? GroupOrdinal { get; init; }
}

/// <summary>A turn to record.</summary>
public sealed record ObserveRequest
{
    [JsonPropertyName("idempotency_key")] public required string IdempotencyKey { get; init; }
    [JsonPropertyName("data_subject_id")] public string? DataSubjectId { get; init; }
    [JsonPropertyName("occurred_at")] public DateTimeOffset? OccurredAt { get; init; }
    [JsonPropertyName("messages")] public required IReadOnlyList<ObserveMessage> Messages { get; init; }
}

/// <summary>The receipt: durable when returned; formation follows.</summary>
public sealed record ObserveReceipt(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("log_offset")] long LogOffset);

/// <summary>How far behind memory is.</summary>
public sealed record Freshness(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("stored")] long? Stored,
    [property: JsonPropertyName("formed")] long? Formed,
    [property: JsonPropertyName("parked")] int Parked);

/// <summary>A question for memory.</summary>
public sealed record RecallRequest
{
    [JsonPropertyName("question")] public required string Question { get; init; }
    [JsonPropertyName("data_subject_id")] public string? DataSubjectId { get; init; }
    [JsonPropertyName("as_of")] public DateTimeOffset? AsOf { get; init; }
    [JsonPropertyName("as_known_at")] public DateTimeOffset? AsKnownAt { get; init; }
    [JsonPropertyName("max_characters")] public int? MaxCharacters { get; init; }
    [JsonPropertyName("source_roles")] public IReadOnlyList<string>? SourceRoles { get; init; }
    [JsonPropertyName("hops")] public int? Hops { get; init; }
    [JsonPropertyName("surfaces")] public IReadOnlyList<string>? Surfaces { get; init; }
    [JsonPropertyName("themes")] public bool? Themes { get; init; }
}

/// <summary>A request for one subject's history under a budget.</summary>
public sealed record ContextRequest
{
    [JsonPropertyName("data_subject_id")] public required string DataSubjectId { get; init; }
    /// <summary>The content ceiling, in Unicode code points; null uses the deployment's.</summary>
    [JsonPropertyName("max_characters")] public int? MaxCharacters { get; init; }
}

/// <summary>
/// One subject's history as the deployment assembles it: the newest turns verbatim, the segments
/// written over the rest, the watermark, and the cost. The arrays are kept as the contract sent
/// them, because an adapter hands them on unchanged.
/// </summary>
public sealed record AssembledContext(
    [property: JsonPropertyName("watermark")] Freshness Watermark,
    [property: JsonPropertyName("segments")] JsonElement Segments,
    [property: JsonPropertyName("turns")] JsonElement Turns,
    [property: JsonPropertyName("characters")] int Characters,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>The words behind a fact and where they were said.</summary>
/// <summary>What the recall did: the effective controls, the surfaces that did not run, the reach.</summary>
public sealed record RecallBundle
{
    [JsonPropertyName("controls")] public JsonElement Controls { get; init; }
    [JsonPropertyName("anchors")] public JsonElement Anchors { get; init; }
    // Unchanged, like every other array here. A typed fact would be this client deciding which of
    // the deployment's fields matter: the ones it did not model would be dropped on the way to the
    // model, and a recall gains fields as the service does.
    [JsonPropertyName("facts")] public JsonElement Facts { get; init; }
    [JsonPropertyName("reports")] public JsonElement Reports { get; init; }
    [JsonPropertyName("passages")] public JsonElement Passages { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("characters")] public int Characters { get; init; }
    [JsonPropertyName("degraded")] public IReadOnlyList<string> Degraded { get; init; } = [];
    [JsonPropertyName("reach")] public JsonElement Reach { get; init; }

    /// <summary>Whether anything at all came back: facts, reports or passages.</summary>
    public bool IsEmpty =>
        (Facts.ValueKind != JsonValueKind.Array || Facts.GetArrayLength() == 0)
        && (Reports.ValueKind != JsonValueKind.Array || Reports.GetArrayLength() == 0)
        && (Passages.ValueKind != JsonValueKind.Array || Passages.GetArrayLength() == 0);
}

/// <summary>A citation resolved to its record.</summary>
public sealed record CitationRequest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("limit")] public int? Limit { get; init; }
}

/// <summary>A refusal from the deployment, carrying the contract's own code.</summary>
public class TaisceException : Exception
{
    public TaisceException(int status, string code, string message) : base($"{status} {code}: {message}")
    {
        Status = status;
        Code = code;
    }

    /// <summary>The HTTP status.</summary>
    public int Status { get; }

    /// <summary>The refusal code from the contract's closed vocabulary; branch on this, not on the message.</summary>
    public string Code { get; }
}

/// <summary>
/// The key was refused the operation: a read-only key asked to erase. The deployment's refusal is
/// the boundary; this type only lets a caller tell it apart from a fault.
/// </summary>
public sealed class TaisceForbiddenException(string message) : TaisceException(403, "forbidden", message);

/// <summary>An export larger than one response. The deployment does not page exports; erase or export in smaller scopes.</summary>
public sealed class TaisceExportTooLargeException(string message) : TaisceException(422, "export_too_large", message);

/// <summary>
/// An erasure whose receipt is not clean: rows still match what was erased. Thrown rather than
/// returned, because an application that never reads <c>Clean</c> would otherwise tell a person their
/// data is gone while it is not. The whole receipt is here for whoever handles it.
/// </summary>
public sealed class TaisceErasureNotCleanException : TaisceException
{
    public TaisceErasureNotCleanException(ErasureReceipt receipt)
        : base(200, "erasure_not_clean", "the erasure left rows that still match (residual: "
            + string.Join(", ", receipt.Residual.Where(r => r.Value != 0).Select(r => $"{r.Key}={r.Value}")) + ")")
    {
        Receipt = receipt;
    }

    /// <summary>What the deployment reported, residual counts included.</summary>
    public ErasureReceipt Receipt { get; }
}

/// <summary>
/// What an erasure removed and what still matches afterwards. <see cref="Residual"/> is a count taken
/// after the delete in the same transaction, so it is a measurement and not a claim.
/// </summary>
public sealed record ErasureReceipt(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("data_subject_id")] string DataSubjectId,
    [property: JsonPropertyName("source_observation_ids")] IReadOnlyList<string>? SourceObservationIds,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("deleted")] IReadOnlyDictionary<string, int> Deleted,
    [property: JsonPropertyName("residual")] IReadOnlyDictionary<string, int> Residual,
    [property: JsonPropertyName("clean")] bool Clean);

/// <summary>Everything held about one subject or set of sources, as raw rows per section.</summary>
public sealed record ExportResult(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("data_subject_id")] string DataSubjectId,
    [property: JsonPropertyName("source_observation_ids")] IReadOnlyList<string>? SourceObservationIds,
    // Raw rows: an export has to include a column a migration added without anybody remembering to
    // add it to a typed shape here.
    [property: JsonPropertyName("sections")] IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> Sections,
    [property: JsonPropertyName("rows")] IReadOnlyDictionary<string, int> Rows);

/// <summary>A question asked of the stored words rather than of what was inferred from them.</summary>
public sealed record PassageRequest
{
    [JsonPropertyName("question")] public required string Question { get; init; }
    [JsonPropertyName("limit")] public int? Limit { get; init; }
    [JsonPropertyName("data_subject_id")] public string? DataSubjectId { get; init; }
    [JsonPropertyName("source_role")] public string? SourceRole { get; init; }
}

/// <summary>One stored message, as the deployment returns it to a passage search.</summary>
public sealed record Passage(
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("preview")] string Preview,
    [property: JsonPropertyName("similarity")] double Similarity,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("preview_complete")] bool PreviewComplete);

/// <summary>
/// What a passage search returned, and how current the index behind it is.
/// </summary>
/// <remarks>
/// <para>
/// <c>Approximate</c> and <c>CoveredThroughOffset</c> are not decoration. A passage search reads an
/// embedding generation, and a generation is built up to a point in the log; a caller that treats
/// the answer as complete when the build is behind is quoting an index rather than the memory.
/// </para>
/// </remarks>
public sealed record PassageResults
{
    [JsonPropertyName("generation_id")] public string? GenerationId { get; init; }
    [JsonPropertyName("through_offset")] public long ThroughOffset { get; init; }
    [JsonPropertyName("covered_through_offset")] public long CoveredThroughOffset { get; init; }
    [JsonPropertyName("build_state")] public string? BuildState { get; init; }
    [JsonPropertyName("approximate")] public bool Approximate { get; init; }
    [JsonPropertyName("passages")] public IReadOnlyList<Passage> Passages { get; init; } = [];
}

/// <summary>An opaque object the deployment stores for an application, and never interprets.</summary>
/// <remarks>
/// The bytes are the application's. The deployment holds them under a project and a person, expires
/// them with that person's retention and removes them with that person's erasure — which is the
/// whole reason to keep session state here rather than in an application's own database, where a
/// deletion request would have two places to sweep and a residual count for only one of them.
/// </remarks>
public sealed record ArtifactPut
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    /// <summary>The version this write expects to replace; omit to create.</summary>
    [JsonPropertyName("expected_version")] public string? ExpectedVersion { get; init; }
    [JsonPropertyName("data_subject_id")] public required string DataSubjectId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("content")] public required byte[] Content { get; init; }
}

/// <summary>What a read of an object returns, including the bytes.</summary>
public sealed record Artifact
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("data_subject_id")] public string DataSubjectId { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("bytes")] public int Bytes { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("content")] public byte[] Content { get; init; } = [];
}

/// <summary>The receipt of a write: what to pass as the next write's expected version.</summary>
public sealed record ArtifactReceipt
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("data_subject_id")] public string DataSubjectId { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
}

/// <summary>Reading or removing one object, optionally requiring whose it is.</summary>
/// <remarks>
/// One credential opens a project, and a project holds every end user's objects. Naming the subject
/// makes "this one is theirs" a requirement the deployment enforces rather than a habit the
/// application keeps: another person's object is answered exactly as one that is not there.
/// </remarks>
public sealed record ArtifactRef
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("data_subject_id")] public string? DataSubjectId { get; init; }
    [JsonPropertyName("expected_version")] public string? ExpectedVersion { get; init; }
}
