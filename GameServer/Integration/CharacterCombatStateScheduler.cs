using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Game.Server.Application.Combat;
using Game.Server.Domain.Combat;
using Game.Server.Domain.Players;
using LiteNetLibManager;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Event-assisted combat-state deadlines. Combat activity schedules one expiry
    /// deadline for that runtime; no player list is scanned to discover combat exit.
    /// </summary>
    public sealed class CharacterCombatStateScheduler : ICoreBudgetedWorkSystem, IDisposable
    {
        private sealed class Entry
        {
            public ICoreScheduledTaskHandle Task;
            public Action<CombatActivityChange> Handler;
        }

        private readonly CoreRuntimeScheduler _scheduler;
        private readonly CombatService _combat;
        private readonly CharacterResourceRuntimeScheduler _resources;
        private readonly Dictionary<PlayerRuntime, Entry> _entries = new Dictionary<PlayerRuntime, Entry>();
        private readonly ConcurrentQueue<PlayerRuntime> _pending = new ConcurrentQueue<PlayerRuntime>();
        private readonly HashSet<PlayerRuntime> _queued = new HashSet<PlayerRuntime>();
        private readonly object _gate = new object();
        private readonly ICoreScheduledSystemHandle _pumpHandle;
        private bool _disposed;

        public string Name => "CharacterCombatSignals";
        public bool HasPendingWork => !_pending.IsEmpty;

        public CharacterCombatStateScheduler(
            CoreRuntimeScheduler scheduler,
            CombatService combat,
            CharacterResourceRuntimeScheduler resources)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _pumpHandle = _scheduler.RegisterWorkSystem(this, "Gameplay", 64, true, 1.0);
        }

        public void Activate(PlayerRuntime runtime)
        {
            if (runtime == null) return;
            Entry entry;
            lock (_gate)
            {
                if (_disposed || _entries.ContainsKey(runtime)) return;
                entry = new Entry();
                entry.Handler = _ => OnCombatActivity(runtime);
                _entries.Add(runtime, entry);
            }
            runtime.CombatActivityChanged += entry.Handler;
            Queue(runtime);
        }

        public void Deactivate(PlayerRuntime runtime)
        {
            if (runtime == null) return;
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(runtime, out entry)) return;
                _entries.Remove(runtime);
                _queued.Remove(runtime);
            }
            if (entry.Handler != null)
                runtime.CombatActivityChanged -= entry.Handler;
            entry.Task?.Cancel();
        }

        private void OnCombatActivity(PlayerRuntime runtime)
        {
            // Entering/refreshing combat changes out-of-combat resource eligibility now.
            _resources.NotifyCombatStateChanged(runtime);
            Queue(runtime);
        }

        public void NotifyDefinitionsChanged(PlayerRuntime runtime) => Queue(runtime);

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (!_pending.TryDequeue(out PlayerRuntime runtime)) return;
            bool active;
            lock (_gate)
            {
                _queued.Remove(runtime);
                active = !_disposed && _entries.ContainsKey(runtime);
            }
            if (active) Evaluate(runtime);
        }

        private void Evaluate(PlayerRuntime runtime)
        {
            Entry entry;
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(runtime, out entry)) return;
            }

            entry.Task?.Cancel();
            entry.Task = null;

            double now = _scheduler.ServerTime;
            if (!_combat.IsInCombat(runtime, now))
            {
                // The single scheduled deadline crossed. Notify only this runtime so
                // out-of-combat regen can resume without a combat-state polling sweep.
                _resources.NotifyCombatStateChanged(runtime);
                return;
            }

            double delay = Math.Max(0.01d, _combat.GetCombatExpiry(runtime) - now);
            if (_scheduler.TrySchedule(delay, () => Queue(runtime), out ICoreScheduledTaskHandle handle))
                entry.Task = handle;
        }

        private void Queue(PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.ContainsKey(runtime) || !_queued.Add(runtime)) return;
            }
            _pending.Enqueue(runtime);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PlayerRuntime[] runtimes;
            lock (_gate)
            {
                runtimes = new PlayerRuntime[_entries.Count];
                _entries.Keys.CopyTo(runtimes, 0);
            }
            for (int i = 0; i < runtimes.Length; ++i) Deactivate(runtimes[i]);
            _pumpHandle?.Unregister();
            while (_pending.TryDequeue(out _)) { }
        }
    }
}
