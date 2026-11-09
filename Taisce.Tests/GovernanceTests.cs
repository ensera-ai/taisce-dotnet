// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using Taisce;
using Xunit;

namespace Taisce.Tests;

// What the client refuses before it sends, and how it maps what the deployment answers. A scripted
// handler is the wire here, which is enough for these: they hold the client's own rules (what it
// will not construct, what it will not send, which error type a refusal becomes), and the paths a
// live deployment cannot be made to take on demand, such as an erasure that leaves a row. That the
// deployment erases and exports what it says is proved against a real one in Taisce.LiveTests.
public class GovernanceTests
{
    private const string Id1 = "0b1f6a54-8b3e-4d2e-9a0c-5f6f1c0f9a11";

    [Theory]
    [InlineData("http://localhost:18080")]
    [InlineData("http://127.0.0.1:5")]
    [InlineData("http://127.9.9.9")]
    [InlineData("http://[::1]:8080")]
    [InlineData("https://memory.example")]
    public void AnAddressThatProtectsTheCredentialIsAccepted(string url)
    {
        using var client = new TaisceClient(url, "tsk");
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("http://memory.example")]
    [InlineData("ftp://localhost")]
    [InlineData("http://localhost.evil.example")]
    // The parsed host here is evil.example: "localhost" is only the user-info.
    [InlineData("http://localhost@evil.example")]
    [InlineData("http://127.0.0.1.evil.example")]
    [InlineData("memory.example")]
    public void AnAddressThatWouldSendTheCredentialInClearIsRefused(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => new TaisceClient(url, "tsk"));
        Assert.Contains(url.StartsWith("memory", StringComparison.Ordinal) ? "absolute" : "clear", ex.Message, StringComparison.Ordinal);
        // Every constructor, not only the one that makes its own HttpClient.
        Assert.Throws<ArgumentException>(() => new TaisceClient(url, "tsk", new HttpClient()));
    }

    [Fact]
    public async Task ErasingBySubjectSendsTheSubjectAndTheReasonAndNothingElse()
    {
        var wire = new Scripted(HttpStatusCode.OK, Receipt(clean: true));
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        var receipt = await client.EraseSubjectAsync("alice", "subject request");

        Assert.Equal("/v1/erasures", wire.Path);
        using var body = JsonDocument.Parse(wire.Body!);
        Assert.Equal("alice", body.RootElement.GetProperty("data_subject_id").GetString());
        Assert.Equal("subject request", body.RootElement.GetProperty("reason").GetString());
        Assert.False(body.RootElement.TryGetProperty("source_observation_ids", out _));
        Assert.True(receipt.Clean);
        Assert.Equal(2, receipt.Deleted["messages"]);
    }

    [Fact]
    public async Task ErasingBySourcesNamesTheSourcesAndNoSubject()
    {
        var wire = new Scripted(HttpStatusCode.OK, Receipt(clean: true));
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        await client.EraseSourcesAsync([Id1], "document withdrawn");

        using var body = JsonDocument.Parse(wire.Body!);
        Assert.Equal(Id1, body.RootElement.GetProperty("source_observation_ids")[0].GetString());
        Assert.False(body.RootElement.TryGetProperty("data_subject_id", out _));
    }

    [Fact]
    public async Task AnErasureThatLeavesARowIsAnErrorCarryingTheWholeReceipt()
    {
        var wire = new Scripted(HttpStatusCode.OK, Receipt(clean: false));
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        var ex = await Assert.ThrowsAsync<TaisceErasureNotCleanException>(() => client.EraseSubjectAsync("alice"));

        Assert.False(ex.Receipt.Clean);
        Assert.Equal(1, ex.Receipt.Residual["messages"]);
        Assert.Equal("req-1", ex.Receipt.RequestId);
        Assert.Contains("messages=1", ex.Message, StringComparison.Ordinal);
        // Still a TaisceException, so a handler for the client's errors sees it.
        Assert.IsAssignableFrom<TaisceException>(ex);
    }

    [Fact]
    public async Task AReadOnlyKeysRefusalIsItsOwnErrorType()
    {
        var wire = new Scripted(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"forbidden\",\"message\":\"this key may not erase\"}}");
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        var ex = await Assert.ThrowsAsync<TaisceForbiddenException>(() => client.EraseSubjectAsync("alice"));

        Assert.Equal(403, ex.Status);
        Assert.Equal("forbidden", ex.Code);
    }

    [Fact]
    public async Task AnExportTooLargeForOneResponseIsItsOwnErrorType()
    {
        var wire = new Scripted(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"export_too_large\",\"message\":\"too many rows\"}}");
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        var ex = await Assert.ThrowsAsync<TaisceExportTooLargeException>(() => client.ExportSubjectAsync("alice"));

        Assert.Equal(422, ex.Status);
    }

    // Other 403s and 422s stay generic: only the two codes the contract names get a type, so a
    // caller who catches one is catching exactly that refusal.
    [Fact]
    public async Task AnotherRefusalWithTheSameStatusStaysAGenericError()
    {
        var wire = new Scripted(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"invalid\",\"message\":\"no\"}}");
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        var ex = await Assert.ThrowsAsync<TaisceException>(() => client.ExportSubjectAsync("alice"));

        Assert.Equal(typeof(TaisceException), ex.GetType());
    }

    [Fact]
    public async Task AnExportKeepsEverySectionsRowsRaw()
    {
        var wire = new Scripted(HttpStatusCode.OK,
            "{\"scope\":\"subject\",\"data_subject_id\":\"alice\",\"sections\":{\"messages\":[{\"id\":1,\"future_column\":\"kept\"}]},\"rows\":{\"messages\":1}}");
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));

        var export = await client.ExportSubjectAsync("alice");

        Assert.Equal("/v1/exports", wire.Path);
        Assert.Equal("kept", export.Sections["messages"][0].GetProperty("future_column").GetString());
        Assert.Equal(1, export.Rows["messages"]);
    }

    [Fact]
    public async Task WhatTheDeploymentWouldRefuseIsRefusedBeforeAnythingIsSent()
    {
        var wire = new Scripted(HttpStatusCode.OK, Receipt(clean: true));
        using var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));
        var tooMany = Enumerable.Range(0, TaisceClient.MaxSources + 1).Select(_ => Id1).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => client.EraseSubjectAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExportSubjectAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EraseSourcesAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExportSourcesAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EraseSourcesAsync(tooMany));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExportSourcesAsync(["not-a-uuid"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.EraseSourcesAsync(null!));

        Assert.Null(wire.Path);
        // The bound itself is allowed.
        await client.EraseSourcesAsync(tooMany.Take(TaisceClient.MaxSources).ToList());
        Assert.Equal("/v1/erasures", wire.Path);
    }

    // The framework adapter's model-facing classes expose no way to erase or export: D24's rule that
    // no model reaches either. Checked by reflection so adding one fails here, not in review.
    [Fact]
    public void NothingOnTheFrameworkAdaptersModelFacingSurfaceCanEraseOrExport()
    {
        var adapter = typeof(Taisce.AgentFramework.TaisceContextProvider).Assembly;
        var offenders = adapter.GetExportedTypes()
            .SelectMany(t => t.GetMembers())
            .Where(m => m.Name.Contains("Erase", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Export", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.DeclaringType!.Name + "." + m.Name);
        Assert.Empty(offenders);
    }

    private static string Receipt(bool clean) =>
        "{\"request_id\":\"req-1\",\"scope\":\"subject\",\"data_subject_id\":\"alice\",\"completed_at\":\"2026-11-09T10:00:00Z\","
        + "\"deleted\":{\"messages\":2},\"residual\":{\"messages\":" + (clean ? "0" : "1") + "},\"clean\":" + (clean ? "true" : "false") + "}";

    private sealed class Scripted(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
