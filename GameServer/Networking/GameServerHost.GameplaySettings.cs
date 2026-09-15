using Game.GameServer.Runtime;
using Game.Shared.Abilities;
using Game.Shared.Content;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private bool _hasCachedGameplaySettingsSnapshot;
    private long _cachedGameplaySettingsRevision = long.MinValue;
    private GameplaySettingsSnapshotMessage _cachedGameplaySettingsSnapshot;

    private void RegisterGameplaySettingsRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(
            handlers,
            GameplaySettingsRequestTypes.Snapshot,
            HandleGameplaySettingsSnapshotRequest);
    }

    private void HandleGameplaySettingsSnapshotRequest(
        ClientSession session,
        uint requestId,
        NetDataReader reader)
    {
        if (!session.Ready || session.Entity == null)
        {
            SendResponse(
                session,
                requestId,
                GameplaySettingsSnapshotMessage.Failed("character is not in world"));
            return;
        }

        SendResponse(session, requestId, BuildGameplaySettingsSnapshot());
    }

    private GameplaySettingsSnapshotMessage BuildGameplaySettingsSnapshot()
    {
        long contentRevision = _runtime.Content.Revision;
        if (_hasCachedGameplaySettingsSnapshot && _cachedGameplaySettingsRevision == contentRevision)
            return _cachedGameplaySettingsSnapshot;

        MovementRulesDefinition movement =
            _runtime.Content.GetMovementRules() ?? new MovementRulesDefinition();
        CombatRulesDefinition combat =
            _runtime.Content.GetCombatRules() ?? new CombatRulesDefinition();
        CharacterResourceDefinition[] resources =
            _runtime.Content.GetResources() ?? Array.Empty<CharacterResourceDefinition>();
        AbilityDefinition[] abilities =
            _runtime.Content.GetAbilities() ?? Array.Empty<AbilityDefinition>();
        StatusEffectDefinition[] statuses =
            _runtime.Content.GetStatusEffects() ?? Array.Empty<StatusEffectDefinition>();
        ItemDefinition[] items =
            _runtime.Content.GetItems() ?? Array.Empty<ItemDefinition>();
        EquipmentSlotDefinition[] equipmentSlots =
            _runtime.Content.GetEquipmentSlotsOrdered() ?? Array.Empty<EquipmentSlotDefinition>();

        var rates = new GameplayResourceRateWire[resources.Length];
        for (int i = 0; i < resources.Length; ++i)
        {
            rates[i] = new GameplayResourceRateWire
            {
                resourceId = (ushort)resources[i].id,
                displayName = resources[i].displayName ?? resources[i].id.ToString(),
                minimum = resources[i].minimum,
                replication = (byte)resources[i].replication,
                ratePerSecond = resources[i].ratePerSecond,
            };
        }

        var abilityRefs = new GameplayAbilityReferenceWire[abilities.Length];
        for (int i = 0; i < abilities.Length; ++i)
        {
            AbilityDefinition ability = abilities[i];
            abilityRefs[i] = new GameplayAbilityReferenceWire
            {
                wireId = ability.wireId,
                definitionId = ability.definitionId ?? string.Empty,
                presentationId = ability.presentationId,
                deliveryType = (byte)ability.deliveryType,
                projectileMode = (byte)ability.visibleProjectileMode,
                projectilePresentationId = ability.projectilePresentationId,
                castTimeSeconds = ability.castTimeSeconds,
                cooldownSeconds = ability.cooldownSeconds,
            };
        }

        var statusRefs = new GameplayStatusReferenceWire[statuses.Length];
        for (int i = 0; i < statuses.Length; ++i)
        {
            StatusEffectDefinition status = statuses[i];
            statusRefs[i] = new GameplayStatusReferenceWire
            {
                wireId = status.wireId,
                definitionId = status.definitionId ?? string.Empty,
                displayName = status.displayName ?? status.definitionId ?? string.Empty,
                classification = (byte)status.classification,
                presentationId = status.presentationId,
            };
        }

        var slotRefs = new GameplayEquipmentSlotReferenceWire[equipmentSlots.Length];
        for (int i = 0; i < equipmentSlots.Length; ++i)
        {
            EquipmentSlotDefinition slot = equipmentSlots[i];
            slotRefs[i] = new GameplayEquipmentSlotReferenceWire
            {
                dataId = slot.dataId,
                slotId = slot.slotId ?? string.Empty,
                displayName = slot.displayName ?? slot.slotId ?? string.Empty,
                order = slot.order,
            };
        }

        var itemRefs = new GameplayItemReferenceWire[items.Length];
        for (int i = 0; i < items.Length; ++i)
        {
            ItemDefinition item = items[i];
            string[] allowed = item.allowedEquipmentSlots ?? Array.Empty<string>();
            var allowedIds = new ushort[allowed.Length];
            int allowedCount = 0;
            for (int j = 0; j < allowed.Length; ++j)
            {
                if (_runtime.Content.TryGetEquipmentSlot(allowed[j], out EquipmentSlotDefinition slot) && slot.dataId != 0)
                    allowedIds[allowedCount++] = slot.dataId;
            }
            if (allowedCount != allowedIds.Length)
                Array.Resize(ref allowedIds, allowedCount);

            itemRefs[i] = new GameplayItemReferenceWire
            {
                dataId = item.dataId,
                definitionId = item.definitionId ?? string.Empty,
                displayName = item.displayName ?? item.definitionId ?? string.Empty,
                presentationId = item.presentationId,
                maxDurability = item.maxDurability,
                unitWeight = item.weight,
                canUse = item.consumeQuantity > 0 && (item.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length > 0,
                consumeQuantity = item.consumeQuantity,
                allowedSlotDataIds = allowedIds,
            };
        }

        _cachedGameplaySettingsSnapshot = new GameplaySettingsSnapshotMessage
        {
            success = true,
            error = string.Empty,
            revision = contentRevision,
            moveSpeed = movement.moveSpeed,
            sprintSpeed = movement.sprintSpeed,
            gravity = movement.gravity,
            jumpSpeed = movement.jumpSpeed,
            basicAttackInterval = BasicAttackCadenceTiming.Clamp(combat.basicAttackInterval),
            resourceRates = rates,
            abilities = abilityRefs,
            statuses = statusRefs,
            items = itemRefs,
            equipmentSlots = slotRefs,
        };
        _cachedGameplaySettingsRevision = contentRevision;
        _hasCachedGameplaySettingsSnapshot = true;
        return _cachedGameplaySettingsSnapshot;
    }

    private void BroadcastGameplaySettingsDelta(
        GameplayContentSnapshot previous,
        GameplayContentSnapshot current)
    {
        if (current == null)
            return;

        bool referenceCatalogChanged = GameplayReferenceCatalogChanged(previous, current);
        GameplaySettingsSnapshotMessage snapshot = referenceCatalogChanged
            ? BuildGameplaySettingsSnapshot()
            : default;
        GameplaySettingsDeltaMessage delta = referenceCatalogChanged
            ? default
            : BuildGameplaySettingsDelta(previous, current);

        foreach (ClientSession session in _sessions.Values)
        {
            if (!IsCurrent(session) || !session.Ready || session.Entity == null)
                continue;

            if (referenceCatalogChanged)
            {
                // Static reference edits are rare. Refresh the compact client reference
                // catalog once so subsequent item/status/resource payloads remain ID-only.
                SendClientMessage(
                    session,
                    GameplaySettingsMessageTypes.Snapshot,
                    snapshot,
                    DeliveryMethod.ReliableOrdered);
            }
            else
            {
                SendClientMessage(
                    session,
                    GameplaySettingsMessageTypes.Delta,
                    delta,
                    DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private static bool GameplayReferenceCatalogChanged(
        GameplayContentSnapshot previous,
        GameplayContentSnapshot current)
    {
        if (previous == null || current == null)
            return true;

        return EquipmentSlotReferencesChanged(
                   previous.equipmentSlots ?? Array.Empty<EquipmentSlotDefinition>(),
                   current.equipmentSlots ?? Array.Empty<EquipmentSlotDefinition>()) ||
               ItemReferencesChanged(
                   previous.items ?? Array.Empty<ItemDefinition>(),
                   current.items ?? Array.Empty<ItemDefinition>()) ||
               AbilityReferencesChanged(
                   previous.abilities ?? Array.Empty<AbilityDefinition>(),
                   current.abilities ?? Array.Empty<AbilityDefinition>()) ||
               StatusReferencesChanged(
                   previous.statusEffects ?? Array.Empty<StatusEffectDefinition>(),
                   current.statusEffects ?? Array.Empty<StatusEffectDefinition>());
    }

    private static bool EquipmentSlotReferencesChanged(
        EquipmentSlotDefinition[] before,
        EquipmentSlotDefinition[] after)
    {
        if (before.Length != after.Length)
            return true;
        for (int i = 0; i < before.Length; ++i)
        {
            EquipmentSlotDefinition a = before[i];
            EquipmentSlotDefinition b = FindEquipmentSlot(after, a?.slotId);
            if (a == null || b == null ||
                a.dataId != b.dataId ||
                a.order != b.order ||
                !string.Equals(a.slotId, b.slotId, StringComparison.Ordinal) ||
                !string.Equals(a.displayName, b.displayName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool ItemReferencesChanged(ItemDefinition[] before, ItemDefinition[] after)
    {
        if (before.Length != after.Length)
            return true;
        for (int i = 0; i < before.Length; ++i)
        {
            ItemDefinition a = before[i];
            ItemDefinition b = FindItemDefinition(after, a?.definitionId);
            if (a == null || b == null ||
                a.dataId != b.dataId ||
                a.presentationId != b.presentationId ||
                a.maxDurability != b.maxDurability ||
                a.consumeQuantity != b.consumeQuantity ||
                !SameFloat(a.weight, b.weight) ||
                !string.Equals(a.definitionId, b.definitionId, StringComparison.Ordinal) ||
                !string.Equals(a.displayName, b.displayName, StringComparison.Ordinal) ||
                !SameOrdinalSet(a.allowedEquipmentSlots, b.allowedEquipmentSlots) ||
                HasUseBehavior(a) != HasUseBehavior(b))
                return true;
        }
        return false;
    }

    private static bool AbilityReferencesChanged(AbilityDefinition[] before, AbilityDefinition[] after)
    {
        if (before.Length != after.Length)
            return true;
        for (int i = 0; i < before.Length; ++i)
        {
            AbilityDefinition a = before[i];
            AbilityDefinition b = FindAbilityDefinition(after, a?.definitionId);
            if (a == null || b == null ||
                a.wireId != b.wireId ||
                a.presentationId != b.presentationId ||
                a.deliveryType != b.deliveryType ||
                a.visibleProjectileMode != b.visibleProjectileMode ||
                a.projectilePresentationId != b.projectilePresentationId ||
                !SameFloat(a.castTimeSeconds, b.castTimeSeconds) ||
                !SameFloat(a.cooldownSeconds, b.cooldownSeconds) ||
                !string.Equals(a.definitionId, b.definitionId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool StatusReferencesChanged(StatusEffectDefinition[] before, StatusEffectDefinition[] after)
    {
        if (before.Length != after.Length)
            return true;
        for (int i = 0; i < before.Length; ++i)
        {
            StatusEffectDefinition a = before[i];
            StatusEffectDefinition b = FindStatusDefinition(after, a?.definitionId);
            if (a == null || b == null ||
                a.wireId != b.wireId ||
                a.classification != b.classification ||
                a.presentationId != b.presentationId ||
                !string.Equals(a.definitionId, b.definitionId, StringComparison.Ordinal) ||
                !string.Equals(a.displayName, b.displayName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static EquipmentSlotDefinition FindEquipmentSlot(
        EquipmentSlotDefinition[] values,
        string slotId)
    {
        for (int i = 0; i < values.Length; ++i)
            if (values[i] != null && string.Equals(values[i].slotId, slotId, StringComparison.Ordinal))
                return values[i];
        return null;
    }

    private static ItemDefinition FindItemDefinition(ItemDefinition[] values, string definitionId)
    {
        for (int i = 0; i < values.Length; ++i)
            if (values[i] != null && string.Equals(values[i].definitionId, definitionId, StringComparison.Ordinal))
                return values[i];
        return null;
    }

    private static AbilityDefinition FindAbilityDefinition(AbilityDefinition[] values, string definitionId)
    {
        for (int i = 0; i < values.Length; ++i)
            if (values[i] != null && string.Equals(values[i].definitionId, definitionId, StringComparison.Ordinal))
                return values[i];
        return null;
    }

    private static StatusEffectDefinition FindStatusDefinition(StatusEffectDefinition[] values, string definitionId)
    {
        for (int i = 0; i < values.Length; ++i)
            if (values[i] != null && string.Equals(values[i].definitionId, definitionId, StringComparison.Ordinal))
                return values[i];
        return null;
    }

    private static bool SameOrdinalSet(string[] left, string[] right)
    {
        string[] a = left ?? Array.Empty<string>();
        string[] b = right ?? Array.Empty<string>();
        if (a.Length != b.Length)
            return false;
        var set = new HashSet<string>(a, StringComparer.Ordinal);
        if (set.Count != a.Length)
            return false;
        for (int i = 0; i < b.Length; ++i)
            if (!set.Contains(b[i] ?? string.Empty))
                return false;
        return true;
    }

    private static bool HasUseBehavior(ItemDefinition item) =>
        item != null &&
        item.consumeQuantity > 0 &&
        (item.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length > 0;

    private static GameplaySettingsDeltaMessage BuildGameplaySettingsDelta(
        GameplayContentSnapshot previous,
        GameplayContentSnapshot current)
    {
        MovementRulesDefinition before = previous?.movement ?? new MovementRulesDefinition();
        MovementRulesDefinition after = current.movement ?? new MovementRulesDefinition();
        CombatRulesDefinition beforeCombat = previous?.combat ?? new CombatRulesDefinition();
        CombatRulesDefinition afterCombat = current.combat ?? new CombatRulesDefinition();

        GameplaySettingsChangeMask mask = GameplaySettingsChangeMask.None;
        if (!SameFloat(before.moveSpeed, after.moveSpeed))
            mask |= GameplaySettingsChangeMask.MoveSpeed;
        if (!SameFloat(before.sprintSpeed, after.sprintSpeed))
            mask |= GameplaySettingsChangeMask.SprintSpeed;
        if (!SameFloat(before.gravity, after.gravity))
            mask |= GameplaySettingsChangeMask.Gravity;
        if (!SameFloat(before.jumpSpeed, after.jumpSpeed))
            mask |= GameplaySettingsChangeMask.JumpSpeed;
        if (!SameFloat(beforeCombat.basicAttackInterval, afterCombat.basicAttackInterval))
            mask |= GameplaySettingsChangeMask.BasicAttackInterval;

        CharacterResourceDefinition[] oldResources =
            previous?.resources ?? Array.Empty<CharacterResourceDefinition>();
        CharacterResourceDefinition[] newResources =
            current.resources ?? Array.Empty<CharacterResourceDefinition>();

        var changedRates = new List<GameplayResourceRateWire>();
        for (int i = 0; i < newResources.Length; ++i)
        {
            CharacterResourceDefinition candidate = newResources[i];
            CharacterResourceDefinition prior = FindResource(oldResources, candidate.id);
            if (prior == null ||
                !SameFloat(prior.ratePerSecond, candidate.ratePerSecond) ||
                prior.minimum != candidate.minimum ||
                prior.replication != candidate.replication ||
                !string.Equals(prior.displayName, candidate.displayName, StringComparison.Ordinal))
            {
                changedRates.Add(new GameplayResourceRateWire
                {
                    resourceId = (ushort)candidate.id,
                    displayName = candidate.displayName ?? candidate.id.ToString(),
                    minimum = candidate.minimum,
                    replication = (byte)candidate.replication,
                    ratePerSecond = candidate.ratePerSecond,
                });
            }
        }

        if (changedRates.Count > 0)
            mask |= GameplaySettingsChangeMask.ResourceRates;

        return new GameplaySettingsDeltaMessage
        {
            revision = current.revision,
            changeMask = (ushort)mask,
            moveSpeed = after.moveSpeed,
            sprintSpeed = after.sprintSpeed,
            gravity = after.gravity,
            jumpSpeed = after.jumpSpeed,
            basicAttackInterval = BasicAttackCadenceTiming.Clamp(afterCombat.basicAttackInterval),
            resourceRates = changedRates.ToArray(),
        };
    }

    private static CharacterResourceDefinition FindResource(
        CharacterResourceDefinition[] resources,
        Game.Shared.Resources.CharacterResourceId id)
    {
        for (int i = 0; i < resources.Length; ++i)
        {
            if (resources[i] != null && resources[i].id == id)
                return resources[i];
        }

        return null;
    }

    private static bool SameFloat(float a, float b) =>
        MathF.Abs(a - b) <= 0.0001f;
}
