using Ghostflyby.Jupyter.Client.Protocol;
using Ghostflyby.Jupyter.Client.Transport;

namespace Ghostflyby.Jupyter.Client;

public sealed class JupyterClient : IJupyterClient
{
    private readonly IJupyterProtocolSession _protocolSession;

    private JupyterClient(IJupyterProtocolSession protocolSession)
    {
        this._protocolSession = protocolSession;
    }

    public IAsyncEnumerable<JupyterClientEvent> WatchEventsAsync(
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.WatchEventsAsync(cancellationToken);
    }

    public Task<JupyterKernelInfo> GetKernelInfoAsync(CancellationToken cancellationToken = default)
    {
        return _protocolSession.GetKernelInfoAsync(cancellationToken);
    }

    public Task<IJupyterExecution> ExecuteAsync(
        JupyterExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.StartExecutionAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IJupyterExecution> ExecuteAsync(
        JupyterExecuteRequest request,
        JupyterExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.StartExecutionAsync(request, options, cancellationToken);
    }

    public Task<JupyterCompleteReply> CompleteAsync(
        JupyterCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.CompleteAsync(request, cancellationToken);
    }

    public Task<JupyterInspectReply> InspectAsync(
        JupyterInspectRequest request,
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.InspectAsync(request, cancellationToken);
    }

    public Task<JupyterIsCompleteReply> IsCompleteAsync(
        JupyterIsCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.IsCompleteAsync(request, cancellationToken);
    }

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        return _protocolSession.PingAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _protocolSession.DisposeAsync();
    }

    public static async Task<JupyterClient> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterSessionIdentity? session = null,
        JupyterTransportOptions? transportOptions = null,
        CancellationToken cancellationToken = default)
    {
        var transport = await ZmqSharpJupyterTransport.ConnectAsync(
            connectionInfo,
            transportOptions,
            cancellationToken).ConfigureAwait(false);
        return new JupyterClient(new JupyterProtocolSession(transport, session));
    }

    internal static JupyterClient CreateForTransport(
        IJupyterTransport transport,
        JupyterSessionIdentity? session = null)
    {
        return new JupyterClient(new JupyterProtocolSession(transport, session));
    }

    internal Task<JupyterKernelInfo> WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        return _protocolSession.WaitForReadyAsync(cancellationToken);
    }

    internal Task<JupyterInterruptReply> InterruptAsync(CancellationToken cancellationToken = default)
    {
        return _protocolSession.InterruptAsync(cancellationToken);
    }

    internal ValueTask SendCommAsync(JupyterMessage message, CancellationToken cancellationToken = default)
    {
        return _protocolSession.SendCommAsync(message, cancellationToken);
    }

    internal Task<JupyterShutdownReply> ShutdownAsync(
        bool restart,
        CancellationToken cancellationToken = default)
    {
        return _protocolSession.ShutdownAsync(restart, cancellationToken);
    }
}
