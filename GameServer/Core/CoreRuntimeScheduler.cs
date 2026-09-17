using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace LiteNetLibManager
{
    /// <summary>
    /// Core runtime governance for authoritative server work.
    ///
    /// The scheduler provides:
    /// - a hard aggregate per-frame CPU budget for governed server work,
    /// - fixed-rate channels with bounded catch-up/backlog,
    /// - round-robin fairness when a channel is overloaded,
    /// - dynamic low-priority throttling/recovery,
    /// - delayed-task admission/budget limits,
    /// - per-system overrun strikes and automatic quarantine,
    /// - a cooperative work-unit interface for large queues/batches.
    ///
    /// Important: C# cannot safely pre-empt an arbitrary Unity main-thread callback
    /// after it has entered user code. A single badly-written callback can still block
    /// until it returns. The scheduler detects that overrun, stops further work for the
    /// frame, and can quarantine the offender. Large/variable workloads should implement
    /// ICoreBudgetedWorkSystem so control returns to the scheduler between work units.
    /// </summary>
    [Serializable]
    public sealed class CoreRuntimeSchedulerSettings
    {
#if UNITY_5_3_OR_NEWER
        [Tooltip("Hard aggregate CPU budget for governed authoritative server work per Unity frame. This cannot be disabled; values <= 0 are clamped to 0.25 ms.")]
        [Min(0.25f)]
#endif
        public float maxCoreMillisecondsPerFrame = 12f;

#if UNITY_5_3_OR_NEWER
        [Tooltip("Clamp unusually large real-time frame deltas before they enter channel accumulators. Channels also have independent backlog caps.")]
        [Min(0.01f)]
#endif
        public float maxFrameDeltaSeconds = 0.25f;

#if UNITY_5_3_OR_NEWER
        [Header("Admission Limits")]
        [Min(1)]
#endif
        public int maxRegisteredSystems = 4096;
#if UNITY_5_3_OR_NEWER
        [Min(1)]
#endif
        public int maxDelayedTasks = 16384;
#if UNITY_5_3_OR_NEWER
        [Min(1)]
#endif
        public int maxDelayedCallbacksPerFrame = 128;
#if UNITY_5_3_OR_NEWER
        [Min(0.1f)]
#endif
        public float maxDelayedTaskMillisecondsPerFrame = 1.5f;

#if UNITY_5_3_OR_NEWER
        [Header("Startup Grace")]
        [Tooltip("Seconds after authoritative server startup during which invocation-time overruns are measured but do not add strikes or quarantine systems. Global/channel CPU budgets remain active. Set to 0 to disable.")]
        [Min(0f)]
#endif
        public float startupGraceSeconds = 5f;

#if UNITY_5_3_OR_NEWER
        [Tooltip("Log invocation overruns during startup grace as informational messages. They are still counted in scheduler metrics.")]
#endif
        public bool logStartupGraceOverruns = true;

#if UNITY_5_3_OR_NEWER
        [Header("Overrun Protection")]
        [Tooltip("A single scheduled system/work-unit at or above this duration receives an overrun strike unless the registration overrides the value.")]
        [Min(0.1f)]
#endif
        public float defaultSingleInvocationLimitMilliseconds = 4f;
#if UNITY_5_3_OR_NEWER
        [Min(1)]
#endif
        public int strikesBeforeQuarantine = 3;
#if UNITY_5_3_OR_NEWER
        [Min(0.1f)]
#endif
        public float baseQuarantineSeconds = 5f;
#if UNITY_5_3_OR_NEWER
        [Min(0.1f)]
#endif
        public float maxQuarantineSeconds = 60f;
#if UNITY_5_3_OR_NEWER
        [Min(0.1f)]
#endif
        public float warningCooldownSeconds = 5f;
#if UNITY_5_3_OR_NEWER
        [Min(1)]
#endif
        public int healthyFramesBeforeRecovery = 30;

#if UNITY_5_3_OR_NEWER
        [Header("Authoritative Tick Channels")]
#endif
        public List<CoreRuntimeChannelConfig> channels = new List<CoreRuntimeChannelConfig>
        {
            new CoreRuntimeChannelConfig("Critical",    100, 20.0, 20.0, 3.0, 2, 0.15, 0.000, false, false),
            new CoreRuntimeChannelConfig("Simulation",   90, 20.0, 15.0, 4.0, 2, 0.20, 0.010, true,  true),
            // New combat requests resolve on a fixed cadence independent from movement.
            // Admission/budgets bound the amount of combat work this channel can consume.
            new CoreRuntimeChannelConfig("Combat",       85, 10.0, 10.0, 3.0, 1, 0.20, 0.015, false, true),
            new CoreRuntimeChannelConfig("Gameplay",     80, 10.0,  5.0, 3.0, 2, 0.35, 0.020, true,  true),
            new CoreRuntimeChannelConfig("AI",           50,  5.0,  2.0, 3.0, 1, 0.60, 0.035, true,  true),
            new CoreRuntimeChannelConfig("Maintenance",  20,  1.0,  0.5, 2.0, 1, 1.50, 0.050, true,  true),
            new CoreRuntimeChannelConfig("Background",    0,  0.2,  0.1, 1.0, 1, 6.00, 0.100, true,  true),
        };

        internal void ClampUnsafeValues()
        {
            if (maxCoreMillisecondsPerFrame <= 0f)
                maxCoreMillisecondsPerFrame = 0.25f;
            if (maxFrameDeltaSeconds <= 0f)
                maxFrameDeltaSeconds = 0.01f;
            if (maxRegisteredSystems < 1)
                maxRegisteredSystems = 1;
            if (maxDelayedTasks < 1)
                maxDelayedTasks = 1;
            if (maxDelayedCallbacksPerFrame < 1)
                maxDelayedCallbacksPerFrame = 1;
            if (maxDelayedTaskMillisecondsPerFrame <= 0f)
                maxDelayedTaskMillisecondsPerFrame = 0.1f;
            if (startupGraceSeconds < 0f)
                startupGraceSeconds = 0f;
            if (defaultSingleInvocationLimitMilliseconds <= 0f)
                defaultSingleInvocationLimitMilliseconds = 0.1f;
            if (strikesBeforeQuarantine < 1)
                strikesBeforeQuarantine = 1;
            if (baseQuarantineSeconds <= 0f)
                baseQuarantineSeconds = 0.1f;
            if (maxQuarantineSeconds < baseQuarantineSeconds)
                maxQuarantineSeconds = baseQuarantineSeconds;
            if (warningCooldownSeconds <= 0f)
                warningCooldownSeconds = 0.1f;
            if (healthyFramesBeforeRecovery < 1)
                healthyFramesBeforeRecovery = 1;
            if (channels == null)
                channels = new List<CoreRuntimeChannelConfig>();

            for (int i = 0; i < channels.Count; ++i)
            {
                if (channels[i] != null)
                    channels[i].ClampUnsafeValues();
            }
        }
    }

    [Serializable]
    public sealed class CoreRuntimeChannelConfig
    {
        public string name = "Gameplay";
        public int priority = 0;
#if UNITY_5_3_OR_NEWER
        [Min(0.01f)]
#endif
        public double baseHz = 10.0;
#if UNITY_5_3_OR_NEWER
        [Min(0.01f)]
#endif
        public double minHz = 5.0;
#if UNITY_5_3_OR_NEWER
        [Min(0.05f)]
#endif
        public double maxMillisecondsPerFrame = 2.0;
#if UNITY_5_3_OR_NEWER
        [Min(1)]
#endif
        public int maxCatchUpTicksPerFrame = 1;
#if UNITY_5_3_OR_NEWER
        [Min(0.01f)]
#endif
        public double maxBacklogSeconds = 0.25;
#if UNITY_5_3_OR_NEWER
        [Min(0f)]
#endif
        public double phaseOffsetSeconds = 0.0;
        public bool allowThrottling = true;
        public bool allowAutoQuarantine = true;

        public CoreRuntimeChannelConfig()
        {
        }

        public CoreRuntimeChannelConfig(
            string name,
            int priority,
            double baseHz,
            double minHz,
            double maxMillisecondsPerFrame,
            int maxCatchUpTicksPerFrame,
            double maxBacklogSeconds,
            double phaseOffsetSeconds,
            bool allowThrottling,
            bool allowAutoQuarantine)
        {
            this.name = name;
            this.priority = priority;
            this.baseHz = baseHz;
            this.minHz = minHz;
            this.maxMillisecondsPerFrame = maxMillisecondsPerFrame;
            this.maxCatchUpTicksPerFrame = maxCatchUpTicksPerFrame;
            this.maxBacklogSeconds = maxBacklogSeconds;
            this.phaseOffsetSeconds = phaseOffsetSeconds;
            this.allowThrottling = allowThrottling;
            this.allowAutoQuarantine = allowAutoQuarantine;
        }

        internal void ClampUnsafeValues()
        {
            if (string.IsNullOrWhiteSpace(name))
                name = "Unnamed";
            if (baseHz <= 0.0)
                baseHz = 0.01;
            if (minHz <= 0.0)
                minHz = 0.01;
            if (minHz > baseHz)
                minHz = baseHz;
            if (maxMillisecondsPerFrame <= 0.0)
                maxMillisecondsPerFrame = 0.05;
            if (maxCatchUpTicksPerFrame < 1)
                maxCatchUpTicksPerFrame = 1;
            if (maxBacklogSeconds <= 0.0)
                maxBacklogSeconds = 0.01;
            if (phaseOffsetSeconds < 0.0)
                phaseOffsetSeconds = 0.0;
        }
    }

    public readonly struct CoreTickContext
    {
        public readonly long TickIndex;
        public readonly double Now;
        public readonly float FixedDelta;
        public readonly double FrameBudgetRemainingMilliseconds;
        public readonly double ChannelBudgetRemainingMilliseconds;

        public CoreTickContext(
            long tickIndex,
            double now,
            float fixedDelta,
            double frameBudgetRemainingMilliseconds,
            double channelBudgetRemainingMilliseconds)
        {
            TickIndex = tickIndex;
            Now = now;
            FixedDelta = fixedDelta;
            FrameBudgetRemainingMilliseconds = frameBudgetRemainingMilliseconds;
            ChannelBudgetRemainingMilliseconds = channelBudgetRemainingMilliseconds;
        }
    }

    /// <summary>
    /// Fixed-cadence authoritative system. Keep each invocation bounded.
    /// Large queues should use ICoreBudgetedWorkSystem instead.
    /// </summary>
    public interface ICoreTickSystem
    {
        string Name { get; }
        void Prepare(in CoreTickContext context);
        void Execute(in CoreTickContext context);
        void Commit(in CoreTickContext context);
    }

    /// <summary>
    /// Cooperative interface for variable-size work. ExecuteOneWorkUnit must do a
    /// small, bounded amount of work and return control to the scheduler. The core
    /// checks time/global/channel budgets between every work unit.
    /// </summary>
    public interface ICoreBudgetedWorkSystem
    {
        string Name { get; }
        bool HasPendingWork { get; }
        void ExecuteOneWorkUnit(in CoreTickContext context);
    }

    /// <summary>
    /// Optional preparation hook for budgeted systems that need to snapshot or reset their
    /// work cursor once per scheduler tick before HasPendingWork is evaluated.
    /// </summary>
    public interface ICorePreparedBudgetedWorkSystem : ICoreBudgetedWorkSystem
    {
        void Prepare(in CoreTickContext context);
    }

    /// <summary>
    /// Optional wake gate for prepared budgeted systems. When false, the scheduler skips
    /// Prepare entirely for that channel tick, so dormant systems do not pay preparation
    /// cost merely because their channel cadence fired. The wake predicate must stay cheap
    /// and side-effect free.
    /// </summary>
    public interface ICoreConditionalPreparedBudgetedWorkSystem : ICorePreparedBudgetedWorkSystem
    {
        bool ShouldPrepare { get; }
    }

    public interface ICoreScheduledSystemHandle
    {
        bool IsRegistered { get; }
        bool IsQuarantined { get; }
        string Name { get; }
        void Unregister();
        void ResumeNow();
    }

    public interface ICoreScheduledTaskHandle
    {
        bool IsActive { get; }
        void Cancel();
    }

    public struct CoreRuntimeSchedulerMetrics
    {
        public bool active;
        public int channelCount;
        public int registeredSystemCount;
        public int quarantinedSystemCount;
        public int delayedTaskCount;
        public int backlogTicks;
        public double backlogSeconds;
        public double lastFrameMilliseconds;
        public double lastScheduledMilliseconds;
        public double lastDelayedTaskMilliseconds;
        public double maximumObservedFrameMilliseconds;
        public long channelBudgetHits;
        public long frameBudgetHits;
        public long droppedTicks;
        public long skippedSystemInvocations;
        public long systemOverruns;
        public long startupGraceOverruns;
        public bool startupGraceActive;
        public double startupGraceRemainingSeconds;
        public long quarantineEvents;
        public long throttleEvents;
        public long recoveryEvents;
        public long delayedTaskBudgetHits;
        public long delayedTaskRejections;
    }

    public struct CoreRuntimeChannelMetrics
    {
        public string name;
        public double currentHz;
        public int registeredSystems;
        public int backlogTicks;
        public double backlogSeconds;
        public double lastFrameMilliseconds;
        public double maximumObservedFrameMilliseconds;
        public long executedTicks;
        public long budgetHits;
        public long droppedTicks;
        public long skippedSystemInvocations;
    }

    internal static class CoreRuntimeSchedulerLog
    {
        public static void Info(string message)
        {
#if UNITY_5_3_OR_NEWER
            Debug.Log(message);
#else
            Console.WriteLine(message);
#endif
        }

        public static void Warning(string message)
        {
#if UNITY_5_3_OR_NEWER
            Debug.LogWarning(message);
#else
            Console.Error.WriteLine(message);
#endif
        }

        public static void Error(string message)
        {
#if UNITY_5_3_OR_NEWER
            Debug.LogError(message);
#else
            Console.Error.WriteLine(message);
#endif
        }
    }

    public sealed class CoreRuntimeScheduler
    {
        private sealed class Channel
        {
            public readonly CoreRuntimeChannelConfig Config;
            public readonly List<Entry> Systems = new List<Entry>();
            public readonly Stopwatch FrameStopwatch = new Stopwatch();
            public double BaseInterval;
            public double CurrentInterval;
            public double Accumulator;
            public double PhaseRemaining;
            public long TickIndex;
            public int RoundRobinCursor;
            public int ConsecutiveBudgetHits;
            public int ConsecutiveHealthyFrames;
            public double LastFrameMilliseconds;
            public double MaximumObservedFrameMilliseconds;
            public long ExecutedTicks;
            public long BudgetHits;
            public long DroppedTicks;
            public long SkippedSystemInvocations;

            public Channel(CoreRuntimeChannelConfig config)
            {
                Config = config;
                BaseInterval = 1.0 / config.baseHz;
                CurrentInterval = BaseInterval;
                PhaseRemaining = Math.Min(Math.Max(0.0, config.phaseOffsetSeconds), BaseInterval);
            }

            public double CurrentHz => 1.0 / CurrentInterval;
            public int BacklogTicks => CurrentInterval > 0.0 ? (int)(Accumulator / CurrentInterval) : 0;
            public double BacklogSeconds => BacklogTicks * CurrentInterval;

            public void ResetRuntimeState()
            {
                Accumulator = 0.0;
                CurrentInterval = BaseInterval;
                PhaseRemaining = Math.Min(Math.Max(0.0, Config.phaseOffsetSeconds), BaseInterval);
                TickIndex = 0;
                RoundRobinCursor = 0;
                ConsecutiveBudgetHits = 0;
                ConsecutiveHealthyFrames = 0;
                LastFrameMilliseconds = 0.0;
                FrameStopwatch.Reset();
            }

            public void AddTime(double deltaTime)
            {
                if (PhaseRemaining > 0.0)
                {
                    double consume = Math.Min(PhaseRemaining, deltaTime);
                    PhaseRemaining -= consume;
                    deltaTime -= consume;
                    if (deltaTime <= 0.0)
                        return;
                }

                Accumulator += deltaTime;
                double cap = Math.Max(Config.maxBacklogSeconds, CurrentInterval);
                if (Accumulator > cap)
                {
                    long oldBacklog = (long)(Accumulator / CurrentInterval);
                    Accumulator = cap;
                    long newBacklog = (long)(Accumulator / CurrentInterval);
                    if (oldBacklog > newBacklog)
                        DroppedTicks += oldBacklog - newBacklog;
                }
            }

            public int GetTicksDue()
            {
                int due = BacklogTicks;
                return Math.Min(due, Config.maxCatchUpTicksPerFrame);
            }

            public void CommitTick()
            {
                Accumulator -= CurrentInterval;
                if (Accumulator < 0.0)
                    Accumulator = 0.0;
                TickIndex++;
            }

            public void DropDueTicks(int count)
            {
                for (int i = 0; i < count && Accumulator >= CurrentInterval; ++i)
                {
                    Accumulator -= CurrentInterval;
                    TickIndex++;
                    DroppedTicks++;
                }
                if (Accumulator < 0.0)
                    Accumulator = 0.0;
            }


            public void DropAllDueTicks()
            {
                int count = BacklogTicks;
                DropDueTicks(count);
            }
        }

        private sealed class Entry : ICoreScheduledSystemHandle
        {
            public readonly CoreRuntimeScheduler Owner;
            public readonly Channel Channel;
            public readonly ICoreTickSystem TickSystem;
            public readonly ICoreBudgetedWorkSystem WorkSystem;
            public readonly int MaxWorkUnitsPerTick;
            public readonly bool Quarantineable;
            public readonly double InvocationLimitMilliseconds;
            public bool Enabled = true;
            public bool PendingRemoval;
            public int OverrunStrikes;
            public int QuarantineLevel;
            public double QuarantineUntil;
            public double LastInvocationMilliseconds;
            public double MaximumInvocationMilliseconds;
            public long InvocationCount;
            public long OverrunCount;
            public long QuarantineCount;
            public double NextWarningTime;

            public Entry(
                CoreRuntimeScheduler owner,
                Channel channel,
                ICoreTickSystem tickSystem,
                ICoreBudgetedWorkSystem workSystem,
                int maxWorkUnitsPerTick,
                bool quarantineable,
                double invocationLimitMilliseconds)
            {
                Owner = owner;
                Channel = channel;
                TickSystem = tickSystem;
                WorkSystem = workSystem;
                MaxWorkUnitsPerTick = Math.Max(1, maxWorkUnitsPerTick);
                Quarantineable = quarantineable;
                InvocationLimitMilliseconds = invocationLimitMilliseconds;
            }

            public string Name
            {
                get
                {
                    if (TickSystem != null)
                        return string.IsNullOrEmpty(TickSystem.Name) ? TickSystem.GetType().Name : TickSystem.Name;
                    if (WorkSystem != null)
                        return string.IsNullOrEmpty(WorkSystem.Name) ? WorkSystem.GetType().Name : WorkSystem.Name;
                    return "Unknown";
                }
            }

            public bool IsRegistered => !PendingRemoval;
            public bool IsQuarantined => !PendingRemoval && Enabled && Owner._serverTime < QuarantineUntil;

            public void Unregister()
            {
                if (!PendingRemoval)
                    PendingRemoval = true;
            }

            public void ResumeNow()
            {
                if (PendingRemoval)
                    return;
                QuarantineUntil = 0.0;
                OverrunStrikes = 0;
                Enabled = true;
            }
        }

        private sealed class DelayedTask : ICoreScheduledTaskHandle
        {
            public double ExecuteAt;
            public Action Callback;
            public bool Cancelled;
            public bool InHeap;

            public bool IsActive => InHeap && !Cancelled && Callback != null;

            public void Cancel()
            {
                Cancelled = true;
                Callback = null;
            }

            public void Reset()
            {
                ExecuteAt = 0.0;
                Callback = null;
                Cancelled = false;
                InHeap = false;
            }
        }

        private readonly CoreRuntimeSchedulerSettings _settings;
        private readonly List<Channel> _channels = new List<Channel>();
        private readonly Dictionary<string, Channel> _channelsByName = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DelayedTask> _delayedHeap = new List<DelayedTask>(64);
        private readonly Stack<DelayedTask> _delayedPool = new Stack<DelayedTask>(64);
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Stopwatch _frameStopwatch = new Stopwatch();
        private readonly Stopwatch _scheduledStopwatch = new Stopwatch();
        private readonly Stopwatch _delayedStopwatch = new Stopwatch();
        private long _lastClockTicks;
        private bool _isRunning;
        private double _serverTime;
        private double _startupGraceUntil;
        private double _frameDelta;
        private double _lastFrameMilliseconds;
        private double _lastScheduledMilliseconds;
        private double _lastDelayedMilliseconds;
        private double _maximumObservedFrameMilliseconds;
        private long _frameBudgetHits;
        private long _systemOverruns;
        private long _startupGraceOverruns;
        private long _quarantineEvents;
        private long _throttleEvents;
        private long _recoveryEvents;
        private long _delayedTaskBudgetHits;
        private long _delayedTaskRejections;

        public CoreRuntimeScheduler(CoreRuntimeSchedulerSettings settings)
        {
            _settings = settings ?? new CoreRuntimeSchedulerSettings();
            _settings.ClampUnsafeValues();
            BuildChannels();
        }

        public bool IsRunning => _isRunning;
        public double ServerTime => _serverTime;
        public bool IsStartupGraceActive => _isRunning && _serverTime < _startupGraceUntil;
        public double StartupGraceRemainingSeconds => IsStartupGraceActive ? Math.Max(0.0, _startupGraceUntil - _serverTime) : 0.0;
        public double FrameDelta => _frameDelta;

        /// <summary>
        /// Begins a bounded overrun-strike grace window. This does not relax the
        /// aggregate frame budget, channel budgets, backlog limits, catch-up limits,
        /// delayed-task limits, or exception handling. It only suppresses overrun
        /// strikes/quarantine for slow-but-successful scheduled invocations.
        /// </summary>
        public void BeginStartupGrace(double seconds = -1.0)
        {
            double duration = seconds >= 0.0 ? seconds : _settings.startupGraceSeconds;
            duration = Math.Max(0.0, duration);
            _startupGraceUntil = Math.Max(_startupGraceUntil, _serverTime + duration);

            // Allow one concise startup-grace report immediately after a new server start.
            for (int c = 0; c < _channels.Count; ++c)
            {
                List<Entry> systems = _channels[c].Systems;
                for (int i = 0; i < systems.Count; ++i)
                {
                    if (!systems[i].PendingRemoval)
                        systems[i].NextWarningTime = 0.0;
                }
            }
        }
        public double FrameElapsedMilliseconds => _frameStopwatch.IsRunning ? _frameStopwatch.Elapsed.TotalMilliseconds : 0.0;
        public double RemainingFrameMilliseconds => Math.Max(0.0, _settings.maxCoreMillisecondsPerFrame - FrameElapsedMilliseconds);
        public bool IsFrameBudgetExhausted => RemainingFrameMilliseconds <= 0.0;

        private void BuildChannels()
        {
            _channels.Clear();
            _channelsByName.Clear();

            for (int i = 0; i < _settings.channels.Count; ++i)
            {
                CoreRuntimeChannelConfig config = _settings.channels[i];
                if (config == null)
                    continue;
                config.ClampUnsafeValues();
                if (_channelsByName.ContainsKey(config.name))
                {
                    CoreRuntimeSchedulerLog.Error($"[CoreRuntimeScheduler] Duplicate channel '{config.name}' ignored.");
                    continue;
                }

                Channel channel = new Channel(config);
                _channels.Add(channel);
                _channelsByName.Add(config.name, channel);
            }

            _channels.Sort((a, b) => b.Config.priority.CompareTo(a.Config.priority));
        }

        public void Start()
        {
            _settings.ClampUnsafeValues();
            if (_isRunning)
                return;

            _clock.Restart();
            _lastClockTicks = 0;
            _serverTime = 0.0;
            _startupGraceUntil = Math.Max(0.0, _settings.startupGraceSeconds);
            _frameDelta = 0.0;
            for (int i = 0; i < _channels.Count; ++i)
            {
                Channel channel = _channels[i];
                channel.ResetRuntimeState();
                for (int j = 0; j < channel.Systems.Count; ++j)
                {
                    Entry entry = channel.Systems[j];
                    entry.OverrunStrikes = 0;
                    entry.QuarantineLevel = 0;
                    entry.QuarantineUntil = 0.0;
                    entry.LastInvocationMilliseconds = 0.0;
                    entry.NextWarningTime = 0.0;
                }
            }
            _isRunning = true;
        }

        public void Stop(bool clearDelayedTasks)
        {
            _isRunning = false;
            _clock.Stop();
            _frameStopwatch.Reset();
            _scheduledStopwatch.Reset();
            _delayedStopwatch.Reset();
            if (clearDelayedTasks)
                ClearDelayedTasks();
        }

        public void BeginFrame()
        {
            if (!_isRunning)
                Start();

            long nowTicks = _clock.ElapsedTicks;
            if (_lastClockTicks <= 0)
            {
                _frameDelta = 0.0;
            }
            else
            {
                _frameDelta = (double)(nowTicks - _lastClockTicks) / Stopwatch.Frequency;
                if (_frameDelta < 0.0)
                    _frameDelta = 0.0;
                if (_frameDelta > _settings.maxFrameDeltaSeconds)
                    _frameDelta = _settings.maxFrameDeltaSeconds;
            }
            _lastClockTicks = nowTicks;
            _serverTime += _frameDelta;
            _lastFrameMilliseconds = 0.0;
            _lastScheduledMilliseconds = 0.0;
            _lastDelayedMilliseconds = 0.0;
            _frameStopwatch.Restart();
        }

        public void EndFrame()
        {
            if (!_frameStopwatch.IsRunning)
                return;

            _frameStopwatch.Stop();
            double elapsed = _frameStopwatch.Elapsed.TotalMilliseconds;
            _lastFrameMilliseconds = elapsed;
            if (elapsed > _maximumObservedFrameMilliseconds)
                _maximumObservedFrameMilliseconds = elapsed;
            if (elapsed >= _settings.maxCoreMillisecondsPerFrame)
                _frameBudgetHits++;
        }

        public double ClampToRemainingFrameBudget(double requestedMilliseconds)
        {
            double remaining = RemainingFrameMilliseconds;
            if (remaining <= 0.0)
                return 0.0;
            if (requestedMilliseconds <= 0.0)
                return remaining;
            return Math.Min(requestedMilliseconds, remaining);
        }

        public ICoreScheduledSystemHandle RegisterSystem(
            ICoreTickSystem system,
            string channelName,
            bool quarantineable = true,
            double invocationLimitMilliseconds = 0.0)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));
            return RegisterInternal(system, null, channelName, 1, quarantineable, invocationLimitMilliseconds);
        }

        public ICoreScheduledSystemHandle RegisterWorkSystem(
            ICoreBudgetedWorkSystem system,
            string channelName,
            int maxWorkUnitsPerTick = 64,
            bool quarantineable = true,
            double invocationLimitMilliseconds = 0.0)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));
            return RegisterInternal(null, system, channelName, maxWorkUnitsPerTick, quarantineable, invocationLimitMilliseconds);
        }

        private ICoreScheduledSystemHandle RegisterInternal(
            ICoreTickSystem tickSystem,
            ICoreBudgetedWorkSystem workSystem,
            string channelName,
            int maxWorkUnitsPerTick,
            bool quarantineable,
            double invocationLimitMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                throw new ArgumentException("A configured core runtime channel name is required.", nameof(channelName));
            if (!_channelsByName.TryGetValue(channelName, out Channel channel))
                throw new InvalidOperationException($"Core runtime channel '{channelName}' is not configured. Registration fails closed rather than creating an unbudgeted channel.");
            if (GetRegisteredSystemCount() >= _settings.maxRegisteredSystems)
                throw new InvalidOperationException($"Core runtime system admission limit reached ({_settings.maxRegisteredSystems}).");

            double limit = invocationLimitMilliseconds > 0.0
                ? invocationLimitMilliseconds
                : _settings.defaultSingleInvocationLimitMilliseconds;

            Entry entry = new Entry(
                this,
                channel,
                tickSystem,
                workSystem,
                maxWorkUnitsPerTick,
                quarantineable && channel.Config.allowAutoQuarantine,
                limit);
            channel.Systems.Add(entry);
            return entry;
        }

        public ICoreScheduledTaskHandle Schedule(double delaySeconds, Action callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            if (!_isRunning)
                throw new InvalidOperationException("Core delayed tasks can only be scheduled while the authoritative server scheduler is running.");
            if (_delayedHeap.Count >= _settings.maxDelayedTasks)
            {
                _delayedTaskRejections++;
                throw new InvalidOperationException($"Core delayed-task admission limit reached ({_settings.maxDelayedTasks}).");
            }

            DelayedTask task = _delayedPool.Count > 0 ? _delayedPool.Pop() : new DelayedTask();
            task.ExecuteAt = _serverTime + Math.Max(0.0, delaySeconds);
            task.Callback = callback;
            task.Cancelled = false;
            task.InHeap = true;
            HeapPush(task);
            return task;
        }

        public bool TrySchedule(double delaySeconds, Action callback, out ICoreScheduledTaskHandle handle)
        {
            handle = null;
            if (!_isRunning || callback == null || _delayedHeap.Count >= _settings.maxDelayedTasks)
            {
                _delayedTaskRejections++;
                return false;
            }
            handle = Schedule(delaySeconds, callback);
            return true;
        }

        public void UpdateScheduledSystems()
        {
            if (!_isRunning || IsFrameBudgetExhausted)
                return;

            _scheduledStopwatch.Restart();
            try
            {
                for (int i = 0; i < _channels.Count; ++i)
                {
                    Channel channel = _channels[i];
                    channel.LastFrameMilliseconds = 0.0;
                    channel.AddTime(_frameDelta);

                    int due = channel.GetTicksDue();
                    if (due <= 0)
                    {
                        RecoverChannel(channel, false);
                        continue;
                    }

                    channel.FrameStopwatch.Restart();
                    bool budgetHit = false;

                    for (int tick = 0; tick < due; ++tick)
                    {
                        if (IsFrameBudgetExhausted || channel.FrameStopwatch.Elapsed.TotalMilliseconds >= channel.Config.maxMillisecondsPerFrame)
                        {
                            // Do not preserve an overload debt that will create a later catch-up spike.
                            // Drop every complete overdue tick and resume from real time next frame.
                            channel.DropAllDueTicks();
                            budgetHit = true;
                            break;
                        }

                        RunOneChannelTick(channel);
                        channel.CommitTick();
                        channel.ExecutedTicks++;
                    }

                    if (IsFrameBudgetExhausted)
                    {
                        budgetHit = true;
                        // Do not carry current-channel overload debt into the next frame.
                        if (channel.BacklogTicks > 0)
                            channel.DropAllDueTicks();
                    }

                    channel.FrameStopwatch.Stop();
                    channel.LastFrameMilliseconds = channel.FrameStopwatch.Elapsed.TotalMilliseconds;
                    if (channel.LastFrameMilliseconds > channel.MaximumObservedFrameMilliseconds)
                        channel.MaximumObservedFrameMilliseconds = channel.LastFrameMilliseconds;

                    if (channel.LastFrameMilliseconds >= channel.Config.maxMillisecondsPerFrame)
                        budgetHit = true;
                    if (budgetHit)
                        channel.BudgetHits++;

                    RecoverChannel(channel, budgetHit);

                    if (IsFrameBudgetExhausted)
                    {
                        for (int j = i + 1; j < _channels.Count; ++j)
                        {
                            Channel lower = _channels[j];
                            lower.AddTime(_frameDelta);
                            if (lower.BacklogTicks > 0)
                                lower.DropAllDueTicks();
                        }
                        break;
                    }
                }
            }
            finally
            {
                _scheduledStopwatch.Stop();
                _lastScheduledMilliseconds = _scheduledStopwatch.Elapsed.TotalMilliseconds;
                CompactRemovedEntries();
            }
        }

        private void RunOneChannelTick(Channel channel)
        {
            int count = channel.Systems.Count;
            if (count <= 0)
                return;

            int start = channel.RoundRobinCursor;
            if (start < 0 || start >= count)
                start = 0;

            int attempted = 0;
            int lastIndex = start;
            while (attempted < count)
            {
                if (IsFrameBudgetExhausted || channel.FrameStopwatch.Elapsed.TotalMilliseconds >= channel.Config.maxMillisecondsPerFrame)
                {
                    channel.SkippedSystemInvocations += count - attempted;
                    break;
                }

                int index = (start + attempted) % count;
                lastIndex = index;
                Entry entry = channel.Systems[index];
                attempted++;

                if (entry.PendingRemoval || !entry.Enabled)
                    continue;
                if (_serverTime < entry.QuarantineUntil)
                    continue;

                if (entry.QuarantineUntil > 0.0 && _serverTime >= entry.QuarantineUntil)
                {
                    entry.QuarantineUntil = 0.0;
                    entry.OverrunStrikes = 0;
                }

                double channelRemaining = Math.Max(0.0, channel.Config.maxMillisecondsPerFrame - channel.FrameStopwatch.Elapsed.TotalMilliseconds);
                CoreTickContext context = new CoreTickContext(
                    channel.TickIndex,
                    _serverTime,
                    (float)channel.CurrentInterval,
                    RemainingFrameMilliseconds,
                    channelRemaining);

                if (entry.WorkSystem != null)
                    ExecuteBudgetedWorkEntry(entry, context, channel);
                else
                    ExecuteTickEntry(entry, context, channel);
            }

            if (count > 0)
                channel.RoundRobinCursor = (lastIndex + 1) % count;
        }

        private void ExecuteTickEntry(Entry entry, in CoreTickContext context, Channel channel)
        {
            long start = Stopwatch.GetTimestamp();
            bool failed = false;
            try
            {
                entry.TickSystem.Prepare(context);
                entry.TickSystem.Execute(context);
                entry.TickSystem.Commit(context);
            }
            catch (Exception ex)
            {
                failed = true;
                HandleSystemException(entry, ex);
            }
            finally
            {
                double elapsed = ElapsedMilliseconds(start);
                RecordInvocation(entry, elapsed, channel, failed);
            }
        }

        private void ExecuteBudgetedWorkEntry(Entry entry, in CoreTickContext context, Channel channel)
        {
            if (entry.WorkSystem is ICoreConditionalPreparedBudgetedWorkSystem conditional)
            {
                bool shouldPrepare;
                try
                {
                    shouldPrepare = conditional.ShouldPrepare;
                }
                catch (Exception ex)
                {
                    HandleSystemException(entry, ex);
                    return;
                }

                if (!shouldPrepare)
                    return;
            }

            if (entry.WorkSystem is ICorePreparedBudgetedWorkSystem prepared)
            {
                long prepareStart = Stopwatch.GetTimestamp();
                bool prepareFailed = false;
                try
                {
                    prepared.Prepare(context);
                }
                catch (Exception ex)
                {
                    prepareFailed = true;
                    HandleSystemException(entry, ex);
                }
                finally
                {
                    double elapsed = ElapsedMilliseconds(prepareStart);
                    RecordInvocation(entry, elapsed, channel, prepareFailed);
                }

                if (prepareFailed || entry.PendingRemoval || _serverTime < entry.QuarantineUntil)
                    return;
            }

            int units = 0;
            while (units < entry.MaxWorkUnitsPerTick && entry.WorkSystem.HasPendingWork)
            {
                if (IsFrameBudgetExhausted || channel.FrameStopwatch.Elapsed.TotalMilliseconds >= channel.Config.maxMillisecondsPerFrame)
                    break;

                long start = Stopwatch.GetTimestamp();
                bool failed = false;
                try
                {
                    entry.WorkSystem.ExecuteOneWorkUnit(context);
                }
                catch (Exception ex)
                {
                    failed = true;
                    HandleSystemException(entry, ex);
                }
                finally
                {
                    double elapsed = ElapsedMilliseconds(start);
                    RecordInvocation(entry, elapsed, channel, failed);
                }

                units++;
                if (failed || entry.PendingRemoval || _serverTime < entry.QuarantineUntil)
                    break;
            }
        }

        private void RecordInvocation(Entry entry, double elapsedMilliseconds, Channel channel, bool failed)
        {
            entry.LastInvocationMilliseconds = elapsedMilliseconds;
            if (elapsedMilliseconds > entry.MaximumInvocationMilliseconds)
                entry.MaximumInvocationMilliseconds = elapsedMilliseconds;
            entry.InvocationCount++;

            if (elapsedMilliseconds < entry.InvocationLimitMilliseconds && !failed)
            {
                if (entry.OverrunStrikes > 0)
                    entry.OverrunStrikes--;
                return;
            }

            entry.OverrunCount++;
            _systemOverruns++;

            // Startup grace is deliberately narrow: only slow-but-successful
            // invocations avoid strikes. Exceptions still follow the normal strike /
            // quarantine path. All global/channel CPU budgets remain enforced.
            if (!failed && IsStartupGraceActive)
            {
                _startupGraceOverruns++;

                if (_settings.logStartupGraceOverruns && _serverTime >= entry.NextWarningTime)
                {
                    CoreRuntimeSchedulerLog.Info(
                        $"[CoreRuntimeScheduler] Startup grace: system '{entry.Name}' in channel '{channel.Config.name}' " +
                        $"took {elapsedMilliseconds:F3} ms (limit {entry.InvocationLimitMilliseconds:F3} ms). " +
                        $"Measured, no strike. Grace remaining {StartupGraceRemainingSeconds:F2}s.");
                    entry.NextWarningTime = _serverTime + _settings.warningCooldownSeconds;
                }
                return;
            }

            entry.OverrunStrikes++;

            if (entry.OverrunStrikes > 1 && _serverTime >= entry.NextWarningTime)
            {
                CoreRuntimeSchedulerLog.Warning(
                    $"[CoreRuntimeScheduler] System '{entry.Name}' in channel '{channel.Config.name}' took {elapsedMilliseconds:F3} ms " +
                    $"(limit {entry.InvocationLimitMilliseconds:F3} ms, strike {entry.OverrunStrikes}/{_settings.strikesBeforeQuarantine}).");
                entry.NextWarningTime = _serverTime + _settings.warningCooldownSeconds;
            }

            if (!entry.Quarantineable || entry.OverrunStrikes < _settings.strikesBeforeQuarantine)
                return;

            entry.QuarantineLevel++;
            double multiplier = Math.Pow(2.0, Math.Min(8, entry.QuarantineLevel - 1));
            double duration = Math.Min(_settings.maxQuarantineSeconds, _settings.baseQuarantineSeconds * multiplier);
            entry.QuarantineUntil = _serverTime + duration;
            entry.OverrunStrikes = 0;
            entry.QuarantineCount++;
            _quarantineEvents++;

            CoreRuntimeSchedulerLog.Error(
                $"[CoreRuntimeScheduler] Quarantined system '{entry.Name}' in channel '{channel.Config.name}' for {duration:F1}s after repeated overruns.");
        }

        private void HandleSystemException(Entry entry, Exception ex)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            CoreRuntimeSchedulerLog.Error($"[CoreRuntimeScheduler] System '{entry.Name}' failed:\n{ex}");
