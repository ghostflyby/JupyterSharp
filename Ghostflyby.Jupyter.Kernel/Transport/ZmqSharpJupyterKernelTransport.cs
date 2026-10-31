using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using ZmqSharp;
using ZmqSharp.Transports;

namespace Ghostflyby.Jupyter.Kernel.Transport;

internal sealed class ZmqSharpJupyterKernelTransport : IJupyterKernelTransport
{
    private readonly ZRouterSocket _control;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ZRepSocket _heartbeat;
    private readonly Channel<JupyterKernelTransportEvent> _incomingEvents;
    private readonly ZXPubSocket _iopub;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly JupyterKernelTransportOptions _options;
    private readonly Channel<SendCommand> _outgoingCommands;
    private readonly IJupyterMessageSerializer _serializer;
    private readonly ZRouterSocket _shell;
    private readonly ZRouterSocket _stdin;
    private int _disposeState;
    private Task _outgoingPump = Task.CompletedTask;
    private Exception? _terminalError;

    private ZmqSharpJupyterKernelTransport(
        JupyterConnectionInfo connectionInfo,
        JupyterKernelTransportOptions options)
    {
        this._options = options;
        _serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
        _incomingEvents = Channel.CreateBounded<JupyterKernelTransportEvent>(
            new BoundedChannelOptions(options.IncomingCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        _outgoingCommands = Channel.CreateBounded<SendCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _shell = CreateRouter(JupyterKernelChannel.Shell);
        _control = CreateRouter(JupyterKernelChannel.Control);
        _stdin = CreateRouter(JupyterKernelChannel.Stdin);
        _iopub = new ZXPubSocket(new ZSocketOptions
        {
            MessageSink = new SubscriptionSink(this)
        });
        _heartbeat = new ZRepSocket();
        _heartbeat.BindRequestHandler(EchoHeartbeatAsync);

        // Abnormal peer ends must be observable: without these subscriptions ZmqSharp removes
        // and disposes the peer connection silently, leaving the kernel heartbeating on a
        // connection that can no longer carry protocol traffic.
        _shell.PeerEnded += OnShellPeerEnded;
        _control.PeerEnded += OnControlPeerEnded;
        _stdin.PeerEnded += OnStdinPeerEnded;
        _iopub.PeerEnded += OnIopubPeerEnded;
        _heartbeat.PeerEnded += OnHeartbeatPeerEnded;
    }

    public IAsyncEnumerable<JupyterKernelTransportEvent> IncomingEvents => _incomingEvents.Reader.ReadAllAsync();

    public async ValueTask SendAsync(
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ThrowIfTerminated();

        var command = new SendCommand(
            channel,
            message,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_outgoingCommands.Writer.TryWrite(command))
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            ThrowIfTerminated();

            var exception = new JupyterKernelBackpressureException(
                $"The Jupyter kernel outgoing queue exceeded its capacity of {_options.OutgoingCapacity}.");
            await TerminateAsync(exception).ConfigureAwait(false);
            throw exception;
        }

        await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            try
            {
                _outgoingCommands.Writer.TryComplete(Volatile.Read(ref _terminalError));
                _incomingEvents.Writer.TryComplete(Volatile.Read(ref _terminalError));
                await _lifetime.CancelAsync().ConfigureAwait(false);
                await _outgoingPump.ConfigureAwait(false);
                await DisposeSocketsAsync().ConfigureAwait(false);
                _disposed.TrySetResult();
            }
            catch (Exception exception)
            {
                _disposed.TrySetException(exception);
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        await _disposed.Task.ConfigureAwait(false);
    }

    public static async Task<ZmqSharpJupyterKernelTransport> BindAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterKernelTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connectionInfo.ValidateSupported();
        var transport = new ZmqSharpJupyterKernelTransport(
            connectionInfo,
            options ?? new JupyterKernelTransportOptions());
        try
        {
            await transport._shell.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Shell),
                cancellationToken).ConfigureAwait(false);
            await transport._control.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Control),
                cancellationToken).ConfigureAwait(false);
            await transport._stdin.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Stdin),
                cancellationToken).ConfigureAwait(false);
            await transport._iopub.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Iopub),
                cancellationToken).ConfigureAwait(false);
            await transport._heartbeat.BindAsync(
                connectionInfo.Endpoint(JupyterChannel.Heartbeat),
                cancellationToken).ConfigureAwait(false);
            transport._outgoingPump = transport.ProcessOutgoingAsync();
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ZRouterSocket CreateRouter(JupyterKernelChannel channel)
    {
        return new ZRouterSocket(new ZSocketOptions
        {
            MessageSink = new MessageSink(this, channel)
        });
    }

    private async Task ProcessOutgoingAsync()
    {
        try
        {
            await foreach (var command in _outgoingCommands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                try
                {
                    await SendCoreAsync(command.Channel, command.Message, _lifetime.Token).ConfigureAwait(false);
                    command.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    command.Completion.TrySetException(
                        new ObjectDisposedException(nameof(ZmqSharpJupyterKernelTransport)));
                    return;
                }
                catch (Exception exception)
                {
                    command.Completion.TrySetException(exception);
                    await TerminateAsync(exception).ConfigureAwait(false);
                    return;
                }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await TerminateAsync(exception).ConfigureAwait(false);
        }
        finally
        {
            var pendingError = Volatile.Read(ref _terminalError)
                               ?? new ObjectDisposedException(nameof(ZmqSharpJupyterKernelTransport));
            while (_outgoingCommands.Reader.TryRead(out var command)) command.Completion.TrySetException(pendingError);
        }
    }

    private async ValueTask SendCoreAsync(
        JupyterKernelChannel channel,
        JupyterWireMessage message,
        CancellationToken cancellationToken)
    {
        var frames = _serializer.Serialize(message);
        switch (channel)
        {
            case JupyterKernelChannel.Shell:
                await SendRouterAsync(_shell, frames, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterKernelChannel.Control:
                await SendRouterAsync(_control, frames, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterKernelChannel.Iopub:
                await _iopub.SendAsync(ZMessage.FromOwned(frames.ToArray()), cancellationToken).ConfigureAwait(false);
                break;
            case JupyterKernelChannel.Stdin:
                await SendRouterAsync(_stdin, frames, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
        }
    }

    private static async ValueTask SendRouterAsync(
        ZRouterSocket socket,
        IReadOnlyList<byte[]> frames,
        CancellationToken cancellationToken)
    {
        if (frames.Count < 2)
            throw new JupyterProtocolException("A routed Jupyter message requires a routing identity and payload.");

        var payload = new byte[frames.Count - 1][];
        for (var index = 1; index < frames.Count; index++) payload[index - 1] = frames[index];

        await socket.SendAsync(
            frames[0],
            ZMessage.FromOwned(payload),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EchoHeartbeatAsync(ZRequestContext context, CancellationToken cancellationToken)
    {
        var frames = new byte[context.Count][];
        for (var index = 0; index < context.Count; index++)
            frames[index] = context[index].ToSequence().ToArray();

        await _heartbeat.SendReplyAsync(
            context,
            ZMessage.FromOwned(frames),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnqueueAsync(JupyterKernelTransportEvent transportEvent)
    {
        if (_incomingEvents.Writer.TryWrite(transportEvent)) return;
        if (_lifetime.IsCancellationRequested) return;

        var exception = new JupyterKernelBackpressureException(
            $"The Jupyter kernel incoming queue exceeded its capacity of {_options.IncomingCapacity}.");
        await TerminateAsync(exception).ConfigureAwait(false);
        throw exception;
    }

    private async ValueTask TerminateAsync(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _terminalError, exception, null) is not null) return;

        _outgoingCommands.Writer.TryComplete(exception);
        _incomingEvents.Writer.TryComplete(exception);
        await _lifetime.CancelAsync().ConfigureAwait(false);
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref _terminalError) is { } exception)
            throw new JupyterProtocolException("The Jupyter kernel transport has terminated.", exception);
    }

    private void OnPeerEnded(string channel, IZConnection peer, Exception? failure)
    {
        if (failure is null || _lifetime.IsCancellationRequested) return;

        // Unlike the client transport (one dedicated peer per socket, where an abnormal end
        // is terminal), the server-side ROUTER/XPUB/REP sockets serve many peers: one peer
        // ending abnormally - typically a client closing while kernel replies are still in
        // flight, which surfaces as a connection reset - is routine and must not terminate
        // the transport for the remaining peers. The teardown is made observable instead of
        // silently removing the peer; events may be dropped when the queue is saturated.
        _incomingEvents.Writer.TryWrite(new JupyterKernelPeerEnded(channel, failure));
    }

    private void OnShellPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("shell", peer, failure);
    }

    private void OnControlPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("control", peer, failure);
    }

    private void OnStdinPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("stdin", peer, failure);
    }

    private void OnIopubPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("IOPub", peer, failure);
    }

    private void OnHeartbeatPeerEnded(IZConnection peer, Exception? failure)
    {
        OnPeerEnded("heartbeat", peer, failure);
    }

    private async Task DisposeSocketsAsync()
    {
        _shell.PeerEnded -= OnShellPeerEnded;
        _control.PeerEnded -= OnControlPeerEnded;
        _stdin.PeerEnded -= OnStdinPeerEnded;
        _iopub.PeerEnded -= OnIopubPeerEnded;
        _heartbeat.PeerEnded -= OnHeartbeatPeerEnded;

        List<Exception>? failures = null;
        // Shutdown travels over control. Closing that ROUTER first preserves the
        // reply-before-EOF order on one connection before unrelated peers end.
        IAsyncDisposable[] sockets = [_control, _shell, _stdin, _iopub, _heartbeat];
        foreach (var socket in sockets)
            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

        if (failures is not null) throw new AggregateException("Failed to dispose ZmqSharp kernel sockets.", failures);
    }

    private sealed record SendCommand(
        JupyterKernelChannel Channel,
        JupyterWireMessage Message,
        TaskCompletionSource Completion);

    private sealed class MessageSink(
        ZmqSharpJupyterKernelTransport owner,
        JupyterKernelChannel channel) : IPatternSink
    {
        public async ValueTask OnMessageAsync(
            IZConnection peer,
            ZMessage message,
            CancellationToken token = default)
        {
            try
            {
                var frames = new byte[message.Count][];
                for (var index = 0; index < message.Count; index++)
                    frames[index] = message[index].ToSequence().ToArray();

                JupyterWireMessage wireMessage;
                try
                {
                    wireMessage = owner._serializer.Deserialize(frames);
                }
                catch (JupyterSignatureException exception)
                {
                    // Frames that fail HMAC verification cannot be trusted as protocol
                    // traffic: terminate instead of interpreting them further.
                    await owner.TerminateAsync(exception).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is JsonException or JupyterProtocolException)
                {
                    // One malformed payload must not remove the peer or fault ZmqSharp's
                    // receive pump for the channel; the message is dropped and the
                    // connection stays usable for well-formed traffic.
                    return;
                }

                await owner.EnqueueAsync(new JupyterKernelMessageReceived(channel, wireMessage)).ConfigureAwait(false);
            }
            finally
            {
                message.Dispose();
            }
        }
    }

    private sealed class SubscriptionSink(ZmqSharpJupyterKernelTransport owner) : IPatternSink
    {
        public async ValueTask OnMessageAsync(
            IZConnection peer,
            ZMessage message,
            CancellationToken token = default)
        {
            try
            {
                for (var index = 0; index < message.Count; index++)
                {
                    var subscription = message[index].ToSequence().ToArray();
                    if (subscription.Length == 0 || subscription[0] == 0) continue;

                    await owner.EnqueueAsync(
                        new JupyterIopubSubscriptionReceived(subscription[1..])).ConfigureAwait(false);
                }
            }
            finally
            {
                message.Dispose();
            }
        }
    }
}

internal sealed class JupyterKernelBackpressureException(string message) : Exception(message);
