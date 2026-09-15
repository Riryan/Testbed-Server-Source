using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.Stats;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.Resources;

namespace Game.Server.Application.Resources
{
    /// <summary>
    /// Authoritative resource mutation service. There is no update/poll loop here:
    /// callers mutate explicitly, while timed automatic changes are driven by the
    /// GameServer scheduler integration.
    /// </summary>
    public sealed class CharacterResourceService
    {
        private readonly GameplayContentCatalog _content;

        public CharacterResourceService(GameplayContentCatalog content) =>
            _content = content ?? throw new ArgumentNullException(nameof(content));

        public CharacterResourcesState CreateInitialState(
            StatsState stats,
            long persistedRevision,
            PersistedCharacterResource[] persisted)
        {
            var saved = new Dictionary<CharacterResourceId, PersistedCharacterResource>();
            if (persisted != null)
            {
                for (int i = 0; i < persisted.Length; ++i)
                {
                    PersistedCharacterResource item = persisted[i];
                    if (item != null && item.ResourceId != CharacterResourceId.None)
                        saved[item.ResourceId] = item;
                }
            }

            CharacterResourceDefinition[] definitions = _content.GetResources();
            var states = new List<CharacterResourceState>(definitions.Length);
            for (int i = 0; i < definitions.Length; ++i)
            {
                CharacterResourceDefinition definition = definitions[i];
                if (definition == null || !definition.enabled)
                    continue;

                int maximum = ResolveMaximum(definition, stats);
                int current = definition.startAtMaximum ? maximum : definition.startingValue;
                if (saved.TryGetValue(definition.id, out PersistedCharacterResource stored) &&
                    definition.persistence == CharacterResourcePersistenceMode.Character)
                {
                    current = stored.Current;
                }

                current = Clamp(definition, current, maximum);
                states.Add(new CharacterResourceState(
                    definition.id,
                    current,
                    definition.minimum,
                    maximum,
                    true,
                    definition.persistence));
            }

            return new CharacterResourcesState(Math.Max(0, persistedRevision), states);
        }

        public CharacterResourcesSnapshot GetSnapshot(PlayerRuntime runtime)
        {
            CharacterResourcesState state = runtime?.CaptureCharacterResources();
            if (state == null)
                return null;

            CharacterResourceState[] source = state.Snapshot();
            var views = new List<CharacterResourceView>(source.Length);
            for (int i = 0; i < source.Length; ++i)
            {
                CharacterResourceState resource = source[i];
                if (resource.Id == CharacterResourceId.None ||
                    !resource.Enabled ||
                    !_content.TryGetResource(resource.Id, out CharacterResourceDefinition definition) ||
                    definition.replication == CharacterResourceReplicationMode.ServerOnly)
                    continue;

                views.Add(new CharacterResourceView
                {
                    id = resource.Id,
                    displayName = definition.displayName ?? resource.Id.ToString(),
                    current = resource.Current,
                    minimum = resource.Minimum,
                    maximum = resource.Maximum,
                    replication = definition.replication,
                });
            }

            return new CharacterResourcesSnapshot
            {
                contentRevision = _content.Revision,
                resourceRevision = state.Revision,
                resources = views.ToArray(),
            };
        }

        public CharacterResourceOperationResult Add(
            PlayerRuntime runtime,
            CharacterResourceId id,
            int amount,
            CharacterResourceChangeReason reason)
        {
            if (amount <= 0)
                return Fail(CharacterResourceOperationStatus.InvalidAmount, "resource amount must be positive");
            return Change(runtime, id, amount, false, reason);
        }

        public CharacterResourceOperationResult Remove(
            PlayerRuntime runtime,
            CharacterResourceId id,
            int amount,
            CharacterResourceChangeReason reason)
        {
            if (amount <= 0)
                return Fail(CharacterResourceOperationStatus.InvalidAmount, "resource amount must be positive");
            return Change(runtime, id, -amount, false, reason);
        }

        public CharacterResourceOperationResult Spend(
            PlayerRuntime runtime,
            CharacterResourceId id,
            int amount,
            CharacterResourceChangeReason reason = CharacterResourceChangeReason.AbilityCost)
        {
            if (amount <= 0)
                return Fail(CharacterResourceOperationStatus.InvalidAmount, "resource cost must be positive");
            if (!_content.TryGetResource(id, out CharacterResourceDefinition definition))
                return Fail(CharacterResourceOperationStatus.ResourceUnavailable, "resource definition is unavailable");
            if (!definition.allowSpending)
                return Fail(CharacterResourceOperationStatus.NotSpendable, "resource cannot be spent");

            if (runtime == null || !runtime.TryGetCharacterResource(id, out _, out CharacterResourceState current) || !current.Enabled)
                return Fail(CharacterResourceOperationStatus.ResourceUnavailable, "resource is unavailable");
            if ((long)current.Current - amount < current.Minimum)
                return Fail(CharacterResourceOperationStatus.Insufficient, "insufficient resource");

            return Change(runtime, id, -amount, true, reason);
        }

