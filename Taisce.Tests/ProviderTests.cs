// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
//
// What holds without a deployment: the filters, the key and the rendering. Everything the provider
// does against a deployment is held by the conformance suite, run through Taisce.Conformance
// against a live one (scripts/conformance.sh); a mock of the deployment here would prove that the
// mock matches the assertion.
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Taisce;
using Taisce.AgentFramework;
using Xunit;

namespace Taisce.Tests;

public class ProviderTests
{
    [Fact]
    public void OnlyTheAssistantsOwnWordsSurviveTheResponseFilter()
    {
        var kept = TaisceContextProvider.ExternalOnly([
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "tool", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "result")]),
            new ChatMessage(ChatRole.Assistant, "   "),
            new ChatMessage(ChatRole.Assistant, "Room A is booked."),
            new ChatMessage(ChatRole.User, "a user message in the response"),
        ]).ToList();
        Assert.Single(kept);
        Assert.Equal("Room A is booked.", kept[0].Text);
    }

    [Fact]
    public void ATurnKeyIsStableForATurnAndDifferentForAnother()
    {
        var turn = new List<ObserveMessage> { new("user", "hello"), new("assistant", "hi") };
        var key = TaisceContextProvider.TurnKey("subject-1", "run-1", turn);
        Assert.Equal(key, TaisceContextProvider.TurnKey("subject-1", "run-1", turn));
        Assert.True(Guid.TryParse(key, out var parsed));
        Assert.Equal('5', key[14]);
        Assert.NotEqual(key, TaisceContextProvider.TurnKey("subject-2", "run-1", turn));
        Assert.NotEqual(key, TaisceContextProvider.TurnKey("subject-1", "run-2", turn));
        Assert.NotEqual(key, TaisceContextProvider.TurnKey("subject-1", "run-1", [new ObserveMessage("user", "hello again")]));
        _ = parsed;
    }

    [Fact]
    public void TheMemoryMessageIsThePrefixLineAndOneDocument()
    {
        var bundle = JsonSerializer.Deserialize<RecallBundle>(
            """{"controls":{"hops":1},"anchors":[],"facts":[{"fact_id":"f1","subject":"Marta","predicate":"works_at","object":"Ensera","statement":"Marta works at Ensera.","source_role":"user","valid_from":"2024-01-01T00:00:00Z","valid_until":null,"evidence":{"observation_id":"o1","source_ordinal":0,"quote":"works at Ensera","byte_start":6,"byte_end":21,"context":"Marta works at Ensera."}}],"reports":[],"passages":[],"truncated":false,"characters":22,"degraded":[],"reach":{"terms":1,"anchored":1}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(bundle.IsEmpty);
        var rendered = TaisceContextProvider.RenderMemoryMessage(new Freshness("p1", 4, 3, 0), bundle);
        Assert.StartsWith(TaisceContextProvider.MemoryMessagePrefix + "\n", rendered, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(rendered[(rendered.IndexOf('\n') + 1)..]);
        Assert.Equal(4, doc.RootElement.GetProperty("watermark").GetProperty("stored").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("plan").GetProperty("controls").GetProperty("hops").GetInt32());
        Assert.Equal("f1", doc.RootElement.GetProperty("facts")[0].GetProperty("fact_id").GetString());
        Assert.True(TaisceContextProvider.IsMemoryMessage(new ChatMessage(ChatRole.User, rendered)));
        Assert.False(TaisceContextProvider.IsMemoryMessage(new ChatMessage(ChatRole.User, "hello")));
    }

    [Fact]
    public void TheContextMessageIsThePrefixLineAndTheDeploymentsArraysUnchanged()
    {
        var context = JsonSerializer.Deserialize<AssembledContext>(
            """{"watermark":{"stored":12,"formed":12,"parked":0},"segments":[{"level":1,"summary":"The office moved."}],"turns":[{"log_offset":9,"messages":[{"role":"user","content":"Book it."}]}],"characters":140,"truncated":false}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var rendered = TaisceContextProvider.RenderContextMessage(context);
        Assert.StartsWith(TaisceContextProvider.MemoryMessagePrefix + "\n", rendered);
        using var doc = JsonDocument.Parse(rendered[(rendered.IndexOf('\n') + 1)..]);
        Assert.Equal(12, doc.RootElement.GetProperty("watermark").GetProperty("stored").GetInt64());
        Assert.Equal(140, doc.RootElement.GetProperty("plan").GetProperty("characters").GetInt32());
        Assert.False(doc.RootElement.GetProperty("plan").GetProperty("truncated").GetBoolean());
        Assert.Equal("The office moved.", doc.RootElement.GetProperty("segments")[0].GetProperty("summary").GetString());
        Assert.Equal("Book it.", doc.RootElement.GetProperty("turns")[0].GetProperty("messages")[0].GetProperty("content").GetString());
        // A context with nothing in it renders empty arrays, never a missing field.
        var bare = TaisceContextProvider.RenderContextMessage(new AssembledContext(null!, default, default, 0, false));
        using var bareDoc = JsonDocument.Parse(bare[(bare.IndexOf('\n') + 1)..]);
        Assert.Equal(0, bareDoc.RootElement.GetProperty("segments").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, bareDoc.RootElement.GetProperty("watermark").GetProperty("stored").ValueKind);
    }

    [Fact]
    public void ThisTurnsInputIsWhatFollowsTheLastReply()
    {
        var request = new List<ChatMessage>
        {
            new(ChatRole.User, "old one"), new(ChatRole.Assistant, "old reply"),
            new(ChatRole.Assistant, "Summary of earlier conversation."), new(ChatRole.User, "now?"),
        };
        Assert.Equal(["now?"], TaisceContextProvider.ThisTurnsInput(request).Select(m => m.Text));
        Assert.Equal(["first"], TaisceContextProvider.ThisTurnsInput([new ChatMessage(ChatRole.User, "first")]).Select(m => m.Text));
    }

    [Fact]
    public void CompactionNeedsASubject()
    {
        using var client = new TaisceClient("http://127.0.0.1:1/", "tsk");
        Assert.Throws<ArgumentException>(() => new TaisceCompactionStrategy(client, new TaisceCompactionOptions()));
        Assert.NotNull(new TaisceCompactionStrategy(client, new TaisceCompactionOptions { DataSubjectId = "s" }));
    }

    [Fact]
    public void AnEmptyBundleIsEmpty()
    {
        var empty = JsonSerializer.Deserialize<RecallBundle>("""{"facts":[],"reports":[],"passages":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void AClientRefusesToBeBuiltWithoutADeploymentOrACredential()
    {
        Assert.Throws<ArgumentException>(() => new TaisceClient("", "tsk"));
        Assert.Throws<ArgumentException>(() => new TaisceClient("http://127.0.0.1:1", " "));
        using var client = new TaisceClient("http://127.0.0.1:1/", "tsk");
        Assert.Equal("http://127.0.0.1:1", client.BaseUrl);
    }

    // ── #21 ───────────────────────────────────────────────────────────────────────────────────
    //
    // The on-ramp: a search over what was said, mapped onto the framework's own seam, adding
    // nothing. Each result keeps enough identity to cite, and an answer from an index still being
    // built says so — a model handed an incomplete answer as a complete one answers confidently
    // from it.
    [Fact]
    public void EveryPassageBecomesOneCitableResultAndAnIncompleteIndexSaysSo()
    {
        var results = JsonSerializer.Deserialize<PassageResults>(
            """{"generation_id":"g1","through_offset":9,"covered_through_offset":4,"build_state":"building","approximate":true,"passages":[{"chunk_id":"c1","source_id":"o1","ordinal":2,"role":"user","preview":"I work at Ensera.","similarity":0.81,"occurred_at":"2026-01-01T00:00:00Z","preview_complete":true}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var mapped = TaisceTextSearch.Map(results);
        Assert.Equal(2, mapped.Count);
        Assert.Equal("I work at Ensera.", mapped[0].Text);
        Assert.Equal("turn o1 message 2", mapped[0].SourceName);
        Assert.Same(results.Passages[0], mapped[0].RawRepresentation);
        Assert.Contains("still being built", mapped[1].Text, StringComparison.Ordinal);

        // Told not to, it says nothing — and a complete answer never carries the caveat.
        Assert.Single(TaisceTextSearch.Map(results, sayWhenApproximate: false));
        var complete = results with { Approximate = false };
        Assert.Single(TaisceTextSearch.Map(complete));
        // Nothing found is nothing said: a caveat with no results is a result.
        var empty = results with { Passages = [] };
        Assert.Empty(TaisceTextSearch.Map(empty));
    }

    // ── #23 ───────────────────────────────────────────────────────────────────────────────────
    //
    // A session belongs to a person, and saying so is not optional. One credential opens a project
    // and a project holds every end user's objects, so a store that forgets whose session it is
    // makes one person's state reachable to another — and no amount of care in the application is a
    // boundary against that.
    [Fact]
    public async Task ASessionWithoutAPersonIsRefusedBeforeItReachesTheDeployment()
    {
        var store = new TaisceSessionStore(new TaisceClient("http://127.0.0.1:1", "tsk"));
        var agent = new ThrowingAgent();
        var session = await agent.CreateSessionAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(agent, session, "s1", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(agent, session, " ", "marta"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(agent, "s1", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("s1", ""));
        // And nothing was sent: the agent would have thrown if serialization had been reached.
        Assert.False(agent.Serialized);
    }

    // The smallest agent that can be asked to serialize: everything else refuses, because nothing
    // in this test should reach it.
    private sealed class BareSession : AgentSession
    {
    }

    private sealed class ThrowingAgent : AIAgent
    {
        public bool Serialized { get; private set; }

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => new(new BareSession());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedSession,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            Serialized = true;
            throw new NotSupportedException("no deployment in this test");
        }
    }

    // A fact reaches the model as the deployment sent it. The client used to rebuild each one from
    // a record of nine fields, so everything else the service knew — how sure it is, how far the
    // walk went, what it was derived with — was dropped on the way in.
    [Fact]
    public void AFactReachesTheModelWithEveryFieldTheDeploymentSent()
    {
        var bundle = JsonSerializer.Deserialize<RecallBundle>(
            """{"controls":{"hops":2},"anchors":[],"facts":[{"fact_id":"f1","subject":"Marta","predicate":"works_at","object":"Ensera","statement":"Marta works at Ensera.","source_role":"user","valid_from":"2024-01-01T00:00:00Z","valid_until":null,"confidence":0.82,"anchored_on":"Marta","hops":2,"path":["Marta","the migration","Ensera"],"via":"depends_on","co_derived_with":["f2"],"evidence":{"observation_id":"o1","source_ordinal":0,"quote":"works at Ensera","byte_start":6,"byte_end":21,"context":"Marta works at Ensera.","extractor_version":"v3"}}],"reports":[],"passages":[],"truncated":false,"characters":22,"degraded":[],"reach":{"terms":1,"anchored":1}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(bundle.IsEmpty);
        var rendered = TaisceContextProvider.RenderMemoryMessage(new Freshness("p1", 4, 3, 0), bundle);
        using var doc = JsonDocument.Parse(rendered[(rendered.IndexOf('\n') + 1)..]);
        var fact = doc.RootElement.GetProperty("facts")[0];
        Assert.Equal("f1", fact.GetProperty("fact_id").GetString());
        Assert.Equal(0.82, fact.GetProperty("confidence").GetDouble(), 3);
        Assert.Equal(2, fact.GetProperty("hops").GetInt32());
        Assert.Equal("depends_on", fact.GetProperty("via").GetString());
        Assert.Equal(3, fact.GetProperty("path").GetArrayLength());
        Assert.Equal("f2", fact.GetProperty("co_derived_with")[0].GetString());
        Assert.Equal("v3", fact.GetProperty("evidence").GetProperty("extractor_version").GetString());
    }

    // A recall carries every control the contract offers, because an adapter that exposes half of
    // them decides for the application which half it may have. Driven through an agent turn,
    // which is how the framework invokes the provider.
    [Fact]
    public async Task TheOptionsCarryEveryRecallControlToTheDeployment()
    {
        var wire = new CapturingHandler();
        var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));
        var provider = new TaisceContextProvider(client, new TaisceContextProviderOptions
        {
            DataSubjectId = "marta",
            MaxCharacters = 4000,
            SourceRoles = ["user"],
            Hops = 3,
            Surfaces = ["facts", "reports"],
            Themes = true,
            AsOf = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
            AsKnownAt = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var agent = new ChatClientAgent(new AnsweringModel(), new ChatClientAgentOptions { AIContextProviders = [provider] });
        await agent.RunAsync("Where does Marta work?", await agent.CreateSessionAsync());

        var recall = wire.Bodies.LastOrDefault(b => b.Path.EndsWith("/recalls", StringComparison.Ordinal));
        Assert.NotNull(recall);
        using var sent = JsonDocument.Parse(recall!.Body);
        Assert.Equal(3, sent.RootElement.GetProperty("hops").GetInt32());
        Assert.True(sent.RootElement.GetProperty("themes").GetBoolean());
        Assert.Equal(2, sent.RootElement.GetProperty("surfaces").GetArrayLength());
        Assert.StartsWith("2024-03-01", sent.RootElement.GetProperty("as_of").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("2024-04-01", sent.RootElement.GetProperty("as_known_at").GetString(), StringComparison.Ordinal);
    }

    // The deployment's context is fetched once per trigger, not once per model call.
    //
    // The framework's trigger counts the messages it can see, and the message this strategy inserts
    // is one of them, so past the threshold it stays tripped: without the guard, every later call
    // fetched the context again and inserted another copy, and the history never shrank.
    // Two turns, so the strategy is offered two chances and takes one.
    [Fact]
    public async Task TheContextIsFetchedOncePerTriggerAndNotOncePerCall()
    {
        var wire = new CapturingHandler();
        var client = new TaisceClient("https://taisce.test", "tsk", new HttpClient(wire));
        var strategy = new TaisceCompactionStrategy(client, new TaisceCompactionOptions
        {
            DataSubjectId = "marta",
            Trigger = CompactionTriggers.MessagesExceed(1),
        });
        var model = new AnsweringModel();
        var chat = new ChatClientBuilder(model).UseAIContextProviders(new CompactionProvider(strategy)).Build();
        var agent = new ChatClientAgent(chat);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("Book room A.", session);
        await agent.RunAsync("And room B?", session);

        Assert.Equal(2, model.Calls);
        Assert.Equal(1, wire.Bodies.Count(b => b.Path.EndsWith("/contexts", StringComparison.Ordinal)));
    }

    private sealed class AnsweringModel : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "At Ensera.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed record SentBody(string Path, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<SentBody> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Bodies.Add(new SentBody(request.RequestUri!.AbsolutePath, body));
            // Answered per path, because the turn ends with a store and a store that answers with
            // a recall is a failure the adapter rethrows by policy.
            var path = request.RequestUri!.AbsolutePath;
            var (status, answer) = path switch
            {
                var p when p.EndsWith("/freshness", StringComparison.Ordinal)
                    => (HttpStatusCode.OK, """{"scope":"p1","stored":1,"formed":1,"parked":0}"""),
                var p when p.EndsWith("/recalls", StringComparison.Ordinal)
                    => (HttpStatusCode.OK, """{"controls":{},"anchors":[],"facts":[],"reports":[],"passages":[],"truncated":false,"characters":0,"degraded":[],"reach":{}}"""),
                var p when p.EndsWith("/observations", StringComparison.Ordinal)
                    => (HttpStatusCode.Created, """{"id":"o1","scope":"p1","log_offset":1}"""),
                var p when p.EndsWith("/contexts", StringComparison.Ordinal)
                    => (HttpStatusCode.OK, """{"watermark":{"stored":1,"formed":1,"parked":0},"segments":[],"turns":[],"characters":0,"truncated":false}"""),
                _ => (HttpStatusCode.OK, "{}"),
            };
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
