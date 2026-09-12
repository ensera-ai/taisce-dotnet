<!-- Copyright 2026 The Taisce Authors -->
<!-- SPDX-License-Identifier: Apache-2.0 -->

# Taisce for .NET

Two packages. `Taisce.Client` speaks the v1 contract of a Taisce deployment and depends on nothing
but the standard library. `Taisce.AgentFramework` is the Microsoft Agent Framework adapter built on
it: a context provider that injects governed memory into a turn and records the turn afterwards.

```csharp
var client = new TaisceClient("https://memory.example", token);
var memory = new TaisceContextProvider(client, new TaisceContextProviderOptions { DataSubjectId = "alice" });
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [memory] });
```

## What the provider does, and holds to

- **Memory arrives as exactly one `user` message, marked untrusted.** Never `system`, never
  `assistant`. The framework accepts a provider's messages as they are and names a memory service as
  the source an indirect prompt injection arrives through; a memory is something somebody said once.
  The message is the line `taisce-memory/v1 untrusted` and a JSON document carrying the freshness
  watermark, what the recall did, and the recall's facts, reports and passages unchanged.
- **Recall failure is not fatal.** The agent runs without memory rather than not at all; `OnError`
  is how an operator still finds out.
- **Write failure is never silent.** A failed store throws, because a lost turn is invisible until a
  subject access request asks for it.
- **A failed turn is not a memory.** The store hook runs only when the invocation succeeded.
- **Only what people said becomes memory by default.** The framework's default request filter keeps
  the person's messages; this provider's response filter keeps the assistant's final text and drops
  tool calls, tool results and anything the provider itself injected.
- **A retried store is one observation.** The idempotency key is derived from the subject, the run
  and the turn.
- **Compaction is replacement, never a summary.** `TaisceCompactionStrategy` is the framework's own
  `CompactionStrategy`: when its `CompactionTrigger` fires, it asks the deployment for the subject's
  context and replaces every group before the person's message, except system groups, with one
  untrusted `user` message carrying the segments and the verbatim turns unchanged. A context that
  cannot be fetched changes nothing. Register it through a `CompactionProvider` on the chat client
  builder, so what it contributes is read by the model and not written into the session's history.

```csharp
var strategy = new TaisceCompactionStrategy(client, new TaisceCompactionOptions
{
    DataSubjectId = "alice",
    Trigger = CompactionTriggers.TurnsExceed(20),
});
var chat = new ChatClientBuilder(model).UseAIContextProviders(new CompactionProvider(strategy)).Build();
var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { AIContextProviders = [provider] });
```

## In an application with a host builder

One call, with the settings where the application already keeps them. The credential is bound from
configuration — an environment variable or a secret store — never written in source.

```csharp
builder.Services.AddTaisceMemory(builder.Configuration);   // reads the "Taisce" section
// or, in code:
builder.Services.AddTaisceMemory(o => { o.Endpoint = endpoint; o.Token = token; o.DataSubjectId = "alice"; o.Hops = 2; });
```

`Endpoint`, `Token`, `DataSubjectId`, `RunId`, `MaxCharacters`, `SourceRoles`, `Hops`, `Surfaces`
and `Themes`. The container gets a `TaisceClient` and a `TaisceContextProvider`; the compaction
strategy stays explicit, because when a history is replaced is the application's decision.

## How it is proved

The adapter conformance suite from the service repository runs against a live deployment with
`Taisce.Conformance` as the driver: a real `ChatClientAgent` with this provider attached and a
stubbed model, one instruction per line in and one report per line out.

```sh
TAISCE_TOKEN=… TAISCE_API=https://memory.example TAISCE_CASES=../taisce/conformance/cases.json scripts/conformance.sh
```

Eight cases, eight passes. `Taisce.Tests` holds what needs no deployment: the response filter, the turn
key, the memory and context message renderings, which messages are the turn's input, and the refusals.

## The head-to-head

`Taisce.HeadToHead` runs the same sessions through the framework's own chat-history memory provider
and through this provider, then asks the same question: a subject who moved from one city to
another, and "where do I live now?". It prints what each provider handed the model and what the
model answered; nothing is scored, and the output is pasted into the service repository's dated
market document as it came out. It needs a live deployment with a forming worker and an
OpenAI-compatible endpoint for the chat and embedding models (a local Ollama by default).

```sh
TAISCE_API=http://127.0.0.1:8080 TAISCE_TOKEN=… dotnet run --project Taisce.HeadToHead
```

## Versions

Built against Microsoft Agent Framework 1.20.0 and Microsoft.Extensions.AI 10.10.0 on .NET 10. The
context provider needs only the abstractions packages; the compaction strategy needs the framework
package, whose compaction API is marked evaluation-only in this version (diagnostic MAAI001, suppressed
in the adapter project because the version is pinned and the seam is held by the conformance suite).
