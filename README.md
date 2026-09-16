# Ghostflyby.Jupyter

[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

A .NET implementation of the classic Jupyter messaging protocol (5.5): the wire protocol, a client for arbitrary
kernels, and a server-side kernel host. Targets `net10.0`, Native AOT compatible, no native dependencies.

## Packages

| Package | Role |
|---|---|
| [`Ghostflyby.Jupyter`](https://www.nuget.org/packages/Ghostflyby.Jupyter) | Transport-independent wire protocol: envelopes, HMAC signing, connection files, MIME bundles, cursor conversion |
| [`Ghostflyby.Jupyter.Client`](https://www.nuget.org/packages/Ghostflyby.Jupyter.Client) | Client for arbitrary Jupyter kernels, plus a local kernel process manager |
| [`Ghostflyby.Jupyter.Kernel`](https://www.nuget.org/packages/Ghostflyby.Jupyter.Kernel) | Server-side kernel host: ROUTER/XPUB/REP transport, dispatch, state publication, interrupt, shutdown |

`Ghostflyby.Jupyter` has no transport dependency. Only `Client` and `Kernel` depend on
[ZmqSharp](https://www.nuget.org/packages/ZmqSharp) for ZeroMQ; the wire protocol package is usable on its own.

## Client

```csharp
using Ghostflyby.Jupyter;
using Ghostflyby.Jupyter.Client;

var connection = await JupyterConnectionInfo.ReadFileAsync("/path/to/kernel-1234.json");
await using var client = await JupyterClient.ConnectAsync(connection);

await using var execution = await client.ExecuteAsync(new JupyterExecuteRequest("1 + 1"));
await foreach (var output in execution.Outputs)
{
    // streamed execute_result / display_data / stream / error output
}

await foreach (var clientEvent in client.WatchEventsAsync())
{
    // independent event stream: kernel status, display updates, late output
}
```

Correlation is by message ID, channel, and expected reply type. An execution completes only when both its reply and its
parented `idle` have arrived, in either order.

## Kernel host

Kernel authors implement the application interfaces and never touch ZeroMQ or wire envelopes:

```csharp
using Ghostflyby.Jupyter;
using Ghostflyby.Jupyter.Kernel;

sealed class MyKernel : IJupyterKernelApplication
{
    public JupyterKernelInfo KernelInfo { get; } = new(
        "5.5",
        "mykernel",
        "1.0.0",
        new JupyterLanguageInfo("text", "1.0"));

    public async ValueTask<JupyterExecuteResult> ExecuteAsync(
        JupyterExecutionContext context,
        JupyterExecuteRequest request,
        CancellationToken cancellationToken)
    {
        await context.WriteStdoutAsync(request.Code, cancellationToken);
        return JupyterExecuteResult.Ok;
    }
}

var connection = JupyterConnectionInfo.CreateLocalTcp();
await using var host = await JupyterKernelHost.StartAsync(connection, new MyKernel());
await host.Completion;
```

`JupyterExecutionContext` also exposes `WriteStderrAsync`, `DisplayAsync`, `DisplayTrackedAsync`,
`UpdateDisplayAsync`, `ClearOutputAsync`, `PublishResultAsync`, `PublishErrorAsync`, and `RequestInputAsync`.
Optional capabilities are separate interfaces: `IJupyterCompletionProvider`, `IJupyterInspectionProvider`,
`IJupyterCodeCompletenessProvider`, `IJupyterCommSink`, and `IJupyterKernelLifecycle` (shutdown notification for an
application that holds resources). The host does not dispose your application — the caller that constructed it owns it.

## Unknown and missing fields

`JupyterMessage.Content` holds the original JSON and is written back verbatim, so unknown fields from a peer survive a
receive-and-forward round trip. The typed records are a convenience projection: projecting through `GetContent<T>` and
re-serializing drops fields the record does not know about.

Missing fields are tolerated, and a field a peer omits is bound to `default` rather than failing deserialization — so a
non-nullable `string` property can hold `null` after a successful read. Where a real kernel omits a field, the record
supplies an explicit default.

## Protocol scope

Classic messaging protocol 5.5 and classic connection files. Readers tolerate unknown optional fields and compatible
older protocol announcements; writers avoid unsupported fields; unsupported capabilities return protocol-valid errors
or fallback statuses; CurveZMQ and unsupported signature schemes fail explicitly.

History, comm-broadcast beyond the classic comm messages, debug, and subshell are out of scope.

## Building

```bash
dotnet build Ghostflyby.Jupyter.slnx -warnaserror
dotnet test Ghostflyby.Jupyter.slnx
dotnet format Ghostflyby.Jupyter.slnx --verify-no-changes --no-restore
```

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
