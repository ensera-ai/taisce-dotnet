# Taisce.AgentFramework

The Microsoft Agent Framework adapter for [Taisce](https://github.com/ensera-ai/taisce): a context
provider that injects governed memory as one untrusted user message and records the turn afterwards.

Taisce is open-source agentic memory you run yourself. It answers from entities rather than passages,
keeps time as a first-class property, shows the words behind every claim, and proves what it deleted.

```csharp
services.AddTaisceMemory(options =>
{
    options.Endpoint = "http://localhost:8080";
    options.Token = Environment.GetEnvironmentVariable("TAISCE_TOKEN")!;
});
```

Memory reaches the model as **one user message marked untrusted**, never as a system message. Text the
deployment returns was written by somebody else, and a model that reads it as instruction is a model
that can be steered by anything anyone ever said to it.

The credential goes to the configured deployment and nowhere else: the adapter never sets it as a
default header on a caller-supplied `HttpClient`, which would send it to every host that client
touches for the rest of its life.

Apache-2.0.
