using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Progression;

namespace Game.Server.Application.Progression
{
    public enum ProgressionDeltaKind : byte
    {
        Experience = 0,
        Track = 1,
        Reputation = 2,
        Heat = 3,
        KnownRecipe = 4,
        Faction = 5,
    }

    public readonly struct ProgressionDelta
    {
        public long Revision { get; }
        public ProgressionDeltaKind Kind { get; }
        public ushort DataId { get; }
        public long Value { get; }
        public long Auxiliary { get; }
        public int Extra { get; }
        public ProgressionDelta(long revision, ProgressionDeltaKind kind, ushort dataId, long value, long auxiliary = 0, int extra = 0)
        { Revision = revision; Kind = kind; DataId = dataId; Value = value; Auxiliary = auxiliary; Extra = extra; }
    }

    public readonly struct IncidentObservation
    {
        public bool Witnessed { get; }
        public bool CameraObserved { get; }
        public int Evidence { get; }
        public IncidentObservation(bool witnessed, bool cameraObserved, int evidence = 0)
        { Witnessed = witnessed; CameraObserved = cameraObserved; Evidence = Math.Max(0, evidence); }
        public bool Observed => Witnessed || CameraObserved;
    }

    /// <summary>
    /// Canonical owner-only progression/reputation/heat state. Mutations are event driven;
    /// Heat decay is applied lazily on reads/incidents and never schedules per-player ticks.
    /// </summary>
    public sealed class ProgressionService
    {
        private readonly GameplayContentCatalog _content;
        public event Action<PlayerRuntime, ProgressionDelta> Changed;

        public ProgressionService(GameplayContentCatalog content) =>
            _content = content ?? throw new ArgumentNullException(nameof(content));

        public CharacterProgressionState Snapshot(PlayerRuntime runtime, long utcTicks = 0)
        {
            if (runtime == null) return CharacterProgressionState.CreateDefault();
            EnsureDefaults(runtime);
            ApplyLazyHeatDecay(runtime, utcTicks <= 0 ? DateTime.UtcNow.Ticks : utcTicks);
            return runtime.CaptureProgressionState();
        }

