using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Game.Shared.Resources;
using Game.Shared.Actors;
using Game.Shared.StatusEffects;
using Game.Shared.Abilities;
using Game.Shared.Effects;
using Game.Shared.Interactions;

namespace Game.Shared.Content
{
    [Serializable, DataContract]
    public sealed class GameplayContentSnapshot
    {
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "initialCharacterSpawn")] public CharacterSpawnDefinition initialCharacterSpawn;
        [DataMember(Name = "baseInventoryCapacity")] public int baseInventoryCapacity;
        [DataMember(Name = "equipmentSlots")] public EquipmentSlotDefinition[] equipmentSlots;
        [DataMember(Name = "items")] public ItemDefinition[] items;
        [DataMember(Name = "starterItems")] public StarterItemDefinition[] starterItems;
        [DataMember(Name = "resources")] public CharacterResourceDefinition[] resources;
        [DataMember(Name = "movement")] public MovementRulesDefinition movement;
        [DataMember(Name = "combat")] public CombatRulesDefinition combat;
        [DataMember(Name = "statusEffects")] public StatusEffectDefinition[] statusEffects;
        [DataMember(Name = "abilities")] public AbilityDefinition[] abilities;
        [DataMember(Name = "damageTypes")] public DamageTypeDefinition[] damageTypes;

        // Canonical recovery/content-v2 collections. Keep the legacy profession/harvest
        // fields during Harvesting Recovery so old fixtures remain readable while new
        // profession authority uses ProgressTrackDefinition(kind = Profession).
        [DataMember(Name = "progression")] public ProgressionRulesDefinition progression;
        [DataMember(Name = "progressTracks")] public ProgressTrackDefinition[] progressTracks;
        [DataMember(Name = "lootTables")] public LootTableDefinition[] lootTables;
        [DataMember(Name = "recipes")] public RecipeDefinition[] recipes;
        [DataMember(Name = "craftingStations")] public CraftingStationDefinition[] craftingStations;
        [DataMember(Name = "factions")] public FactionDefinition[] factions;
        [DataMember(Name = "factionRelationships")] public FactionRelationshipDefinition[] factionRelationships;
        [DataMember(Name = "jurisdictions")] public JurisdictionDefinition[] jurisdictions;
        [DataMember(Name = "incidents")] public IncidentDefinition[] incidents;

        [DataMember(Name = "professions")] public ProfessionDefinition[] professions;
        [DataMember(Name = "harvestProfiles")] public HarvestProfileDefinition[] harvestProfiles;
    }

    [Serializable, DataContract]
    public sealed class CharacterSpawnDefinition
    {
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
        [DataMember(Name = "positionX")] public float positionX;
        [DataMember(Name = "positionY")] public float positionY;
        [DataMember(Name = "positionZ")] public float positionZ;
        [DataMember(Name = "yawDegrees")] public float yawDegrees;
    }

    [Serializable, DataContract]
    public sealed class EquipmentSlotDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "slotId")] public string slotId;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "order")] public int order;
        [DataMember(Name = "presentationSlotId")] public ushort presentationSlotId;
    }

    [Serializable, DataContract]
    public sealed class StarterItemDefinition
    {
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "quantity")] public int quantity;
    }

    [Serializable, DataContract]
    public sealed class ProfessionDefinition
    {
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "maximumLevel")] public int maximumLevel = 100;
    }


    public enum HarvestRespawnPolicy : byte
    {
        None = 0,
        Timed = 1,
    }

    [Flags]
    public enum HarvestCapability : ushort
    {
        None = 0,
        Hands = 1 << 0,
        Salvage = 1 << 1,
    }

    public enum HarvestToolAccess : byte
    {
        None = 0,
        Equipped = 1,
        Inventory = 2,
        EquippedOrInventory = 3,
    }

    [Serializable, DataContract]
    public sealed class HarvestMethodDefinition
    {
        [DataMember(Name = "capability")] public HarvestCapability capability;
        [DataMember(Name = "toolAccess")] public HarvestToolAccess toolAccess;
        [DataMember(Name = "minimumToolTier")] public byte minimumToolTier;
        [DataMember(Name = "rewardTier")] public byte rewardTier;
        [DataMember(Name = "baseSuccessBasisPoints")] public ushort baseSuccessBasisPoints = 10000;
        [DataMember(Name = "skillBasisPointsPerLevel")] public ushort skillBasisPointsPerLevel;
        [DataMember(Name = "lootTableId")] public string lootTableId = string.Empty;
        [DataMember(Name = "lootTableDataId")] public ushort lootTableDataId;
    }

    [Serializable, DataContract]
    public sealed class HarvestProfileDefinition
    {
        [DataMember(Name = "profileId")] public ushort profileId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "actionId")] public Game.Shared.Interactions.InteractionActionId actionId = Game.Shared.Interactions.InteractionActionId.Harvest;

        // Legacy profession identifier remains readable for older recovery content.
        [DataMember(Name = "professionId")] public string professionId;
        [DataMember(Name = "professionTrackDefinitionId")] public string professionTrackDefinitionId = string.Empty;
        [DataMember(Name = "professionTrackDataId")] public ushort professionTrackDataId;

        [DataMember(Name = "durationSeconds")] public float durationSeconds = 3f;
        [DataMember(Name = "maximumUseDistance")] public float maximumUseDistance = 3.25f;
        [DataMember(Name = "maximumFacingAngle")] public float maximumFacingAngle = 180f;

        // Legacy single-tool/single-table fields remain as a fail-safe compatibility path.
        [DataMember(Name = "requiredEquippedItemTag")] public string requiredEquippedItemTag;
        [DataMember(Name = "lootTableId")] public string lootTableId;
        [DataMember(Name = "professionReward")] public int professionReward = 1;

        [DataMember(Name = "minimumProfessionLevel")] public int minimumProfessionLevel;
        [DataMember(Name = "minimumCharges")] public int minimumCharges = 1;
        [DataMember(Name = "maximumCharges")] public int maximumCharges = 1;
        [DataMember(Name = "emptyDelaySeconds")] public float emptyDelaySeconds;
        [DataMember(Name = "respawnSeconds")] public float respawnSeconds = 30f;
        [DataMember(Name = "respawnPolicy")] public HarvestRespawnPolicy respawnPolicy = HarvestRespawnPolicy.Timed;

        [DataMember(Name = "successProfessionReward")] public int successProfessionReward = 1;
        [DataMember(Name = "failureProfessionReward")] public int failureProfessionReward;
        [DataMember(Name = "consumeChargeOnFailure")] public bool consumeChargeOnFailure;
        [DataMember(Name = "methods")] public HarvestMethodDefinition[] methods = Array.Empty<HarvestMethodDefinition>();
        [DataMember(Name = "presentationId")] public ushort presentationId;
    }

    public enum ItemKind : byte
    {
        None = 0,
        Equipment = 1,
        Consumable = 2,
        Ammo = 3,
        Material = 4,
        Junk = 5,
        Quest = 6,
    }

    public enum EquipmentItemSubtype : byte
    {
        None = 0,
        Weapon = 1,
        Armor = 2,
        Tool = 3,
    }

    public enum ItemUseEffectKind : byte
    {
        None = 0,
        RestoreResource = 1,
        ApplyStatusEffect = 2,
    }

    [Serializable, DataContract]
    public sealed class ItemUseEffectDefinition
    {
        [DataMember(Name = "kind")] public ItemUseEffectKind kind;
        [DataMember(Name = "resourceId")] public CharacterResourceId resourceId;
        [DataMember(Name = "amount")] public int amount;
        [DataMember(Name = "statusEffectId")] public string statusEffectId;
        [DataMember(Name = "stacks")] public int stacks = 1;
    }

    [Serializable, DataContract]
    public sealed class ItemDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "kind")] public ItemKind kind;
        [DataMember(Name = "subtype")] public EquipmentItemSubtype subtype;
        [DataMember(Name = "presentationId")] public ushort presentationId;
        [DataMember(Name = "maxStack")] public int maxStack;
        [DataMember(Name = "weight")] public float weight;
        [DataMember(Name = "maxDurability")] public int maxDurability;
        [DataMember(Name = "tags")] public string[] tags;
        [DataMember(Name = "allowedEquipmentSlots")] public string[] allowedEquipmentSlots;
        [DataMember(Name = "statModifiers")] public StatModifierDefinition[] statModifiers;
        [DataMember(Name = "damageMin")] public float damageMin;
        [DataMember(Name = "damageMax")] public float damageMax;
        [DataMember(Name = "damageTypeId")] public ushort damageTypeId;
        [DataMember(Name = "canCrit")] public bool canCrit = true;
        [DataMember(Name = "armorPenetrationFlat")] public float armorPenetrationFlat;
        [DataMember(Name = "armorPenetrationPercent")] public float armorPenetrationPercent;
        [DataMember(Name = "ammoFamily")] public string ammoFamily;
        [DataMember(Name = "firearmMagazineCapacity")] public int firearmMagazineCapacity;
        [DataMember(Name = "firearmFireMode")] public FirearmFireMode firearmFireMode = FirearmFireMode.SemiAutomatic;
        [DataMember(Name = "firearmRoundsPerTrigger")] public int firearmRoundsPerTrigger;
        [DataMember(Name = "firearmRoundsPerSecond")] public float firearmRoundsPerSecond;
        [DataMember(Name = "firearmBaseHitChance")] public float firearmBaseHitChance = 1f;
        [DataMember(Name = "firearmBloomPerShot")] public float firearmBloomPerShot = 0.035f;
        [DataMember(Name = "firearmMaximumBloom")] public float firearmMaximumBloom = 0.45f;
        [DataMember(Name = "firearmBloomRecoveryPerSecond")] public float firearmBloomRecoveryPerSecond = 0.35f;
        [DataMember(Name = "firearmAimBloomMultiplier")] public float firearmAimBloomMultiplier = 0.65f;
        [DataMember(Name = "basicAttackRange")] public float basicAttackRange;
        [DataMember(Name = "basicAttackInterval")] public float basicAttackInterval;
        [DataMember(Name = "harvestCapabilities")] public HarvestCapability harvestCapabilities;
        [DataMember(Name = "harvestToolTier")] public byte harvestToolTier;
        [DataMember(Name = "consumeQuantity")] public int consumeQuantity;
        [DataMember(Name = "useEffects")] public ItemUseEffectDefinition[] useEffects;
    }


    [Serializable, DataContract]
    public sealed class MovementRulesDefinition
    {
        [DataMember(Name = "moveSpeed")] public float moveSpeed = 3.0f;
        [DataMember(Name = "sprintSpeed")] public float sprintSpeed = 5.95f;
        [DataMember(Name = "gravity")] public float gravity = 24f;
        [DataMember(Name = "jumpSpeed")] public float jumpSpeed = 7f;
    }

    [Serializable, DataContract]
    public sealed class DamageTypeDefinition
    {
        [DataMember(Name = "wireId")] public ushort wireId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "defenseStatId")] public string defenseStatId = "Armor";
    }

    [Serializable, DataContract]
    public sealed class DamageResponseRuleDefinition
    {
        [DataMember(Name = "damageTypeId")] public ushort damageTypeId;
        [DataMember(Name = "targetTag")] public string targetTag = string.Empty;
        [DataMember(Name = "multiplier")] public float multiplier = 1f;
        [DataMember(Name = "flatAdjustment")] public float flatAdjustment;
    }

    public readonly struct DamageResponseAggregate
    {
        public float Multiplier { get; }
        public float FlatAdjustment { get; }

        public DamageResponseAggregate(float multiplier, float flatAdjustment)
        {
            Multiplier = multiplier;
            FlatAdjustment = flatAdjustment;
        }

        public static DamageResponseAggregate Identity => new DamageResponseAggregate(1f, 0f);
    }

    [Serializable, DataContract]
    public sealed class CharacterResourceDefinition
    {
        [DataMember(Name = "id")] public CharacterResourceId id;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "tags")] public CharacterResourceTags tags;
        [DataMember(Name = "enabled")] public bool enabled = true;
        [DataMember(Name = "minimum")] public int minimum;
        [DataMember(Name = "baseMaximum")] public int baseMaximum = 100;
        [DataMember(Name = "maximumStatId")] public string maximumStatId;
        [DataMember(Name = "startAtMaximum")] public bool startAtMaximum = true;
        [DataMember(Name = "startingValue")] public int startingValue = 100;
        [DataMember(Name = "allowSpending")] public bool allowSpending = true;
        [DataMember(Name = "updateMode")] public CharacterResourceUpdateMode updateMode;
        [DataMember(Name = "updateCondition")] public CharacterResourceUpdateCondition updateCondition;
        [DataMember(Name = "ratePerSecond")] public float ratePerSecond;
        [DataMember(Name = "persistence")] public CharacterResourcePersistenceMode persistence = CharacterResourcePersistenceMode.Character;
        [DataMember(Name = "replication")] public CharacterResourceReplicationMode replication = CharacterResourceReplicationMode.OwnerOnly;
        [DataMember(Name = "onDeath")] public CharacterResourceResetMode onDeath;
        [DataMember(Name = "onRespawn")] public CharacterResourceResetMode onRespawn;
    }

    [Serializable, DataContract]
    public sealed class CombatRulesDefinition
    {
        [DataMember(Name = "combatStateSeconds")] public float combatStateSeconds = 5f;
        [DataMember(Name = "minimumDamageAfterDefense")] public int minimumDamageAfterDefense = 1;
        [DataMember(Name = "criticalDamageMultiplier")] public float criticalDamageMultiplier = 2f;
        [DataMember(Name = "blockDamageMultiplier")] public float blockDamageMultiplier;
        [DataMember(Name = "maximumCriticalChance")] public float maximumCriticalChance = 0.75f;
        [DataMember(Name = "maximumBlockChance")] public float maximumBlockChance = 0.75f;
        [DataMember(Name = "basicAttackRange")] public float basicAttackRange = 2f;
        [DataMember(Name = "basicAttackInterval")] public float basicAttackInterval = BasicAttackCadenceTiming.StandardInterval;
        [DataMember(Name = "unarmedBasicAttackDamage")] public int unarmedBasicAttackDamage = 1;
        [DataMember(Name = "unarmedDamageTypeId")] public ushort unarmedDamageTypeId;
        [DataMember(Name = "unarmedLightStaminaCost")] public int unarmedLightStaminaCost;
        [DataMember(Name = "unarmedHeavyStaminaCost")] public int unarmedHeavyStaminaCost;
        [DataMember(Name = "unarmedHeavyDamageMultiplier")] public float unarmedHeavyDamageMultiplier = 1.5f;
        [DataMember(Name = "unarmedMaximumComboStep")] public int unarmedMaximumComboStep = 1;
        [DataMember(Name = "unarmedComboWindowSeconds")] public float unarmedComboWindowSeconds = 2.5f;
        [DataMember(Name = "unarmedComboDamagePerStep")] public float unarmedComboDamagePerStep;
        [DataMember(Name = "minimumDamageResponseMultiplier")] public float minimumDamageResponseMultiplier;
        [DataMember(Name = "maximumDamageResponseMultiplier")] public float maximumDamageResponseMultiplier = 3f;
        [DataMember(Name = "damageResponses")] public DamageResponseRuleDefinition[] damageResponses = Array.Empty<DamageResponseRuleDefinition>();
    }

    [Serializable, DataContract]
    public sealed class AbilityDefinition
    {
        [DataMember(Name = "wireId")] public ushort wireId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "legacyStableId")] public int legacyStableId;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "category")] public AbilityCategory category;
        [DataMember(Name = "allowedActors")] public GameplayActorAccessMask allowedActors = GameplayActorAccessMask.Player;
        [DataMember(Name = "targetMode")] public AbilityTargetMode targetMode = AbilityTargetMode.Entity;
        [DataMember(Name = "targetRelation")] public AbilityTargetRelation targetRelation = AbilityTargetRelation.Hostile;
        [DataMember(Name = "castTimeSeconds")] public float castTimeSeconds;
        [DataMember(Name = "cooldownPolicy")] public AbilityCooldownPolicy cooldownPolicy = AbilityCooldownPolicy.AbilityDefined;
        [DataMember(Name = "cooldownSeconds")] public float cooldownSeconds = 1f;
        [DataMember(Name = "range")] public float range = 5f;
        [DataMember(Name = "resourceId")] public CharacterResourceId resourceId = CharacterResourceId.Mana;
        [DataMember(Name = "resourceCost")] public int resourceCost;
        [DataMember(Name = "movementPolicy")] public AbilityMovementPolicy movementPolicy = AbilityMovementPolicy.Stationary;
        [DataMember(Name = "requiredWeaponTag")] public string requiredWeaponTag;
        [DataMember(Name = "presentationId")] public ushort presentationId;
        [DataMember(Name = "deliveryType")] public AbilityDeliveryType deliveryType = AbilityDeliveryType.Direct;
        [DataMember(Name = "visibleProjectileMode")] public VisibleProjectileMode visibleProjectileMode;
        [DataMember(Name = "projectilePresentationId")] public ushort projectilePresentationId;
        [DataMember(Name = "effects")] public GameplayEffectDefinition[] effects;
    }

    [Serializable, DataContract]
    public sealed class GameplayEffectDefinition
    {
        [DataMember(Name = "kind")] public GameplayEffectKind kind;
        [DataMember(Name = "timing")] public GameplayEffectTiming timing;
        [DataMember(Name = "pulseTiming")] public GameplayPulseTiming pulseTiming = GameplayPulseTiming.Delayed;
        [DataMember(Name = "chance")] public float chance = 1f;
        [DataMember(Name = "minValue")] public float minValue;
        [DataMember(Name = "maxValue")] public float maxValue;
        [DataMember(Name = "minValuePerRank")] public float minValuePerRank;
        [DataMember(Name = "maxValuePerRank")] public float maxValuePerRank;
        [DataMember(Name = "casterStatId")] public string casterStatId = string.Empty;
        [DataMember(Name = "casterStatScale")] public float casterStatScale;
        [DataMember(Name = "damageTypeId")] public ushort damageTypeId;
        [DataMember(Name = "flags")] public GameplayEffectFlags flags;
        [DataMember(Name = "critChanceModifier")] public float critChanceModifier;
        [DataMember(Name = "critMultiplierModifier")] public float critMultiplierModifier;
        [DataMember(Name = "armorPenetrationFlat")] public float armorPenetrationFlat;
        [DataMember(Name = "armorPenetrationPercent")] public float armorPenetrationPercent;
        [DataMember(Name = "resourceId")] public CharacterResourceId resourceId;
        [DataMember(Name = "resourceOperation")] public ResourceChangeOperation resourceOperation = ResourceChangeOperation.Add;
        [DataMember(Name = "statusEffectId")] public string statusEffectId = string.Empty;
        [DataMember(Name = "stacks")] public int stacks = 1;
        [DataMember(Name = "statId")] public string statId = string.Empty;
        [DataMember(Name = "statOperation")] public StatModifierOperation statOperation;
        [DataMember(Name = "controlType")] public ControlEffectType controlType;
        [DataMember(Name = "pulseIntervalSeconds")] public float pulseIntervalSeconds;
        [DataMember(Name = "pulseCount")] public int pulseCount;
    }

    [Serializable, DataContract]
    public sealed class AbilityEffectDefinition
    {
        [DataMember(Name = "kind")] public AbilityEffectKind kind;
        [DataMember(Name = "amount")] public int amount;
        [DataMember(Name = "amountPerRank")] public int amountPerRank;
        [DataMember(Name = "addCasterAttackPower")] public bool addCasterAttackPower;
        [DataMember(Name = "resourceId")] public CharacterResourceId resourceId;
        [DataMember(Name = "statusEffectId")] public string statusEffectId;
        [DataMember(Name = "stacks")] public int stacks = 1;
    }

    [Serializable, DataContract]
    public sealed class StatusEffectDefinition
    {
        [DataMember(Name = "wireId")] public ushort wireId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "legacyStableId")] public int legacyStableId;
        [DataMember(Name = "displayName")] public string displayName;
        [DataMember(Name = "classification")] public StatusEffectClassification classification;
        [DataMember(Name = "allowedActors")] public GameplayActorAccessMask allowedActors = GameplayActorAccessMask.All;
        [DataMember(Name = "durationSeconds")] public float durationSeconds = 5f;
        [DataMember(Name = "stackingPolicy")] public StatusEffectStackingPolicy stackingPolicy;
        [DataMember(Name = "maximumStacks")] public int maximumStacks = 1;
        [DataMember(Name = "removeOnDeath")] public bool removeOnDeath = true;
        [DataMember(Name = "tickIntervalSeconds")] public float tickIntervalSeconds;
        [DataMember(Name = "periodicDamage")] public int periodicDamage;
        [DataMember(Name = "periodicHealing")] public int periodicHealing;
        [DataMember(Name = "damageDealtMultiplier")] public float damageDealtMultiplier = 1f;
        [DataMember(Name = "damageTakenMultiplier")] public float damageTakenMultiplier = 1f;
        [DataMember(Name = "healingReceivedMultiplier")] public float healingReceivedMultiplier = 1f;
        [DataMember(Name = "presentationId")] public ushort presentationId;
        [DataMember(Name = "tags")] public string[] tags = Array.Empty<string>();
        [DataMember(Name = "effects")] public GameplayEffectDefinition[] effects = Array.Empty<GameplayEffectDefinition>();

        public bool HasPeriodicEffect
        {
            get
            {
                if (tickIntervalSeconds > 0f && (periodicDamage > 0 || periodicHealing > 0))
                    return true;
                GameplayEffectDefinition[] values = effects ?? Array.Empty<GameplayEffectDefinition>();
                for (int i = 0; i < values.Length; ++i)
                    if (values[i] != null && values[i].timing == GameplayEffectTiming.Periodic) return true;
                return false;
            }
        }
    }

    [Serializable, DataContract]
    public sealed class StatModifierDefinition
    {
        [DataMember(Name = "statId")] public string statId;
        [DataMember(Name = "additive")] public float additive;
        [DataMember(Name = "multiplier")] public float multiplier = 1f;
    }

    public static class GameplayContentDataIds
    {
        public static void EnsureAssigned(GameplayContentSnapshot snapshot)
        {
            if (snapshot == null) return;
            Assign(snapshot.equipmentSlots, x => x?.slotId, x => x.dataId, (x, id) => x.dataId = id, 0x53u, "equipment slot");
            Assign(snapshot.items, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x49u, "item");
            Assign(snapshot.abilities, x => x?.definitionId, x => x.wireId, (x, id) => x.wireId = id, 0x41u, "ability");
            Assign(snapshot.statusEffects, x => x?.definitionId, x => x.wireId, (x, id) => x.wireId = id, 0x54u, "status effect");
            Assign(snapshot.damageTypes, x => x?.definitionId, x => x.wireId, (x, id) => x.wireId = id, 0x44u, "damage type");
            RecoveryContentDataIds.EnsureAssignedAndResolve(snapshot);
        }

        private static void Assign<T>(T[] values, Func<T, string> getName, Func<T, ushort> getId, Action<T, ushort> setId, uint salt, string kind)
        {
            values = values ?? Array.Empty<T>();
            var used = new Dictionary<ushort, string>();
            var automatic = new List<T>();
            for (int i = 0; i < values.Length; ++i)
            {
                T value = values[i];
                if (value is null) continue;
                string name = getName(value);
                if (string.IsNullOrWhiteSpace(name)) continue;
                ushort id = getId(value);
                if (id == 0) automatic.Add(value);
                else Claim(used, id, name, kind);
            }
            automatic.Sort((a, b) => string.CompareOrdinal(getName(a), getName(b)));
            for (int i = 0; i < automatic.Count; ++i)
            {
                string name = getName(automatic[i]);
                ushort candidate = StableId(name, salt);
                while (used.ContainsKey(candidate)) candidate = candidate == ushort.MaxValue ? (ushort)1 : (ushort)(candidate + 1);
                Claim(used, candidate, name, kind);
                setId(automatic[i], candidate);
            }
        }

        private static void Claim(Dictionary<ushort, string> used, ushort id, string name, string kind)
        {
            if (id == 0) throw new InvalidOperationException($"{kind} '{name}' uses reserved id 0");
            if (used.TryGetValue(id, out string existing) && !string.Equals(existing, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"{kind} id collision {id}: '{existing}' and '{name}'");
            used[id] = name;
        }

        private static ushort StableId(string value, uint salt)
        {
            unchecked
            {
                uint hash = 2166136261u ^ salt;
                string source = value ?? string.Empty;
                for (int i = 0; i < source.Length; ++i)
                {
                    char c = source[i];
                    hash ^= (byte)c; hash *= 16777619u;
                    hash ^= (byte)(c >> 8); hash *= 16777619u;
                }
                ushort result = (ushort)((hash ^ (hash >> 16)) & 0xFFFFu);
                return result == 0 ? (ushort)1 : result;
            }
        }
    }

    /// <summary>
    /// Dependency-free validation used by both the standalone backend and the game server.
    /// Definitions are team-authored data; invalid revisions fail closed before becoming active.
    /// </summary>
    public static class GameplayContentValidation
    {
        public static bool TryValidate(GameplayContentSnapshot snapshot, out string error)
        {
            if (snapshot == null)
                return Fail("content snapshot is missing", out error);
            GameplayContentDataIds.EnsureAssigned(snapshot);
            if (snapshot.revision <= 0)
                return Fail("content revision must be positive", out error);
            if (snapshot.baseInventoryCapacity < 1 || snapshot.baseInventoryCapacity > 10000)
                return Fail("base inventory capacity is invalid", out error);

            CharacterSpawnDefinition initialSpawn = snapshot.initialCharacterSpawn;
            if (initialSpawn != null)
            {
                if (!IsStableId(initialSpawn.mapId))
                    return Fail("initial character spawn has an invalid map id", out error);
                if ((initialSpawn.instanceId ?? string.Empty).Length > 128)
                    return Fail("initial character spawn has an invalid instance id", out error);
                if (!IsFinite(initialSpawn.positionX) ||
                    !IsFinite(initialSpawn.positionY) ||
                    !IsFinite(initialSpawn.positionZ) ||
                    !IsFinite(initialSpawn.yawDegrees))
                {
                    return Fail("initial character spawn contains a non-finite transform", out error);
                }
            }

            EquipmentSlotDefinition[] slots = snapshot.equipmentSlots ?? Array.Empty<EquipmentSlotDefinition>();
            ItemDefinition[] items = snapshot.items ?? Array.Empty<ItemDefinition>();
            StarterItemDefinition[] starters = snapshot.starterItems ?? Array.Empty<StarterItemDefinition>();
            CharacterResourceDefinition[] resources = snapshot.resources ?? Array.Empty<CharacterResourceDefinition>();
            StatusEffectDefinition[] statusEffects = snapshot.statusEffects ?? Array.Empty<StatusEffectDefinition>();
            AbilityDefinition[] abilities = snapshot.abilities ?? Array.Empty<AbilityDefinition>();
            ProfessionDefinition[] professions = snapshot.professions ?? Array.Empty<ProfessionDefinition>();
            LootTableDefinition[] lootTables = snapshot.lootTables ?? Array.Empty<LootTableDefinition>();
            HarvestProfileDefinition[] harvestProfiles = snapshot.harvestProfiles ?? Array.Empty<HarvestProfileDefinition>();

            var slotIds = new HashSet<string>(StringComparer.Ordinal);
            var slotPresentationIds = new HashSet<ushort>();
            for (int i = 0; i < slots.Length; ++i)
            {
                EquipmentSlotDefinition slot = slots[i];
                if (slot == null || !IsStableId(slot.slotId))
                    return Fail($"equipment slot {i} has an invalid id", out error);
                if (!slotIds.Add(slot.slotId))
                    return Fail($"duplicate equipment slot '{slot.slotId}'", out error);
                if (string.IsNullOrWhiteSpace(slot.displayName))
                    return Fail($"equipment slot '{slot.slotId}' has no display name", out error);
                if (slot.presentationSlotId != 0 && !slotPresentationIds.Add(slot.presentationSlotId))
                    return Fail($"duplicate equipment presentation slot id '{slot.presentationSlotId}'", out error);
            }

            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var itemPresentationIds = new HashSet<ushort>();
            for (int i = 0; i < items.Length; ++i)
            {
                ItemDefinition item = items[i];
                if (item == null || !IsStableId(item.definitionId))
                    return Fail($"item {i} has an invalid definition id", out error);
                if (!itemIds.Add(item.definitionId))
                    return Fail($"duplicate item definition '{item.definitionId}'", out error);
                if (string.IsNullOrWhiteSpace(item.displayName))
                    return Fail($"item '{item.definitionId}' has no display name", out error);
                if (item.kind == ItemKind.None)
                    return Fail($"item '{item.definitionId}' has no primary kind", out error);
                if (item.kind == ItemKind.Equipment && item.subtype == EquipmentItemSubtype.None)
                    return Fail($"equipment item '{item.definitionId}' requires an equipment subtype", out error);
                if (item.kind != ItemKind.Equipment && item.subtype != EquipmentItemSubtype.None)
                    return Fail($"non-equipment item '{item.definitionId}' cannot declare an equipment subtype", out error);
                if (item.presentationId != 0 && !itemPresentationIds.Add(item.presentationId))
                    return Fail($"duplicate item presentation id '{item.presentationId}'", out error);
                if (item.maxStack < 1 || item.maxStack > 1000000)
                    return Fail($"item '{item.definitionId}' has invalid maxStack", out error);
                if (!IsFiniteNonNegative(item.weight))
                    return Fail($"item '{item.definitionId}' has invalid weight", out error);
                if (item.maxDurability < 0)
                    return Fail($"item '{item.definitionId}' has invalid durability", out error);
                if (!IsFiniteNonNegative(item.damageMin) || !IsFiniteNonNegative(item.damageMax) || item.damageMax < item.damageMin)
                    return Fail($"item '{item.definitionId}' has invalid damage range", out error);
                if (!IsFiniteNonNegative(item.basicAttackRange))
                    return Fail($"item '{item.definitionId}' has invalid basic attack range", out error);
                if (!IsFiniteNonNegative(item.basicAttackInterval) ||
                    (item.basicAttackInterval > 0f &&
                     (item.basicAttackInterval < BasicAttackCadenceTiming.MinimumInterval || item.basicAttackInterval > BasicAttackCadenceTiming.MaximumInterval)))
                    return Fail($"item '{item.definitionId}' has invalid basic attack interval", out error);
                if (item.consumeQuantity < 0 || item.consumeQuantity > item.maxStack)
                    return Fail($"item '{item.definitionId}' has invalid consume quantity", out error);

                ItemUseEffectDefinition[] useEffects = item.useEffects ?? Array.Empty<ItemUseEffectDefinition>();
                if ((item.consumeQuantity == 0) != (useEffects.Length == 0))
                    return Fail($"item '{item.definitionId}' must define both consumeQuantity and useEffects, or neither", out error);
                if (useEffects.Length > 16)
                    return Fail($"item '{item.definitionId}' has too many use effects", out error);
                for (int e = 0; e < useEffects.Length; ++e)
                {
                    ItemUseEffectDefinition effect = useEffects[e];
                    if (effect == null || effect.kind == ItemUseEffectKind.None)
                        return Fail($"item '{item.definitionId}' has an invalid use effect", out error);
                    if (effect.kind != ItemUseEffectKind.RestoreResource && effect.kind != ItemUseEffectKind.ApplyStatusEffect)
                        return Fail($"item '{item.definitionId}' has an unsupported use effect", out error);
                    if (effect.amount < 0 || effect.stacks < 1 || effect.stacks > byte.MaxValue)
                        return Fail($"item '{item.definitionId}' has invalid use effect values", out error);
                }

                string[] allowed = item.allowedEquipmentSlots ?? Array.Empty<string>();
                for (int j = 0; j < allowed.Length; ++j)
                    if (!slotIds.Contains(allowed[j]))
                        return Fail($"item '{item.definitionId}' references unknown equipment slot '{allowed[j]}'", out error);

                StatModifierDefinition[] modifiers = item.statModifiers ?? Array.Empty<StatModifierDefinition>();
                for (int j = 0; j < modifiers.Length; ++j)
                {
                    StatModifierDefinition modifier = modifiers[j];
                    if (modifier == null || !IsStableId(modifier.statId))
                        return Fail($"item '{item.definitionId}' has invalid stat modifier", out error);
                    if (!IsFinite(modifier.additive) || !IsFinite(modifier.multiplier) || modifier.multiplier < 0f)
                        return Fail($"item '{item.definitionId}' has invalid modifier values", out error);
                }
            }

            for (int i = 0; i < starters.Length; ++i)
            {
                StarterItemDefinition starter = starters[i];
                if (starter == null || !itemIds.Contains(starter.definitionId))
                    return Fail($"starter item {i} references an unknown definition", out error);
                if (starter.quantity < 1)
                    return Fail($"starter item '{starter.definitionId}' has invalid quantity", out error);
                ItemDefinition definition = FindItem(items, starter.definitionId);
                if (definition != null && starter.quantity > definition.maxStack)
                    return Fail($"starter item '{starter.definitionId}' exceeds maxStack", out error);
            }

            if (starters.Length > snapshot.baseInventoryCapacity)
                return Fail("starter item count exceeds base inventory capacity", out error);

            var professionIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < professions.Length; ++i)
            {
                ProfessionDefinition profession = professions[i];
                if (profession == null || !IsStableId(profession.definitionId))
                    return Fail($"profession {i} has an invalid definition id", out error);
                if (!professionIds.Add(profession.definitionId))
                    return Fail($"duplicate profession '{profession.definitionId}'", out error);
                if (string.IsNullOrWhiteSpace(profession.displayName))
                    return Fail($"profession '{profession.definitionId}' has no display name", out error);
                if (profession.maximumLevel < 1 || profession.maximumLevel > 1000000)
                    return Fail($"profession '{profession.definitionId}' has invalid maximum level", out error);
            }

            var lootTableIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lootTables.Length; ++i)
            {
                LootTableDefinition table = lootTables[i];
                if (table == null || !IsStableId(table.definitionId))
                    return Fail($"loot table {i} has an invalid definition id", out error);
                if (!lootTableIds.Add(table.definitionId))
                    return Fail($"duplicate loot table '{table.definitionId}'", out error);

                LootTableEntryDefinition[] entries = table.entries ?? Array.Empty<LootTableEntryDefinition>();
                if (entries.Length == 0 || entries.Length > 1024)
                    return Fail($"loot table '{table.definitionId}' has an invalid entry count", out error);
                for (int e = 0; e < entries.Length; ++e)
                {
                    LootTableEntryDefinition entry = entries[e];
                    if (entry == null || !itemIds.Contains(entry.itemDefinitionId))
                        return Fail($"loot table '{table.definitionId}' entry {e} references an unknown item", out error);
                    if (entry.minQuantity < 1 || entry.maxQuantity < entry.minQuantity)
                        return Fail($"loot table '{table.definitionId}' entry {e} has an invalid quantity range", out error);
                    ItemDefinition item = FindItem(items, entry.itemDefinitionId);
                    if (item != null && entry.maxQuantity > item.maxStack)
                        return Fail($"loot table '{table.definitionId}' entry {e} exceeds item maxStack", out error);
                    if (!IsFinite(entry.chance) || entry.chance < 0f || entry.chance > 1f)
                        return Fail($"loot table '{table.definitionId}' entry {e} has an invalid chance", out error);
                }
            }

            var harvestProfileIds = new HashSet<ushort>();
            var harvestDefinitionIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < harvestProfiles.Length; ++i)
            {
                HarvestProfileDefinition profile = harvestProfiles[i];
                if (profile == null || profile.profileId == 0 || !IsStableId(profile.definitionId))
                    return Fail($"harvest profile {i} has an invalid identity", out error);
                if (!harvestProfileIds.Add(profile.profileId))
                    return Fail($"duplicate harvest profile id '{profile.profileId}'", out error);
                if (!harvestDefinitionIds.Add(profile.definitionId))
                    return Fail($"duplicate harvest profile '{profile.definitionId}'", out error);
                if (profile.actionId != Game.Shared.Interactions.InteractionActionId.Harvest)
                    return Fail($"harvest profile '{profile.definitionId}' must use the Harvest action", out error);
                HarvestMethodDefinition[] harvestMethods = profile.methods ?? Array.Empty<HarvestMethodDefinition>();
                bool usesModernProfessionTrack = !string.IsNullOrWhiteSpace(profile.professionTrackDefinitionId);
                bool usesModernMethods = harvestMethods.Length > 0;
                if (!usesModernProfessionTrack && !professionIds.Contains(profile.professionId ?? string.Empty))
                    return Fail($"harvest profile '{profile.definitionId}' references an unknown profession", out error);
                if (!usesModernMethods && !lootTableIds.Contains(profile.lootTableId ?? string.Empty))
                    return Fail($"harvest profile '{profile.definitionId}' references an unknown loot table", out error);
                if (!IsFinite(profile.durationSeconds) || profile.durationSeconds <= 0f || profile.durationSeconds > 3600f)
                    return Fail($"harvest profile '{profile.definitionId}' has an invalid duration", out error);
                if (!IsFinite(profile.maximumUseDistance) || profile.maximumUseDistance < 0.1f || profile.maximumUseDistance > 100f)
                    return Fail($"harvest profile '{profile.definitionId}' has an invalid use distance", out error);
                if (!IsFinite(profile.maximumFacingAngle) || profile.maximumFacingAngle < 0f || profile.maximumFacingAngle > 180f)
                    return Fail($"harvest profile '{profile.definitionId}' has an invalid facing angle", out error);
                if (profile.minimumProfessionLevel < 0 || profile.professionReward < 0 ||
                    profile.successProfessionReward < 0 || profile.failureProfessionReward < 0)
                    return Fail($"harvest profile '{profile.definitionId}' has invalid profession values", out error);
                if (profile.minimumCharges < 1 || profile.maximumCharges < profile.minimumCharges || profile.maximumCharges > 1000000)
                    return Fail($"harvest profile '{profile.definitionId}' has an invalid charge range", out error);
                if (!IsFiniteNonNegative(profile.emptyDelaySeconds) || !IsFiniteNonNegative(profile.respawnSeconds))
                    return Fail($"harvest profile '{profile.definitionId}' has invalid respawn timing", out error);
                if (profile.respawnPolicy == HarvestRespawnPolicy.Timed && profile.respawnSeconds <= 0f)
                    return Fail($"harvest profile '{profile.definitionId}' requires a positive timed respawn", out error);
                if ((profile.requiredEquippedItemTag ?? string.Empty).Length > 96)
                    return Fail($"harvest profile '{profile.definitionId}' has an invalid equipped item tag", out error);
            }

            var resourceIds = new HashSet<CharacterResourceId>();
            for (int i = 0; i < resources.Length; ++i)
            {
                CharacterResourceDefinition resource = resources[i];
                if (resource == null || resource.id == CharacterResourceId.None)
                    return Fail($"resource {i} has an invalid id", out error);
                if (!resourceIds.Add(resource.id))
                    return Fail($"duplicate resource '{resource.id}'", out error);
                if (string.IsNullOrWhiteSpace(resource.displayName))
                    return Fail($"resource '{resource.id}' has no display name", out error);
                if (resource.baseMaximum < resource.minimum)
                    return Fail($"resource '{resource.id}' has an invalid range", out error);
                if (!string.IsNullOrWhiteSpace(resource.maximumStatId) && !IsStableId(resource.maximumStatId))
                    return Fail($"resource '{resource.id}' has an invalid maximum stat id", out error);
                if (!IsFiniteNonNegative(resource.ratePerSecond))
                    return Fail($"resource '{resource.id}' has an invalid automatic update rate", out error);
            }

            CombatRulesDefinition combat = snapshot.combat;
            if (combat != null)
            {
                if (!IsFinite(combat.combatStateSeconds) || combat.combatStateSeconds < 0f || combat.combatStateSeconds > 3600f)
                    return Fail("combat combatStateSeconds is invalid", out error);
                if (combat.minimumDamageAfterDefense < 0)
                    return Fail("combat minimumDamageAfterDefense is invalid", out error);
                if (!IsFinite(combat.criticalDamageMultiplier) || combat.criticalDamageMultiplier < 1f)
                    return Fail("combat criticalDamageMultiplier is invalid", out error);
                if (!IsFinite(combat.blockDamageMultiplier) || combat.blockDamageMultiplier < 0f || combat.blockDamageMultiplier > 1f)
                    return Fail("combat blockDamageMultiplier is invalid", out error);
                if (!IsFinite(combat.maximumCriticalChance) || combat.maximumCriticalChance < 0f || combat.maximumCriticalChance > 1f)
                    return Fail("combat maximumCriticalChance is invalid", out error);
                if (!IsFinite(combat.maximumBlockChance) || combat.maximumBlockChance < 0f || combat.maximumBlockChance > 1f)
                    return Fail("combat maximumBlockChance is invalid", out error);
                if (!IsFinite(combat.basicAttackRange) || combat.basicAttackRange < 0.1f || combat.basicAttackRange > 100f)
                    return Fail("combat basicAttackRange is invalid", out error);
                if (!IsFinite(combat.basicAttackInterval) ||
                    combat.basicAttackInterval < BasicAttackCadenceTiming.MinimumInterval ||
                    combat.basicAttackInterval > BasicAttackCadenceTiming.MaximumInterval)
                    return Fail("combat basicAttackInterval is invalid", out error);
                if (combat.unarmedBasicAttackDamage < 1)
                    return Fail("combat unarmedBasicAttackDamage is invalid", out error);
            }

            var statusIds = new HashSet<string>(StringComparer.Ordinal);
            var legacyStatusIds = new HashSet<int>();
            for (int i = 0; i < statusEffects.Length; ++i)
            {
                StatusEffectDefinition status = statusEffects[i];
                if (status == null || !IsStableId(status.definitionId))
                    return Fail($"status effect {i} has an invalid definition id", out error);
                if (!statusIds.Add(status.definitionId))
                    return Fail($"duplicate status effect '{status.definitionId}'", out error);
                if (status.legacyStableId != 0 && !legacyStatusIds.Add(status.legacyStableId))
                    return Fail($"duplicate legacy status effect id '{status.legacyStableId}'", out error);
                if (string.IsNullOrWhiteSpace(status.displayName))
                    return Fail($"status effect '{status.definitionId}' has no display name", out error);
                if (status.allowedActors == GameplayActorAccessMask.None)
                    return Fail($"status effect '{status.definitionId}' has no allowed actors", out error);
                if (!IsFinite(status.durationSeconds) || status.durationSeconds < 0.05f || status.durationSeconds > 86400f)
                    return Fail($"status effect '{status.definitionId}' has invalid duration", out error);
                if (status.maximumStacks < 1 || status.maximumStacks > byte.MaxValue)
                    return Fail($"status effect '{status.definitionId}' has invalid maximumStacks", out error);
                if (!IsFiniteNonNegative(status.tickIntervalSeconds))
                    return Fail($"status effect '{status.definitionId}' has invalid tick interval", out error);
                if (status.periodicDamage < 0 || status.periodicHealing < 0)
                    return Fail($"status effect '{status.definitionId}' has invalid periodic amount", out error);
                if ((status.periodicDamage > 0 || status.periodicHealing > 0) && status.tickIntervalSeconds < 0.05f)
                    return Fail($"status effect '{status.definitionId}' requires a tick interval of at least 0.05 seconds", out error);
                if (!IsFiniteNonNegative(status.damageDealtMultiplier) ||
                    !IsFiniteNonNegative(status.damageTakenMultiplier) ||
                    !IsFiniteNonNegative(status.healingReceivedMultiplier))
                {
                    return Fail($"status effect '{status.definitionId}' has invalid combat multipliers", out error);
                }
            }

            for (int i = 0; i < items.Length; ++i)
            {
                ItemDefinition item = items[i];
                ItemUseEffectDefinition[] useEffects = item?.useEffects ?? Array.Empty<ItemUseEffectDefinition>();
                for (int e = 0; e < useEffects.Length; ++e)
                {
                    ItemUseEffectDefinition effect = useEffects[e];
                    if (effect.kind == ItemUseEffectKind.RestoreResource &&
                        (effect.amount <= 0 || effect.resourceId == CharacterResourceId.None || !resourceIds.Contains(effect.resourceId)))
                    {
                        return Fail($"item '{item.definitionId}' restores an unknown resource", out error);
                    }
                    if (effect.kind == ItemUseEffectKind.ApplyStatusEffect &&
                        (string.IsNullOrWhiteSpace(effect.statusEffectId) || !statusIds.Contains(effect.statusEffectId)))
                    {
                        return Fail($"item '{item.definitionId}' references an unknown status effect", out error);
                    }
                }
            }

            var abilityIds = new HashSet<string>(StringComparer.Ordinal);
            var legacyAbilityIds = new HashSet<int>();
            for (int i = 0; i < abilities.Length; ++i)
            {
                AbilityDefinition ability = abilities[i];
                if (ability == null || !IsStableId(ability.definitionId))
                    return Fail($"ability {i} has an invalid definition id", out error);
                if (!abilityIds.Add(ability.definitionId))
                    return Fail($"duplicate ability '{ability.definitionId}'", out error);
                if (ability.legacyStableId != 0 && !legacyAbilityIds.Add(ability.legacyStableId))
                    return Fail($"duplicate legacy ability id '{ability.legacyStableId}'", out error);
                if (string.IsNullOrWhiteSpace(ability.displayName))
                    return Fail($"ability '{ability.definitionId}' has no display name", out error);
                if (ability.allowedActors == GameplayActorAccessMask.None)
                    return Fail($"ability '{ability.definitionId}' has no allowed actors", out error);
                if (!IsFiniteNonNegative(ability.castTimeSeconds) || ability.castTimeSeconds > 3600f)
                    return Fail($"ability '{ability.definitionId}' has invalid cast time", out error);
                if (!IsFiniteNonNegative(ability.cooldownSeconds) || ability.cooldownSeconds > 86400f)
                    return Fail($"ability '{ability.definitionId}' has invalid cooldown", out error);
                if (!IsFiniteNonNegative(ability.range) || ability.range > 10000f)
                    return Fail($"ability '{ability.definitionId}' has invalid range", out error);
                if (ability.resourceCost < 0)
                    return Fail($"ability '{ability.definitionId}' has invalid resource cost", out error);
                if (ability.resourceCost > 0 && (ability.resourceId == CharacterResourceId.None || !resourceIds.Contains(ability.resourceId)))
                    return Fail($"ability '{ability.definitionId}' references an unknown cost resource", out error);

                GameplayEffectDefinition[] effects = ability.effects ?? Array.Empty<GameplayEffectDefinition>();
                if (effects.Length == 0)
                    return Fail($"ability '{ability.definitionId}' has no effects", out error);
                for (int e = 0; e < effects.Length; ++e)
                {
                    GameplayEffectDefinition effect = effects[e];
                    if (effect == null)
                        return Fail($"ability '{ability.definitionId}' has a null effect", out error);
                    if (!IsFinite(effect.chance) || effect.chance < 0f || effect.chance > 1f ||
                        !IsFinite(effect.minValue) || !IsFinite(effect.maxValue) || effect.maxValue < effect.minValue ||
                        effect.stacks < 1 || effect.stacks > byte.MaxValue)
                        return Fail($"ability '{ability.definitionId}' has invalid effect values", out error);
                    if (effect.kind == GameplayEffectKind.ResourceChange &&
                        (effect.resourceId == CharacterResourceId.None || !resourceIds.Contains(effect.resourceId)))
                        return Fail($"ability '{ability.definitionId}' references an unknown resource", out error);
                    if ((effect.kind == GameplayEffectKind.ApplyStatusEffect || effect.kind == GameplayEffectKind.RemoveStatusEffect) &&
                        (string.IsNullOrWhiteSpace(effect.statusEffectId) || !statusIds.Contains(effect.statusEffectId)))
                        return Fail($"ability '{ability.definitionId}' references an unknown status effect", out error);
                }
            }

            if (!RecoveryContentValidation.TryValidate(snapshot, out error))
                return false;

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Validates a hot-reload candidate against the active revision. Existing stable IDs
        /// cannot disappear and item/container structure cannot change live. Balance values
        /// such as weight, damage and stat modifiers remain freely tuneable.
        /// </summary>
        public static bool TryValidateCompatibleUpdate(
            GameplayContentSnapshot current,
            GameplayContentSnapshot candidate,
            out string error)
        {
            if (!TryValidate(candidate, out error))
                return false;
            if (current == null)
                return true;
            if (candidate.revision <= current.revision)
                return Fail("content revision must increase for hot reload", out error);

            EquipmentSlotDefinition[] oldSlots = current.equipmentSlots ?? Array.Empty<EquipmentSlotDefinition>();
            EquipmentSlotDefinition[] newSlots = candidate.equipmentSlots ?? Array.Empty<EquipmentSlotDefinition>();
            for (int i = 0; i < oldSlots.Length; ++i)
            {
                EquipmentSlotDefinition existing = oldSlots[i];
                if (FindSlot(newSlots, existing.slotId) == null)
                    return Fail($"hot reload cannot remove equipment slot '{existing.slotId}'", out error);
            }

            ProfessionDefinition[] oldProfessions = current.professions ?? Array.Empty<ProfessionDefinition>();
            ProfessionDefinition[] newProfessions = candidate.professions ?? Array.Empty<ProfessionDefinition>();
            for (int i = 0; i < oldProfessions.Length; ++i)
            {
                ProfessionDefinition existing = oldProfessions[i];
                ProfessionDefinition replacement = FindProfession(newProfessions, existing.definitionId);
                if (replacement == null)
                    return Fail($"hot reload cannot remove profession '{existing.definitionId}'", out error);
                if (replacement.maximumLevel != existing.maximumLevel)
                    return Fail($"hot reload cannot structurally change maximum level for profession '{existing.definitionId}'", out error);
            }

            HarvestProfileDefinition[] oldHarvestProfiles = current.harvestProfiles ?? Array.Empty<HarvestProfileDefinition>();
            HarvestProfileDefinition[] newHarvestProfiles = candidate.harvestProfiles ?? Array.Empty<HarvestProfileDefinition>();
            for (int i = 0; i < oldHarvestProfiles.Length; ++i)
            {
                HarvestProfileDefinition existing = oldHarvestProfiles[i];
                HarvestProfileDefinition replacement = FindHarvestProfile(newHarvestProfiles, existing.definitionId);
                if (replacement == null)
                    return Fail($"hot reload cannot remove harvest profile '{existing.definitionId}'", out error);
                if (replacement.profileId != existing.profileId || replacement.actionId != existing.actionId ||
                    !string.Equals(replacement.professionId ?? string.Empty, existing.professionId ?? string.Empty, StringComparison.Ordinal))
                {
                    return Fail($"hot reload cannot structurally change identity/action/profession for harvest profile '{existing.definitionId}'", out error);
                }
                // Respawn policy changes can strand already-depleted transient nodes because their
                // one-shot callback was scheduled under the prior policy. Require a GameServer restart
                // for this structural change; restart intentionally restores transient harvestables
                // from their authored/profile baseline. Timing/charge balance values may still tune live.
                if (replacement.respawnPolicy != existing.respawnPolicy)
                    return Fail($"hot reload cannot change respawn policy for harvest profile '{existing.definitionId}'; restart GameServer for this change", out error);
            }

            CharacterResourceDefinition[] oldResources = current.resources ?? Array.Empty<CharacterResourceDefinition>();
            CharacterResourceDefinition[] newResources = candidate.resources ?? Array.Empty<CharacterResourceDefinition>();
            if (oldResources.Length != newResources.Length)
                return Fail("hot reload cannot add or remove resource definitions; restart GameServer for structural resource changes", out error);
            for (int i = 0; i < oldResources.Length; ++i)
            {
                CharacterResourceDefinition existing = oldResources[i];
                CharacterResourceDefinition replacement = FindResource(newResources, existing.id);
                if (replacement == null)
                    return Fail($"hot reload cannot remove or replace resource '{existing.id}'", out error);
                if (replacement.enabled != existing.enabled ||
                    replacement.persistence != existing.persistence ||
                    replacement.replication != existing.replication)
                {
                    return Fail($"hot reload cannot structurally change resource '{existing.id}' enabled/persistence/replication settings", out error);
                }
            }

            StatusEffectDefinition[] oldStatuses = current.statusEffects ?? Array.Empty<StatusEffectDefinition>();
            StatusEffectDefinition[] newStatuses = candidate.statusEffects ?? Array.Empty<StatusEffectDefinition>();
            for (int i = 0; i < oldStatuses.Length; ++i)
            {
                StatusEffectDefinition existing = oldStatuses[i];
                StatusEffectDefinition replacement = FindStatusEffect(newStatuses, existing.definitionId);
                if (replacement == null)
                    return Fail($"hot reload cannot remove status effect '{existing.definitionId}'", out error);
                if (existing.legacyStableId != replacement.legacyStableId)
                    return Fail($"hot reload cannot change legacy stable id for status effect '{existing.definitionId}'", out error);
            }

            AbilityDefinition[] oldAbilities = current.abilities ?? Array.Empty<AbilityDefinition>();
            AbilityDefinition[] newAbilities = candidate.abilities ?? Array.Empty<AbilityDefinition>();
            for (int i = 0; i < oldAbilities.Length; ++i)
            {
                AbilityDefinition existing = oldAbilities[i];
                AbilityDefinition replacement = FindAbility(newAbilities, existing.definitionId);
                if (replacement == null)
                    return Fail($"hot reload cannot remove ability '{existing.definitionId}'", out error);
                if (existing.legacyStableId != replacement.legacyStableId)
                    return Fail($"hot reload cannot change legacy stable id for ability '{existing.definitionId}'", out error);
            }

            ItemDefinition[] oldItems = current.items ?? Array.Empty<ItemDefinition>();
            ItemDefinition[] newItems = candidate.items ?? Array.Empty<ItemDefinition>();
            for (int i = 0; i < oldItems.Length; ++i)
            {
                ItemDefinition existing = oldItems[i];
                ItemDefinition replacement = FindItem(newItems, existing.definitionId);
                if (replacement == null)
                    return Fail($"hot reload cannot remove item definition '{existing.definitionId}'", out error);
                if (replacement.kind != existing.kind || replacement.subtype != existing.subtype)
                    return Fail($"hot reload cannot structurally change item kind/subtype for '{existing.definitionId}'", out error);
                if (replacement.maxStack != existing.maxStack || replacement.maxDurability != existing.maxDurability)
                    return Fail($"hot reload cannot structurally change stack/durability for '{existing.definitionId}'", out error);
                if (!SameStringSet(existing.allowedEquipmentSlots, replacement.allowedEquipmentSlots))
                    return Fail($"hot reload cannot change equipment-slot compatibility for '{existing.definitionId}'", out error);
                if (replacement.consumeQuantity != existing.consumeQuantity ||
                    !SameItemUseEffectShape(existing.useEffects, replacement.useEffects))
                {
                    return Fail($"hot reload cannot structurally change use behavior for '{existing.definitionId}'", out error);
                }
            }

            error = string.Empty;
            return true;
        }

        private static ProfessionDefinition FindProfession(ProfessionDefinition[] professions, string id)
        {
            for (int i = 0; i < professions.Length; ++i)
                if (professions[i] != null && string.Equals(professions[i].definitionId, id, StringComparison.Ordinal))
                    return professions[i];
            return null;
        }

        private static HarvestProfileDefinition FindHarvestProfile(HarvestProfileDefinition[] profiles, string id)
        {
            for (int i = 0; i < profiles.Length; ++i)
                if (profiles[i] != null && string.Equals(profiles[i].definitionId, id, StringComparison.Ordinal))
                    return profiles[i];
            return null;
        }

        private static AbilityDefinition FindAbility(AbilityDefinition[] abilities, string id)
        {
            for (int i = 0; i < abilities.Length; ++i)
                if (abilities[i] != null && string.Equals(abilities[i].definitionId, id, StringComparison.Ordinal))
                    return abilities[i];
            return null;
        }

        private static StatusEffectDefinition FindStatusEffect(StatusEffectDefinition[] effects, string id)
        {
            for (int i = 0; i < effects.Length; ++i)
                if (effects[i] != null && string.Equals(effects[i].definitionId, id, StringComparison.Ordinal))
                    return effects[i];
            return null;
        }

        private static CharacterResourceDefinition FindResource(CharacterResourceDefinition[] resources, CharacterResourceId id)
        {
            for (int i = 0; i < resources.Length; ++i)
                if (resources[i] != null && resources[i].id == id)
                    return resources[i];
            return null;
        }

        private static EquipmentSlotDefinition FindSlot(EquipmentSlotDefinition[] slots, string id)
        {
            for (int i = 0; i < slots.Length; ++i)
                if (slots[i] != null && string.Equals(slots[i].slotId, id, StringComparison.Ordinal))
                    return slots[i];
            return null;
        }

        private static bool SameItemUseEffectShape(ItemUseEffectDefinition[] a, ItemUseEffectDefinition[] b)
        {
            if (a == null) a = Array.Empty<ItemUseEffectDefinition>();
            if (b == null) b = Array.Empty<ItemUseEffectDefinition>();
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; ++i)
            {
                ItemUseEffectDefinition left = a[i];
                ItemUseEffectDefinition right = b[i];
                if (left == null || right == null ||
                    left.kind != right.kind ||
                    left.resourceId != right.resourceId ||
                    !string.Equals(left.statusEffectId ?? string.Empty, right.statusEffectId ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameStringSet(string[] a, string[] b)
        {
            if (a == null) a = Array.Empty<string>();
            if (b == null) b = Array.Empty<string>();
            if (a.Length != b.Length) return false;
            var set = new HashSet<string>(a, StringComparer.Ordinal);
            if (set.Count != a.Length) return false;
            for (int i = 0; i < b.Length; ++i)
                if (!set.Contains(b[i])) return false;
            return true;
        }

        public static bool IsStableId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
                return false;
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        private static ItemDefinition FindItem(ItemDefinition[] items, string id)
        {
            for (int i = 0; i < items.Length; ++i)
                if (items[i] != null && string.Equals(items[i].definitionId, id, StringComparison.Ordinal))
                    return items[i];
            return null;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsFiniteNonNegative(float value) => IsFinite(value) && value >= 0f;

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
