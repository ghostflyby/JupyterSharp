using System.Threading.Channels;
using Ghostflyby.Jupyter.Client.Transport;

namespace Ghostflyby.Jupyter.Tests;

internal sealed class FakeJupyterTransport : IJupyterTransport
{
    private readonly Channel<JupyterTransportMessage> _incomingMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly Channel<JupyterTransportMessage> _sentMessages =
        Channel.CreateUnbounded<JupyterTransportMessage>();

    private readonly List<JupyterTransportMessage> _sentMessagesHistory = [];
    private readonly Lock _sentMessagesGate = new();

    public IReadOnlyList<JupyterTransportMessage> SentMessages
    {
        get
        {
            lock (_sentMessagesGate)
            {
                return _sentMessagesHistory.ToArray();
            }
        }
    }

    public IAsyncEnumerable<JupyterTransportMessage> SentMessageStream => _sentMessages.Reader.ReadAllAsync();

    public IAsyncEnumerable<JupyterTransportMessage> IncomingMessages => _incomingMessages.Reader.ReadAllAsync();

    public ValueTask SendAsync(
        JupyterTransportChannel channel,
        JupyterMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transportMessage = new JupyterTransportMessage(channel, JupyterWireMessage.Create(message));
        _sentMessages.Writer.TryWrite(transportMessage);
        lock (_sentMessagesGate)
        {
            _sentMessagesHistory.Add(transportMessage);
        }

        return ValueTask.CompletedTask;
    }

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TimeSpan.FromMilliseconds(1));
    }

    public ValueTask DisposeAsync()
    {
        _incomingMessages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Receive(
        JupyterTransportChannel channel,
        JupyterMessage message,
        IReadOnlyList<byte[]>? identities = null)
    {
        _incomingMessages.Writer.TryWrite(new JupyterTransportMessage(
            channel,
            new JupyterWireMessage(identities ?? [], message, [])));
    }
}
