// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taisce;

/// <summary>
/// Client for a Taisce deployment over the v1 contract.
/// </summary>
/// <remarks>
/// <para>Knows nothing about any agent framework. The adapter in <c>Taisce.AgentFramework</c> is built
/// on this, so a caller who wants governed memory without the framework does not acquire its
/// dependency tree by asking.</para>
/// <para>Thread-safe and meant to be shared: register it once. It holds no per-turn state.</para>
/// <para>The credential is a bearer token bound to one project; a request never names a project.
/// It is read once at construction and never logged, and this client sends it nowhere but the
/// deployment it was constructed for.</para>
/// </remarks>
public sealed class TaisceClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    // The credential rides on each request this client sends and nowhere else. A caller-supplied
    // HttpClient is the caller's: setting its default headers put the project credential on every
    // request it sent to any host, for the rest of its life.
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Creates a client with its own <see cref="HttpClient"/> and a thirty-second timeout.</summary>
    public TaisceClient(string baseUrl, string token)
        : this(baseUrl, token, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsHttp: true)
    {
    }

    /// <summary>Creates a client over a caller-supplied <see cref="HttpClient"/>, which the caller owns.</summary>
    public TaisceClient(string baseUrl, string token, HttpClient http) : this(baseUrl, token, http, ownsHttp: false)
    {
    }

    private TaisceClient(string baseUrl, string token, HttpClient http, bool ownsHttp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        RequireTransportThatProtectsTheCredential(baseUrl);
        ArgumentNullException.ThrowIfNull(http);
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http;
        _authorization = new AuthenticationHeaderValue("Bearer", token);
        _ownsHttp = ownsHttp;
    }

    // The credential is a bearer token for a whole project: whoever reads one request owns every
    // person's memory in it until it is revoked. So plaintext is refused at construction, where a
    // mistyped or downgraded setting is caught before a request exists, rather than documented.
    // Loopback is exempt because the traffic never leaves the machine. There is no opt-in for a
    // plaintext remote host: nothing needs one, and an escape hatch is one a deployment script will
    // set once and forget. Hosts are compared as parsed, not as written, so
    // "http://localhost@evil.example" (whose host is evil.example) and "localhost.evil.example" fail.
    private static void RequireTransportThatProtectsTheCredential(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("the base URL is not an absolute URL", nameof(baseUrl));
        }
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }
        var host = uri.Host.Trim('[', ']');
        var loopback = uri.Scheme == Uri.UriSchemeHttp
            && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip)));
        if (!loopback)
        {
            throw new ArgumentException(
                $"the base URL must be https, or http to a loopback host: the project credential would cross the network in clear to '{uri.Host}'",
                nameof(baseUrl));
        }
    }

    /// <summary>The deployment's base URL, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Records a turn. Durable when this returns; formation follows.</summary>
    public Task<ObserveReceipt> ObserveAsync(ObserveRequest request, CancellationToken cancellationToken = default)
        => PostAsync<ObserveRequest, ObserveReceipt>("/v1/observations", request, HttpStatusCode.Created, cancellationToken);

    /// <summary>How far behind memory is.</summary>
    public async Task<Freshness> FreshnessAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/freshness", null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<Freshness>(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks memory a question.</summary>
    public Task<RecallBundle> RecallAsync(RecallRequest request, CancellationToken cancellationToken = default)
        => PostAsync<RecallRequest, RecallBundle>("/v1/recalls", request, HttpStatusCode.OK, cancellationToken);

    /// <summary>
    /// One subject's history under a budget: the newest turns verbatim and, over the rest, the
    /// segments the deployment wrote. No model call is made.
    /// </summary>
    public Task<AssembledContext> ContextAsync(ContextRequest request, CancellationToken cancellationToken = default)
        => PostAsync<ContextRequest, AssembledContext>("/v1/contexts", request, HttpStatusCode.OK, cancellationToken);

    /// <summary>
    /// Searches the stored words themselves, for a question no fact answers.
    /// </summary>
    /// <remarks>
    /// This is the other half of retrieval and deliberately separate from <see cref="RecallAsync"/>:
    /// recall answers from what was inferred, this answers from what was said. A caller that wants
    /// grounding without adopting the memory model uses only this.
    /// </remarks>
    public Task<PassageResults> SearchPassagesAsync(PassageRequest request, CancellationToken cancellationToken = default)
        => PostAsync<PassageRequest, PassageResults>("/v1/passages/search", request, HttpStatusCode.OK, cancellationToken);

    /// <summary>Stores an opaque object under a person, replacing the version it names.</summary>
    public Task<ArtifactReceipt> PutArtifactAsync(ArtifactPut request, CancellationToken cancellationToken = default)
        => PostAsync<ArtifactPut, ArtifactReceipt>("/v1/artifacts/put", request, HttpStatusCode.OK, cancellationToken);

    /// <summary>Reads one object, optionally requiring it to belong to the person named.</summary>
    public Task<Artifact> GetArtifactAsync(ArtifactRef request, CancellationToken cancellationToken = default)
        => PostAsync<ArtifactRef, Artifact>("/v1/artifacts/get", request, HttpStatusCode.OK, cancellationToken);

    /// <summary>Removes one object, optionally requiring it to belong to the person named.</summary>
    public Task<JsonElement> DeleteArtifactAsync(ArtifactRef request, CancellationToken cancellationToken = default)
        => PostAsync<ArtifactRef, JsonElement>("/v1/artifacts/delete", request, HttpStatusCode.OK, cancellationToken);

    /// <summary>Resolves a fact to its record, as the contract returns it.</summary>
    public Task<JsonElement> ResolveCitationAsync(CitationRequest request, CancellationToken cancellationToken = default)
        => PostAsync<CitationRequest, JsonElement>("/v1/citations/resolve", request, HttpStatusCode.OK, cancellationToken);


    // ── Governance ───────────────────────────────────────────────────────────────────────────
    //
    // Reached from application code only. None of this is on the framework adapter's model-facing
    // surface (context provider, session store, text search tool): text that reaches a model is
    // written by someone else, and an erasure cannot be undone. Whether a request to forget is
    // genuine is the application's to decide, in code, before it calls these.

    /// <summary>The most source observations one erasure or export may name; a larger set goes in more than one request.</summary>
    public const int MaxSources = 10000;

    /// <summary>Erases everything held about one data subject.</summary>
    /// <param name="dataSubjectId">The subject as registered.</param>
    /// <param name="reason">Passed through to the receipt.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="TaisceErasureNotCleanException">Rows still match after the delete; the receipt is on the exception.</exception>
    /// <exception cref="TaisceForbiddenException">The key may not erase.</exception>
    public Task<ErasureReceipt> EraseSubjectAsync(string dataSubjectId, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSubjectId);
        return EraseAsync(new EraseBody { DataSubjectId = dataSubjectId, Reason = reason }, cancellationToken);
    }

    /// <summary>Erases the observations named, for material observed with no person (a document).</summary>
    /// <param name="sourceObservationIds">One to <see cref="MaxSources"/> observation ids.</param>
    /// <param name="reason">Passed through to the receipt.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="TaisceErasureNotCleanException">Rows still match after the delete; the receipt is on the exception.</exception>
    /// <exception cref="TaisceForbiddenException">The key may not erase.</exception>
    public Task<ErasureReceipt> EraseSourcesAsync(IReadOnlyList<string> sourceObservationIds, string? reason = null, CancellationToken cancellationToken = default)
    {
        RequireSources(sourceObservationIds);
        return EraseAsync(new EraseBody { SourceObservationIds = sourceObservationIds, Reason = reason }, cancellationToken);
    }

    /// <summary>Everything held about one data subject.</summary>
    /// <exception cref="TaisceExportTooLargeException">More than one response holds; the deployment does not page.</exception>
    public Task<ExportResult> ExportSubjectAsync(string dataSubjectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSubjectId);
        return PostAsync<EraseBody, ExportResult>("/v1/exports", new EraseBody { DataSubjectId = dataSubjectId }, HttpStatusCode.OK, cancellationToken);
    }

    /// <summary>Everything held for the observations named.</summary>
    /// <exception cref="TaisceExportTooLargeException">More than one response holds; the deployment does not page.</exception>
    public Task<ExportResult> ExportSourcesAsync(IReadOnlyList<string> sourceObservationIds, CancellationToken cancellationToken = default)
    {
        RequireSources(sourceObservationIds);
        return PostAsync<EraseBody, ExportResult>("/v1/exports", new EraseBody { SourceObservationIds = sourceObservationIds }, HttpStatusCode.OK, cancellationToken);
    }

    private async Task<ErasureReceipt> EraseAsync(EraseBody body, CancellationToken cancellationToken)
    {
        var receipt = await PostAsync<EraseBody, ErasureReceipt>("/v1/erasures", body, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        // The residual is the product: a receipt handed back as a value lets a caller who never
        // reads Clean tell a person their data is gone while rows still match.
        return receipt.Clean ? receipt : throw new TaisceErasureNotCleanException(receipt);
    }

    // What the deployment would refuse, refused here first so the caller learns it from the
    // argument and not from a 400. The two shapes are separate methods, so "both" and "neither"
    // cannot be written at all.
    private static void RequireSources(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            throw new ArgumentException("name at least one source observation", nameof(ids));
        }
        if (ids.Count > MaxSources)
        {
            throw new ArgumentException($"at most {MaxSources} source observations per request; send the rest in another", nameof(ids));
        }
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out _))
            {
                throw new ArgumentException("every source observation id is a UUID", nameof(ids));
            }
        }
    }

    // The wire body shared by erase and export; a null member is omitted, so each method sends
    // exactly the one subject-or-sources field it was given.
    private sealed class EraseBody
    {
        [JsonPropertyName("data_subject_id")] public string? DataSubjectId { get; init; }
        [JsonPropertyName("source_observation_ids")] public IReadOnlyList<string>? SourceObservationIds { get; init; }
        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path) { Content = content };
        request.Headers.Authorization = _authorization;
        return _http.SendAsync(request, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, HttpStatusCode success, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, JsonContent.Create(body, options: Json), cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TResponse>(response, success, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode success, CancellationToken cancellationToken)
    {
        if (response.StatusCode == success)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
            return value ?? throw new TaisceException((int)response.StatusCode, "empty_response", "the deployment answered with no body");
        }
        // A refusal is {"error": {"code", "message"}}; anything else is not this deployment.
        var code = "unexpected_response";
        var message = "the deployment answered " + (int)response.StatusCode;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() ?? code : code;
                message = error.TryGetProperty("message", out var m) ? m.GetString() ?? message : message;
            }
        }
        catch (JsonException)
        {
            // Not JSON: keep the generic message. The status still says what happened.
        }
        throw (int)response.StatusCode switch
        {
            403 when code == "forbidden" => new TaisceForbiddenException(message),
            422 when code == "export_too_large" => new TaisceExportTooLargeException(message),
            _ => new TaisceException((int)response.StatusCode, code, message),
        };
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
