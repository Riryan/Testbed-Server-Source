using System;
using System.Collections.Generic;
using Game.Server.Domain.Players;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    /// <summary>
    /// Tracks dirty runtimes without polling all connected players. Ordinary checkpoint
    /// selection is age-gated and deterministically staggered per character so soft-state
    /// persistence is spread across the configured window instead of forming synchronized
    /// save spikes.
    /// </summary>
    public sealed class DirtyPlayerTracker
    {
        private sealed class DirtyEntry
        {
            public PlayerRuntime Runtime;
            public long FirstDirtyUtcTicks;

            public DirtyEntry(PlayerRuntime runtime, long firstDirtyUtcTicks)
            {
                Runtime = runtime;
                FirstDirtyUtcTicks = firstDirtyUtcTicks;
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<CharacterId, DirtyEntry> _dirty = new Dictionary<CharacterId, DirtyEntry>();
        private readonly HashSet<CharacterId> _tracked = new HashSet<CharacterId>();

        public int DirtyCount
        {
            get
            {
                lock (_gate)
                    return _dirty.Count;
            }
        }

        public void Track(PlayerRuntime runtime)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            lock (_gate)
            {
                if (!_tracked.Add(runtime.CharacterId))
                    return;

                // Subscribe while holding the tracker gate so there is no window
                // where the runtime can become dirty after registration but before
                // the event handler exists.
                runtime.BecameDirty += OnBecameDirty;
                if (runtime.IsDirty)
                    _dirty[runtime.CharacterId] = new DirtyEntry(runtime, DateTime.UtcNow.Ticks);
            }
        }

        public void Untrack(PlayerRuntime runtime)
        {
            if (runtime == null)
                return;

            lock (_gate)
            {
                if (!_tracked.Remove(runtime.CharacterId))
                    return;

                runtime.BecameDirty -= OnBecameDirty;
                _dirty.Remove(runtime.CharacterId);
            }
        }

        /// <summary>
        /// Force-selection path used by lifecycle boundaries such as graceful shutdown.
        /// It intentionally ignores soft checkpoint age.
        /// </summary>
        public PlayerRuntime[] GetBatch(int maxCount) =>
            GetBatch(maxCount, (PlayerDirtyFlags)ushort.MaxValue);

        /// <summary>
        /// Force-selection path used by lifecycle boundaries. Separate persistence streams
        /// can select only the dirty categories they actually own.
        /// </summary>
        public PlayerRuntime[] GetBatch(int maxCount, PlayerDirtyFlags requiredFlags)
        {
            if (maxCount <= 0 || requiredFlags == PlayerDirtyFlags.None)
                return Array.Empty<PlayerRuntime>();

            lock (_gate)
            {
                var result = new List<PlayerRuntime>(Math.Min(maxCount, _dirty.Count));
                foreach (DirtyEntry entry in _dirty.Values)
                {
                    PlayerRuntime runtime = entry.Runtime;
                    if (runtime == null || (runtime.DirtyFlags & requiredFlags) == 0)
                        continue;

                    result.Add(runtime);
                    if (result.Count == maxCount)
                        break;
                }
                return result.ToArray();
            }
        }

        /// <summary>
        /// Returns only characters whose soft persistence deadline is due. The preferred
        /// age is the earliest save point; each character receives a deterministic offset
        /// across the preferred..hard window. The hard age is never extended by subsequent
        /// mutations while the character remains dirty.
        /// </summary>
        public PlayerRuntime[] GetDueBatch(
            int maxCount,
            PlayerDirtyFlags requiredFlags,
            DateTime utcNow,
            TimeSpan preferredAge,
            TimeSpan hardAge)
        {
            if (maxCount <= 0 || requiredFlags == PlayerDirtyFlags.None)
                return Array.Empty<PlayerRuntime>();
            if (preferredAge < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(preferredAge));
            if (hardAge < preferredAge)
                throw new ArgumentOutOfRangeException(nameof(hardAge));

            long nowTicks = utcNow.Ticks;
            long preferredTicks = preferredAge.Ticks;
            long hardTicks = hardAge.Ticks;
            long jitterWindow = Math.Max(0L, hardTicks - preferredTicks);

            lock (_gate)
            {
                var due = new List<(PlayerRuntime Runtime, long DueTicks)>(Math.Min(maxCount, _dirty.Count));
                foreach (KeyValuePair<CharacterId, DirtyEntry> pair in _dirty)
                {
                    DirtyEntry entry = pair.Value;
                    PlayerRuntime runtime = entry.Runtime;
                    if (runtime == null || (runtime.DirtyFlags & requiredFlags) == 0)
                        continue;

                    long ageTicks = Math.Max(0L, nowTicks - entry.FirstDirtyUtcTicks);
                    long staggerTicks = DeterministicJitterTicks(pair.Key, jitterWindow);
                    long dueAgeTicks = preferredTicks + staggerTicks;
                    if (ageTicks < dueAgeTicks && ageTicks < hardTicks)
                        continue;

                    long dueTicks = entry.FirstDirtyUtcTicks + dueAgeTicks;
                    due.Add((runtime, dueTicks));
                }

                due.Sort((a, b) => a.DueTicks.CompareTo(b.DueTicks));
                int count = Math.Min(maxCount, due.Count);
                var result = new PlayerRuntime[count];
                for (int i = 0; i < count; ++i)
                    result[i] = due[i].Runtime;
                return result;
            }
        }

        public void RefreshAfterSave(PlayerRuntime runtime) =>
            RefreshAfterSave(runtime, false);

        /// <summary>
        /// Refreshes tracker membership after a persistence attempt. When a durable write
        /// succeeded but the runtime mutated while that write was in flight, reset the
        /// dirty-age window instead of immediately hammering persistence again on the next
        /// sweep. A later lifecycle force-flush still ignores this age.
        /// </summary>
        public void RefreshAfterSave(PlayerRuntime runtime, bool durableWriteSucceeded)
        {
            if (runtime == null)
                return;

            lock (_gate)
            {
                if (!_tracked.Contains(runtime.CharacterId))
                    return;

                if (runtime.IsDirty)
                {
                    if (_dirty.TryGetValue(runtime.CharacterId, out DirtyEntry entry))
                    {
                        entry.Runtime = runtime;
                        if (durableWriteSucceeded)
                            entry.FirstDirtyUtcTicks = DateTime.UtcNow.Ticks;
                    }
                    else
                    {
                        _dirty[runtime.CharacterId] = new DirtyEntry(runtime, DateTime.UtcNow.Ticks);
                    }
                }
                else
                {
                    _dirty.Remove(runtime.CharacterId);
                }
            }
        }

        private void OnBecameDirty(PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (!_tracked.Contains(runtime.CharacterId))
                    return;

                // BecameDirty is emitted only on the clean -> dirty transition, so this
                // timestamp is the first dirty time and later mutations cannot move the
                // hard checkpoint deadline forward indefinitely.
                _dirty[runtime.CharacterId] = new DirtyEntry(runtime, DateTime.UtcNow.Ticks);
            }
        }

        private static long DeterministicJitterTicks(CharacterId characterId, long windowTicks)
        {
            if (windowTicks <= 0)
                return 0;

            // SplitMix64-style avalanche. Stable for the character id, cheap, and avoids
            // storing another timer/random value per dirty runtime.
            ulong x = unchecked((ulong)characterId.Value) + 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (long)(x % ((ulong)windowTicks + 1UL));
        }
    }
}
