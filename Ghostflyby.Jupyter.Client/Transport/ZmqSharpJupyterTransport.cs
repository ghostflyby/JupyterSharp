using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using ZmqSharp;
using ZmqSharp.Transports;

namespace Ghostflyby.Jupyter.Client.Transport;

/// <summary>
/// Provides an asynchronous ZeroMQ transport for a Jupyter client.
/// </summary>
internal sealed class ZmqSharpJupyterTransport : IJupyterTransport, IJupyterTransportConnectionReadiness
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly JupyterConnectionInfo _connectionInfo;
    private readonly Channel<PingCommand> _heartbeatCommands;
    private readonly Channel<JupyterTransportMessage> _incomingMessages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly JupyterTransportOptions _options;
    private readonly Channel<ClientCommand> _outgoingCommands;
    private readonly IJupyterMessageSerializer _serializer;
    private readonly TaskCompletionSource _stdinConnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeState;
    private Task _ownerTask = Task.CompletedTask;
    private Exception? _terminalError;

    private ZmqSharpJupyterTransport(JupyterConnectionInfo connectionInfo, JupyterTransportOptions options)
    {
        this._connectionInfo = connectionInfo;
        this._options = options;
        _serializer = new JupyterMessageSerializer(connectionInfo.Key, connectionInfo.SignatureScheme);
        _incomingMessages = Channel.CreateBounded<JupyterTransportMessage>(
            new BoundedChannelOptions(options.IncomingCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        _outgoingCommands = Channel.CreateBounded<ClientCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _heartbeatCommands = Channel.CreateBounded<PingCommand>(new BoundedChannelOptions(options.OutgoingCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <inheritdoc />
    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => _incomingMessages.Reader.ReadAllAsync();

    /// <inheritdoc />
    public async ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        if (channel == JupyterTransportChannel.Iopub)
            throw new InvalidOperationException("A Jupyter client cannot send messages on IOPub.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new SendCommand(channel, message, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new PingCommand(completion), cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    Task IJupyterTransportConnectionReadiness.WaitForStdinConnectedAsync(CancellationToken cancellationToken)
    {
        return _stdinConnected.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var ownsDisposal = Interlocked.Exchange(ref _disposeState, 1) == 0;
        if (ownsDisposal) await _lifetime.CancelAsync().ConfigureAwait(false);

        await _ownerTask.ConfigureAwait(false);
        if (ownsDisposal) _lifetime.Dispose();
    }

    /// <summary>
    /// Starts a client transport for the supplied Jupyter connection.
    /// </summary>
    public static Task<ZmqSharpJupyterTransport> ConnectAsync(
        JupyterConnectionInfo connectionInfo,
        JupyterTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionInfo);
        cancellationToken.ThrowIfCancellationRequested();
        connectionInfo.ValidateSupported();
        var transport = new ZmqSharpJupyterTransport(connectionInfo, options ?? new JupyterTransportOptions());
        transport.Start();
        return Task.FromResult(transport);
    }

    private void Start()
    {
        _ownerTask = RunOwnerAsync();
    }

    private async Task RunOwnerAsync()
    {
        var sockets = new ClientSocketOwner(this);
        try
        {
            await sockets.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
            await Task.WhenAll(
                    ObservePumpAsync(() => ProcessCommandsAsync(sockets, _lifetime.Token)),
                    ObservePumpAsync(() => ProcessHeartbeatsAsync(sockets.Heartbeat, _lifetime.Token)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
        }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);

            try
            {
                await sockets.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _terminalError, exception, null);
            }

            var completionError = Volatile.Read(ref _terminalError);
            var pendingError = completionError ?? new ObjectDisposedException(nameof(ZmqSharpJupyterTransport));
            _stdinConnected.TrySetException(pendingError);
            _outgoingCommands.Writer.TryComplete(completionError);
            _heartbeatCommands.Writer.TryComplete(completionError);
            while (_outgoingCommands.Reader.TryRead(out var command)) command.Fail(pendingError);
            while (_heartbeatCommands.Reader.TryRead(out var ping)) ping.Fail(pendingError);

            _incomingMessages.Writer.TryComplete(completionError);
        }
    }

    private async Task ObservePumpAsync(Func<Task> pump)
    {
        try
        {
            await pump().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
    }

    private async Task ProcessCommandsAsync(ClientSocketOwner sockets, CancellationToken cancellationToken)
    {
        await foreach (var command in _outgoingCommands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            switch (command)
            {
                case SendCommand send:
                    try
                    {
                        await SendAsync(sockets, send, cancellationToken).ConfigureAwait(false);
                        send.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        send.Fail(exception);
                        throw;
                    }

                    break;
                case PingCommand ping:
                    if (_heartbeatCommands.Writer.TryWrite(ping)) break;

                    var backpressure = CreateOutgoingBackpressureException();
                    ping.Fail(backpressure);
                    throw backpressure;
            }
    }

    private async Task ProcessHeartbeatsAsync(ZReqSocket heartbeat, CancellationToken cancellationToken)
    {
        await foreach (var command in _heartbeatCommands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = Guid.NewGuid().ToByteArray();
            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                using var response = await heartbeat.RequestAsync(
                        ZMessage.FromOwned(payload), cancellationToken)
                    .ConfigureAwait(false);
                if (response.Count != 1 || !response[0].ToSequence().ToArray().AsSpan().SequenceEqual(payload))
                    throw new JupyterProtocolException("Heartbeat reply did not match its request.");

                command.Completion.TrySetResult(Stopwatch.GetElapsedTime(startTimestamp));
            }
            catch (Exception exception)
            {
                command.Fail(exception);
                throw;
            }
        }
    }

    private async ValueTask SendAsync(
        ClientSocketOwner sockets,
        SendCommand command,
        CancellationToken cancellationToken)
    {
        var frames = _serializer.Serialize(JupyterWireMessage.Create(command.Message)).ToArray();
        var message = ZMessage.FromOwned(frames);
        switch (command.Channel)
        {
            case JupyterTransportChannel.Shell:
                await sockets.Shell.SendAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterTransportChannel.Control:
                await sockets.Control.SendAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case JupyterTransportChannel.Stdin:
                await sockets.Stdin.SendAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            default:
                message.Dispose();
                throw new ArgumentOutOfRangeException(nameof(command), command.Channel, null);
        }
    }

    private ValueTask EnqueueAsync(ClientCommand command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminated();

        if (_outgoingCommands.Writer.TryWrite(command)) return ValueTask.CompletedTask;

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ThrowIfTerminated();
        var exception = CreateOutgoingBackpressureException();
        command.Fail(exception);
        Terminate(exception);
        throw exception;
    }

    private JupyterBackpressureException CreateOutgoingBackpressureException()
    {
        return new JupyterBackpressureException(
            $"The Jupyter client outgoing queue exceeded its capacity of {_options.OutgoingCapacity}.");
    }

    private void Receive(JupyterTransportChannel channel, ZMessage message)
    {
        try
        {
            var frames = message.Select(static frame => frame.ToSequence().ToArray()).ToArray();
            var wireMessage = _serializer.Deserialize(frames);
            if (_incomingMessages.Writer.TryWrite(new JupyterTransportMessage(channel, wireMessage))) return;

            Terminate(new JupyterBackpressureException(
                $"The Jupyter client incoming queue exceeded its capacity of {_options.IncomingCapacity}."));
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
        finally
        {
            message.Dispose();
        }
    }

    private void OnPeerEnded(string channel, IZConnection peer, Exception? failure)
    {
        if (_lifetime.IsCancellationRequested) return;

        // A peer closing the connection cleanly (failure == null) is a normal
        // shutdown signal, not an error: the kernel closes its sockets after
        // the shutdown exchange, and EOF arrival order across the five TCP
        // connections is not guaranteed - terminating the session on any
        // clean EOF would race the in-flight shutdown_reply (and fail pending
        // requests). Only an abnormal peer end (protocol/IO failure) is
        // terminal for the transport.
        if (failure is null) return;

        Terminate(failure);
    }

    private void Terminate(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _terminalError, exception, null) is not null) return;

        _incomingMessages.Writer.TryComplete(exception);
        _lifetime.Cancel();
    }

    private void ThrowIfTerminated()
    {
        if (Volatile.Read(ref _terminalError) is { } exception)
            throw new JupyterProtocolException("The Jupyter client transport has terminated.", exception);
    }

    private static async Task ConnectWithRetryAsync(
        IZSocket socket,
        string endpoint,
        CancellationToken cancellationToken)
    {
        while (true)
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode == SocketError.ConnectionRefused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
            }
    }

    private abstract record ClientCommand
    {
        public abstract void Fail(Exception exception);
    }

    private sealed record SendCommand(
        JupyterTransportChannel Channel,
        JupyterMessage Message,
        TaskCompletionSource Completion) : ClientCommand
    {
        public override void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    private sealed record PingCommand(TaskCompletionSource<TimeSpan> Completion) : ClientCommand
    {
        public override void Fail(Exception exception)
        {
            Completion.TrySetException(exception);
        }
    }

    private sealed class IncomingSink(ZmqSharpJupyterTransport owner, JupyterTransportChannel channel) : IPatternSink
    {
        public ValueTask OnMessageAsync(
            IZConnection peer,
            ZMessage message,
            CancellationToken token = default)
        {
            owner.Receive(channel, message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ClientSocketOwner : IAsyncDisposable
    {
        private readonly ZmqSharpJupyterTransport _owner;

        public ClientSocketOwner(ZmqSharpJupyterTransport owner)
        {
            this._owner = owner;
            var clientIdentity = Guid.NewGuid().ToByteArray();
            Shell = CreateDealer(clientIdentity, new IncomingSink(owner, JupyterTransportChannel.Shell));
            Control = CreateDealer(Guid.NewGuid().ToByteArray(),
                new IncomingSink(owner, JupyterTransportChannel.Control));
            Stdin = CreateDealer(clientIdentity, new IncomingSink(owner, JupyterTransportChannel.Stdin));
            Iopub = new ZSubSocket(new ZSocketOptions
            {
                MessageSink = new IncomingSink(owner, JupyterTransportChannel.Iopub)
            });
            Heartbeat = new ZReqSocket();
            Iopub.Subscribe([]);

            Shell.PeerEnded += OnShellEnded;
            Control.PeerEnded += OnControlEnded;
            Stdin.PeerEnded += OnStdinEnded;
            Iopub.PeerEnded += OnIopubEnded;
            Heartbeat.PeerEnded += OnHeartbeatEnded;
        }

        public ZDealerSocket Shell { get; }

        public ZDealerSocket Control { get; }

        public ZDealerSocket Stdin { get; }

        public ZSubSocket Iopub { get; }

        public ZReqSocket Heartbeat { get; }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await Task.WhenAll(
                    ObserveConnectAsync(Shell, _owner._connectionInfo.Endpoint(JupyterChannel.Shell), cancellationToken),
                    ObserveConnectAsync(Control, _owner._connectionInfo.Endpoint(JupyterChannel.Control),
                        cancellationToken),
                    ConnectStdinAsync(cancellationToken),
                    ObserveConnectAsync(Iopub, _owner._connectionInfo.Endpoint(JupyterChannel.Iopub), cancellationToken),
                    ObserveConnectAsync(Heartbeat, _owner._connectionInfo.Endpoint(JupyterChannel.Heartbeat),
                        cancellationToken))
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Shell.PeerEnded -= OnShellEnded;
            Control.PeerEnded -= OnControlEnded;
            Stdin.PeerEnded -= OnStdinEnded;
            Iopub.PeerEnded -= OnIopubEnded;
            Heartbeat.PeerEnded -= OnHeartbeatEnded;

            Exception? failure = null;
            failure = await DisposeSocketAsync(Heartbeat, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Iopub, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Stdin, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Control, failure).ConfigureAwait(false);
            failure = await DisposeSocketAsync(Shell, failure).ConfigureAwait(false);
            if (failure is not null) throw failure;
        }

        private async Task ConnectStdinAsync(CancellationToken cancellationToken)
        {
            await ObserveConnectAsync(
                    Stdin,
                    _owner._connectionInfo.Endpoint(JupyterChannel.Stdin),
                    cancellationToken)
                .ConfigureAwait(false);
            _owner._stdinConnected.TrySetResult();
        }

        private async Task ObserveConnectAsync(
            IZSocket socket,
            string endpoint,
            CancellationToken cancellationToken)
        {
            try
            {
                await ConnectWithRetryAsync(socket, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _owner.Terminate(exception);
                throw;
            }
        }

        private static async ValueTask<Exception?> DisposeSocketAsync(
            IAsyncDisposable socket,
            Exception? failure)
        {
            try
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            return failure;
        }

        private static ZDealerSocket CreateDealer(byte[] identity, IPatternSink sink)
        {
            return new ZDealerSocket(new ZSocketOptions
            {
                Identity = identity,
                MessageSink = sink
            });
        }

        private void OnShellEnded(IZConnection peer, Exception? failure)
        {
            _owner.OnPeerEnded("shell", peer, failure);
        }

        private void OnControlEnded(IZConnection peer, Exception? failure)
        {
            _owner.OnPeerEnded("control", peer, failure);
        }

        private void OnStdinEnded(IZConnection peer, Exception? failure)
        {
            _owner.OnPeerEnded("stdin", peer, failure);
        }

        private void OnIopubEnded(IZConnection peer, Exception? failure)
        {
            _owner.OnPeerEnded("IOPub", peer, failure);
        }

        private void OnHeartbeatEnded(IZConnection peer, Exception? failure)
        {
            _owner.OnPeerEnded("heartbeat", peer, failure);
        }
    }
}
