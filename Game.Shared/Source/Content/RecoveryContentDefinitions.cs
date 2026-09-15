using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Game.Shared.Resources;

namespace Game.Shared.Content
{
    public enum ProgressTrackKind : byte
    {
        Mastery = 0,
        Profession = 1,
        Hunter = 2,
        Vampire = 3,
        General = 4,
    }

    public enum ProgressTrackMode : byte
    {
        Gain = 0,
        Lock = 1,
        Decay = 2,
    }

    public enum UnlockPredicateKind : byte
    {
        MinimumLevel = 0,
        MinimumTrack = 1,
        MinimumReputation = 2,
        MaximumHeat = 3,
        Faction = 4,
        KnownRecipe = 5,
    }

    [Serializable, DataContract]
    public sealed class ProgressionRulesDefinition
    {
        [DataMember(Name = "maxLevel")] public int maxLevel = 100;
        [DataMember(Name = "baseExperiencePerLevel")] public long baseExperiencePerLevel = 100;
        [DataMember(Name = "experienceGrowthPerLevel")] public float experienceGrowthPerLevel = 1.10f;
        [DataMember(Name = "defaultFactionDataId")] public ushort defaultFactionDataId;
        [DataMember(Name = "defaultFactionDefinitionId")] public string defaultFactionDefinitionId = string.Empty;
    }

