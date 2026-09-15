using System;
using System.Collections.Generic;
using Game.Server.Application.Abilities;
using Game.Server.Domain.Actions;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using LiteNetLibManager;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Schedules only active cast completion deadlines. Characters with no active
    /// cast own no delayed task and consume no ability polling/tick work.
    /// </summary>
    public sealed class CharacterAbilityCastScheduler : IDisposable
    {
        private sealed class Entry
        {
            public ICoreScheduledTaskHandle Task;
            public Action<CharacterActionChange> Handler;
            public long ScheduledCastId;
        }

        private readonly CoreRuntimeScheduler _scheduler;
        private readonly AbilityService _abilities;
        private readonly Func<long, PlayerRuntime> _resolvePlayer;
        private readonly Func<PlayerRuntime, PlayerRuntime, bool> _canCompleteTarget;
        private readonly Dictionary<PlayerRuntime, Entry> _entries = new Dictionary<PlayerRuntime, Entry>();
        private bool _disposed;

        public CharacterAbilityCastScheduler(
            CoreRuntimeScheduler scheduler,
            AbilityService abilities,
            Func<long, PlayerRuntime> resolvePlayer,
            Func<PlayerRuntime, PlayerRuntime, bool> canCompleteTarget = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _abilities = abilities ?? throw new ArgumentNullException(nameof(abilities));
            _resolvePlayer = resolvePlayer ?? throw new ArgumentNullException(nameof(resolvePlayer));
            _canCompleteTarget = canCompleteTarget;
        }

        public void Activate(PlayerRuntime runtime)
        {
            if (runtime == null || _disposed || _entries.ContainsKey(runtime))
                return;

            var entry = new Entry();
            entry.Handler = _ => Reconcile(runtime);
            _entries.Add(runtime, entry);
            runtime.ActionStateChanged += entry.Handler;
            Reconcile(runtime);
        }

        public void Deactivate(PlayerRuntime runtime)
        {
            if (runtime == null || !_entries.TryGetValue(runtime, out Entry entry))
                return;
            _entries.Remove(runtime);
            runtime.ActionStateChanged -= entry.Handler;
            entry.Task?.Cancel();
            entry.Task = null;
        }

        private void Reconcile(PlayerRuntime runtime)
        {
            if (_disposed || runtime == null || !_entries.TryGetValue(runtime, out Entry entry))
                return;

            ActiveAbilityCastState cast = runtime.CaptureActionState().ActiveCast;
            if (!cast.IsActive)
            {
                entry.Task?.Cancel();
                entry.Task = null;
                entry.ScheduledCastId = 0;
                return;
            }

            if (entry.Task != null && entry.Task.IsActive && entry.ScheduledCastId == cast.CastId)
                return;

            entry.Task?.Cancel();
            entry.Task = null;
            entry.ScheduledCastId = cast.CastId;
            double delay = Math.Max(0d, cast.CompletesAt - _scheduler.ServerTime);
            if (_scheduler.TrySchedule(delay, () => Complete(runtime, cast.CastId), out ICoreScheduledTaskHandle handle))
                entry.Task = handle;
        }

        private void Complete(PlayerRuntime source, long castId)
        {
            if (_disposed || source == null || !_entries.TryGetValue(source, out Entry entry))
                return;

            entry.Task = null;
            entry.ScheduledCastId = 0;
            ActiveAbilityCastState cast = source.CaptureActionState().ActiveCast;
            if (!cast.IsActive || cast.CastId != castId)
                return;

            PlayerRuntime target = cast.TargetCharacterId > 0 ? _resolvePlayer(cast.TargetCharacterId) : null;

            // Range/state/effects are revalidated inside AbilityService.TryCompleteCast.
            // Integration-owned world visibility/occlusion must also be revalidated at the
            // actual impact boundary because a target may move or world blockers may change
            // while a timed cast is in flight.
            if (target != null &&
                !ReferenceEquals(source, target) &&
                _canCompleteTarget != null &&
                !_canCompleteTarget(source, target))
            {
                _abilities.CancelCast(source, AbilityCastFailure.InvalidTarget);
                Reconcile(source);
                return;
            }

            _abilities.TryCompleteCast(source, target, castId, _scheduler.ServerTime);
            Reconcile(source);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var runtimes = new PlayerRuntime[_entries.Count];
            _entries.Keys.CopyTo(runtimes, 0);
            for (int i = 0; i < runtimes.Length; ++i)
                Deactivate(runtimes[i]);
        }
    }
}
