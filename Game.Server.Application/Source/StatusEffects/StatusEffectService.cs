using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Actors;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Combat;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;

namespace Game.Server.Application.StatusEffects
{
    /// <summary>
    /// Authoritative temporary-status service adapted from uMMORPG's shared status runtime.
    /// There is no status polling loop. Apply/remove/refresh mutations emit typed events;
    /// timed expiry/periodic effects are scheduled by the GameServer integration layer.
    /// </summary>
    public sealed class StatusEffectService
    {
        private readonly GameplayContentCatalog _content;

        public StatusEffectService(GameplayContentCatalog content) =>
            _content = content ?? throw new ArgumentNullException(nameof(content));

        public StatusEffectsSnapshot GetSnapshot(PlayerRuntime runtime)
        {
            if (runtime == null)
                return null;

            CharacterStatusEffectsState state = runtime.CaptureStatusEffects();
            StatusEffectInstanceState[] active = state.Snapshot();
            var views = new List<StatusEffectView>(active.Length);
            for (int i = 0; i < active.Length; ++i)
            {
                StatusEffectInstanceState instance = active[i];
                if (!_content.TryGetStatusEffect(instance.DefinitionId, out StatusEffectDefinition definition))
                    continue;

                views.Add(new StatusEffectView
                {
                    wireId = definition.wireId,
                    definitionId = instance.DefinitionId,
                    displayName = definition.displayName ?? instance.DefinitionId,
                    classification = (byte)definition.classification,
                    stacks = instance.Stacks,
                    endTime = instance.EndTime,
                    presentationId = definition.presentationId,
                });
            }

            return new StatusEffectsSnapshot
            {
                contentRevision = _content.Revision,
                statusRevision = state.Revision,
                effects = views.ToArray(),
            };
        }

        public StatusEffectOperationResult Apply(
            PlayerRuntime runtime,
            string definitionId,
            long sourceCharacterIdValue,
            int requestedStacks,
            double now,
            StatusEffectChangeReason reason)
        {
            if (runtime == null)
                return Fail(StatusEffectOperationStatus.CharacterUnavailable, "character runtime unavailable");
            if (requestedStacks < 1)
                return Fail(StatusEffectOperationStatus.InvalidStacks, "status stacks must be positive");
            if (!IsValidTime(now))
                return Fail(StatusEffectOperationStatus.StaleState, "server time is invalid");
            if (!_content.TryGetStatusEffect(definitionId, out StatusEffectDefinition definition))
                return Fail(StatusEffectOperationStatus.UnknownDefinition, "status definition is unavailable");
            if ((definition.allowedActors & GameplayActorAccessMask.Player) == 0)
                return Fail(StatusEffectOperationStatus.ActorNotAllowed, "status is not allowed on players");
            if (!IsAlive(runtime))
                return Fail(StatusEffectOperationStatus.TargetDead, "status target is dead");

            CharacterStatusEffectsState current = runtime.CaptureStatusEffects();
            StatusEffectInstanceState[] effects = current.Snapshot();
            int index = FindIndex(effects, definition.definitionId);
            if (index >= 0 && definition.stackingPolicy == StatusEffectStackingPolicy.IgnoreWhileActive)
                return Fail(StatusEffectOperationStatus.IgnoredWhileActive, "status is already active");

            int stacks = Clamp(requestedStacks, 1, definition.maximumStacks);
            double endTime = now + Math.Max(0.05d, definition.durationSeconds);
            StatusEffectChangeKind kind;
            bool resetPeriodicClock;

            if (index >= 0)
            {
                StatusEffectInstanceState previous = effects[index];
                int nextStacks;
                switch (definition.stackingPolicy)
                {
                    case StatusEffectStackingPolicy.AddStacksAndRefresh:
                        nextStacks = Clamp(previous.Stacks + stacks, 1, definition.maximumStacks);
                        kind = nextStacks != previous.Stacks
                            ? StatusEffectChangeKind.StacksChanged
                            : StatusEffectChangeKind.Refreshed;
                        resetPeriodicClock = false;
                        break;

                    case StatusEffectStackingPolicy.Replace:
                        nextStacks = stacks;
                        kind = StatusEffectChangeKind.Replaced;
                        resetPeriodicClock = true;
                        break;

                    default:
                        nextStacks = Math.Max(previous.Stacks, stacks);
                        kind = nextStacks != previous.Stacks
                            ? StatusEffectChangeKind.StacksChanged
                            : StatusEffectChangeKind.Refreshed;
                        resetPeriodicClock = false;
                        break;
                }

                effects[index] = new StatusEffectInstanceState(
                    definition.definitionId,
                    nextStacks,
                    endTime,
                    sourceCharacterIdValue);
            }
            else
            {
                var expanded = new StatusEffectInstanceState[effects.Length + 1];
                if (effects.Length > 0)
                    Array.Copy(effects, expanded, effects.Length);
                expanded[effects.Length] = new StatusEffectInstanceState(
                    definition.definitionId,
                    stacks,
                    endTime,
                    sourceCharacterIdValue);
                effects = expanded;
                index = effects.Length - 1;
                kind = StatusEffectChangeKind.Applied;
                resetPeriodicClock = true;
            }

            StatusEffectMultipliers multipliers = StatusEffectMultipliers.Identity;
            StatusStatModifierState statModifiers = BuildStatModifiers(effects);
            long nextRevision = current.Revision + 1;
            StatusEffectInstanceState changed = effects[index];
            var next = new CharacterStatusEffectsState(nextRevision, effects, multipliers, statModifiers, BuildControlMask(effects));
            var change = new StatusEffectChange(
                runtime.CharacterId,
                nextRevision,
                kind,
                changed.DefinitionId,
                changed.Stacks,
                changed.EndTime,
                changed.SourceCharacterIdValue,
                reason,
                resetPeriodicClock);

            return runtime.TryCommitStatusEffects(current.Revision, next, change)
                ? StatusEffectOperationResult.Succeeded()
                : Fail(StatusEffectOperationStatus.StaleState, "status state changed concurrently");
        }

