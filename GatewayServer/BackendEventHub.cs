using System.Threading.Channels;

namespace Game.BackendServer;

/// <summary>
/// In-process fan-out for backend-originated control-plane events. Each connected
/// GameServer gets its own bounded channel, so a slow/disconnected consumer cannot
/// create unbounded memory growth or steal events from another GameServer.
/// </summary>
internal sealed class BackendEventHub
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Channel<long>> _contentRevisionSubscribers = new();
    private long _nextSubscriberId;

    public ContentRevisionSubscription SubscribeContentRevisions()
    {
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (_gate)
        {
            long id = ++_nextSubscriberId;
            _contentRevisionSubscribers.Add(id, channel);
            return new ContentRevisionSubscription(this, id, channel.Reader);
        }
    }

    public void PublishContentRevision(long revision)
    {
        if (revision <= 0)
            return;

        Channel<long>[] subscribers;
        lock (_gate)
        {
            subscribers = new Channel<long>[_contentRevisionSubscribers.Count];
            int index = 0;
            foreach (Channel<long> channel in _contentRevisionSubscribers.Values)
                subscribers[index++] = channel;
        }

        for (int i = 0; i < subscribers.Length; ++i)
            subscribers[i].Writer.TryWrite(revision);
    }

    private void Unsubscribe(long id)
    {
        Channel<long> channel = null;
        lock (_gate)
        {
            if (_contentRevisionSubscribers.TryGetValue(id, out channel))
                _contentRevisionSubscribers.Remove(id);
        }

        channel?.Writer.TryComplete();
    }

    internal sealed class ContentRevisionSubscription : IDisposable
    {
        private BackendEventHub _owner;
        private readonly long _id;

        public ChannelReader<long> Reader { get; }

        internal ContentRevisionSubscription(BackendEventHub owner, long id, ChannelReader<long> reader)
        {
            _owner = owner;
            _id = id;
            Reader = reader;
        }

        public void Dispose()
        {
            BackendEventHub owner = Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(_id);
        }
    }
}
