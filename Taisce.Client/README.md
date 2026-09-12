# Taisce.Client

The client for a [Taisce](https://github.com/ensera-ai/taisce) memory deployment: the v1 contract and
nothing else. No agent framework, and no dependencies beyond the standard library — a caller who wants
governed memory without an agent framework does not acquire one by asking.

Taisce is open-source agentic memory you run yourself. It answers from entities rather than passages,
keeps time as a first-class property, shows the words behind every claim, and proves what it deleted.

```csharp
using Taisce;

var client = new TaisceClient("http://localhost:8080", Environment.GetEnvironmentVariable("TAISCE_TOKEN")!);

await client.ObserveAsync(new ObserveRequest
{
    DataSubjectId = "alice",
    Messages = [new Message { Role = "user", Content = "I work at Ensera and I live in Dublin." }],
});

var recall = await client.RecallAsync(new RecallRequest
{
    Question = "Where do I work?",
    DataSubjectId = "alice",
});
```

Every recalled fact carries the quote it came from and the byte span it occupies, so an answer can be
shown with its evidence rather than asserted.

For the Microsoft Agent Framework, use **Taisce.AgentFramework**, which wires this client in as a
context provider.

Apache-2.0.