        public StatusEffectOperationResult Remove(
            PlayerRuntime runtime,
            string definitionId,
            StatusEffectChangeReason reason,
            StatusEffectChangeKind kind = StatusEffectChangeKind.Removed)
        {
            if (runtime == null)
                return Fail(StatusEffectOperationStatus.CharacterUnavailable, "character runtime unavailable");

            CharacterStatusEffectsState current = runtime.CaptureStatusEffects();
            StatusEffectInstanceState[] effects = current.Snapshot();
            int index = FindIndex(effects, definitionId);
            if (index < 0)
                return Fail(StatusEffectOperationStatus.NotActive, "status is not active");

            StatusEffectInstanceState removed = effects[index];
            var nextEffects = new StatusEffectInstanceState[effects.Length - 1];
            if (index > 0)
                Array.Copy(effects, 0, nextEffects, 0, index);
            if (index + 1 < effects.Length)
                Array.Copy(effects, index + 1, nextEffects, index, effects.Length - index - 1);

            long nextRevision = current.Revision + 1;
            var next = new CharacterStatusEffectsState(
                nextRevision,
                nextEffects,
                StatusEffectMultipliers.Identity,
                BuildStatModifiers(nextEffects),
                BuildControlMask(nextEffects));
            var change = new StatusEffectChange(
                runtime.CharacterId,
                nextRevision,
                kind,
                removed.DefinitionId,
                0,
                0d,
                removed.SourceCharacterIdValue,
                reason,
                false);

            return runtime.TryCommitStatusEffects(current.Revision, next, change)
                ? StatusEffectOperationResult.Succeeded()
                : Fail(StatusEffectOperationStatus.StaleState, "status state changed concurrently");
        }

        public bool ExpireIfDue(PlayerRuntime runtime, string definitionId, double now)
        {
            if (runtime == null || !IsValidTime(now))
                return false;
            CharacterStatusEffectsState state = runtime.CaptureStatusEffects();
            if (!state.TryGet(definitionId, out StatusEffectInstanceState active) || now < active.EndTime)
                return false;
            return Remove(
                runtime,
                definitionId,
                StatusEffectChangeReason.Expired,
                StatusEffectChangeKind.Expired).Success;
        }

        public int ClearOnDeath(PlayerRuntime runtime)
        {
            if (runtime == null)
                return 0;

            int removed = 0;
            StatusEffectInstanceState[] active = runtime.CaptureStatusEffects().Snapshot();
            for (int i = 0; i < active.Length; ++i)
            {
                StatusEffectInstanceState instance = active[i];
                if (!_content.TryGetStatusEffect(instance.DefinitionId, out StatusEffectDefinition definition) ||
                    definition.removeOnDeath)
                {
                    if (Remove(
                            runtime,
                            instance.DefinitionId,
                            StatusEffectChangeReason.Death,
                            StatusEffectChangeKind.ClearedOnDeath).Success)
                    {
                        removed++;
                    }
                }
            }
            return removed;
        }

