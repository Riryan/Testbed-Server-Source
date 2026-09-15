using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Game.GameServer.Networking;

internal enum MainThreadCompletionPriority : byte
{
    Critical = 0,
    Normal = 1,
    Background = 2,
}

/// <summary>
/// Bounded cross-thread handoff into the authoritative GameServer loop.
/// Producers apply backpressure instead of allowing an unbounded Action queue to grow.
/// Critical authority/lifecycle completions have reserved capacity and are drained first.
/// </summary>
internal sealed class BoundedMainThreadQueue
{
    private readonly struct QueuedAction
    {
        public readonly Action Action;
        public readonly long EnqueuedAt;
        public QueuedAction(Action action)
        {
            Action = action;
            EnqueuedAt = Stopwatch.GetTimestamp();
        }
    }

    private readonly object _gate = new object();
    private readonly Queue<QueuedAction> _critical = new Queue<QueuedAction>();
    private readonly Queue<QueuedAction> _normal = new Queue<QueuedAction>();
    private readonly Queue<QueuedAction> _background = new Queue<QueuedAction>();
    private readonly int _criticalCapacity;
    private readonly int _normalCapacity;
    private readonly int _backgroundCapacity;
    private int _ownerThreadId;
    private bool _completed;
    private int _highWater;
    private long _producerWaits;
    private long _maxDequeuedAgeMilliseconds;

    public BoundedMainThreadQueue(int criticalCapacity, int normalCapacity, int backgroundCapacity)
    {
        if (criticalCapacity < 1) throw new ArgumentOutOfRangeException(nameof(criticalCapacity));
        if (normalCapacity < 1) throw new ArgumentOutOfRangeException(nameof(normalCapacity));
        if (backgroundCapacity < 1) throw new ArgumentOutOfRangeException(nameof(backgroundCapacity));
        _criticalCapacity = criticalCapacity;
        _normalCapacity = normalCapacity;
        _backgroundCapacity = backgroundCapacity;
    }

    public int Count
    {
        get { lock (_gate) return TotalCountUnsafe(); }
    }

    public int HighWater
    {
        get { lock (_gate) return _highWater; }
    }

    public long ProducerWaits => Interlocked.Read(ref _producerWaits);
    public long MaxDequeuedAgeMilliseconds => Interlocked.Read(ref _maxDequeuedAgeMilliseconds);
    public long OldestAgeMilliseconds
    {
        get
        {
            lock (_gate)
            {
                long oldest = long.MaxValue;
                if (_critical.Count > 0) oldest = Math.Min(oldest, _critical.Peek().EnqueuedAt);
                if (_normal.Count > 0) oldest = Math.Min(oldest, _normal.Peek().EnqueuedAt);
                if (_background.Count > 0) oldest = Math.Min(oldest, _background.Peek().EnqueuedAt);
                return oldest == long.MaxValue ? 0 : ElapsedMilliseconds(oldest);
            }
        }
    }

    public void AttachCurrentThread()
    {
        lock (_gate)
            _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public bool Enqueue(Action action, MainThreadCompletionPriority priority = MainThreadCompletionPriority.Normal)
    {
        if (action == null)
            return false;

        bool ownerThread = Environment.CurrentManagedThreadId == Volatile.Read(ref _ownerThreadId) &&
                           Volatile.Read(ref _ownerThreadId) != 0;

        while (true)
        {
            lock (_gate)
            {
                if (_completed)
                    return false;

                Queue<QueuedAction> queue = SelectQueue(priority);
                int capacity = SelectCapacity(priority);
                if (queue.Count < capacity)
                {
                    queue.Enqueue(new QueuedAction(action));
                    int total = TotalCountUnsafe();
                    if (total > _highWater) _highWater = total;
                    Monitor.PulseAll(_gate);
                    return true;
                }

                // Never block the authoritative loop waiting for itself to drain. In the
                // exceptional case where a same-thread producer fills its lane, execute the
                // completion immediately rather than dropping authoritative state.
                if (ownerThread)
                    break;

                Interlocked.Increment(ref _producerWaits);
                Monitor.Wait(_gate);
            }
        }

        action();
        return true;
    }

    public int Drain(int maxActions, Action<Exception> onError = null)
    {
        if (maxActions <= 0)
            return 0;

        int drained = 0;
        while (drained < maxActions)
        {
            QueuedAction queued;
            lock (_gate)
            {
                if (!TryDequeueUnsafe(out queued))
                    break;
                Monitor.PulseAll(_gate);
            }

            long age = ElapsedMilliseconds(queued.EnqueuedAt);
            UpdateMax(ref _maxDequeuedAgeMilliseconds, age);
            try { queued.Action(); }
            catch (Exception ex) { onError?.Invoke(ex); }
            drained++;
        }
        return drained;
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private bool TryDequeueUnsafe(out QueuedAction action)
    {
        if (_critical.Count > 0)
        {
            action = _critical.Dequeue();
            return true;
        }
        if (_normal.Count > 0)
        {
            action = _normal.Dequeue();
            return true;
        }
        if (_background.Count > 0)
        {
            action = _background.Dequeue();
            return true;
        }
        action = default;
        return false;
    }

    private Queue<QueuedAction> SelectQueue(MainThreadCompletionPriority priority) => priority switch
    {
        MainThreadCompletionPriority.Critical => _critical,
        MainThreadCompletionPriority.Background => _background,
        _ => _normal,
    };

    private int SelectCapacity(MainThreadCompletionPriority priority) => priority switch
    {
        MainThreadCompletionPriority.Critical => _criticalCapacity,
        MainThreadCompletionPriority.Background => _backgroundCapacity,
        _ => _normalCapacity,
    };

    private static long ElapsedMilliseconds(long timestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - timestamp;
        return elapsed <= 0 ? 0 : (long)(elapsed * 1000d / Stopwatch.Frequency);
    }

    private static void UpdateMax(ref long target, long candidate)
    {
        while (true)
        {
            long observed = Interlocked.Read(ref target);
            if (candidate <= observed)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, observed) == observed)
                return;
        }
    }

    private int TotalCountUnsafe() => _critical.Count + _normal.Count + _background.Count;
}
