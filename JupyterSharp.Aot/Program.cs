using JupyterSharp;
using JupyterSharp.Client;
using JupyterSharp.Kernel;

// A real end-to-end round trip published as Native AOT: binds a kernel host, connects the
// client over loopback TCP, executes, and checks the streamed output. Running under AOT is
// the point - reflection-based JSON or an unannotated trim path fails here, not in a
// consumer's publish.
var connection = JupyterConnectionInfo.CreateLocalTcp();
await using var host = await JupyterKernelHost.StartAsync(connection, new EchoKernel());
await using var client = await JupyterClient.ConnectAsync(connection);

var info = await client.GetKernelInfoAsync();
if (info.ProtocolVersion != "5.5" || info.Implementation != "jupyter-sharp-aot")
{
    Console.Error.WriteLine($"FAIL: unexpected kernel info {info.ProtocolVersion}/{info.Implementation}");
    return 1;
}

await using var execution = await client.ExecuteAsync(new JupyterExecuteRequest("hello"));
var stdout = new List<string>();
await foreach (var output in execution.Outputs)
{
    if (output is JupyterStdout stream) stdout.Add(stream.Text);
}

var result = await execution.Completion;
if (result.Reply.Status != "ok" || !stdout.Contains("hello"))
{
    Console.Error.WriteLine($"FAIL: status={result.Reply.Status} stdout=[{string.Join(",", stdout)}]");
    return 1;
}

Console.WriteLine("ok: AOT round trip completed");
return 0;

internal sealed class EchoKernel : IJupyterKernelApplication
{
    public JupyterKernelInfo KernelInfo { get; } = new(
        "5.5",
        "jupyter-sharp-aot",
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
