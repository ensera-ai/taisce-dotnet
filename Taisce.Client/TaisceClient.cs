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
        ArgumentNullException.ThrowIfNull(http);
        BaseUrl = baseUrl.TrimEnd('/');
        _http = http;
        _authorization = new AuthenticationHeaderValue("Bearer", token);
        _ownsHttp = ownsHttp;
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
        throw new TaisceException((int)response.StatusCode, code, message);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
