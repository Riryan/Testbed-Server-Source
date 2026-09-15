using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Game.Server.Application.Effects;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using LiteNetLibManager;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Deadline scheduler for status expiry and periodic canonical effects. Work enters the
    /// 10 Hz Combat scheduler lane, while authored pulse intervals remain independent.
    /// No per-frame or per-20-Hz status polling exists.
    /// </summary>
    public sealed class CharacterStatusEffectRuntimeScheduler : ICoreBudgetedWorkSystem, IDisposable
    {
        private readonly struct PeriodicKey : IEquatable<PeriodicKey>
        {
            public readonly string DefinitionId;
            public readonly ushort EffectIndex;

            public PeriodicKey(string definitionId, int effectIndex)
            {
                DefinitionId = definitionId ?? string.Empty;
                EffectIndex = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, effectIndex));
            }

            public bool Equals(PeriodicKey other) =>
                EffectIndex == other.EffectIndex &&
                string.Equals(DefinitionId, other.DefinitionId, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is PeriodicKey other && Equals(other);
            public override int GetHashCode() => (DefinitionId.GetHashCode() * 397) ^ EffectIndex;
        }

        private sealed class Entry
        {
            public ICoreScheduledTaskHandle Task;
            public Action<StatusEffectChange> Handler;
            public readonly Dictionary<PeriodicKey, double> NextTicks = new Dictionary<PeriodicKey, double>();
            public readonly Dictionary<PeriodicKey, int> PulseCounts = new Dictionary<PeriodicKey, int>();
            public readonly HashSet<string> ResetDefinitions = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<PeriodicKey> RemoveKeys = new List<PeriodicKey>(8);
        }

        private readonly CoreRuntimeScheduler _scheduler;
        private readonly StatusEffectService _statuses;
        private readonly GameplayEffectService _effects;
        private readonly Func<long, PlayerRuntime> _runtimeResolver;
        private readonly Dictionary<PlayerRuntime, Entry> _entries = new Dictionary<PlayerRuntime, Entry>();
        private readonly ConcurrentQueue<PlayerRuntime> _pending = new ConcurrentQueue<PlayerRuntime>();
        private readonly HashSet<PlayerRuntime> _queued = new HashSet<PlayerRuntime>();
        private readonly object _gate = new object();
        private readonly ICoreScheduledSystemHandle _pumpHandle;
        private bool _disposed;

        public string Name => "CharacterStatusSignals";
        public bool HasPendingWork => !_pending.IsEmpty;

        public CharacterStatusEffectRuntimeScheduler(
            CoreRuntimeScheduler scheduler,
            StatusEffectService statuses,
            GameplayEffectService effects,
            Func<long, PlayerRuntime> runtimeResolver = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
            _effects = effects ?? throw new ArgumentNullException(nameof(effects));
            _runtimeResolver = runtimeResolver;
            _pumpHandle = _scheduler.RegisterWorkSystem(this, "Combat", 32, true, 1.0);
        }

        public void Activate(PlayerRuntime runtime)
        {
            if (runtime == null) return;
            Entry entry;
            lock (_gate)
            {
                if (_disposed || _entries.ContainsKey(runtime)) return;
                entry = new Entry();
                entry.Handler = change => OnStatusChanged(runtime, change);
                _entries.Add(runtime, entry);
            }
            runtime.StatusEffectChanged += entry.Handler;
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
                runtime.StatusEffectChanged -= entry.Handler;
            entry.Task?.Cancel();
            entry.NextTicks.Clear();
            entry.PulseCounts.Clear();
            entry.ResetDefinitions.Clear();
        }

        public void NotifyDefinitionsChanged(PlayerRuntime runtime) => Queue(runtime);

        private void OnStatusChanged(PlayerRuntime runtime, StatusEffectChange change)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(runtime, out Entry entry)) return;
                if (!string.IsNullOrWhiteSpace(change.DefinitionId))
                {
                    if (change.Stacks == 0)
                        RemoveDefinitionKeys(entry, change.DefinitionId);
                    else if (change.ResetPeriodicClock)
                        entry.ResetDefinitions.Add(change.DefinitionId);
                }
            }
            Queue(runtime);
        }

        public void ExecuteOneWorkUnit(in CoreTickContext context)
        {
            if (!_pending.TryDequeue(out PlayerRuntime runtime)) return;
            bool active;
            lock (_gate)
            {
                _queued.Remove(runtime);
                active = !_disposed && _entries.ContainsKey(runtime);
            }
            if (active) EvaluateAndSchedule(runtime);
        }

        private void EvaluateAndSchedule(PlayerRuntime runtime)
        {
            Entry entry;
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(runtime, out entry)) return;
            }

            entry.Task?.Cancel();
            entry.Task = null;

            double now = _scheduler.ServerTime;
            StatusEffectInstanceState[] active = runtime.CaptureStatusEffects().Snapshot();
            double nextDeadline = double.PositiveInfinity;

            for (int i = 0; i < active.Length; ++i)
            {
                StatusEffectInstanceState state = active[i];
                if (now >= state.EndTime)
                {
                    _statuses.ExpireIfDue(runtime, state.DefinitionId, now);
                    RemoveDefinitionKeys(entry, state.DefinitionId);
                    continue;
                }

                if (state.EndTime < nextDeadline)
                    nextDeadline = state.EndTime;

                if (!_statuses.TryGetPeriodicDefinition(state.DefinitionId, out StatusEffectDefinition definition))
                {
                    RemoveDefinitionKeys(entry, state.DefinitionId);
                    continue;
                }

                bool resetDefinition = entry.ResetDefinitions.Remove(state.DefinitionId);
                GameplayEffectDefinition[] definitions = definition.effects ?? Array.Empty<GameplayEffectDefinition>();
                for (int effectIndex = 0; effectIndex < definitions.Length; ++effectIndex)
                {
                    GameplayEffectDefinition effect = definitions[effectIndex];
                    if (effect == null || effect.timing != GameplayEffectTiming.Periodic)
                        continue;

                    var key = new PeriodicKey(state.DefinitionId, effectIndex);
                    double interval = Math.Max(0.1d, effect.pulseIntervalSeconds);
                    if (resetDefinition)
                    {
                        entry.NextTicks.Remove(key);
                        entry.PulseCounts.Remove(key);
                    }

                    if (!entry.NextTicks.TryGetValue(key, out double nextTick) || nextTick <= 0d)
                    {
                        nextTick = effect.pulseTiming == GameplayPulseTiming.Immediate
                            ? now
                            : now + interval;
                    }

                    int completedPulses = entry.PulseCounts.TryGetValue(key, out int oldPulses) ? oldPulses : 0;
                    int safety = 0;
                    while (now + 0.0001d >= nextTick && safety++ < 4)
                    {
                        if (effect.pulseCount > 0 && completedPulses >= effect.pulseCount)
                            break;

                        PlayerRuntime source = state.SourceCharacterIdValue > 0 ? _runtimeResolver?.Invoke(state.SourceCharacterIdValue) : null;
                        _effects.Apply(
                            source,
                            runtime,
                            effect,
                            1,
                            now,
                            StatusEffectChangeReason.Combat,
                            CombatDamageCause.SharedStatus,
                            Math.Max(1, (int)state.Stacks));
                        completedPulses++;
                        nextTick += interval;

                        if (!IsAlive(runtime))
                            break;
                    }

                    if (!IsAlive(runtime) || (effect.pulseCount > 0 && completedPulses >= effect.pulseCount))
                    {
                        entry.NextTicks.Remove(key);
                        entry.PulseCounts.Remove(key);
                        continue;
                    }

                    entry.NextTicks[key] = nextTick;
                    entry.PulseCounts[key] = completedPulses;
                    if (nextTick < nextDeadline)
                        nextDeadline = nextTick;
                }
            }

            entry.RemoveKeys.Clear();
            foreach (KeyValuePair<PeriodicKey, double> pair in entry.NextTicks)
                if (Find(active, pair.Key.DefinitionId) < 0)
                    entry.RemoveKeys.Add(pair.Key);
            for (int i = 0; i < entry.RemoveKeys.Count; ++i)
            {
                PeriodicKey key = entry.RemoveKeys[i];
                entry.NextTicks.Remove(key);
                entry.PulseCounts.Remove(key);
            }

            if (double.IsInfinity(nextDeadline))
                return;

            double delay = Math.Max(0.01d, nextDeadline - now);
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

        private static void RemoveDefinitionKeys(Entry entry, string definitionId)
        {
            entry.RemoveKeys.Clear();
            foreach (PeriodicKey key in entry.NextTicks.Keys)
                if (string.Equals(key.DefinitionId, definitionId, StringComparison.Ordinal))
                    entry.RemoveKeys.Add(key);
            for (int i = 0; i < entry.RemoveKeys.Count; ++i)
            {
                PeriodicKey key = entry.RemoveKeys[i];
                entry.NextTicks.Remove(key);
                entry.PulseCounts.Remove(key);
            }
            entry.ResetDefinitions.Remove(definitionId);
        }

        private static int Find(StatusEffectInstanceState[] active, string definitionId)
        {
            for (int i = 0; i < active.Length; ++i)
                if (string.Equals(active[i].DefinitionId, definitionId, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static bool IsAlive(PlayerRuntime runtime) =>
            runtime != null &&
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) &&
            health.Enabled && health.Current > health.Minimum;

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
