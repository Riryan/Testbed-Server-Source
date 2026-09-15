using System.Diagnostics;
using System.Collections.Concurrent;

namespace Game.BackendServer;

internal enum DatabaseWorkPriority : byte
{
    Critical = 0,
    Normal = 1,
    Background = 2,
}

/// <summary>
/// Bounded single-owner execution boundary for SQLite work. SQLite remains serialized,
/// but ASP.NET/password workers no longer pile up directly behind the database lock.
/// Critical authority/fencing operations have independent reserved capacity.
/// </summary>
internal sealed class BackendDatabaseDispatcher : IDisposable
{
    private abstract class WorkItem
    {
        public long EnqueuedAt { get; } = Stopwatch.GetTimestamp();
        public abstract void Execute(BackendDatabase database);
        public abstract void Fail(Exception error);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<BackendDatabase, T> _operation;
        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<BackendDatabase, T> operation) => _operation = operation;

        public override void Execute(BackendDatabase database)
        {
            try { Completion.TrySetResult(_operation(database)); }
            catch (Exception ex) { Completion.TrySetException(ex); }
        }

        public override void Fail(Exception error) => Completion.TrySetException(error);
    }

    private readonly BackendDatabase _database;
    private readonly ConcurrentQueue<WorkItem> _critical = new();
    private readonly ConcurrentQueue<WorkItem> _normal = new();
    private readonly ConcurrentQueue<WorkItem> _background = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _worker;
    private readonly int _criticalCapacity;
    private readonly int _normalCapacity;
    private readonly int _backgroundCapacity;
    private int _criticalCount;
    private int _normalCount;
    private int _backgroundCount;
    private int _stopping;
    private int _highWater;
    private long _rejections;
    private long _criticalRejections;
    private long _normalRejections;
    private long _backgroundRejections;
    private long _completed;
    private long _lastQueueWaitMilliseconds;
    private long _lastExecutionMilliseconds;
    private long _maxQueueWaitMilliseconds;
    private long _maxExecutionMilliseconds;
    private int _dequeueCycle;

    public int QueueDepth =>
        Volatile.Read(ref _criticalCount) +
        Volatile.Read(ref _normalCount) +
        Volatile.Read(ref _backgroundCount);

    public int HighWater => Volatile.Read(ref _highWater);
    public int CriticalQueueDepth => Volatile.Read(ref _criticalCount);
    public int NormalQueueDepth => Volatile.Read(ref _normalCount);
    public int BackgroundQueueDepth => Volatile.Read(ref _backgroundCount);
    public int CriticalCapacity => _criticalCapacity;
    public int NormalCapacity => _normalCapacity;
    public int BackgroundCapacity => _backgroundCapacity;
    public long Rejections => Interlocked.Read(ref _rejections);
    public long CriticalRejections => Interlocked.Read(ref _criticalRejections);
    public long NormalRejections => Interlocked.Read(ref _normalRejections);
    public long BackgroundRejections => Interlocked.Read(ref _backgroundRejections);
    public long Completed => Interlocked.Read(ref _completed);
    public long LastQueueWaitMilliseconds => Interlocked.Read(ref _lastQueueWaitMilliseconds);
    public long LastExecutionMilliseconds => Interlocked.Read(ref _lastExecutionMilliseconds);
    public long MaxQueueWaitMilliseconds => Interlocked.Read(ref _maxQueueWaitMilliseconds);
    public long MaxExecutionMilliseconds => Interlocked.Read(ref _maxExecutionMilliseconds);
    public long OldestQueueAgeMilliseconds
    {
        get
        {
            long oldest = long.MaxValue;
            if (_critical.TryPeek(out WorkItem critical)) oldest = Math.Min(oldest, critical.EnqueuedAt);
            if (_normal.TryPeek(out WorkItem normal)) oldest = Math.Min(oldest, normal.EnqueuedAt);
            if (_background.TryPeek(out WorkItem background)) oldest = Math.Min(oldest, background.EnqueuedAt);
            return oldest == long.MaxValue ? 0 : ElapsedMilliseconds(oldest);
        }
    }

    public BackendDatabaseDispatcher(
        BackendDatabase database,
        int criticalCapacity,
        int normalCapacity,
        int backgroundCapacity)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        if (criticalCapacity < 1) throw new ArgumentOutOfRangeException(nameof(criticalCapacity));
        if (normalCapacity < 1) throw new ArgumentOutOfRangeException(nameof(normalCapacity));
        if (backgroundCapacity < 1) throw new ArgumentOutOfRangeException(nameof(backgroundCapacity));

        _criticalCapacity = criticalCapacity;
        _normalCapacity = normalCapacity;
        _backgroundCapacity = backgroundCapacity;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "GatewayDatabaseWorker",
        };
        _worker.Start();
    }

    public bool TryQueue<T>(
        DatabaseWorkPriority priority,
        Func<BackendDatabase, T> operation,
        out Task<T> completion)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        var work = new WorkItem<T>(operation);
        completion = work.Completion.Task;

        if (Volatile.Read(ref _stopping) != 0 || !TryReserve(priority))
        {
            Interlocked.Increment(ref _rejections);
            IncrementPriorityRejection(priority);
            work.Fail(new InvalidOperationException("database work queue is saturated or stopping"));
            return false;
        }

        SelectQueue(priority).Enqueue(work);
        UpdateHighWater();
        _signal.Release();
        return true;
    }

    public bool TryExecute<T>(
        DatabaseWorkPriority priority,
        Func<BackendDatabase, T> operation,
        out T result)
    {
        if (!TryQueue(priority, operation, out Task<T> completion))
        {
            result = default;
            return false;
        }

        try
        {
            result = completion.GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private void WorkerLoop()
    {
        while (Volatile.Read(ref _stopping) == 0 || QueueDepth > 0)
        {
            try
            {
                _signal.Wait();
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // Each accepted work item contributes one semaphore permit. Execute one item
            // per permit so a large drain does not leave thousands of stale wakeups behind.
            if (TryDequeue(out WorkItem work))
            {
                long queueWait = ElapsedMilliseconds(work.EnqueuedAt);
                Interlocked.Exchange(ref _lastQueueWaitMilliseconds, queueWait);
                UpdateMax(ref _maxQueueWaitMilliseconds, queueWait);

                long startedAt = Stopwatch.GetTimestamp();
                work.Execute(_database);
                long execution = ElapsedMilliseconds(startedAt);
                Interlocked.Exchange(ref _lastExecutionMilliseconds, execution);
                UpdateMax(ref _maxExecutionMilliseconds, execution);
                Interlocked.Increment(ref _completed);
            }
        }

        var stopped = new ObjectDisposedException(nameof(BackendDatabaseDispatcher));
        while (TryDequeue(out WorkItem remaining))
            remaining.Fail(stopped);
    }

    private void IncrementPriorityRejection(DatabaseWorkPriority priority)
    {
        switch (priority)
        {
            case DatabaseWorkPriority.Critical:
                Interlocked.Increment(ref _criticalRejections);
                break;
            case DatabaseWorkPriority.Background:
                Interlocked.Increment(ref _backgroundRejections);
                break;
            default:
                Interlocked.Increment(ref _normalRejections);
                break;
        }
    }

    private bool TryReserve(DatabaseWorkPriority priority) => priority switch
    {
        DatabaseWorkPriority.Critical => TryIncrementBounded(ref _criticalCount, _criticalCapacity),
        DatabaseWorkPriority.Background => TryIncrementBounded(ref _backgroundCount, _backgroundCapacity),
        _ => TryIncrementBounded(ref _normalCount, _normalCapacity),
    };

    private static bool TryIncrementBounded(ref int counter, int capacity)
    {
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current >= capacity)
                return false;
            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
                return true;
        }
    }

    private bool TryDequeue(out WorkItem work)
    {
        // Weighted service prevents checkpoints/background reads from starving forever
        // under sustained authority traffic while still strongly favoring lease/item work.
        // 16 critical : 8 normal : 2 background when all lanes remain saturated.
        const int cycleLength = 26;
        for (int attempt = 0; attempt < cycleLength; ++attempt)
        {
            int slot = (int)((uint)_dequeueCycle++ % cycleLength);
            if (slot < 16)
            {
                if (TryDequeueFrom(_critical, ref _criticalCount, out work))
                    return true;
            }
            else if (slot < 24)
            {
                if (TryDequeueFrom(_normal, ref _normalCount, out work))
                    return true;
            }
            else
            {
                if (TryDequeueFrom(_background, ref _backgroundCount, out work))
                    return true;
            }
        }

        work = null;
        return false;
    }

    private static bool TryDequeueFrom(
        ConcurrentQueue<WorkItem> queue,
        ref int count,
        out WorkItem work)
    {
        if (!queue.TryDequeue(out work))
            return false;

        Interlocked.Decrement(ref count);
        return true;
    }

    private ConcurrentQueue<WorkItem> SelectQueue(DatabaseWorkPriority priority) => priority switch
    {
        DatabaseWorkPriority.Critical => _critical,
        DatabaseWorkPriority.Background => _background,
        _ => _normal,
    };

    private void UpdateHighWater()
    {
        int current = QueueDepth;
        while (true)
        {
            int observed = Volatile.Read(ref _highWater);
            if (current <= observed)
                return;
            if (Interlocked.CompareExchange(ref _highWater, current, observed) == observed)
                return;
        }
    }


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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        _signal.Release();
        _worker.Join();
        _signal.Dispose();
    }
}
