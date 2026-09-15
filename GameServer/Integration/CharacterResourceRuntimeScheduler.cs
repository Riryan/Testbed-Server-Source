using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Game.Server.Application.Resources;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Shared.Content;
using Game.Shared.Resources;
using LiteNetLibManager;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Event-assisted timed resource work. There is no active-player polling pass.
    /// A runtime owns one delayed task only while at least one automatic resource has
    /// work due. The next wake-up is calculated from the configured rate, so a 1/sec
    /// resource wakes roughly once per second rather than every server tick.
    /// </summary>
    public sealed class CharacterResourceRuntimeScheduler : ICoreBudgetedWorkSystem, IDisposable
    {
        private const double MinimumSchedulerDelaySeconds = 0.01;
        private readonly double _minimumAutomaticCadenceSeconds;

        private sealed class RuntimeEntry
        {
            public ICoreScheduledTaskHandle Task;
            public Action<CharacterResourceChange> ChangeHandler;
            public readonly Dictionary<CharacterResourceId, double> Accumulators =
                new Dictionary<CharacterResourceId, double>();
            public double LastEvaluationTime;
            public bool ClockInitialized;
        }

        private readonly CoreRuntimeScheduler _scheduler;
        private readonly CharacterResourceService _resources;
        private readonly Func<PlayerRuntime, bool> _isInCombat;
        private readonly Dictionary<PlayerRuntime, RuntimeEntry> _entries =
            new Dictionary<PlayerRuntime, RuntimeEntry>();
        private readonly ConcurrentQueue<PlayerRuntime> _pending =
            new ConcurrentQueue<PlayerRuntime>();
        private readonly HashSet<PlayerRuntime> _queued = new HashSet<PlayerRuntime>();
        private readonly object _gate = new object();
        private readonly ICoreScheduledSystemHandle _pumpHandle;
        private bool _disposed;

        public string Name => "CharacterResourceSignals";
        public bool HasPendingWork => !_pending.IsEmpty;

        public CharacterResourceRuntimeScheduler(
            CoreRuntimeScheduler scheduler,
            CharacterResourceService resources,
            Func<PlayerRuntime, bool> isInCombat = null,
            double maximumAutomaticHz = 1.0)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _isInCombat = isInCombat ?? (_ => false);
            if (double.IsNaN(maximumAutomaticHz) || double.IsInfinity(maximumAutomaticHz) || maximumAutomaticHz <= 0d)
                maximumAutomaticHz = 1.0;
            _minimumAutomaticCadenceSeconds = Math.Max(MinimumSchedulerDelaySeconds, 1.0 / maximumAutomaticHz);
            _pumpHandle = _scheduler.RegisterWorkSystem(this, "Gameplay", 64, true, 1.0);
        }

        public void Activate(PlayerRuntime runtime)
        {
            if (runtime == null)
                return;

            RuntimeEntry entry;
            lock (_gate)
            {
                if (_disposed || _entries.ContainsKey(runtime))
                    return;

                entry = new RuntimeEntry();
                entry.ChangeHandler = change => OnResourceChanged(runtime, change);
                _entries.Add(runtime, entry);
            }

            runtime.ResourceChanged += entry.ChangeHandler;
            Queue(runtime);
        }

        /// <summary>
        /// Future combat-state events call this to stop/start condition-bound regen
        /// without introducing a combat-state polling pass.
        /// </summary>
        public void NotifyCombatStateChanged(PlayerRuntime runtime) =>
            ResetAutomaticClockAndQueue(runtime);

        /// <summary>
        /// Re-evaluates automatic resource deadlines after a validated content revision
        /// changes rates/conditions. This is event-triggered by content activation.
        /// </summary>
        public void NotifyDefinitionsChanged(PlayerRuntime runtime) =>
            ResetAutomaticClockAndQueue(runtime);

        public void Deactivate(PlayerRuntime runtime)
        {
            if (runtime == null)
                return;

            RuntimeEntry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(runtime, out entry))
                    return;
                _entries.Remove(runtime);
                _queued.Remove(runtime);
            }

            if (entry.ChangeHandler != null)
                runtime.ResourceChanged -= entry.ChangeHandler;
            entry.Task?.Cancel();
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (!_pending.TryDequeue(out PlayerRuntime runtime))
                return;

            bool active;
            lock (_gate)
            {
                _queued.Remove(runtime);
                active = !_disposed && _entries.ContainsKey(runtime);
            }
            if (active)
                EvaluateAndSchedule(runtime);
        }

        private void OnResourceChanged(PlayerRuntime runtime, CharacterResourceChange change)
        {
            // Passive changes were produced by this scheduler and already schedule their
            // successor. Ordinary external mutations must not pull passive regen forward
            // faster than its configured cadence; they only wake a runtime that currently
            // has no automatic-resource deadline (for example, damage from a full resource).
            if (change.Reason == CharacterResourceChangeReason.PassiveRecovery ||
                change.Reason == CharacterResourceChangeReason.PassiveDecay)
                return;

            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(runtime, out RuntimeEntry entry))
                    return;
                if (entry.Task != null && entry.Task.IsActive)
                    return;
            }

            Queue(runtime);
        }

        private void ResetAutomaticClockAndQueue(PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(runtime, out RuntimeEntry entry))
                    return;

                entry.Task?.Cancel();
                entry.Task = null;
                entry.Accumulators.Clear();
                entry.ClockInitialized = true;
                entry.LastEvaluationTime = _scheduler.ServerTime;
            }

            Queue(runtime);
        }

        private void Queue(PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.ContainsKey(runtime) || !_queued.Add(runtime))
                    return;
            }
            _pending.Enqueue(runtime);
        }

        private void EvaluateAndSchedule(PlayerRuntime runtime)
        {
            RuntimeEntry entry;
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(runtime, out entry))
                    return;
            }

            entry.Task?.Cancel();
            entry.Task = null;

            double now = _scheduler.ServerTime;
            double elapsed = entry.ClockInitialized
                ? Math.Max(0.0, now - entry.LastEvaluationTime)
                : 0.0;
            entry.ClockInitialized = true;
            entry.LastEvaluationTime = now;

            bool inCombat = _isInCombat(runtime);
            CharacterResourceDefinition[] definitions = _resources.GetAutomaticDefinitions();

            // Accrue only resources that are currently eligible. No full-player/world
            // scan occurs; this touches one runtime because an event or its own deadline fired.
            for (int i = 0; i < definitions.Length; ++i)
            {
                CharacterResourceDefinition definition = definitions[i];
                if (!_resources.ShouldAutomaticallyUpdate(runtime, definition, inCombat))
                {
                    entry.Accumulators.Remove(definition.id);
                    continue;
                }

                entry.Accumulators.TryGetValue(definition.id, out double accumulator);
                accumulator += definition.ratePerSecond * elapsed;
                int whole = accumulator >= int.MaxValue
                    ? int.MaxValue
                    : (int)Math.Floor(accumulator);
                if (whole > 0)
                {
                    accumulator -= whole;
                    _resources.ApplyAutomaticAmount(runtime, definition, whole, inCombat);
                }
                entry.Accumulators[definition.id] = accumulator;
            }

            // Re-evaluate eligibility after mutations may have reached min/max, then
            // schedule exactly the nearest next integer change across this runtime.
            double nextDelay = double.PositiveInfinity;
            for (int i = 0; i < definitions.Length; ++i)
            {
                CharacterResourceDefinition definition = definitions[i];
                if (!_resources.ShouldAutomaticallyUpdate(runtime, definition, inCombat))
                {
                    entry.Accumulators.Remove(definition.id);
                    continue;
                }

                entry.Accumulators.TryGetValue(definition.id, out double accumulator);
                double remaining = Math.Max(0.0, 1.0 - accumulator);
                double delay = remaining / definition.ratePerSecond;
                if (delay < nextDelay)
                    nextDelay = delay;
            }

            if (double.IsInfinity(nextDelay))
            {
                entry.Accumulators.Clear();
                entry.ClockInitialized = false;
                return;
            }

            // Passive regen/decay keeps exact fractional accumulation but does not
            // automatically wake faster than the configured low-frequency cadence.
            nextDelay = Math.Max(_minimumAutomaticCadenceSeconds, nextDelay);
            if (_scheduler.TrySchedule(nextDelay, () => Queue(runtime), out ICoreScheduledTaskHandle handle))
                entry.Task = handle;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            PlayerRuntime[] runtimes;
            lock (_gate)
            {
                runtimes = new PlayerRuntime[_entries.Count];
                _entries.Keys.CopyTo(runtimes, 0);
            }
            for (int i = 0; i < runtimes.Length; ++i)
                Deactivate(runtimes[i]);
            _pumpHandle?.Unregister();
            while (_pending.TryDequeue(out _)) { }
        }
    }
}