#else
            CoreRuntimeSchedulerLog.Error($"[CoreRuntimeScheduler] System '{entry.Name}' failed: {ex.GetType().Name}");
#endif
        }

        private void RecoverChannel(Channel channel, bool budgetHit)
        {
            if (!channel.Config.allowThrottling)
                return;

            if (budgetHit)
            {
                channel.ConsecutiveBudgetHits++;
                channel.ConsecutiveHealthyFrames = 0;
                if (channel.ConsecutiveBudgetHits >= 3)
                {
                    double previousHz = channel.CurrentHz;
                    double nextHz = Math.Max(channel.Config.minHz, previousHz * 0.75);
                    channel.CurrentInterval = 1.0 / nextHz;
                    channel.ConsecutiveBudgetHits = 0;
                    if (nextHz < previousHz - 0.001)
                        _throttleEvents++;
                }
            }
            else
            {
                channel.ConsecutiveHealthyFrames++;
                channel.ConsecutiveBudgetHits = 0;
                if (channel.ConsecutiveHealthyFrames >= _settings.healthyFramesBeforeRecovery)
                {
                    if (channel.CurrentInterval > channel.BaseInterval + 0.0000001)
                    {
                        double currentHz = channel.CurrentHz;
                        double nextHz = Math.Min(channel.Config.baseHz, currentHz * 1.25);
                        channel.CurrentInterval = 1.0 / nextHz;
                        _recoveryEvents++;
                    }
                    channel.ConsecutiveHealthyFrames = 0;
                }
            }
        }

        public void ProcessDelayedTasks()
        {
            if (!_isRunning || _delayedHeap.Count <= 0 || IsFrameBudgetExhausted)
                return;

            _delayedStopwatch.Restart();
            int callbacks = 0;
            bool hitBudget = false;

            while (_delayedHeap.Count > 0)
            {
                if (callbacks >= _settings.maxDelayedCallbacksPerFrame ||
                    _delayedStopwatch.Elapsed.TotalMilliseconds >= _settings.maxDelayedTaskMillisecondsPerFrame ||
                    IsFrameBudgetExhausted)
                {
                    hitBudget = true;
                    break;
                }

                DelayedTask task = _delayedHeap[0];
                if (task.ExecuteAt > _serverTime)
                    break;

                HeapPop();
                callbacks++;

                if (!task.Cancelled && task.Callback != null)
                {
                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        task.Callback();
                    }
                    catch (Exception ex)
                    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        CoreRuntimeSchedulerLog.Error($"[CoreRuntimeScheduler] Delayed task failed:\n{ex}");
#else
                        CoreRuntimeSchedulerLog.Error($"[CoreRuntimeScheduler] Delayed task failed: {ex.GetType().Name}");
#endif
                    }
                    finally
                    {
                        double elapsed = ElapsedMilliseconds(start);
                        if (elapsed >= _settings.defaultSingleInvocationLimitMilliseconds)
                            _systemOverruns++;
                    }
                }

                RecycleDelayedTask(task);
            }

            _delayedStopwatch.Stop();
            _lastDelayedMilliseconds = _delayedStopwatch.Elapsed.TotalMilliseconds;
            if (hitBudget)
                _delayedTaskBudgetHits++;
        }

        private void CompactRemovedEntries()
        {
            for (int c = 0; c < _channels.Count; ++c)
            {
                List<Entry> systems = _channels[c].Systems;
                for (int i = systems.Count - 1; i >= 0; --i)
                {
                    if (systems[i].PendingRemoval)
                        systems.RemoveAt(i);
                }
                if (_channels[c].RoundRobinCursor >= systems.Count)
                    _channels[c].RoundRobinCursor = 0;
            }
        }

        private int GetRegisteredSystemCount()
        {
            int total = 0;
            for (int i = 0; i < _channels.Count; ++i)
            {
                List<Entry> systems = _channels[i].Systems;
                for (int j = 0; j < systems.Count; ++j)
                {
                    if (!systems[j].PendingRemoval)
                        total++;
                }
            }
            return total;
        }

        private void ClearDelayedTasks()
        {
            while (_delayedHeap.Count > 0)
            {
                DelayedTask task = _delayedHeap[_delayedHeap.Count - 1];
                _delayedHeap.RemoveAt(_delayedHeap.Count - 1);
                RecycleDelayedTask(task);
            }
        }

        private void HeapPush(DelayedTask task)
        {
            _delayedHeap.Add(task);
            int i = _delayedHeap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_delayedHeap[parent].ExecuteAt <= task.ExecuteAt)
                    break;
                _delayedHeap[i] = _delayedHeap[parent];
                i = parent;
            }
            _delayedHeap[i] = task;
        }

        private void HeapPop()
        {
            int last = _delayedHeap.Count - 1;
            DelayedTask root = _delayedHeap[0];
            DelayedTask tail = _delayedHeap[last];
            _delayedHeap.RemoveAt(last);
            root.InHeap = false;
            if (last == 0)
                return;

            int i = 0;
            while (true)
            {
                int left = (i << 1) + 1;
                if (left >= last)
                    break;
                int right = left + 1;
                int smallest = right < last && _delayedHeap[right].ExecuteAt < _delayedHeap[left].ExecuteAt ? right : left;
                if (_delayedHeap[smallest].ExecuteAt >= tail.ExecuteAt)
                    break;
                _delayedHeap[i] = _delayedHeap[smallest];
                i = smallest;
            }
            _delayedHeap[i] = tail;
        }

        private void RecycleDelayedTask(DelayedTask task)
        {
            if (task == null)
                return;
            task.Reset();
            if (_delayedPool.Count < _settings.maxDelayedTasks)
                _delayedPool.Push(task);
        }

        private static double ElapsedMilliseconds(long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            return elapsed * 1000.0 / Stopwatch.Frequency;
        }

        public bool TryGetChannelMetrics(string channelName, out CoreRuntimeChannelMetrics metrics)
        {
            metrics = default;
            if (string.IsNullOrEmpty(channelName) || !_channelsByName.TryGetValue(channelName, out Channel channel))
                return false;

            int registered = 0;
            for (int i = 0; i < channel.Systems.Count; ++i)
            {
                if (!channel.Systems[i].PendingRemoval)
                    registered++;
            }

            metrics = new CoreRuntimeChannelMetrics
            {
                name = channel.Config.name,
                currentHz = channel.CurrentHz,
                registeredSystems = registered,
                backlogTicks = channel.BacklogTicks,
                backlogSeconds = channel.BacklogSeconds,
                lastFrameMilliseconds = channel.LastFrameMilliseconds,
                maximumObservedFrameMilliseconds = channel.MaximumObservedFrameMilliseconds,
                executedTicks = channel.ExecutedTicks,
                budgetHits = channel.BudgetHits,
                droppedTicks = channel.DroppedTicks,
                skippedSystemInvocations = channel.SkippedSystemInvocations,
            };
            return true;
        }

        public CoreRuntimeSchedulerMetrics GetMetricsSnapshot()
        {
            CoreRuntimeSchedulerMetrics metrics = new CoreRuntimeSchedulerMetrics
            {
                active = _isRunning,
                channelCount = _channels.Count,
                delayedTaskCount = _delayedHeap.Count,
                lastFrameMilliseconds = _lastFrameMilliseconds,
                lastScheduledMilliseconds = _lastScheduledMilliseconds,
                lastDelayedTaskMilliseconds = _lastDelayedMilliseconds,
                maximumObservedFrameMilliseconds = _maximumObservedFrameMilliseconds,
                frameBudgetHits = _frameBudgetHits,
                systemOverruns = _systemOverruns,
                startupGraceOverruns = _startupGraceOverruns,
                startupGraceActive = IsStartupGraceActive,
                startupGraceRemainingSeconds = StartupGraceRemainingSeconds,
                quarantineEvents = _quarantineEvents,
                throttleEvents = _throttleEvents,
                recoveryEvents = _recoveryEvents,
                delayedTaskBudgetHits = _delayedTaskBudgetHits,
                delayedTaskRejections = _delayedTaskRejections,
            };

            for (int c = 0; c < _channels.Count; ++c)
            {
                Channel channel = _channels[c];
                metrics.backlogTicks += channel.BacklogTicks;
                metrics.backlogSeconds += channel.BacklogSeconds;
                metrics.channelBudgetHits += channel.BudgetHits;
                metrics.droppedTicks += channel.DroppedTicks;
                metrics.skippedSystemInvocations += channel.SkippedSystemInvocations;
                for (int i = 0; i < channel.Systems.Count; ++i)
                {
                    Entry entry = channel.Systems[i];
                    if (entry.PendingRemoval)
                        continue;
                    metrics.registeredSystemCount++;
                    if (_serverTime < entry.QuarantineUntil)
                        metrics.quarantinedSystemCount++;
                }
            }
            return metrics;
        }
    }
}
