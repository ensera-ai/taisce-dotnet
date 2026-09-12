// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0
//
// The conformance driver: the adapter wrapped in the subprocess protocol the suite speaks. One JSON
// instruction per line on stdin, one report per line on stdout. The agent is a real ChatClientAgent
// with the provider attached; the model is a stub that records what it was handed and answers the
// turn's reply, or fails when the turn says so. Nothing about the deployment is stubbed.
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Taisce;
using Taisce.AgentFramework;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
using var stdin = Console.OpenStandardInput();
using var reader = new StreamReader(stdin);
while (await reader.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }
    var instruction = JsonSerializer.Deserialize<Instruction>(line, json) ?? throw new InvalidOperationException("instruction did not parse");
    var report = await RunTurnAsync(instruction);
    Console.Out.WriteLine(JsonSerializer.Serialize(report, json));
    await Console.Out.FlushAsync();
}

static async Task<Report> RunTurnAsync(Instruction instruction)
{
    var recorder = new ObserveRecorder();
    var http = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(10) };
    using var client = new TaisceClient(instruction.Api, instruction.Token, http);
    var report = new Report();
    var provider = new TaisceContextProvider(client, new TaisceContextProviderOptions
    {
        DataSubjectId = instruction.DataSubjectId,
        RunId = instruction.Case,
        OnError = (stage, ex) =>
        {
            if (stage == "observe")
            {
                report.StoreError = ex.Message;
            }
        },
    });
    IChatClient model = new StubModel(instruction.Turn, report);
    if (instruction.Turn.Compact)
    {
        // The compaction seam, on the chat client builder: any history at all trips it here.
        var strategy = new TaisceCompactionStrategy(client, new TaisceCompactionOptions
        {
            DataSubjectId = instruction.DataSubjectId,
            Trigger = CompactionTriggers.MessagesExceed(1),
            OnError = (stage, ex) => Console.Error.WriteLine($"{instruction.Case}: {stage} failed: {ex.Message}"),
        });
        model = new ChatClientBuilder(model).UseAIContextProviders(new CompactionProvider(strategy)).Build();
    }
    var agent = new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [provider] });
    var session = await agent.CreateSessionAsync();
    if (instruction.Turn.History is { Count: > 0 } history)
    {
        // The earlier turns the application kept, in the framework's own session history.
        session.SetInMemoryChatHistory(history.Select(m => new ChatMessage(new ChatRole(m.Role), m.Content)).ToList());
    }
    var input = new List<ChatMessage>();
    foreach (var s in instruction.Turn.Synthetic ?? [])
    {
        input.Add(new ChatMessage(new ChatRole(s.Role), s.Content));
    }
    input.Add(new ChatMessage(ChatRole.User, instruction.Turn.User));
    try
    {
        await agent.RunAsync(input, session);
    }
    catch (Exception)
    {
        // The model failing, or the store failing: both reach the application as the run's error.
        // Which one it was is in the report's fields.
        report.Fatal = report.StoreError is null;
    }
    report.Observed = recorder.Observed;
    return report;
}

/// <summary>Sees every request the client makes, so the report can say whether a store was attempted.</summary>
sealed class ObserveRecorder : DelegatingHandler
{
    public ObserveRecorder() : base(new HttpClientHandler()) { }
    public bool Observed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/observations", StringComparison.Ordinal) == true)
        {
            Observed = true;
        }
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>The stubbed model: records what it was handed, answers the turn's reply or fails.</summary>
sealed class StubModel(Turn turn, Report report) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        report.ModelMessages = messages.Select(m => new ReportMessage(
            m.Role.Value, m.Text,
            m.AdditionalProperties is { } p && p.TryGetValue(TaisceContextProvider.UntrustedProperty, out var u) && u is true)).ToList();
        if (turn.ModelFailure)
        {
            throw new InvalidOperationException("the model failed");
        }
        var response = new List<ChatMessage>();
        var i = 0;
        foreach (var call in turn.ToolCalls ?? [])
        {
            var id = "call-" + (++i);
            response.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, "tool", new Dictionary<string, object?> { ["call"] = call.Call })]));
            response.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(id, call.Result)]));
        }
        if (!string.IsNullOrEmpty(turn.Assistant))
        {
            response.Add(new ChatMessage(ChatRole.Assistant, turn.Assistant));
        }
        return Task.FromResult(new ChatResponse(response));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the driver runs turns without streaming");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

// The protocol's shapes, as conformance/README.md in the service repository defines them.
sealed record Instruction(
    [property: JsonPropertyName("case")] string Case,
    [property: JsonPropertyName("api")] string Api,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("data_subject_id")] string DataSubjectId,
    [property: JsonPropertyName("turn")] Turn Turn);

sealed record Turn(
    [property: JsonPropertyName("history")] List<SyntheticMessage>? History,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("tool_calls")] List<ToolCall>? ToolCalls,
    [property: JsonPropertyName("synthetic")] List<SyntheticMessage>? Synthetic,
    [property: JsonPropertyName("assistant")] string? Assistant,
    [property: JsonPropertyName("model_failure")] bool ModelFailure,
    [property: JsonPropertyName("compact")] bool Compact);

sealed record ToolCall([property: JsonPropertyName("call")] string Call, [property: JsonPropertyName("result")] string Result);

sealed record SyntheticMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

sealed record ReportMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("untrusted")] bool Untrusted);

sealed class Report
{
    [JsonPropertyName("model_messages")] public List<ReportMessage> ModelMessages { get; set; } = [];
    [JsonPropertyName("fatal")] public bool Fatal { get; set; }
    [JsonPropertyName("observed")] public bool Observed { get; set; }
    [JsonPropertyName("store_error")] public string? StoreError { get; set; }
}
