using Ghostflyby.Jupyter;

namespace Ghostflyby.Jupyter.Client.Transport;

/// <summary>
///     The channel-level transport seam: how the five Jupyter channels are carried between the
///     protocol session and a peer.
/// </summary>
/// <remarks>
///     This is deliberately internal. It is an in-process seam — the test transport, and any
///     future in-process kernel embedding — not a third-party transport extension point. The ZMQ
///     byte-pipe layer below it is ZmqSharp's <c>IZTransport</c>, which is where the endpoint
///     family (tcp / ipc / arbitrary stream) is selected; the channel topology above it is fixed
///     by the Jupyter protocol, so swapping this seam cannot produce a peer-to-peer alternative to
///     ZMQ. The frontend-to-jupyter_server WebSocket transport is a different client with its own
///     REST plane, not another implementation of this interface.
/// </remarks>
internal interface IJupyterTransport : IAsyncDisposable
{
    IAsyncEnumerable<JupyterTransportMessage> IncomingMessages { get; }

    ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default);

    Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default);
}

internal interface IJupyterTransportConnectionReadiness
{
    Task WaitForStdinConnectedAsync(CancellationToken cancellationToken = default);
}

public sealed record JupyterTransportOptions
{
    public int IncomingCapacity { get; init; } = 1024;

    public int OutgoingCapacity { get; init; } = 256;
}
