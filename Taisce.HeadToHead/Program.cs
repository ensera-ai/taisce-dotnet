// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
//
// The head-to-head: the same sessions through the framework's own chat-history memory provider and
// through Taisce, then the same question, and what each one hands the model. Nothing is scored; the
// output is the evidence, printed so it can be pasted into the dated market document as it came out.
//
// The incumbent stores every message in a vector store and searches it by similarity before each
// invocation. Taisce forms facts from the turns, supersedes the one an answer changed, and recalls
// the current one with the stale one marked. A conversation where an answer changed is the case
// that separates the two, and it is the only case this program runs.
//
//   TAISCE_API=http://127.0.0.1:18080 TAISCE_TOKEN=… dotnet run --project Taisce.HeadToHead
//
// Models are reached over an OpenAI-compatible endpoint (OPENAI_ENDPOINT, default a local Ollama):
// OPENAI_CHAT_MODEL answers the question, OPENAI_EMBEDDING_MODEL embeds for the incumbent, from
// OPENAI_EMBEDDING_ENDPOINT when embedding lives elsewhere. No number
// here is a performance measurement: it runs on whatever machine runs it.
using System.ClientModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using CommunityToolkit.VectorData.InMemory;
using OpenAI;
using Taisce;
using Taisce.AgentFramework;

var api = Environment.GetEnvironmentVariable("TAISCE_API") ?? "http://127.0.0.1:18080";
var token = Environment.GetEnvironmentVariable("TAISCE_TOKEN") ?? throw new InvalidOperationException("TAISCE_TOKEN is required");
var endpoint = new Uri(Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? "http://127.0.0.1:11434/v1");
var embeddingEndpoint = new Uri(Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_ENDPOINT") ?? endpoint.ToString());
var chatModel = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL") ?? "qwen3.8:27b-mxfp8";
var embeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "qwen3-embedding:4b-q8_0";
var dimensions = int.Parse(Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_DIMENSIONS") ?? "2560");

// The incumbent logs its failures rather than raising them, so a logger is how they are seen.
using var logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
// A model on the same machine answers slowly while another model is forming; the client waits
// rather than deciding the machine is down.
var openai = new OpenAIClient(new ApiKeyCredential(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "local"),
    new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = TimeSpan.FromMinutes(10) });
IChatClient chat = openai.GetChatClient(chatModel).AsIChatClient();
// The embedding model may live on another endpoint (a deployment keeps the two apart); the
// same credential and timeout apply.
var embeddingClient = new OpenAIClient(new ApiKeyCredential(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "local"),
    new OpenAIClientOptions { Endpoint = embeddingEndpoint, NetworkTimeout = TimeSpan.FromMinutes(10) });
IEmbeddingGenerator<string, Embedding<float>> embeddings = embeddingClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();

// The session set: one subject, two sessions weeks apart in which an answer changed, then a question.
var user = "alice-" + Guid.NewGuid().ToString("N")[..8];
var sessions = new[]
{
    new Session("2026-06-02", "I've just moved to Dublin for the new job, so my address is in Dublin now.", "Congratulations on the move to Dublin. I'll keep that in mind."),
    new Session("2026-08-15", "Quick update: we relocated the family to Cork last week, so I live in Cork now and work from the Cork office.", "Noted: you live in Cork now and work from the Cork office."),
};
const string question = "Where do I live now?";

Console.WriteLine($"# Head-to-head: an answer that changed\n");
Console.WriteLine($"Subject `{user}`. Sessions: {string.Join("; ", sessions.Select(s => $"{s.Date} \"{s.User}\""))}. Question: \"{question}\".\n");

var sessionCounter = 0;

// ── Taisce: facts formed from the turns, superseded when an answer changed ────────────────────
using var client = new TaisceClient(api, token);
var clock = new FixedClock();
var taisce = new TaisceContextProvider(client, new TaisceContextProviderOptions { DataSubjectId = user, Clock = clock });
await RunAsync("Taisce", taisce, s => new ChatClientAgentOptions { AIContextProviders = [taisce] }, waitForFormation: async () =>
{
    // Formation is asynchronous: wait until the deployment has formed what it stored. A model on
    // the same machine makes that minutes, and a poll that fails while the machine is busy is
    // retried rather than taken as an answer.
    var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            var f = await client.FreshnessAsync();
            if (f.Stored is not null && f.Formed == f.Stored)
            {
                return;
            }
        }
        catch (TaisceException ex)
        {
            Console.Error.WriteLine($"freshness poll failed, retrying: {ex.Message}");
        }
        await Task.Delay(5000);
    }
    throw new TimeoutException("formation did not catch up");
}, beforeSession: date => clock.Now = DateTimeOffset.Parse(date + "T10:00:00Z"));

// ── The incumbent: chat history in a vector store, searched by similarity ────────────────────
var store = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = embeddings });
var incumbent = new ChatHistoryMemoryProvider(store, "history", dimensions,
    session => new ChatHistoryMemoryProvider.State(
        new ChatHistoryMemoryProviderScope { UserId = user, SessionId = "session-" + sessionCounter },
        new ChatHistoryMemoryProviderScope { UserId = user }),
    new ChatHistoryMemoryProviderOptions { MaxResults = 3, EnableSensitiveTelemetryData = true }, logging);
await RunAsync("ChatHistoryMemoryProvider", incumbent, s => new ChatClientAgentOptions { AIContextProviders = [incumbent] }, waitForFormation: null);

async Task RunAsync(string name, AIContextProvider provider, Func<Session, ChatClientAgentOptions> options, Func<Task>? waitForFormation, Action<string>? beforeSession = null)
{
    Console.WriteLine($"## {name}\n");
    foreach (var s in sessions)
    {
        sessionCounter++;
        beforeSession?.Invoke(s.Date);
        // Storage turns: a scripted reply, so what is stored is the session as written and not what a
        // model felt like saying today.
        var storing = new ChatClientAgent(new ScriptedClient(s.Assistant), options(s));
        var session = await storing.CreateSessionAsync();
        await storing.RunAsync(s.User, session);
    }
    if (waitForFormation is not null)
    {
        await waitForFormation();
    }
    sessionCounter++;
    beforeSession?.Invoke("2026-09-10");
    var recorder = new Recorder(chat);
    var asking = new ChatClientAgent(recorder, options(sessions[0]));
    var askSession = await asking.CreateSessionAsync();
    var answer = await asking.RunAsync(question, askSession);
    Console.WriteLine("What the provider handed the model, before the question:\n");
    foreach (var m in recorder.Request.Where(m => m.Role != ChatRole.User || m.Text != question))
    {
        Console.WriteLine($"```\n[{m.Role}] {m.Text}\n```\n");
    }
    Console.WriteLine($"The model's answer: \"{answer.Text.Trim()}\"\n");
}

sealed record Session(string Date, string User, string Assistant);

/// <summary>Answers a fixed reply; the storage turns need no model.</summary>
sealed class ScriptedClient(string reply) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Keeps the request the model received, which is the evidence.</summary>
sealed class Recorder(IChatClient inner) : DelegatingChatClient(inner)
{
    public List<ChatMessage> Request { get; private set; } = [];
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Request = messages.ToList();
        return base.GetResponseAsync(Request, options, cancellationToken);
    }
}

/// <summary>A clock the run sets per session, so each turn is stored as of the day it was said.</summary>
sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
