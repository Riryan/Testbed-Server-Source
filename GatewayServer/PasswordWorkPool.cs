using System.Collections.Concurrent;

namespace Game.BackendServer;

/// <summary>
/// Bounded dedicated-thread isolation for PBKDF-backed account operations. Public HTTP
/// request threads never execute password derivation directly, and saturation rejects new
/// work rather than starving the Gateway thread pool or the internal game-server API.
/// </summary>
internal sealed class PasswordWorkPool : IAsyncDisposable
{
    private enum Operation : byte
    {
        Login = 1,
        Create = 2,
    }

    private sealed class WorkItem
    {
        public Operation Kind { get; }
        public string Account { get; }
        public string Password { get; }
        public TaskCompletionSource<AuthOperationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Operation kind, string account, string password)
        {
            Kind = kind;
            Account = account;
            Password = password;
        }
    }

    private readonly AccountAuthService _accounts;
    private readonly BlockingCollection<WorkItem> _queue;
    private readonly Thread[] _workers;
    private readonly int _queueCapacity;
    private int _stopping;
    private int _activeWorkers;
    private int _highWater;
    private long _rejections;
    private long _completed;
    private long _loginCompleted;
    private long _createCompleted;

    public int QueueDepth => _queue.Count;
    public int QueueCapacity => _queueCapacity;
    public int HighWater => Volatile.Read(ref _highWater);
    public int WorkerCount => _workers.Length;
    public int ActiveWorkers => Volatile.Read(ref _activeWorkers);
    public long Rejections => Interlocked.Read(ref _rejections);
    public long Completed => Interlocked.Read(ref _completed);
    public long LoginCompleted => Interlocked.Read(ref _loginCompleted);
    public long CreateCompleted => Interlocked.Read(ref _createCompleted);

    public PasswordWorkPool(AccountAuthService accounts, int workerCount, int queueCapacity)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        if (workerCount <= 0) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));

        _queueCapacity = queueCapacity;
        _queue = new BlockingCollection<WorkItem>(
            new ConcurrentQueue<WorkItem>(),
            queueCapacity);
        _workers = new Thread[workerCount];
        for (int i = 0; i < _workers.Length; ++i)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"GatewayPasswordWorker-{i + 1}",
            };
            _workers[i] = thread;
            thread.Start();
        }
    }

    public bool TryQueueLogin(string account, string password, out Task<AuthOperationResult> completion) =>
        TryQueue(Operation.Login, account, password, out completion);

    public bool TryQueueCreate(string account, string password, out Task<AuthOperationResult> completion) =>
        TryQueue(Operation.Create, account, password, out completion);

    private bool TryQueue(
        Operation operation,
        string account,
        string password,
        out Task<AuthOperationResult> completion)
    {
        var work = new WorkItem(operation, account, password);
        completion = work.Completion.Task;
        try
        {
            if (Volatile.Read(ref _stopping) == 0 && _queue.TryAdd(work))
            {
                UpdateHighWater(_queue.Count);
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Shutdown completed the bounded collection between the stopping check and add.
        }

        Interlocked.Increment(ref _rejections);
        work.Completion.TrySetResult(AuthOperationResult.Failed());
        return false;
    }

    private void WorkerLoop()
    {
        foreach (WorkItem work in _queue.GetConsumingEnumerable())
        {
            Interlocked.Increment(ref _activeWorkers);
            try
            {
                AuthOperationResult result = work.Kind == Operation.Login
                    ? _accounts.Login(work.Account, work.Password)
                    : _accounts.Create(work.Account, work.Password);
                work.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Password worker failed: {ex.Message}");
                work.Completion.TrySetResult(AuthOperationResult.Failed());
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
                Interlocked.Increment(ref _completed);
                if (work.Kind == Operation.Login)
                    Interlocked.Increment(ref _loginCompleted);
                else
                    Interlocked.Increment(ref _createCompleted);
            }
        }
    }

    private void UpdateHighWater(int current)
    {
        while (true)
        {
            int observed = Volatile.Read(ref _highWater);
            if (current <= observed)
                return;
            if (Interlocked.CompareExchange(ref _highWater, current, observed) == observed)
                return;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return ValueTask.CompletedTask;

        while (_queue.TryTake(out WorkItem queued))
            queued.Completion.TrySetResult(AuthOperationResult.Failed());
        _queue.CompleteAdding();

        for (int i = 0; i < _workers.Length; ++i)
            _workers[i].Join();

        _queue.Dispose();
        return ValueTask.CompletedTask;
    }
}