        /// <summary>
        /// Called only after a validated content revision activates. Unknown effects are
        /// removed and aggregate multipliers are rebuilt; there is no periodic scan.
        /// </summary>
        public int ReconcileDefinitions(PlayerRuntime runtime)
        {
            if (runtime == null)
                return 0;

            int changes = 0;
            StatusEffectInstanceState[] active = runtime.CaptureStatusEffects().Snapshot();
            for (int i = 0; i < active.Length; ++i)
            {
                if (!_content.TryGetStatusEffect(active[i].DefinitionId, out _))
                {
                    if (Remove(
                            runtime,
                            active[i].DefinitionId,
                            StatusEffectChangeReason.ContentRevision,
                            StatusEffectChangeKind.Reconciled).Success)
                    {
                        changes++;
                    }
                }
            }

            // Existing IDs are preserved by compatible hot reload. Refresh an active
            // state once so multiplier tuning applies immediately even when membership
            // itself did not change.
            CharacterStatusEffectsState current = runtime.CaptureStatusEffects();
            StatusEffectInstanceState[] effects = current.Snapshot();
            StatusStatModifierState rebuilt = BuildStatModifiers(effects);

            // Status content changes are intentionally revisioned even if only a numeric
            // modifier changed; this is an infrequent content-activation path.
            long nextRevision = current.Revision + 1;
            var next = new CharacterStatusEffectsState(
                nextRevision,
                effects,
                StatusEffectMultipliers.Identity,
                rebuilt,
                BuildControlMask(effects));
            var change = new StatusEffectChange(
                runtime.CharacterId,
                nextRevision,
                StatusEffectChangeKind.Reconciled,
                string.Empty,
                0,
                0d,
                0,
                StatusEffectChangeReason.ContentRevision,
                false);
            if (runtime.TryCommitStatusEffects(current.Revision, next, change))
                changes++;
            return changes;
        }

        public bool TryGetPeriodicDefinition(string definitionId, out StatusEffectDefinition definition)
        {
            return _content.TryGetStatusEffect(definitionId, out definition) &&
                   definition != null && definition.HasPeriodicEffect;
        }

        private ulong BuildControlMask(StatusEffectInstanceState[] active)
        {
            ulong mask = 0UL;
            for (int i = 0; i < active.Length; ++i)
            {
                if (!_content.TryGetStatusEffect(active[i].DefinitionId, out StatusEffectDefinition definition))
                    continue;
                GameplayEffectDefinition[] effects = definition.effects ?? Array.Empty<GameplayEffectDefinition>();
                for (int e = 0; e < effects.Length; ++e)
                {
                    GameplayEffectDefinition effect = effects[e];
                    if (effect == null || effect.kind != GameplayEffectKind.Control ||
                        effect.timing != GameplayEffectTiming.WhileActive || effect.controlType == ControlEffectType.None)
                        continue;
                    int bit = (int)effect.controlType;
                    if (bit > 0 && bit < 64)
                        mask |= 1UL << bit;
                }
            }
            return mask;
        }