        public CharacterResourceOperationResult Set(
            PlayerRuntime runtime,
            CharacterResourceId id,
            int value,
            CharacterResourceChangeReason reason)
        {
            if (runtime == null || !runtime.TryGetCharacterResource(id, out long resourceRevision, out CharacterResourceState current) || !current.Enabled)
                return Fail(CharacterResourceOperationStatus.ResourceUnavailable, "resource is unavailable");
            if (!_content.TryGetResource(id, out CharacterResourceDefinition definition))
                return Fail(CharacterResourceOperationStatus.ResourceUnavailable, "resource definition is unavailable");

            int nextValue = Clamp(definition, value, current.Maximum);
            if (nextValue == current.Current)
                return CharacterResourceOperationResult.Succeeded();

            var next = new CharacterResourceState(id, nextValue, current.Minimum, current.Maximum, current.Enabled, current.Persistence);
            if (!runtime.TryCommitCharacterResource(resourceRevision, next, reason))
                return Fail(CharacterResourceOperationStatus.StaleState, "resource state changed concurrently");
            return CharacterResourceOperationResult.Succeeded();
        }

        public int ApplyDeathResets(PlayerRuntime runtime) =>
            ApplyLifecycleResets(runtime, respawn: false);

        public int ApplyRespawnResets(PlayerRuntime runtime) =>
            ApplyLifecycleResets(runtime, respawn: true);

        private int ApplyLifecycleResets(PlayerRuntime runtime, bool respawn)
        {
            if (runtime == null)
                return 0;

            int changed = 0;
            CharacterResourceDefinition[] definitions = _content.GetResources();
            for (int i = 0; i < definitions.Length; ++i)
            {
                CharacterResourceDefinition definition = definitions[i];
                if (definition == null || !definition.enabled ||
                    !runtime.TryGetCharacterResource(definition.id, out _, out CharacterResourceState current))
                    continue;

                CharacterResourceResetMode mode = respawn ? definition.onRespawn : definition.onDeath;
                int target;
                switch (mode)
                {
                    case CharacterResourceResetMode.SetToMinimum:
                        target = current.Minimum;
                        break;
                    case CharacterResourceResetMode.SetToStartingValue:
                        target = definition.startAtMaximum
                            ? current.Maximum
                            : Math.Max(current.Minimum, Math.Min(current.Maximum, definition.startingValue));
                        break;
                    case CharacterResourceResetMode.SetToMaximum:
                        target = current.Maximum;
                        break;
                    default:
                        continue;
                }

                int before = current.Current;
                CharacterResourceOperationResult result = Set(
                    runtime,
                    definition.id,
                    target,
                    respawn ? CharacterResourceChangeReason.Respawn : CharacterResourceChangeReason.Death);
                if (result.Success && before != target)
                    changed++;
            }
            return changed;
        }

        public bool RecalculateMaximums(PlayerRuntime runtime, CharacterResourceChangeReason reason = CharacterResourceChangeReason.Administrative)
        {
            if (runtime == null)
                return false;

            PlayerItemSystemsRuntimeSnapshot items = runtime.CapturePlayerItemSystems();
            StatsState stats = items?.Stats;
            bool anyChanged = false;
            CharacterResourceDefinition[] definitions = _content.GetResources();
            for (int i = 0; i < definitions.Length; ++i)
            {
                CharacterResourceDefinition definition = definitions[i];
                if (!runtime.TryGetCharacterResource(definition.id, out long resourceRevision, out CharacterResourceState current))
                    continue;

                int maximum = ResolveMaximum(definition, stats);
                int nextCurrent = Clamp(definition, current.Current, maximum);
                if (maximum == current.Maximum &&
                    nextCurrent == current.Current &&
                    definition.minimum == current.Minimum &&
                    definition.enabled == current.Enabled &&
                    definition.persistence == current.Persistence)
                    continue;

                var next = new CharacterResourceState(
                    current.Id,
                    nextCurrent,
                    definition.minimum,
                    maximum,
                    definition.enabled,
                    definition.persistence);
                if (runtime.TryCommitCharacterResource(resourceRevision, next, reason))
                    anyChanged = true;
            }
            return anyChanged;
        }

