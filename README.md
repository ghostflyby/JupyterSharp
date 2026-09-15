# JupyterSharp

[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

A .NET implementation of the classic Jupyter messaging protocol (5.5): the wire protocol, a client for arbitrary
kernels, and a server-side kernel host. Targets `net10.0`, Native AOT compatible, no native dependencies.

## Packages

| Package | Role |
|---|---|
| [`JupyterSharp`](https://www.nuget.org/packages/JupyterSharp) | Transport-independent wire protocol: envelopes, HMAC signing, connection files, MIME bundles, cursor conversion |
| [`JupyterSharp.Client`](https://www.nuget.org/packages/JupyterSharp.Client) | Client for arbitrary Jupyter kernels, plus a local kernel process manager |
| [`JupyterSharp.Kernel`](https://www.nuget.org/packages/JupyterSharp.Kernel) | Server-side kernel host: ROUTER/XPUB/REP transport, dispatch, state publication, interrupt, shutdown |

`JupyterSharp` has no transport dependency. Only `Client` and `Kernel` depend on
[ZmqSharp](https://www.nuget.org/packages/ZmqSharp) for ZeroMQ; the wire protocol package is usable on its own.

## Client

```csharp
using JupyterSharp;
using JupyterSharp.Client;

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
using JupyterSharp;
using JupyterSharp.Kernel;

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
`IJupyterCodeCompletenessProvider`, and `IJupyterCommSink`.

## Protocol scope

Classic messaging protocol 5.5 and classic connection files. Readers tolerate unknown optional fields and compatible
older protocol announcements; writers avoid unsupported fields; unsupported capabilities return protocol-valid errors
or fallback statuses; CurveZMQ and unsupported signature schemes fail explicitly.

History, comm-broadcast beyond the classic comm messages, debug, and subshell are out of scope.

## Building

```bash
dotnet build JupyterSharp.slnx -warnaserror
dotnet test JupyterSharp.slnx
dotnet format JupyterSharp.slnx --verify-no-changes --no-restore
```

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
