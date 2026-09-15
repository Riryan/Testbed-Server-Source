using System;
using System.Collections.Generic;
using Game.Shared.Abilities;
using LiteNetLib.Utils;
using Player.Shared;

namespace Player.Networking
{
    public static class GameplaySettingsRequestTypes
    {
        // System-reserved request range. Full snapshot is requested on world admission
        // and only again if a client detects a revision gap.
        public const ushort Snapshot = 450;
    }

    public static class GameplaySettingsMessageTypes
    {
        // Top-level LiteNetLib message id. Deltas are rare ReliableOrdered updates.
        public const ushort Delta = 50;
        public const ushort Snapshot = 55;
    }

    [Flags]
    public enum GameplaySettingsChangeMask : ushort
    {
        None = 0,
        MoveSpeed = 1 << 0,
        SprintSpeed = 1 << 1,
        Gravity = 1 << 2,
        JumpSpeed = 1 << 3,
        ResourceRates = 1 << 4,
        BasicAttackInterval = 1 << 5,
        AllMovement = MoveSpeed | SprintSpeed | Gravity | JumpSpeed,
        All = AllMovement | ResourceRates | BasicAttackInterval,
    }

    public struct GameplayResourceRateWire : INetSerializable
    {
        public ushort resourceId;
        public string displayName;
        public int minimum;
        public byte replication;
        public float ratePerSecond;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(resourceId);
            writer.Put(displayName ?? string.Empty);
            writer.Put(minimum);
            writer.Put(replication);
            writer.Put(ratePerSecond);
        }