        public void EnsureDefaults(PlayerRuntime runtime)
        {
            if (runtime == null) return;
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                var tracks = new List<ProgressTrackState>(current.tracks ?? Array.Empty<ProgressTrackState>());
                var recipes = new HashSet<ushort>(current.knownRecipeDataIds ?? Array.Empty<ushort>());
                bool changed = false;

                ProgressTrackDefinition[] definitions = _content.GetProgressTracks();
                for (int i = 0; i < definitions.Length; ++i)
                {
                    ProgressTrackDefinition definition = definitions[i];
                    if (definition == null || FindTrack(tracks, definition.dataId) != null) continue;
                    tracks.Add(new ProgressTrackState { dataId = definition.dataId, value = 0, mode = definition.defaultMode, lastChangedUtcTicks = 0 });
                    changed = true;
                }

                RecipeDefinition[] recipeDefinitions = _content.GetRecipes();
                for (int i = 0; i < recipeDefinitions.Length; ++i)
                    if (recipeDefinitions[i] != null && recipeDefinitions[i].learnByDefault && recipes.Add(recipeDefinitions[i].dataId)) changed = true;

                ProgressionRulesDefinition rules = _content.GetProgressionRules();
                int expectedLevel = CalculateLevel(current.experience, rules);
                ushort expectedFaction = current.factionDataId == 0 ? ResolveDefaultFactionDataId(rules) : current.factionDataId;
                if (current.level != expectedLevel || current.factionDataId != expectedFaction) changed = true;
                if (!changed) return;

                CharacterProgressionState next = current.Clone();
                next.revision = current.revision + 1;
                next.level = expectedLevel;
                next.factionDataId = expectedFaction;
                next.tracks = tracks.ToArray();
                next.knownRecipeDataIds = ToSortedArray(recipes);
                if (runtime.TryCommitProgression(current.revision, next))
                {
                    if (current.level != next.level)
                        Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Experience, 0, next.experience, next.level));
                    if (current.factionDataId != next.factionDataId)
                        Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Faction, next.factionDataId, next.factionDataId));

                    ushort[] previousRecipes = current.knownRecipeDataIds ?? Array.Empty<ushort>();
                    ushort[] currentRecipes = next.knownRecipeDataIds ?? Array.Empty<ushort>();
                    for (int i = 0; i < currentRecipes.Length; ++i)
                    {
                        ushort recipeDataId = currentRecipes[i];
                        if (Array.BinarySearch(previousRecipes, recipeDataId) < 0)
                            Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.KnownRecipe, recipeDataId, 1));
                    }
                    return;
                }
            }
            throw new InvalidOperationException("Failed to initialize progression state due to repeated concurrent mutations.");
        }

        public bool GainExperience(PlayerRuntime runtime, long amount)
        {
            if (runtime == null || amount <= 0) return false;
            EnsureDefaults(runtime);
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                CharacterProgressionState next = current.Clone();
                next.revision = current.revision + 1;
                next.experience = current.experience > long.MaxValue - amount ? long.MaxValue : current.experience + amount;
                if (next.experience == current.experience) return false;
                next.level = CalculateLevel(next.experience, _content.GetProgressionRules());
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Experience, 0, next.experience, next.level));
                return true;
            }
            return false;
        }

        public bool SetTrackMode(PlayerRuntime runtime, ushort trackDataId, ProgressTrackMode mode, long utcTicks = 0)
        {
            if (runtime == null || !_content.TryGetProgressTrack(trackDataId, out ProgressTrackDefinition definition)) return false;
            if ((byte)mode > (byte)ProgressTrackMode.Decay) return false;
            EnsureDefaults(runtime);
            long now = utcTicks <= 0 ? DateTime.UtcNow.Ticks : utcTicks;
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                CharacterProgressionState next = current.Clone();
                ProgressTrackState state = FindTrack(next.tracks, trackDataId);
                if (state == null || state.mode == mode) return state != null;
                state.mode = mode;
                state.lastChangedUtcTicks = now;
                next.revision = current.revision + 1;
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                // Gain/Lock/Decay is authoritative server state. Mode changes persist but are not replicated.
                return true;
            }
            return false;
        }

        public bool GainTrack(PlayerRuntime runtime, ushort trackDataId, int amount, long utcTicks = 0)
        {
            if (runtime == null || amount <= 0 || !_content.TryGetProgressTrack(trackDataId, out ProgressTrackDefinition definition)) return false;
            EnsureDefaults(runtime);
            long now = utcTicks <= 0 ? DateTime.UtcNow.Ticks : utcTicks;
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                if (!MeetsPredicates(current, definition.unlockPredicates, now)) return false;
                CharacterProgressionState next = current.Clone();
                ProgressTrackState target = FindTrack(next.tracks, trackDataId);
                if (target == null || target.mode != ProgressTrackMode.Gain || target.value >= definition.maximumValue) return false;

                int desired = Math.Min(definition.maximumValue, target.value + amount);
                int gain = desired - target.value;
                if (gain <= 0) return false;
                if (!MakeGroupCapacity(next, definition, gain, now)) return false;

                target.value += gain;
                target.lastChangedUtcTicks = now;
                next.revision = current.revision + 1;
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                EmitTrackChanges(runtime, current, next);
                return true;
            }
            return false;
        }

        public bool LearnRecipe(PlayerRuntime runtime, ushort recipeDataId)
        {
            if (runtime == null || !_content.TryGetRecipe(recipeDataId, out RecipeDefinition recipe)) return false;
            EnsureDefaults(runtime);
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                if (!MeetsPredicates(current, recipe.unlockPredicates, DateTime.UtcNow.Ticks)) return false;
                var known = new HashSet<ushort>(current.knownRecipeDataIds ?? Array.Empty<ushort>());
                if (!known.Add(recipeDataId)) return true;
                CharacterProgressionState next = current.Clone();
                next.revision = current.revision + 1;
                next.knownRecipeDataIds = ToSortedArray(known);
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.KnownRecipe, recipeDataId, 1));
                return true;
            }
            return false;
        }

        public bool IsRecipeKnown(PlayerRuntime runtime, ushort recipeDataId)
        {
            if (runtime == null) return false;
            EnsureDefaults(runtime);
            ushort[] known = runtime.CaptureProgressionState().knownRecipeDataIds ?? Array.Empty<ushort>();
            return Array.BinarySearch(known, recipeDataId) >= 0;
        }

        public bool SetFaction(PlayerRuntime runtime, ushort factionDataId)
        {
            if (runtime == null || (factionDataId != 0 && !_content.TryGetFaction(factionDataId, out _))) return false;
            EnsureDefaults(runtime);
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                if (current.factionDataId == factionDataId) return true;
                CharacterProgressionState next = current.Clone();
                next.revision = current.revision + 1;
                next.factionDataId = factionDataId;
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Faction, factionDataId, factionDataId));
                return true;
            }
            return false;
        }

        public bool AdjustReputation(PlayerRuntime runtime, ushort factionDataId, int amount)
        {
            if (runtime == null || amount == 0 || !_content.TryGetFaction(factionDataId, out FactionDefinition faction)) return false;
            EnsureDefaults(runtime);
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                CharacterProgressionState next = current.Clone();
                var entries = new List<ReputationState>(next.reputation ?? Array.Empty<ReputationState>());
                ReputationState entry = FindReputation(entries, factionDataId);
                int previousValue = entry?.value ?? 0;
                long adjusted = (long)previousValue + amount;
                int nextValue = (int)Math.Max(faction.minimumReputation, Math.Min(faction.maximumReputation, adjusted));
                if (nextValue == previousValue) return false;
                if (entry == null)
                {
                    entry = new ReputationState { factionDataId = factionDataId };
                    entries.Add(entry);
                }
                entry.value = nextValue;
                next.reputation = entries.ToArray();
                next.revision = current.revision + 1;
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Reputation, factionDataId, entry.value));
                return true;
            }
            return false;
        }

        public bool RecordIncident(PlayerRuntime runtime, ushort incidentDataId, ushort jurisdictionDataId, IncidentObservation observation, long utcTicks = 0)
        {
            if (runtime == null || !_content.TryGetIncident(incidentDataId, out IncidentDefinition incident) ||
                !_content.TryGetJurisdiction(jurisdictionDataId, out JurisdictionDefinition jurisdiction)) return false;
            if (incident.requiresWitnessOrCamera && !observation.Observed) return false;
            long now = utcTicks <= 0 ? DateTime.UtcNow.Ticks : utcTicks;
            ApplyLazyHeatDecay(runtime, now);

            bool heatChanged = false;
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                CharacterProgressionState next = current.Clone();
                var entries = new List<HeatState>(next.heat ?? Array.Empty<HeatState>());
                HeatState entry = FindHeat(entries, jurisdictionDataId);
                int oldValue = entry?.value ?? 0;
                int oldEvidence = entry?.evidence ?? 0;
                long oldBounty = entry?.bounty ?? 0;

                int newValue = Math.Max(0, Math.Min(jurisdiction.maximumHeat, oldValue + Math.Max(0, incident.heat)));
                int newEvidence = Math.Max(0, oldEvidence + Math.Max(0, incident.evidence) + observation.Evidence);
                long newBounty = Math.Max(oldBounty, (long)newValue * jurisdiction.bountyPerHeat);
                if (newValue == oldValue && newEvidence == oldEvidence && newBounty == oldBounty)
                    break;

                if (entry == null)
                {
                    entry = new HeatState { jurisdictionDataId = jurisdictionDataId };
                    entries.Add(entry);
                }
                entry.value = newValue;
                entry.evidence = newEvidence;
                entry.bounty = newBounty;
                entry.updatedUtcTicks = now;
                next.heat = entries.ToArray();
                next.revision = current.revision + 1;
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Heat, jurisdictionDataId, entry.value, entry.bounty, entry.evidence));
                heatChanged = true;
                break;
            }

            bool reputationChanged = false;
            RewardReputationDefinition[] reputation = incident.reputation ?? Array.Empty<RewardReputationDefinition>();
            for (int i = 0; i < reputation.Length; ++i)
            {
                RewardReputationDefinition value = reputation[i];
                if (value != null && value.amount != 0 && AdjustReputation(runtime, value.factionDataId, value.amount))
                    reputationChanged = true;
            }
            return heatChanged || reputationChanged;
        }

        public bool MeetsPredicates(PlayerRuntime runtime, UnlockPredicateDefinition[] predicates)
        {
            if (runtime == null) return false;
            EnsureDefaults(runtime);
            ApplyLazyHeatDecay(runtime, DateTime.UtcNow.Ticks);
            return MeetsPredicates(runtime.CaptureProgressionState(), predicates, DateTime.UtcNow.Ticks);
        }

        private bool MeetsPredicates(CharacterProgressionState state, UnlockPredicateDefinition[] predicates, long now)
        {
            predicates = predicates ?? Array.Empty<UnlockPredicateDefinition>();
            for (int i = 0; i < predicates.Length; ++i)
            {
                UnlockPredicateDefinition p = predicates[i];
                if (p == null) return false;
                switch (p.kind)
                {
                    case UnlockPredicateKind.MinimumLevel:
                        if (state.level < p.minimumValue) return false;
                        break;
                    case UnlockPredicateKind.MinimumTrack:
                        if ((FindTrack(state.tracks, p.dataId)?.value ?? 0) < p.minimumValue) return false;
                        break;
                    case UnlockPredicateKind.MinimumReputation:
                        if ((FindReputation(state.reputation, p.dataId)?.value ?? 0) < p.minimumValue) return false;
                        break;
                    case UnlockPredicateKind.MaximumHeat:
                        if ((FindHeat(state.heat, p.dataId)?.value ?? 0) > p.minimumValue) return false;
                        break;
                    case UnlockPredicateKind.Faction:
                        if (state.factionDataId != p.dataId) return false;
                        break;
                    case UnlockPredicateKind.KnownRecipe:
                        if (Array.BinarySearch(state.knownRecipeDataIds ?? Array.Empty<ushort>(), p.dataId) < 0) return false;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private ushort ResolveDefaultFactionDataId(ProgressionRulesDefinition rules)
        {
            if (rules != null && rules.defaultFactionDataId != 0)
                return rules.defaultFactionDataId;
            FactionDefinition[] factions = _content.GetFactions();
            for (int i = 0; i < factions.Length; ++i)
            {
                FactionDefinition faction = factions[i];
                string[] tags = faction?.tags ?? Array.Empty<string>();
                for (int j = 0; j < tags.Length; ++j)
                    if (string.Equals(tags[j], "default", StringComparison.OrdinalIgnoreCase))
                        return faction.dataId;
            }
            return 0;
        }

        private void ApplyLazyHeatDecay(PlayerRuntime runtime, long now)
        {
            for (int attempts = 0; attempts < 8; ++attempts)
            {
                CharacterProgressionState current = runtime.CaptureProgressionState();
                CharacterProgressionState next = current.Clone();
                var changedIds = new List<ushort>();
                HeatState[] entries = next.heat ?? Array.Empty<HeatState>();
                for (int i = 0; i < entries.Length; ++i)
                {
                    HeatState entry = entries[i];
                    if (entry == null || entry.value <= 0 || !_content.TryGetJurisdiction(entry.jurisdictionDataId, out JurisdictionDefinition jurisdiction)) continue;
                    if (entry.updatedUtcTicks <= 0 || now <= entry.updatedUtcTicks || jurisdiction.heatDecayPerMinute <= 0f) continue;
                    double minutes = TimeSpan.FromTicks(now - entry.updatedUtcTicks).TotalMinutes;
                    int decay = (int)Math.Floor(minutes * jurisdiction.heatDecayPerMinute);
                    if (decay <= 0) continue;

                    int oldValue = entry.value;
                    long oldBounty = entry.bounty;
                    entry.value = Math.Max(0, entry.value - decay);
                    entry.bounty = Math.Min(entry.bounty, (long)entry.value * jurisdiction.bountyPerHeat);
                    if (entry.value == oldValue && entry.bounty == oldBounty) continue;

                    double consumedMinutes = decay / (double)jurisdiction.heatDecayPerMinute;
                    long consumedTicks = TimeSpan.FromMinutes(consumedMinutes).Ticks;
                    entry.updatedUtcTicks = Math.Min(now, entry.updatedUtcTicks + Math.Max(1, consumedTicks));
                    changedIds.Add(entry.jurisdictionDataId);
                }
                if (changedIds.Count == 0) return;
                next.revision = current.revision + 1;
                if (!runtime.TryCommitProgression(current.revision, next)) continue;
                for (int i = 0; i < changedIds.Count; ++i)
                {
                    HeatState entry = FindHeat(entries, changedIds[i]);
                    if (entry != null)
                        Changed?.Invoke(runtime, new ProgressionDelta(next.revision, ProgressionDeltaKind.Heat, entry.jurisdictionDataId, entry.value, entry.bounty, entry.evidence));
                }
                return;
            }
        }

        private bool MakeGroupCapacity(CharacterProgressionState state, ProgressTrackDefinition targetDefinition, int gain, long now)
        {
            if (string.IsNullOrWhiteSpace(targetDefinition.groupId) || targetDefinition.groupCap <= 0) return true;
            int total = 0;
            var candidates = new List<ProgressTrackState>();
            ProgressTrackState[] tracks = state.tracks ?? Array.Empty<ProgressTrackState>();
            for (int i = 0; i < tracks.Length; ++i)
            {
                ProgressTrackState entry = tracks[i];
                if (entry == null || !_content.TryGetProgressTrack(entry.dataId, out ProgressTrackDefinition def) ||
                    !string.Equals(def.groupId, targetDefinition.groupId, StringComparison.Ordinal)) continue;
                total += Math.Max(0, entry.value);
                if (entry.dataId != targetDefinition.dataId && entry.mode == ProgressTrackMode.Decay && entry.value > 0 &&
                    (entry.lastChangedUtcTicks <= 0 || now - entry.lastChangedUtcTicks >= TimeSpan.FromSeconds(Math.Max(0, def.decayCooldownSeconds)).Ticks))
                    candidates.Add(entry);
            }
            int overflow = total + gain - targetDefinition.groupCap;
            if (overflow <= 0) return true;
            candidates.Sort((a, b) => b.value.CompareTo(a.value));
            for (int i = 0; i < candidates.Count && overflow > 0; ++i)
            {
                int remove = Math.Min(candidates[i].value, overflow);
                candidates[i].value -= remove;
                candidates[i].lastChangedUtcTicks = now;
                overflow -= remove;
            }
            return overflow <= 0;
        }

        private static int CalculateLevel(long experience, ProgressionRulesDefinition rules)
        {
            rules = rules ?? new ProgressionRulesDefinition();
            int max = Math.Max(1, rules.maxLevel);
            long remaining = Math.Max(0, experience);
            int level = 1;
            double requirement = Math.Max(1, rules.baseExperiencePerLevel);
            double growth = Math.Max(1d, rules.experienceGrowthPerLevel);
            while (level < max && remaining >= (long)Math.Ceiling(requirement))
            {
                long required = Math.Max(1, (long)Math.Ceiling(requirement));
                remaining -= required;
                level++;
                requirement = Math.Min(long.MaxValue, requirement * growth);
            }
            return level;
        }

        private void EmitTrackChanges(PlayerRuntime runtime, CharacterProgressionState before, CharacterProgressionState after)
        {
            ProgressTrackState[] previous = before?.tracks ?? Array.Empty<ProgressTrackState>();
            ProgressTrackState[] current = after?.tracks ?? Array.Empty<ProgressTrackState>();
            for (int i = 0; i < current.Length; ++i)
            {
                ProgressTrackState now = current[i];
                if (now == null) continue;
                ProgressTrackState old = FindTrack(previous, now.dataId);
                // Internal mode/timestamp changes are intentionally invisible to the client.
                if (old != null && old.value == now.value) continue;
                Changed?.Invoke(runtime, new ProgressionDelta(after.revision, ProgressionDeltaKind.Track, now.dataId, now.value));
            }
        }

        private static ProgressTrackState FindTrack(IEnumerable<ProgressTrackState> entries, ushort dataId)
        { if (entries != null) foreach (ProgressTrackState entry in entries) if (entry != null && entry.dataId == dataId) return entry; return null; }
        private static ReputationState FindReputation(IEnumerable<ReputationState> entries, ushort dataId)
        { if (entries != null) foreach (ReputationState entry in entries) if (entry != null && entry.factionDataId == dataId) return entry; return null; }
        private static HeatState FindHeat(IEnumerable<HeatState> entries, ushort dataId)
        { if (entries != null) foreach (HeatState entry in entries) if (entry != null && entry.jurisdictionDataId == dataId) return entry; return null; }
        private static ushort[] ToSortedArray(HashSet<ushort> values) { ushort[] result = new ushort[values.Count]; values.CopyTo(result); Array.Sort(result); return result; }
    }
}
