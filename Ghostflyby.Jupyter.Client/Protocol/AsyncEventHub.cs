using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Ghostflyby.Jupyter.Client.Transport;

namespace Ghostflyby.Jupyter.Client.Protocol;

internal sealed class AsyncEventHub<T>(int capacity)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Channel<T>> _subscribers = [];
    private bool _completed;
    private Exception? _completionError;
    private long _nextSubscriberId;

    public IAsyncEnumerable<T> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        long subscriberId = -1;

        lock (_gate)
        {
            if (_completed)
            {
                channel.Writer.TryComplete(_completionError);
            }
            else
            {
                subscriberId = _nextSubscriberId++;
                _subscribers[subscriberId] = channel;
            }
        }

        return ReadSubscriberAsync(subscriberId, channel, cancellationToken);
    }

    private async IAsyncEnumerable<T> ReadSubscriberAsync(
        long subscriberId,
        Channel<T> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            if (subscriberId >= 0)
                lock (_gate)
                {
                    _subscribers.Remove(subscriberId);
                }
        }
    }

    public void Publish(T item)
    {
        KeyValuePair<long, Channel<T>>[] snapshot;
        lock (_gate)
        {
            if (_completed) return;

            snapshot = _subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            if (subscriber.Value.Writer.TryWrite(item)) continue;

            subscriber.Value.Writer.TryComplete(new JupyterBackpressureException(
                "A Jupyter client event subscriber stopped consuming events."));
            lock (_gate)
            {
                _subscribers.Remove(subscriber.Key);
            }
        }
    }

    public void Complete(Exception? error = null)
    {
        Channel<T>[] snapshot;
        lock (_gate)
        {
            if (_completed) return;

            _completed = true;
            _completionError = error;
            snapshot = _subscribers.Values.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in snapshot) subscriber.Writer.TryComplete(error);
    }
}
