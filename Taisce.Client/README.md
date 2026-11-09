# Taisce.Client

The client for a [Taisce](https://github.com/ensera-ai/taisce) memory deployment: the v1 contract and
nothing else. No agent framework, and no dependencies beyond the standard library — a caller who wants
governed memory without an agent framework does not acquire one by asking.

Taisce is open-source agentic memory you run yourself. It answers from entities rather than passages,
keeps time as a first-class property, shows the words behind every claim, and proves what it deleted.

```csharp
using Taisce;

var client = new TaisceClient("https://memory.example", Environment.GetEnvironmentVariable("TAISCE_TOKEN")!);

ObserveMessage[] turn = [new("user", "I work at Ensera and I live in Dublin.")];
await client.ObserveAsync(new ObserveRequest
{
    IdempotencyKey = TurnKey.Derive("alice", runId: null, turn),
    DataSubjectId = "alice",
    Messages = turn,
});

var recall = await client.RecallAsync(new RecallRequest
{
    Question = "Where do I work?",
    DataSubjectId = "alice",
});
```

Every recalled fact carries the quote it came from and the byte span it occupies, so an answer can be
shown with its evidence rather than asserted.

## The address

The constructor refuses a base URL that is not `https`, with an `ArgumentException`, unless its host is
`localhost`, a `127.0.0.0/8` address or `::1`: the credential is a bearer token for a whole project, and
sending it over plaintext to another host would hand it to anyone on the path. There is no opt-in.

## Erasure and export

```csharp
try
{
    var receipt = await client.EraseSubjectAsync("alice", reason: "subject access request");
    // receipt.Deleted and receipt.Residual are counts per kind of row; Clean is true here.
}
catch (TaisceErasureNotCleanException e)
{
    // Rows still match. e.Receipt is the whole receipt, residual counts included.
}

await client.EraseSourcesAsync(documentObservationIds, "document withdrawn"); // up to 10000 ids
var export = await client.ExportSubjectAsync("alice");  // export.Sections: raw JSON rows per section
await client.ExportSourcesAsync(documentObservationIds); // export.Rows: the count per section
```

By subject and by sources are separate methods because the deployment refuses a request that names both
or neither; this way that request cannot be written. An erasure whose receipt is not clean **throws**
`TaisceErasureNotCleanException` rather than returning, so an application that never reads `Clean`
cannot tell a person their data is gone while rows still match. A read-only key's refusal is a
`TaisceForbiddenException`, and an export larger than one response (the deployment does not page) a
`TaisceExportTooLargeException`; both are `TaisceException`s. An empty subject, an empty or oversized
(more than 10000) source list, and an id that is not a UUID are an `ArgumentException` before anything
is sent.

These live on the client and nowhere on `Taisce.AgentFramework`'s model-facing surface: text that
reaches a model is written by someone else and an erasure cannot be undone. Whether a request to forget
is genuine is the application's to decide, in code, before it calls these. Every read-write key can
erase every subject in its project.

## The turn key

`TurnKey.Derive(subject, run, messages)` is the idempotency key every Taisce adapter derives the same
way: SHA-256 over length-prefixed (UTF-8 byte count) fields, shaped as a version-5 UUID. Retrying a turn
sends the same key and is stored once. 0.2.0 changed the derivation, so a turn retried across the
upgrade is stored twice, once.

For the Microsoft Agent Framework, use **Taisce.AgentFramework**, which wires this client in as a
context provider.

Apache-2.0.