        private StatusStatModifierState BuildStatModifiers(StatusEffectInstanceState[] active)
        {
            var aggregate = new Dictionary<string, MutableStatModifier>(StringComparer.Ordinal);
            for (int i = 0; i < active.Length; ++i)
            {
                if (!_content.TryGetStatusEffect(active[i].DefinitionId, out StatusEffectDefinition definition))
                    continue;

                int stacks = Math.Max(1, (int)active[i].Stacks);
                GameplayEffectDefinition[] effects = definition.effects ?? Array.Empty<GameplayEffectDefinition>();
                for (int e = 0; e < effects.Length; ++e)
                {
                    GameplayEffectDefinition effect = effects[e];
                    if (effect == null ||
                        effect.kind != GameplayEffectKind.StatModifier ||
                        effect.timing != GameplayEffectTiming.WhileActive ||
                        string.IsNullOrWhiteSpace(effect.statId))
                        continue;

                    float value = effect.minValue;
                    if (!aggregate.TryGetValue(effect.statId, out MutableStatModifier current))
                        current = MutableStatModifier.Identity;

                    switch (effect.statOperation)
                    {
                        case StatModifierOperation.FlatAdd:
                            current.FlatAdd += value * stacks;
                            break;
                        case StatModifierOperation.PercentAdd:
                            current.PercentAdd += value * stacks;
                            break;
                        case StatModifierOperation.Multiply:
                            current.Multiply *= PowClamped(value, stacks);
                            break;
                        case StatModifierOperation.Override:
                            // Definitions are iterated in stable active-state order; a later
                            // active definition is the deterministic override winner.
                            current.HasOverride = true;
                            current.OverrideValue = value;
                            break;
                    }

                    aggregate[effect.statId] = current;
                }

                // Status tags participate in the same weakness/resistance response system as
                // equipment/material tags. Precompute them on status changes, not per hit.
                string[] tags = definition.tags ?? Array.Empty<string>();
                DamageResponseRuleDefinition[] responses =
                    (_content.GetCombatRules() ?? new CombatRulesDefinition()).damageResponses
                    ?? Array.Empty<DamageResponseRuleDefinition>();
                for (int r = 0; r < responses.Length; ++r)
                {
                    DamageResponseRuleDefinition response = responses[r];
                    if (response == null || string.IsNullOrWhiteSpace(response.targetTag) ||
                        !ContainsTag(tags, response.targetTag))
                        continue;

                    string multiplierId = CombatDerivedStatIds.DamageResponseMultiplier(response.damageTypeId);
                    string flatId = CombatDerivedStatIds.DamageResponseFlat(response.damageTypeId);

                    if (!aggregate.TryGetValue(multiplierId, out MutableStatModifier responseMultiplier))
                        responseMultiplier = MutableStatModifier.Identity;
                    responseMultiplier.Multiply *= PowClamped(response.multiplier, stacks);
                    aggregate[multiplierId] = responseMultiplier;

                    if (response.flatAdjustment != 0f)
                    {
                        if (!aggregate.TryGetValue(flatId, out MutableStatModifier responseFlat))
                            responseFlat = MutableStatModifier.Identity;
                        responseFlat.FlatAdd += response.flatAdjustment * stacks;
                        aggregate[flatId] = responseFlat;
                    }
                }
            }

            if (aggregate.Count == 0)
                return StatusStatModifierState.Empty;

            var entries = new StatusStatModifierEntry[aggregate.Count];
            int index = 0;
            foreach (KeyValuePair<string, MutableStatModifier> pair in aggregate)
            {
                MutableStatModifier value = pair.Value;
                entries[index++] = new StatusStatModifierEntry(
                    pair.Key,
                    value.FlatAdd,
                    value.PercentAdd,
                    value.Multiply,
                    value.HasOverride,
                    value.OverrideValue);
            }
            Array.Sort(entries, (a, b) => string.CompareOrdinal(a.StatId, b.StatId));
            return new StatusStatModifierState(entries);
        }

        private struct MutableStatModifier
        {
            public float FlatAdd;
            public float PercentAdd;
            public float Multiply;
            public bool HasOverride;
            public float OverrideValue;

            public static MutableStatModifier Identity => new MutableStatModifier { Multiply = 1f };
        }

        private static bool ContainsTag(string[] tags, string target)
        {
            for (int i = 0; i < tags.Length; ++i)
                if (string.Equals(tags[i], target, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static float PowClamped(float value, int stacks)
        {
            double result = Math.Pow(Math.Max(0f, value), Math.Max(1, stacks));
            if (double.IsNaN(result) || result <= 0d) return 0f;
            if (double.IsInfinity(result) || result > 1000d) return 1000f;
            return (float)result;
        }

        private static bool IsAlive(PlayerRuntime runtime)
        {
            return runtime.TryGetCharacterResource(
                       CharacterResourceId.Health,
                       out _,
                       out CharacterResourceState health) &&
                   health.Enabled && health.Current > health.Minimum;
        }

        private static int FindIndex(StatusEffectInstanceState[] effects, string definitionId)
        {
            for (int i = 0; i < effects.Length; ++i)
                if (string.Equals(effects[i].DefinitionId, definitionId, StringComparison.Ordinal))
                    return i;
            return -1;
        }



        private static int Clamp(int value, int minimum, int maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;

        private static bool IsValidTime(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

        private static StatusEffectOperationResult Fail(StatusEffectOperationStatus status, string error) =>
            StatusEffectOperationResult.Failed(status, error);
    }
}