        public void Deserialize(NetDataReader reader)
        {
            resourceId = reader.GetUShort();
            displayName = reader.GetString(128);
            minimum = reader.GetInt();
            replication = reader.GetByte();
            ratePerSecond = reader.GetFloat();
        }
    }

    public struct GameplayAbilityReferenceWire : INetSerializable
    {
        public ushort wireId;
        public string definitionId;
        public ushort presentationId;
        public byte deliveryType;
        public byte projectileMode;
        public ushort projectilePresentationId;
        public float castTimeSeconds;
        public float cooldownSeconds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(wireId);
            writer.Put(definitionId ?? string.Empty);
            writer.Put(presentationId);
            writer.Put(deliveryType);
            writer.Put(projectileMode);
            writer.Put(projectilePresentationId);
            writer.Put(castTimeSeconds);
            writer.Put(cooldownSeconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            wireId = reader.GetUShort();
            definitionId = reader.GetString(192);
            presentationId = reader.GetUShort();
            deliveryType = reader.GetByte();
            projectileMode = reader.GetByte();
            projectilePresentationId = reader.GetUShort();
            castTimeSeconds = reader.GetFloat();
            cooldownSeconds = reader.GetFloat();
        }
    }

    public struct GameplayEquipmentSlotReferenceWire : INetSerializable
    {
        public ushort dataId;
        public string slotId;
        public string displayName;
        public int order;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(dataId);
            writer.Put(slotId ?? string.Empty);
            writer.Put(displayName ?? string.Empty);
            writer.Put(order);
        }

        public void Deserialize(NetDataReader reader)
        {
            dataId = reader.GetUShort();
            slotId = reader.GetString(96);
            displayName = reader.GetString(128);
            order = reader.GetInt();
        }
    }

    public struct GameplayItemReferenceWire : INetSerializable
    {
        public const int MaxAllowedSlots = 16;

        public ushort dataId;
        public string definitionId;
        public string displayName;
        public ushort presentationId;
        public int maxDurability;
        public float unitWeight;
        public bool canUse;
        public int consumeQuantity;
        public ushort[] allowedSlotDataIds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(dataId);
            writer.Put(definitionId ?? string.Empty);
            writer.Put(displayName ?? string.Empty);
            writer.Put(presentationId);
            writer.Put(maxDurability);
            writer.Put(unitWeight);
            writer.Put(canUse);
            writer.Put(consumeQuantity);

            ushort[] source = allowedSlotDataIds ?? Array.Empty<ushort>();
            int count = Math.Min(source.Length, MaxAllowedSlots);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                writer.Put(source[i]);
        }

        public void Deserialize(NetDataReader reader)
        {
            dataId = reader.GetUShort();
            definitionId = reader.GetString(128);
            displayName = reader.GetString(128);
            presentationId = reader.GetUShort();
            maxDurability = reader.GetInt();
            unitWeight = reader.GetFloat();
            canUse = reader.GetBool();
            consumeQuantity = reader.GetInt();

            int count = reader.GetByte();
            if (count > MaxAllowedSlots)
                throw new InvalidOperationException("gameplay item reference has too many equipment slots");
            allowedSlotDataIds = new ushort[count];
            for (int i = 0; i < count; ++i)
                allowedSlotDataIds[i] = reader.GetUShort();
        }
    }

    public struct GameplayStatusReferenceWire : INetSerializable
    {
        public ushort wireId;
        public string definitionId;
        public string displayName;
        public byte classification;
        public ushort presentationId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(wireId);
            writer.Put(definitionId ?? string.Empty);
            writer.Put(displayName ?? string.Empty);
            writer.Put(classification);
            writer.Put(presentationId);
        }

        public void Deserialize(NetDataReader reader)
        {
            wireId = reader.GetUShort();
            definitionId = reader.GetString(192);
            displayName = reader.GetString(192);
            classification = reader.GetByte();
            presentationId = reader.GetUShort();
        }
    }

    public struct GameplaySettingsSnapshotRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct GameplaySettingsSnapshotMessage : INetSerializable
    {
        public const int MaxResourceRates = 128;
        public const int MaxAbilities = 1024;
        public const int MaxStatuses = 1024;
        public const int MaxItems = 4096;
        public const int MaxEquipmentSlots = 255;

        public bool success;
        public string error;
        public long revision;
        public float moveSpeed;
        public float sprintSpeed;
        public float gravity;
        public float jumpSpeed;
        public float basicAttackInterval;
        public GameplayResourceRateWire[] resourceRates;
        public GameplayAbilityReferenceWire[] abilities;
        public GameplayStatusReferenceWire[] statuses;
        public GameplayItemReferenceWire[] items;
        public GameplayEquipmentSlotReferenceWire[] equipmentSlots;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(error ?? string.Empty);
            writer.Put(revision);
            writer.Put(moveSpeed);
            writer.Put(sprintSpeed);
            writer.Put(gravity);
            writer.Put(jumpSpeed);
            writer.Put(basicAttackInterval);

            GameplayResourceRateWire[] source = resourceRates ?? Array.Empty<GameplayResourceRateWire>();
            int count = Math.Min(source.Length, MaxResourceRates);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                source[i].Serialize(writer);

            GameplayAbilityReferenceWire[] abilitySource = abilities ?? Array.Empty<GameplayAbilityReferenceWire>();
            int abilityCount = Math.Min(abilitySource.Length, MaxAbilities);
            writer.Put((ushort)abilityCount);
            for (int i = 0; i < abilityCount; ++i)
                abilitySource[i].Serialize(writer);

            GameplayStatusReferenceWire[] statusSource = statuses ?? Array.Empty<GameplayStatusReferenceWire>();
            int statusCount = Math.Min(statusSource.Length, MaxStatuses);
            writer.Put((ushort)statusCount);
            for (int i = 0; i < statusCount; ++i)
                statusSource[i].Serialize(writer);

            GameplayItemReferenceWire[] itemSource = items ?? Array.Empty<GameplayItemReferenceWire>();
            int itemCount = Math.Min(itemSource.Length, MaxItems);
            writer.Put((ushort)itemCount);
            for (int i = 0; i < itemCount; ++i)
                itemSource[i].Serialize(writer);

            GameplayEquipmentSlotReferenceWire[] slotSource = equipmentSlots ?? Array.Empty<GameplayEquipmentSlotReferenceWire>();
            int slotCount = Math.Min(slotSource.Length, MaxEquipmentSlots);
            writer.Put((byte)slotCount);
            for (int i = 0; i < slotCount; ++i)
                slotSource[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            error = reader.GetString(256);
            revision = reader.GetLong();
            moveSpeed = reader.GetFloat();
            sprintSpeed = reader.GetFloat();
            gravity = reader.GetFloat();
            jumpSpeed = reader.GetFloat();
            basicAttackInterval = reader.GetFloat();

            int count = reader.GetByte();
            if (count > MaxResourceRates)
                throw new InvalidOperationException("gameplay settings resource-rate snapshot is too large");

            resourceRates = new GameplayResourceRateWire[count];
            for (int i = 0; i < count; ++i)
            {
                GameplayResourceRateWire rate = default;
                rate.Deserialize(reader);
                resourceRates[i] = rate;
            }

            int abilityCount = reader.GetUShort();
            if (abilityCount > MaxAbilities)
                throw new InvalidOperationException("gameplay settings ability snapshot is too large");
            abilities = new GameplayAbilityReferenceWire[abilityCount];
            for (int i = 0; i < abilityCount; ++i)
            {
                GameplayAbilityReferenceWire ability = default;
                ability.Deserialize(reader);
                abilities[i] = ability;
            }

            int statusCount = reader.GetUShort();
            if (statusCount > MaxStatuses)
                throw new InvalidOperationException("gameplay settings status snapshot is too large");
            statuses = new GameplayStatusReferenceWire[statusCount];
            for (int i = 0; i < statusCount; ++i)
            {
                GameplayStatusReferenceWire status = default;
                status.Deserialize(reader);
                statuses[i] = status;
            }

            int itemCount = reader.GetUShort();
            if (itemCount > MaxItems)
                throw new InvalidOperationException("gameplay settings item reference snapshot is too large");
            items = new GameplayItemReferenceWire[itemCount];
            for (int i = 0; i < itemCount; ++i)
            {
                GameplayItemReferenceWire item = default;
                item.Deserialize(reader);
                items[i] = item;
            }

            int slotCount = reader.GetByte();
            if (slotCount > MaxEquipmentSlots)
                throw new InvalidOperationException("gameplay settings equipment-slot reference snapshot is too large");
            equipmentSlots = new GameplayEquipmentSlotReferenceWire[slotCount];
            for (int i = 0; i < slotCount; ++i)
            {
                GameplayEquipmentSlotReferenceWire slot = default;
                slot.Deserialize(reader);
                equipmentSlots[i] = slot;
            }
        }

        public static GameplaySettingsSnapshotMessage Failed(string error) =>
            new GameplaySettingsSnapshotMessage
            {
                success = false,
                error = error ?? string.Empty,
                revision = 0,
                moveSpeed = PlayerMovementDefaults.WalkSpeed,
                sprintSpeed = PlayerMovementDefaults.SprintSpeed,
                gravity = PlayerMovementDefaults.Gravity,
                jumpSpeed = PlayerMovementDefaults.JumpSpeed,
                basicAttackInterval = BasicAttackCadenceTiming.StandardInterval,
                resourceRates = Array.Empty<GameplayResourceRateWire>(),
                abilities = Array.Empty<GameplayAbilityReferenceWire>(),
                statuses = Array.Empty<GameplayStatusReferenceWire>(),
                items = Array.Empty<GameplayItemReferenceWire>(),
                equipmentSlots = Array.Empty<GameplayEquipmentSlotReferenceWire>(),
            };
    }

    public struct GameplaySettingsDeltaMessage : INetSerializable
    {
        public const int MaxResourceRates = 128;

        public long revision;
        public ushort changeMask;
        public float moveSpeed;
        public float sprintSpeed;
        public float gravity;
        public float jumpSpeed;
        public float basicAttackInterval;
        public GameplayResourceRateWire[] resourceRates;

        public GameplaySettingsChangeMask ChangeMask =>
            (GameplaySettingsChangeMask)changeMask;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(revision);
            writer.Put(changeMask);

            GameplaySettingsChangeMask mask = ChangeMask;
            if ((mask & GameplaySettingsChangeMask.MoveSpeed) != 0) writer.Put(moveSpeed);
            if ((mask & GameplaySettingsChangeMask.SprintSpeed) != 0) writer.Put(sprintSpeed);
            if ((mask & GameplaySettingsChangeMask.Gravity) != 0) writer.Put(gravity);
            if ((mask & GameplaySettingsChangeMask.JumpSpeed) != 0) writer.Put(jumpSpeed);
            if ((mask & GameplaySettingsChangeMask.BasicAttackInterval) != 0) writer.Put(basicAttackInterval);

            if ((mask & GameplaySettingsChangeMask.ResourceRates) != 0)
            {
                GameplayResourceRateWire[] source = resourceRates ?? Array.Empty<GameplayResourceRateWire>();
                int count = Math.Min(source.Length, MaxResourceRates);
                writer.Put((byte)count);
                for (int i = 0; i < count; ++i)
                    source[i].Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            revision = reader.GetLong();
            changeMask = reader.GetUShort();

            GameplaySettingsChangeMask mask = ChangeMask;
            moveSpeed = (mask & GameplaySettingsChangeMask.MoveSpeed) != 0 ? reader.GetFloat() : 0f;
            sprintSpeed = (mask & GameplaySettingsChangeMask.SprintSpeed) != 0 ? reader.GetFloat() : 0f;
            gravity = (mask & GameplaySettingsChangeMask.Gravity) != 0 ? reader.GetFloat() : 0f;
            jumpSpeed = (mask & GameplaySettingsChangeMask.JumpSpeed) != 0 ? reader.GetFloat() : 0f;
            basicAttackInterval = (mask & GameplaySettingsChangeMask.BasicAttackInterval) != 0
                ? reader.GetFloat()
                : 0f;

            if ((mask & GameplaySettingsChangeMask.ResourceRates) != 0)
            {
                int count = reader.GetByte();
                if (count > MaxResourceRates)
                    throw new InvalidOperationException("gameplay settings resource-rate delta is too large");

                resourceRates = new GameplayResourceRateWire[count];
                for (int i = 0; i < count; ++i)
                {
                    GameplayResourceRateWire rate = default;
                    rate.Deserialize(reader);
                    resourceRates[i] = rate;
                }
            }
            else
            {
                resourceRates = Array.Empty<GameplayResourceRateWire>();
            }
        }
    }

    /// <summary>
    /// Client-side, server-authored tuning snapshot. These values are prediction and
    /// presentation inputs only; the standalone GameServer remains authoritative.
    /// </summary>
    public static class PlayerGameplaySettingsRuntime
    {
        private static readonly Dictionary<ushort, float> ResourceRates =
            new Dictionary<ushort, float>();
        private static readonly Dictionary<ushort, GameplayResourceRateWire> ResourceReferences =
            new Dictionary<ushort, GameplayResourceRateWire>();
        private static readonly Dictionary<string, GameplayAbilityReferenceWire> AbilitiesByDefinition =
            new Dictionary<string, GameplayAbilityReferenceWire>(StringComparer.Ordinal);
        private static readonly Dictionary<ushort, GameplayAbilityReferenceWire> AbilitiesByWireId =
            new Dictionary<ushort, GameplayAbilityReferenceWire>();
        private static readonly Dictionary<ushort, GameplayStatusReferenceWire> StatusesByWireId =
            new Dictionary<ushort, GameplayStatusReferenceWire>();
        private static readonly Dictionary<ushort, GameplayItemReferenceWire> ItemsByDataId =
            new Dictionary<ushort, GameplayItemReferenceWire>();
        private static readonly Dictionary<string, GameplayItemReferenceWire> ItemsByDefinition =
            new Dictionary<string, GameplayItemReferenceWire>(StringComparer.Ordinal);
        private static readonly Dictionary<ushort, GameplayEquipmentSlotReferenceWire> SlotsByDataId =
            new Dictionary<ushort, GameplayEquipmentSlotReferenceWire>();
        private static readonly Dictionary<string, GameplayEquipmentSlotReferenceWire> SlotsBySemanticId =
            new Dictionary<string, GameplayEquipmentSlotReferenceWire>(StringComparer.Ordinal);

        public static long Revision { get; private set; }
        public static float MoveSpeed { get; private set; } = PlayerMovementDefaults.WalkSpeed;
        public static float SprintSpeed { get; private set; } = PlayerMovementDefaults.SprintSpeed;
        public static float Gravity { get; private set; } = PlayerMovementDefaults.Gravity;
        public static float JumpSpeed { get; private set; } = PlayerMovementDefaults.JumpSpeed;
        public static float BasicAttackInterval { get; private set; } = BasicAttackCadenceTiming.StandardInterval;

        public static void Reset()
        {
            Revision = 0;
            MoveSpeed = PlayerMovementDefaults.WalkSpeed;
            SprintSpeed = PlayerMovementDefaults.SprintSpeed;
            Gravity = PlayerMovementDefaults.Gravity;
            JumpSpeed = PlayerMovementDefaults.JumpSpeed;
            BasicAttackInterval = BasicAttackCadenceTiming.StandardInterval;
            ResourceRates.Clear();
            ResourceReferences.Clear();
            AbilitiesByDefinition.Clear();
            AbilitiesByWireId.Clear();
            StatusesByWireId.Clear();
            ItemsByDataId.Clear();
            ItemsByDefinition.Clear();
            SlotsByDataId.Clear();
            SlotsBySemanticId.Clear();
        }

        public static bool ApplySnapshot(GameplaySettingsSnapshotMessage snapshot)
        {
            if (!snapshot.success || snapshot.revision <= 0)
                return false;

            Revision = snapshot.revision;
            MoveSpeed = PositiveOrFallback(snapshot.moveSpeed, PlayerMovementDefaults.WalkSpeed);
            SprintSpeed = Math.Max(
                MoveSpeed,
                PositiveOrFallback(snapshot.sprintSpeed, PlayerMovementDefaults.SprintSpeed));
            Gravity = NonNegativeOrFallback(snapshot.gravity, PlayerMovementDefaults.Gravity);
            JumpSpeed = NonNegativeOrFallback(snapshot.jumpSpeed, PlayerMovementDefaults.JumpSpeed);
            BasicAttackInterval = PositiveOrFallback(
                snapshot.basicAttackInterval,
                BasicAttackCadenceTiming.StandardInterval);

            ResourceRates.Clear();
            ResourceReferences.Clear();
            GameplayResourceRateWire[] rates = snapshot.resourceRates ?? Array.Empty<GameplayResourceRateWire>();
            for (int i = 0; i < rates.Length; ++i)
            {
                ResourceRates[rates[i].resourceId] = NonNegativeOrFallback(rates[i].ratePerSecond, 0f);
                ResourceReferences[rates[i].resourceId] = rates[i];
            }

            AbilitiesByDefinition.Clear();
            AbilitiesByWireId.Clear();
            GameplayAbilityReferenceWire[] abilityRefs = snapshot.abilities ?? Array.Empty<GameplayAbilityReferenceWire>();
            for (int i = 0; i < abilityRefs.Length; ++i)
            {
                GameplayAbilityReferenceWire ability = abilityRefs[i];
                if (ability.wireId == 0 || string.IsNullOrWhiteSpace(ability.definitionId)) continue;
                AbilitiesByDefinition[ability.definitionId] = ability;
                AbilitiesByWireId[ability.wireId] = ability;
            }

            StatusesByWireId.Clear();
            GameplayStatusReferenceWire[] statusRefs = snapshot.statuses ?? Array.Empty<GameplayStatusReferenceWire>();
            for (int i = 0; i < statusRefs.Length; ++i)
                if (statusRefs[i].wireId != 0) StatusesByWireId[statusRefs[i].wireId] = statusRefs[i];

            ItemsByDataId.Clear();
            ItemsByDefinition.Clear();
            GameplayItemReferenceWire[] itemRefs = snapshot.items ?? Array.Empty<GameplayItemReferenceWire>();
            for (int i = 0; i < itemRefs.Length; ++i)
            {
                GameplayItemReferenceWire item = itemRefs[i];
                if (item.dataId == 0 || string.IsNullOrWhiteSpace(item.definitionId)) continue;
                ItemsByDataId[item.dataId] = item;
                ItemsByDefinition[item.definitionId] = item;
            }

            SlotsByDataId.Clear();
            SlotsBySemanticId.Clear();
            GameplayEquipmentSlotReferenceWire[] slotRefs =
                snapshot.equipmentSlots ?? Array.Empty<GameplayEquipmentSlotReferenceWire>();
            for (int i = 0; i < slotRefs.Length; ++i)
            {
                GameplayEquipmentSlotReferenceWire slot = slotRefs[i];
                if (slot.dataId == 0 || string.IsNullOrWhiteSpace(slot.slotId)) continue;
                SlotsByDataId[slot.dataId] = slot;
                SlotsBySemanticId[slot.slotId] = slot;
            }

            return true;
        }

        public static bool ApplyDelta(GameplaySettingsDeltaMessage delta)
        {
            if (delta.revision <= Revision)
                return false;
            if (Revision > 0 && delta.revision != Revision + 1)
                return false;

            GameplaySettingsChangeMask mask = delta.ChangeMask;
            if ((mask & GameplaySettingsChangeMask.MoveSpeed) != 0)
                MoveSpeed = PositiveOrFallback(delta.moveSpeed, MoveSpeed);
            if ((mask & GameplaySettingsChangeMask.SprintSpeed) != 0)
                SprintSpeed = Math.Max(MoveSpeed, PositiveOrFallback(delta.sprintSpeed, SprintSpeed));
            if ((mask & GameplaySettingsChangeMask.Gravity) != 0)
                Gravity = NonNegativeOrFallback(delta.gravity, Gravity);
            if ((mask & GameplaySettingsChangeMask.JumpSpeed) != 0)
                JumpSpeed = NonNegativeOrFallback(delta.jumpSpeed, JumpSpeed);
            if ((mask & GameplaySettingsChangeMask.BasicAttackInterval) != 0)
                BasicAttackInterval = PositiveOrFallback(delta.basicAttackInterval, BasicAttackInterval);

            if ((mask & GameplaySettingsChangeMask.ResourceRates) != 0)
            {
                GameplayResourceRateWire[] rates = delta.resourceRates ?? Array.Empty<GameplayResourceRateWire>();
                for (int i = 0; i < rates.Length; ++i)
                {
                    ResourceRates[rates[i].resourceId] = NonNegativeOrFallback(rates[i].ratePerSecond, 0f);
                    ResourceReferences[rates[i].resourceId] = rates[i];
                }
            }

            Revision = delta.revision;
            return true;
        }

        public static bool TryGetResourceRate(ushort resourceId, out float ratePerSecond) =>
            ResourceRates.TryGetValue(resourceId, out ratePerSecond);

        public static bool TryGetResource(ushort resourceId, out GameplayResourceRateWire resource)
        {
            resource = default;
            return resourceId != 0 && ResourceReferences.TryGetValue(resourceId, out resource);
        }

        public static bool TryGetAbility(string definitionId, out GameplayAbilityReferenceWire ability) =>
            AbilitiesByDefinition.TryGetValue(definitionId ?? string.Empty, out ability);

        public static bool TryGetAbility(ushort wireId, out GameplayAbilityReferenceWire ability)
        {
            ability = default;
            return wireId != 0 && AbilitiesByWireId.TryGetValue(wireId, out ability);
        }

        public static bool TryGetStatus(ushort wireId, out GameplayStatusReferenceWire status)
        {
            status = default;
            return wireId != 0 && StatusesByWireId.TryGetValue(wireId, out status);
        }

        public static bool TryGetItem(ushort dataId, out GameplayItemReferenceWire item)
        {
            item = default;
            return dataId != 0 && ItemsByDataId.TryGetValue(dataId, out item);
        }

        public static bool TryGetItem(string definitionId, out GameplayItemReferenceWire item) =>
            ItemsByDefinition.TryGetValue(definitionId ?? string.Empty, out item);

        public static bool TryGetEquipmentSlot(ushort dataId, out GameplayEquipmentSlotReferenceWire slot)
        {
            slot = default;
            return dataId != 0 && SlotsByDataId.TryGetValue(dataId, out slot);
        }

        public static bool TryGetEquipmentSlot(string slotId, out GameplayEquipmentSlotReferenceWire slot) =>
            SlotsBySemanticId.TryGetValue(slotId ?? string.Empty, out slot);

        public static string[] ResolveEquipmentSlotNames(ushort[] dataIds)
        {
            ushort[] source = dataIds ?? Array.Empty<ushort>();
            if (source.Length == 0)
                return Array.Empty<string>();

            var result = new string[source.Length];
            int count = 0;
            for (int i = 0; i < source.Length; ++i)
            {
                if (!TryGetEquipmentSlot(source[i], out GameplayEquipmentSlotReferenceWire slot) ||
                    string.IsNullOrWhiteSpace(slot.slotId))
                    continue;
                result[count++] = slot.slotId;
            }

            if (count == result.Length)
                return result;
            if (count == 0)
                return Array.Empty<string>();

            Array.Resize(ref result, count);
            return result;
        }

        private static float PositiveOrFallback(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? fallback : value;

        private static float NonNegativeOrFallback(float value, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? fallback : value;
    }
}