        public bool ShouldReplicateToOwner(CharacterResourceId id) =>
            _content.TryGetResource(id, out CharacterResourceDefinition definition) &&
            definition.replication != CharacterResourceReplicationMode.ServerOnly;

        public CharacterResourceDefinition[] GetAutomaticDefinitions() => _content.GetResources();

        public bool ShouldAutomaticallyUpdate(PlayerRuntime runtime, CharacterResourceDefinition definition, bool inCombat)
        {
            if (runtime == null || definition == null || !definition.enabled ||
                definition.updateMode == CharacterResourceUpdateMode.None || definition.ratePerSecond <= 0f)
                return false;
            if (definition.updateCondition == CharacterResourceUpdateCondition.InCombatOnly && !inCombat)
                return false;
            if (definition.updateCondition == CharacterResourceUpdateCondition.OutOfCombatOnly && inCombat)
                return false;

            if (!runtime.TryGetCharacterResource(definition.id, out _, out CharacterResourceState current) || !current.Enabled)
                return false;

            // Preserve the useful uMMORPG Energy rule: passive recovery does not
            // resurrect a dead character, and other regenerating resources pause while
            // Health is empty. The scheduler is re-armed by the actual respawn/health
            // mutation event later; there is no dead-state polling pass.
            if (definition.updateMode == CharacterResourceUpdateMode.Regenerate &&
                runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) &&
                health.Enabled && health.Current <= health.Minimum)
            {
                return false;
            }

            return definition.updateMode == CharacterResourceUpdateMode.Regenerate
                ? current.Current < current.Maximum
                : current.Current > current.Minimum;
        }

        public bool ApplyAutomaticAmount(PlayerRuntime runtime, CharacterResourceDefinition definition, int amount, bool inCombat)
        {
            if (amount <= 0 || !ShouldAutomaticallyUpdate(runtime, definition, inCombat))
                return false;

            CharacterResourceOperationResult result = definition.updateMode == CharacterResourceUpdateMode.Regenerate
                ? Add(runtime, definition.id, amount, CharacterResourceChangeReason.PassiveRecovery)
                : Remove(runtime, definition.id, amount, CharacterResourceChangeReason.PassiveDecay);
            return result.Success;
        }

        private CharacterResourceOperationResult Change(
            PlayerRuntime runtime,
            CharacterResourceId id,
            int signedAmount,
            bool requireFullAmount,
            CharacterResourceChangeReason reason)
        {
            if (runtime == null || !runtime.TryGetCharacterResource(id, out long resourceRevision, out CharacterResourceState current) || !current.Enabled)
                return Fail(CharacterResourceOperationStatus.ResourceUnavailable, "resource is unavailable");
            if (!_content.TryGetResource(id, out CharacterResourceDefinition definition))
                return Fail(CharacterResourceOperationStatus.ResourceUnavailable, "resource definition is unavailable");

            long requestedLong = (long)current.Current + signedAmount;
            int requested = requestedLong > int.MaxValue ? int.MaxValue : requestedLong < int.MinValue ? int.MinValue : (int)requestedLong;
            int nextValue = Clamp(definition, requested, current.Maximum);
            if (requireFullAmount && nextValue != requested)
                return Fail(CharacterResourceOperationStatus.Insufficient, "insufficient resource");
            if (nextValue == current.Current)
                return CharacterResourceOperationResult.Succeeded();

            var next = new CharacterResourceState(id, nextValue, current.Minimum, current.Maximum, current.Enabled, current.Persistence);
            if (!runtime.TryCommitCharacterResource(resourceRevision, next, reason))
                return Fail(CharacterResourceOperationStatus.StaleState, "resource state changed concurrently");
            return CharacterResourceOperationResult.Succeeded();
        }

        private int ResolveMaximum(CharacterResourceDefinition definition, StatsState stats)
        {
            float value = definition.baseMaximum;
            if (!string.IsNullOrWhiteSpace(definition.maximumStatId) && stats != null)
                value = stats.Get(definition.maximumStatId, definition.baseMaximum);
            if (float.IsNaN(value) || float.IsInfinity(value))
                value = definition.baseMaximum;
            long rounded = (long)Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded > int.MaxValue) rounded = int.MaxValue;
            if (rounded < definition.minimum) rounded = definition.minimum;
            return (int)rounded;
        }

        private static int Clamp(CharacterResourceDefinition definition, int value, int maximum)
        {
            maximum = Math.Max(definition.minimum, maximum);
            if (value < definition.minimum) return definition.minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private static CharacterResourceOperationResult Fail(
            CharacterResourceOperationStatus status,
            string error) =>
            CharacterResourceOperationResult.Failed(status, error);

    }
}