    [Serializable, DataContract]
    public sealed class UnlockPredicateDefinition
    {
        [DataMember(Name = "kind")] public UnlockPredicateKind kind;
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "minimumValue")] public int minimumValue;
    }

    [Serializable, DataContract]
    public sealed class ProgressTrackDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "kind")] public ProgressTrackKind kind;
        [DataMember(Name = "maximumValue")] public int maximumValue = 100;
        [DataMember(Name = "groupId")] public string groupId = string.Empty;
        [DataMember(Name = "groupCap")] public int groupCap;
        [DataMember(Name = "decayCooldownSeconds")] public int decayCooldownSeconds = 86400;
        [DataMember(Name = "defaultMode")] public ProgressTrackMode defaultMode = ProgressTrackMode.Gain;
        [DataMember(Name = "unlockPredicates")] public UnlockPredicateDefinition[] unlockPredicates = Array.Empty<UnlockPredicateDefinition>();
    }

    [Serializable, DataContract]
    public sealed class RewardItemDefinition
    {
        [DataMember(Name = "itemDataId")] public ushort itemDataId;
        [DataMember(Name = "itemDefinitionId")] public string itemDefinitionId = string.Empty;
        [DataMember(Name = "quantity")] public int quantity = 1;
    }

    [Serializable, DataContract]
    public sealed class RewardTrackDefinition
    {
        [DataMember(Name = "trackDataId")] public ushort trackDataId;
        [DataMember(Name = "trackDefinitionId")] public string trackDefinitionId = string.Empty;
        [DataMember(Name = "amount")] public int amount;
    }

    [Serializable, DataContract]
    public sealed class RewardReputationDefinition
    {
        [DataMember(Name = "factionDataId")] public ushort factionDataId;
        [DataMember(Name = "factionDefinitionId")] public string factionDefinitionId = string.Empty;
        [DataMember(Name = "amount")] public int amount;
    }

    /// <summary>
    /// Generic authoritative reward description. Items are critical ownership mutations;
    /// XP/tracks/reputation/recipe knowledge are soft state and use the character coalescer.
    /// </summary>
    [Serializable, DataContract]
    public sealed class RewardBundleDefinition
    {
        [DataMember(Name = "items")] public RewardItemDefinition[] items = Array.Empty<RewardItemDefinition>();
        [DataMember(Name = "experience")] public long experience;
        [DataMember(Name = "tracks")] public RewardTrackDefinition[] tracks = Array.Empty<RewardTrackDefinition>();
        [DataMember(Name = "reputation")] public RewardReputationDefinition[] reputation = Array.Empty<RewardReputationDefinition>();
        [DataMember(Name = "knownRecipeDataIds")] public ushort[] knownRecipeDataIds = Array.Empty<ushort>();
        [DataMember(Name = "knownRecipeDefinitionIds")] public string[] knownRecipeDefinitionIds = Array.Empty<string>();
    }

    [Serializable, DataContract]
    public sealed class LootTableEntryDefinition
    {
        [DataMember(Name = "itemDataId")] public ushort itemDataId;
        [DataMember(Name = "itemDefinitionId")] public string itemDefinitionId = string.Empty;
        [DataMember(Name = "minQuantity")] public int minQuantity = 1;
        [DataMember(Name = "maxQuantity")] public int maxQuantity = 1;
        [DataMember(Name = "chance")] public float chance = 1f;
        // Explicit weighted-pool value. New Weighted tables should use this rather than overloading chance.
        // A zero value preserves compatibility by falling back to chance as the legacy weight.
        [DataMember(Name = "weight")] public ushort weight;
    }

    public enum LootRollMode : byte
    {
        Independent = 0,
        Weighted = 1,
    }

    [Serializable, DataContract]
    public sealed class LootTableDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;

        // Explicitly separates searchable-container semantics (independent entry chances)
        // from harvest/reward-pool semantics (weighted choice). This is server/content data;
        // the table itself is never replicated to clients.
        [DataMember(Name = "rollMode")] public LootRollMode rollMode = LootRollMode.Independent;
        [DataMember(Name = "rolls")] public byte rolls = 1;

        [DataMember(Name = "entries")] public LootTableEntryDefinition[] entries = Array.Empty<LootTableEntryDefinition>();
    }

    [Serializable, DataContract]
    public sealed class RecipeIngredientDefinition
    {
        [DataMember(Name = "itemDataId")] public ushort itemDataId;
        [DataMember(Name = "itemDefinitionId")] public string itemDefinitionId = string.Empty;
        [DataMember(Name = "quantity")] public int quantity = 1;
    }

    [Serializable, DataContract]
    public sealed class CraftingStationDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "tags")] public string[] tags = Array.Empty<string>();
    }

    [Serializable, DataContract]
    public sealed class RecipeDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "ingredients")] public RecipeIngredientDefinition[] ingredients = Array.Empty<RecipeIngredientDefinition>();
        [DataMember(Name = "rewards")] public RewardBundleDefinition rewards = new RewardBundleDefinition();
        [DataMember(Name = "stationTag")] public string stationTag = string.Empty;
        [DataMember(Name = "toolTag")] public string toolTag = string.Empty;
        [DataMember(Name = "learnByDefault")] public bool learnByDefault;
        [DataMember(Name = "unlockPredicates")] public UnlockPredicateDefinition[] unlockPredicates = Array.Empty<UnlockPredicateDefinition>();
    }

    [Serializable, DataContract]
    public sealed class FactionGameplayProfileDefinition
    {
        [DataMember(Name = "primaryResourceId")] public CharacterResourceId primaryResourceId;
        [DataMember(Name = "secondaryResourceId")] public CharacterResourceId secondaryResourceId;
        [DataMember(Name = "feedingEnabled")] public bool feedingEnabled;
        [DataMember(Name = "feedingGainResourceId")] public CharacterResourceId feedingGainResourceId;
        [DataMember(Name = "feedingHealthPerPulse")] public int feedingHealthPerPulse = 8;
        [DataMember(Name = "feedingResourcePerHealth")] public float feedingResourcePerHealth = 1f;
        [DataMember(Name = "feedingPulseSeconds")] public float feedingPulseSeconds = 1f;
        [DataMember(Name = "feedingMaximumDurationSeconds")] public float feedingMaximumDurationSeconds = 7f;
        [DataMember(Name = "feedingMinimumTargetHealthPercent")] public float feedingMinimumTargetHealthPercent = 0.20f;
        [DataMember(Name = "feedingMaximumRange")] public float feedingMaximumRange = 2.5f;
        [DataMember(Name = "feedingExposureResourceId")] public CharacterResourceId feedingExposureResourceId;
        [DataMember(Name = "feedingExposurePerPulse")] public int feedingExposurePerPulse;
        [DataMember(Name = "feedingTrackDataId")] public ushort feedingTrackDataId;
        [DataMember(Name = "feedingTrackDefinitionId")] public string feedingTrackDefinitionId = string.Empty;
        [DataMember(Name = "feedingTrackGainPerPulse")] public int feedingTrackGainPerPulse;
        [DataMember(Name = "feedingIncidentDataId")] public ushort feedingIncidentDataId;
        [DataMember(Name = "feedingIncidentDefinitionId")] public string feedingIncidentDefinitionId = string.Empty;
        [DataMember(Name = "feedingJurisdictionDataId")] public ushort feedingJurisdictionDataId;
        [DataMember(Name = "feedingJurisdictionDefinitionId")] public string feedingJurisdictionDefinitionId = string.Empty;
        [DataMember(Name = "feedingUnlockPredicates")] public UnlockPredicateDefinition[] feedingUnlockPredicates = Array.Empty<UnlockPredicateDefinition>();
    }

    [Serializable, DataContract]
    public sealed class FactionDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "tags")] public string[] tags = Array.Empty<string>();
        [DataMember(Name = "minimumReputation")] public int minimumReputation = -1000;
        [DataMember(Name = "maximumReputation")] public int maximumReputation = 1000;
        [DataMember(Name = "gameplayProfile")] public FactionGameplayProfileDefinition gameplayProfile = new FactionGameplayProfileDefinition();
    }

    [Serializable, DataContract]
    public sealed class FactionRelationshipDefinition
    {
        [DataMember(Name = "sourceFactionDataId")] public ushort sourceFactionDataId;
        [DataMember(Name = "sourceFactionDefinitionId")] public string sourceFactionDefinitionId = string.Empty;
        [DataMember(Name = "targetFactionDataId")] public ushort targetFactionDataId;
        [DataMember(Name = "targetFactionDefinitionId")] public string targetFactionDefinitionId = string.Empty;
        [DataMember(Name = "value")] public int value;
    }

    [Serializable, DataContract]
    public sealed class JurisdictionDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "maximumHeat")] public int maximumHeat = 1000;
        [DataMember(Name = "heatDecayPerMinute")] public float heatDecayPerMinute = 1f;
        [DataMember(Name = "bountyPerHeat")] public int bountyPerHeat;
    }

    [Serializable, DataContract]
    public sealed class IncidentDefinition
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "heat")] public int heat;
        [DataMember(Name = "evidence")] public int evidence;
        [DataMember(Name = "requiresWitnessOrCamera")] public bool requiresWitnessOrCamera = true;
        [DataMember(Name = "reputation")] public RewardReputationDefinition[] reputation = Array.Empty<RewardReputationDefinition>();
        [DataMember(Name = "tags")] public string[] tags = Array.Empty<string>();
    }

    public static class RecoveryContentDataIds
    {
        public static void EnsureAssigned(GameplayContentSnapshot snapshot)
        {
            if (snapshot == null) return;
            Assign(snapshot.progressTracks, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x50u, "progress track");
            Assign(snapshot.lootTables, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x4Cu, "loot table");
            Assign(snapshot.harvestProfiles, x => x?.definitionId, x => x.profileId, (x, id) => x.profileId = id, 0x48u, "harvest profile");
            Assign(snapshot.recipes, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x52u, "recipe");
            Assign(snapshot.craftingStations, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x43u, "crafting station");
            Assign(snapshot.factions, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x46u, "faction");
            Assign(snapshot.jurisdictions, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x4Au, "jurisdiction");
            Assign(snapshot.incidents, x => x?.definitionId, x => x.dataId, (x, id) => x.dataId = id, 0x49u, "incident");
        }

        public static void EnsureAssignedAndResolve(GameplayContentSnapshot snapshot)
        {
            EnsureAssigned(snapshot);
            if (snapshot != null) ResolveReferences(snapshot);
        }

        private static void ResolveReferences(GameplayContentSnapshot snapshot)
        {
            var itemIds = Map(snapshot.items, x => x?.definitionId, x => x.dataId);
            var trackIds = Map(snapshot.progressTracks, x => x?.definitionId, x => x.dataId);
            var lootIds = Map(snapshot.lootTables, x => x?.definitionId, x => x.dataId);
            var recipeIds = Map(snapshot.recipes, x => x?.definitionId, x => x.dataId);
            var factionIds = Map(snapshot.factions, x => x?.definitionId, x => x.dataId);
            var jurisdictionIds = Map(snapshot.jurisdictions, x => x?.definitionId, x => x.dataId);
            var incidentIds = Map(snapshot.incidents, x => x?.definitionId, x => x.dataId);

            if (snapshot.progression != null && !string.IsNullOrWhiteSpace(snapshot.progression.defaultFactionDefinitionId))
                Resolve(snapshot.progression.defaultFactionDefinitionId, ref snapshot.progression.defaultFactionDataId, factionIds, "default faction");

            LootTableDefinition[] loot = snapshot.lootTables ?? Array.Empty<LootTableDefinition>();
            var lootDataIds = new HashSet<ushort>();
            for (int i = 0; i < loot.Length; ++i)
                if (loot[i] != null) lootDataIds.Add(loot[i].dataId);
            for (int i = 0; i < loot.Length; ++i)
            {
                LootTableEntryDefinition[] entries = loot[i]?.entries ?? Array.Empty<LootTableEntryDefinition>();
                for (int j = 0; j < entries.Length; ++j)
                {
                    LootTableEntryDefinition entry = entries[j];
                    if (entry != null) Resolve(entry.itemDefinitionId, ref entry.itemDataId, itemIds, "loot item");
                }
            }

            HarvestProfileDefinition[] harvest = snapshot.harvestProfiles ?? Array.Empty<HarvestProfileDefinition>();
            for (int i = 0; i < harvest.Length; ++i)
            {
                HarvestProfileDefinition profile = harvest[i];
                if (profile == null) continue;

                string trackId = profile.professionTrackDefinitionId;
                if (string.IsNullOrWhiteSpace(trackId) && !string.IsNullOrWhiteSpace(profile.professionId))
                {
                    // Recovery profiles historically used "salvaging" while the new track is
                    // "profession.salvaging". Resolve either shape without putting strings on wire.
                    if (trackIds.ContainsKey(profile.professionId))
                        trackId = profile.professionId;
                    else
                    {
                        string prefixed = "profession." + profile.professionId;
                        if (trackIds.ContainsKey(prefixed))
                            trackId = prefixed;
                    }
                }
                if (!string.IsNullOrWhiteSpace(trackId))
                {
                    profile.professionTrackDefinitionId = trackId;
                    Resolve(trackId, ref profile.professionTrackDataId, trackIds, "harvest profession track");
                }

                HarvestMethodDefinition[] methods = profile.methods ?? Array.Empty<HarvestMethodDefinition>();
                for (int j = 0; j < methods.Length; ++j)
                {
                    HarvestMethodDefinition method = methods[j];
                    if (method == null) continue;
                    if (!string.IsNullOrWhiteSpace(method.lootTableId))
                        Resolve(method.lootTableId, ref method.lootTableDataId, lootIds, "harvest loot table");
                }
            }

            RecipeDefinition[] recipes = snapshot.recipes ?? Array.Empty<RecipeDefinition>();
            for (int i = 0; i < recipes.Length; ++i)
            {
                RecipeDefinition recipe = recipes[i];
                if (recipe == null) continue;
                RecipeIngredientDefinition[] ingredients = recipe.ingredients ?? Array.Empty<RecipeIngredientDefinition>();
                for (int j = 0; j < ingredients.Length; ++j)
                {
                    RecipeIngredientDefinition ingredient = ingredients[j];
                    if (ingredient != null) Resolve(ingredient.itemDefinitionId, ref ingredient.itemDataId, itemIds, "recipe ingredient");
                }
                ResolveReward(recipe.rewards, itemIds, trackIds, factionIds, recipeIds);
                ResolvePredicates(recipe.unlockPredicates, trackIds, factionIds, recipeIds);
            }

            ProgressTrackDefinition[] tracks = snapshot.progressTracks ?? Array.Empty<ProgressTrackDefinition>();
            var trackDataIds = new HashSet<ushort>();
            for (int i = 0; i < tracks.Length; ++i)
                if (tracks[i] != null) trackDataIds.Add(tracks[i].dataId);
            for (int i = 0; i < tracks.Length; ++i)
                ResolvePredicates(tracks[i]?.unlockPredicates, trackIds, factionIds, recipeIds);

            IncidentDefinition[] incidents = snapshot.incidents ?? Array.Empty<IncidentDefinition>();
            for (int i = 0; i < incidents.Length; ++i)
            {
                RewardReputationDefinition[] rep = incidents[i]?.reputation ?? Array.Empty<RewardReputationDefinition>();
                for (int j = 0; j < rep.Length; ++j)
                {
                    RewardReputationDefinition entry = rep[j];
                    if (entry != null) Resolve(entry.factionDefinitionId, ref entry.factionDataId, factionIds, "incident faction");
                }
            }


            FactionDefinition[] factionDefinitions = snapshot.factions ?? Array.Empty<FactionDefinition>();
            for (int i = 0; i < factionDefinitions.Length; ++i)
            {
                FactionGameplayProfileDefinition profile = factionDefinitions[i]?.gameplayProfile;
                if (profile == null) continue;
                if (!string.IsNullOrWhiteSpace(profile.feedingTrackDefinitionId))
                    Resolve(profile.feedingTrackDefinitionId, ref profile.feedingTrackDataId, trackIds, "feeding track");
                if (!string.IsNullOrWhiteSpace(profile.feedingIncidentDefinitionId))
                    Resolve(profile.feedingIncidentDefinitionId, ref profile.feedingIncidentDataId, incidentIds, "feeding incident");
                if (!string.IsNullOrWhiteSpace(profile.feedingJurisdictionDefinitionId))
                    Resolve(profile.feedingJurisdictionDefinitionId, ref profile.feedingJurisdictionDataId, jurisdictionIds, "feeding jurisdiction");
                ResolvePredicates(profile.feedingUnlockPredicates, trackIds, factionIds, recipeIds);
            }

            FactionRelationshipDefinition[] relationships = snapshot.factionRelationships ?? Array.Empty<FactionRelationshipDefinition>();
            for (int i = 0; i < relationships.Length; ++i)
            {
                FactionRelationshipDefinition rel = relationships[i];
                if (rel == null) continue;
                Resolve(rel.sourceFactionDefinitionId, ref rel.sourceFactionDataId, factionIds, "source faction");
                Resolve(rel.targetFactionDefinitionId, ref rel.targetFactionDataId, factionIds, "target faction");
            }
        }

        private static void ResolveReward(
            RewardBundleDefinition reward,
            Dictionary<string, ushort> itemIds,
            Dictionary<string, ushort> trackIds,
            Dictionary<string, ushort> factionIds,
            Dictionary<string, ushort> recipeIds)
        {
            if (reward == null) return;
            RewardItemDefinition[] items = reward.items ?? Array.Empty<RewardItemDefinition>();
            for (int i = 0; i < items.Length; ++i)
                if (items[i] != null) Resolve(items[i].itemDefinitionId, ref items[i].itemDataId, itemIds, "reward item");
            RewardTrackDefinition[] tracks = reward.tracks ?? Array.Empty<RewardTrackDefinition>();
            for (int i = 0; i < tracks.Length; ++i)
                if (tracks[i] != null) Resolve(tracks[i].trackDefinitionId, ref tracks[i].trackDataId, trackIds, "reward track");
            RewardReputationDefinition[] factions = reward.reputation ?? Array.Empty<RewardReputationDefinition>();
            for (int i = 0; i < factions.Length; ++i)
                if (factions[i] != null) Resolve(factions[i].factionDefinitionId, ref factions[i].factionDataId, factionIds, "reward faction");
            string[] semanticRecipes = reward.knownRecipeDefinitionIds ?? Array.Empty<string>();
            if (semanticRecipes.Length > 0)
            {
                var resolved = new ushort[semanticRecipes.Length];
                for (int i = 0; i < semanticRecipes.Length; ++i)
                {
                    ushort value = 0;
                    Resolve(semanticRecipes[i], ref value, recipeIds, "reward recipe");
                    resolved[i] = value;
                }
                reward.knownRecipeDataIds = resolved;
            }
        }

        private static void ResolvePredicates(
            UnlockPredicateDefinition[] predicates,
            Dictionary<string, ushort> trackIds,
            Dictionary<string, ushort> factionIds,
            Dictionary<string, ushort> recipeIds)
        {
            predicates = predicates ?? Array.Empty<UnlockPredicateDefinition>();
            for (int i = 0; i < predicates.Length; ++i)
            {
                UnlockPredicateDefinition predicate = predicates[i];
                if (predicate == null || predicate.kind == UnlockPredicateKind.MinimumLevel) continue;
                Dictionary<string, ushort> ids = predicate.kind == UnlockPredicateKind.MinimumTrack
                    ? trackIds
                    : predicate.kind == UnlockPredicateKind.KnownRecipe
                        ? recipeIds
                        : factionIds;
                Resolve(predicate.definitionId, ref predicate.dataId, ids, "unlock predicate");
            }
        }

        private static Dictionary<string, ushort> Map<T>(T[] values, Func<T, string> getName, Func<T, ushort> getId)
        {
            var result = new Dictionary<string, ushort>(StringComparer.Ordinal);
            values = values ?? Array.Empty<T>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i] is null) continue;
                string name = getName(values[i]);
                ushort id = getId(values[i]);
                if (!string.IsNullOrWhiteSpace(name) && id != 0) result[name] = id;
            }
            return result;
        }

        private static void Resolve(string semanticId, ref ushort dataId, Dictionary<string, ushort> ids, string kind)
        {
            if (dataId != 0) return;
            if (string.IsNullOrWhiteSpace(semanticId) || !ids.TryGetValue(semanticId, out dataId))
                throw new InvalidOperationException($"{kind} reference '{semanticId ?? string.Empty}' is not loaded");
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
            if (id == 0) throw new InvalidOperationException($"{kind} '{name}' uses reserved dataId 0");
            if (used.TryGetValue(id, out string existing) && !string.Equals(existing, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"{kind} dataId collision {id}: '{existing}' and '{name}'");
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

    public static class RecoveryContentValidation
    {
        public static bool TryValidate(GameplayContentSnapshot snapshot, out string error)
        {
            if (snapshot == null) return Fail("content snapshot is missing", out error);
            RecoveryContentDataIds.EnsureAssignedAndResolve(snapshot);

            if (snapshot.progression != null &&
                (snapshot.progression.maxLevel < 1 || snapshot.progression.maxLevel > 10000 ||
                 snapshot.progression.baseExperiencePerLevel < 1 ||
                 float.IsNaN(snapshot.progression.experienceGrowthPerLevel) ||
                 float.IsInfinity(snapshot.progression.experienceGrowthPerLevel) ||
                 snapshot.progression.experienceGrowthPerLevel < 1f ||
                 snapshot.progression.experienceGrowthPerLevel > 10f))
                return Fail("progression rules are invalid", out error);

            if (!ValidateIds(snapshot.progressTracks, x => x?.definitionId, x => x.dataId, "progress track", out error) ||
                !ValidateIds(snapshot.lootTables, x => x?.definitionId, x => x.dataId, "loot table", out error) ||
                !ValidateIds(snapshot.recipes, x => x?.definitionId, x => x.dataId, "recipe", out error) ||
                !ValidateIds(snapshot.craftingStations, x => x?.definitionId, x => x.dataId, "crafting station", out error) ||
                !ValidateIds(snapshot.factions, x => x?.definitionId, x => x.dataId, "faction", out error) ||
                !ValidateIds(snapshot.jurisdictions, x => x?.definitionId, x => x.dataId, "jurisdiction", out error) ||
                !ValidateIds(snapshot.incidents, x => x?.definitionId, x => x.dataId, "incident", out error))
                return false;

            ProgressTrackDefinition[] tracks = snapshot.progressTracks ?? Array.Empty<ProgressTrackDefinition>();
            var trackDataIds = new HashSet<ushort>();
            for (int i = 0; i < tracks.Length; ++i)
            {
                ProgressTrackDefinition track = tracks[i];
                if (track != null) trackDataIds.Add(track.dataId);
                if (track.maximumValue < 1 || track.maximumValue > 100000000 || track.groupCap < 0 || track.decayCooldownSeconds < 0)
                    return Fail($"progress track '{track.definitionId}' has invalid bounds", out error);
                if (!ValidatePredicates(track.unlockPredicates, out error)) return false;
            }

            LootTableDefinition[] loot = snapshot.lootTables ?? Array.Empty<LootTableDefinition>();
            var lootDataIds = new HashSet<ushort>();
            for (int i = 0; i < loot.Length; ++i)
            {
                LootTableDefinition table = loot[i];
                if (table != null) lootDataIds.Add(table.dataId);
                if (table == null || (byte)table.rollMode > (byte)LootRollMode.Weighted || table.rolls < 1 || table.rolls > 32)
                    return Fail($"loot table '{table?.definitionId}' has invalid roll settings", out error);
                LootTableEntryDefinition[] entries = table.entries ?? Array.Empty<LootTableEntryDefinition>();
                if (entries.Length > 256) return Fail($"loot table '{table.definitionId}' has too many entries", out error);
                for (int j = 0; j < entries.Length; ++j)
                {
                    LootTableEntryDefinition entry = entries[j];
                    if (entry == null || entry.itemDataId == 0 || entry.minQuantity < 1 || entry.maxQuantity < entry.minQuantity || entry.maxQuantity > 1000000 ||
                        float.IsNaN(entry.chance) || float.IsInfinity(entry.chance) || entry.chance < 0f || entry.chance > 1f ||
                        (table.rollMode == LootRollMode.Weighted && entry.weight == 0 && entry.chance <= 0f))
                        return Fail($"loot table '{table.definitionId}' has invalid entry {j}", out error);
                }
            }

            HarvestProfileDefinition[] harvest = snapshot.harvestProfiles ?? Array.Empty<HarvestProfileDefinition>();
            for (int i = 0; i < harvest.Length; ++i)
            {
                HarvestProfileDefinition profile = harvest[i];
                if (profile == null || profile.profileId == 0)
                    return Fail($"harvest profile {i} is invalid", out error);
                if (profile.professionTrackDataId == 0 || !trackDataIds.Contains(profile.professionTrackDataId))
                    return Fail($"harvest profile '{profile.definitionId}' has no valid profession track", out error);
                if (profile.successProfessionReward < 0 || profile.failureProfessionReward < 0)
                    return Fail($"harvest profile '{profile.definitionId}' has invalid profession rewards", out error);

                HarvestMethodDefinition[] methods = profile.methods ?? Array.Empty<HarvestMethodDefinition>();
                if (methods.Length > 32)
                    return Fail($"harvest profile '{profile.definitionId}' has too many methods", out error);
                for (int j = 0; j < methods.Length; ++j)
                {
                    HarvestMethodDefinition method = methods[j];
                    if (method == null || method.capability == HarvestCapability.None || method.lootTableDataId == 0 || !lootDataIds.Contains(method.lootTableDataId) ||
                        (byte)method.toolAccess > (byte)HarvestToolAccess.EquippedOrInventory ||
                        (method.capability != HarvestCapability.Hands && method.toolAccess == HarvestToolAccess.None) ||
                        method.baseSuccessBasisPoints > 10000 || method.skillBasisPointsPerLevel > 10000)
                        return Fail($"harvest profile '{profile.definitionId}' has invalid method {j}", out error);
                }
            }

            RecipeDefinition[] recipes = snapshot.recipes ?? Array.Empty<RecipeDefinition>();
            for (int i = 0; i < recipes.Length; ++i)
            {
                RecipeDefinition recipe = recipes[i];
                RecipeIngredientDefinition[] ingredients = recipe.ingredients ?? Array.Empty<RecipeIngredientDefinition>();
                if (ingredients.Length == 0 || ingredients.Length > 64) return Fail($"recipe '{recipe.definitionId}' has invalid ingredients", out error);
                for (int j = 0; j < ingredients.Length; ++j)
                    if (ingredients[j] == null || ingredients[j].itemDataId == 0 || ingredients[j].quantity < 1 || ingredients[j].quantity > 1000000)
                        return Fail($"recipe '{recipe.definitionId}' has invalid ingredient {j}", out error);
                if (!ValidateReward(recipe.rewards, out error) || !ValidatePredicates(recipe.unlockPredicates, out error)) return false;
            }

            FactionDefinition[] factions = snapshot.factions ?? Array.Empty<FactionDefinition>();
            for (int i = 0; i < factions.Length; ++i)
            {
                FactionDefinition faction = factions[i];
                if (faction.minimumReputation > faction.maximumReputation)
                    return Fail($"faction '{faction.definitionId}' has invalid reputation bounds", out error);

                FactionGameplayProfileDefinition profile = faction.gameplayProfile;
                if (profile == null) continue;
                if (!profile.feedingEnabled) continue;
                if (profile.feedingGainResourceId == CharacterResourceId.None ||
                    profile.feedingHealthPerPulse < 1 || profile.feedingHealthPerPulse > 1000000 ||
                    float.IsNaN(profile.feedingResourcePerHealth) || float.IsInfinity(profile.feedingResourcePerHealth) || profile.feedingResourcePerHealth <= 0f ||
                    float.IsNaN(profile.feedingPulseSeconds) || float.IsInfinity(profile.feedingPulseSeconds) || profile.feedingPulseSeconds < 0.05f || profile.feedingPulseSeconds > 60f ||
                    float.IsNaN(profile.feedingMaximumDurationSeconds) || float.IsInfinity(profile.feedingMaximumDurationSeconds) || profile.feedingMaximumDurationSeconds < profile.feedingPulseSeconds || profile.feedingMaximumDurationSeconds > 3600f ||
                    float.IsNaN(profile.feedingMinimumTargetHealthPercent) || float.IsInfinity(profile.feedingMinimumTargetHealthPercent) || profile.feedingMinimumTargetHealthPercent < 0f || profile.feedingMinimumTargetHealthPercent >= 1f ||
                    float.IsNaN(profile.feedingMaximumRange) || float.IsInfinity(profile.feedingMaximumRange) || profile.feedingMaximumRange <= 0f || profile.feedingMaximumRange > 20f ||
                    profile.feedingExposurePerPulse < 0 || profile.feedingTrackGainPerPulse < 0)
                    return Fail($"faction '{faction.definitionId}' has invalid feeding rules", out error);
                if ((profile.feedingIncidentDataId == 0) != (profile.feedingJurisdictionDataId == 0))
                    return Fail($"faction '{faction.definitionId}' must configure feeding incident and jurisdiction together", out error);
                if (!ValidatePredicates(profile.feedingUnlockPredicates, out error)) return false;
            }

            JurisdictionDefinition[] jurisdictions = snapshot.jurisdictions ?? Array.Empty<JurisdictionDefinition>();
            for (int i = 0; i < jurisdictions.Length; ++i)
                if (jurisdictions[i].maximumHeat < 1 || jurisdictions[i].heatDecayPerMinute < 0f ||
                    float.IsNaN(jurisdictions[i].heatDecayPerMinute) || float.IsInfinity(jurisdictions[i].heatDecayPerMinute) || jurisdictions[i].bountyPerHeat < 0)
                    return Fail($"jurisdiction '{jurisdictions[i].definitionId}' has invalid heat rules", out error);

            error = string.Empty;
            return true;
        }

        private static bool ValidateReward(RewardBundleDefinition reward, out string error)
        {
            reward = reward ?? new RewardBundleDefinition();
            if (reward.experience < 0) return Fail("reward experience cannot be negative", out error);
            RewardItemDefinition[] items = reward.items ?? Array.Empty<RewardItemDefinition>();
            for (int i = 0; i < items.Length; ++i)
                if (items[i] == null || items[i].itemDataId == 0 || items[i].quantity < 1 || items[i].quantity > 1000000)
                    return Fail("reward contains an invalid item", out error);
            RewardTrackDefinition[] tracks = reward.tracks ?? Array.Empty<RewardTrackDefinition>();
            for (int i = 0; i < tracks.Length; ++i)
                if (tracks[i] == null || tracks[i].trackDataId == 0 || tracks[i].amount == 0)
                    return Fail("reward contains an invalid track delta", out error);
            error = string.Empty;
            return true;
        }

        private static bool ValidatePredicates(UnlockPredicateDefinition[] predicates, out string error)
        {
            predicates = predicates ?? Array.Empty<UnlockPredicateDefinition>();
            if (predicates.Length > 32) return Fail("unlock predicate list is too large", out error);
            for (int i = 0; i < predicates.Length; ++i)
            {
                UnlockPredicateDefinition predicate = predicates[i];
                if (predicate == null || (predicate.kind != UnlockPredicateKind.MinimumLevel && predicate.dataId == 0))
                    return Fail("unlock predicate is invalid", out error);
            }
            error = string.Empty;
            return true;
        }

        private static bool ValidateIds<T>(T[] values, Func<T, string> getName, Func<T, ushort> getId, string kind, out string error)
        {
            values = values ?? Array.Empty<T>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<ushort>();
            for (int i = 0; i < values.Length; ++i)
            {
                T value = values[i];
                if (value is null) return Fail($"{kind} {i} is null", out error);
                string name = getName(value);
                ushort id = getId(value);
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || id == 0)
                    return Fail($"{kind} {i} has an invalid id", out error);
                if (!names.Add(name) || !ids.Add(id))
                    return Fail($"duplicate {kind} '{name}'", out error);
            }
            error = string.Empty;
            return true;
        }

        private static bool Fail(string value, out string error) { error = value; return false; }
    }
}
