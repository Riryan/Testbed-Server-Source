using System.Text.Json;
using Game.Shared.Content;
using Game.Shared.Abilities;
using Game.Shared.Resources;

namespace Game.BackendServer;

/// <summary>
/// Loads Content Catalog V2 authoring files into the single runtime
/// GameplayContentSnapshot consumed by GatewayServer and GameServer.
/// Authoring is split by domain; runtime resolution remains unified.
/// </summary>
internal sealed class GameplayContentCatalogV2Reader
{
    public const int SupportedCatalogVersion = 2;

    private static readonly CatalogFileSpec[] ItemFiles =
    {
        new("Items/Equipment/Weapons.json", ItemKind.Equipment, EquipmentItemSubtype.Weapon),
        new("Items/Equipment/Armor.json", ItemKind.Equipment, EquipmentItemSubtype.Armor),
        new("Items/Equipment/Tools.json", ItemKind.Equipment, EquipmentItemSubtype.Tool),
        new("Items/Consumables.json", ItemKind.Consumable, EquipmentItemSubtype.None),
        new("Items/Ammo.json", ItemKind.Ammo, EquipmentItemSubtype.None),
        new("Items/Materials.json", ItemKind.Material, EquipmentItemSubtype.None),
        new("Items/Junk.json", ItemKind.Junk, EquipmentItemSubtype.None),
        new("Items/Quest.json", ItemKind.Quest, EquipmentItemSubtype.None),
    };

    private readonly string _manifestPath;
    private readonly string _contentRoot;
    private readonly JsonSerializerOptions _json;
    private readonly HashSet<string> _watchedPaths;

    public GameplayContentCatalogV2Reader(string manifestPath, JsonSerializerOptions json)
    {
        _manifestPath = Path.GetFullPath(manifestPath ?? throw new ArgumentNullException(nameof(manifestPath)));
        _contentRoot = Path.GetDirectoryName(_manifestPath)
            ?? throw new InvalidOperationException("Gameplay content manifest must have a parent directory.");
        _json = json ?? throw new ArgumentNullException(nameof(json));

        _watchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(_manifestPath),
            NormalizePath(Resolve("Abilities/Abilities.json")),
            NormalizePath(Resolve("StatusEffects/StatusEffects.json")),
            NormalizePath(Resolve("DamageTypes/DamageTypes.json")),
            NormalizePath(Resolve("Loot/LootTables.json")),
            NormalizePath(Resolve("Loot/RecoveryLootTables.json")),
            NormalizePath(Resolve("Crafting/Recipes.json")),
            NormalizePath(Resolve("Crafting/RecoveryRecipes.json")),
            NormalizePath(Resolve("Skills/Skills.json")),
            NormalizePath(Resolve("Skills/RecoverySkills.json")),
            NormalizePath(Resolve("Factions/Factions.json")),
            NormalizePath(Resolve("Factions/RecoveryFactions.json")),
            NormalizePath(Resolve("Resources/RecoveryResources.json")),
            NormalizePath(Resolve("Items/RecoveryMaterials.json")),
            NormalizePath(Resolve("Items/RecoveryTools.json")),
            NormalizePath(Resolve("Harvest/HarvestNodes.json")),
        };

        foreach (CatalogFileSpec spec in ItemFiles)
            _watchedPaths.Add(NormalizePath(Resolve(spec.RelativePath)));
    }

    public string ContentRoot => _contentRoot;

    public bool IsCatalogFile(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        _watchedPaths.Contains(NormalizePath(Path.GetFullPath(path)));

    public GameplayContentSnapshot Load()
    {
        GameplayContentSnapshot snapshot = ReadManifest();

        var items = new List<ItemDefinition>();
        foreach (CatalogFileSpec spec in ItemFiles)
            LoadItems(spec, items);
        LoadOptionalItems("Items/RecoveryMaterials.json", ItemKind.Material, EquipmentItemSubtype.None, items);
        LoadOptionalItems("Items/RecoveryTools.json", ItemKind.Equipment, EquipmentItemSubtype.Tool, items);

        snapshot.items = items.ToArray();
        ResourceCatalogDocument recoveryResources = ReadOptionalDocument<ResourceCatalogDocument>("Resources/RecoveryResources.json");
        snapshot.resources = MergeResources(snapshot.resources, recoveryResources?.resources);
        snapshot.abilities = ReadRequiredDocument<AbilityCatalogDocument>(
            "Abilities/Abilities.json").abilities ?? Array.Empty<AbilityDefinition>();
        snapshot.statusEffects = ReadRequiredDocument<StatusEffectCatalogDocument>(
            "StatusEffects/StatusEffects.json").statusEffects ?? Array.Empty<StatusEffectDefinition>();
        snapshot.damageTypes = ReadOptionalDocument<DamageTypeCatalogDocument>(
            "DamageTypes/DamageTypes.json")?.damageTypes ?? Array.Empty<DamageTypeDefinition>();

        LootCatalogDocument loot = ReadRequiredDocument<LootCatalogDocument>("Loot/LootTables.json");
        LootCatalogDocument recoveryLoot = ReadOptionalDocument<LootCatalogDocument>("Loot/RecoveryLootTables.json");
        snapshot.lootTables = Concat(loot.lootTables, recoveryLoot?.lootTables);

        CraftingCatalogDocument crafting = ReadRequiredDocument<CraftingCatalogDocument>("Crafting/Recipes.json");
        CraftingCatalogDocument recoveryCrafting = ReadOptionalDocument<CraftingCatalogDocument>("Crafting/RecoveryRecipes.json");
        snapshot.recipes = Concat(crafting.recipes, recoveryCrafting?.recipes);
        snapshot.craftingStations = Concat(crafting.stations, recoveryCrafting?.stations);

        ProgressCatalogDocument progress = ReadRequiredDocument<ProgressCatalogDocument>("Skills/Skills.json");
        ProgressCatalogDocument recoveryProgress = ReadOptionalDocument<ProgressCatalogDocument>("Skills/RecoverySkills.json");
        snapshot.progression = recoveryProgress?.progression ?? progress.progression ?? snapshot.progression ?? new ProgressionRulesDefinition();
        snapshot.progressTracks = Concat(progress.skills, recoveryProgress?.skills);

        FactionCatalogDocument factions = ReadOptionalDocument<FactionCatalogDocument>("Factions/Factions.json");
        FactionCatalogDocument recoveryFactions = ReadOptionalDocument<FactionCatalogDocument>("Factions/RecoveryFactions.json");
        snapshot.factions = Concat(factions?.factions, recoveryFactions?.factions);
        snapshot.factionRelationships = Concat(factions?.relationships, recoveryFactions?.relationships);
        snapshot.jurisdictions = Concat(factions?.jurisdictions, recoveryFactions?.jurisdictions);
        snapshot.incidents = Concat(factions?.incidents, recoveryFactions?.incidents);

        HarvestCatalogDocument harvest = ReadOptionalDocument<HarvestCatalogDocument>("Harvest/HarvestNodes.json");
        snapshot.harvestProfiles = Concat(snapshot.harvestProfiles, harvest?.harvestProfiles);

        GameplayContentDataIds.EnsureAssigned(snapshot);
        return snapshot;
    }

    private GameplayContentSnapshot ReadManifest()
    {
        if (!File.Exists(_manifestPath))
            throw new FileNotFoundException("Gameplay content manifest was not found.", _manifestPath);

        string json = ReadAllTextShared(_manifestPath);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("GameplayContent.json must contain a JSON object.");

        if (!TryReadCatalogVersion(root, out int catalogVersion) || catalogVersion != SupportedCatalogVersion)
            throw new InvalidOperationException(
                $"GameplayContent.json must declare catalogVersion {SupportedCatalogVersion}.");

        RejectLegacyInlineCollection(root, "items");
        RejectLegacyInlineCollection(root, "abilities");
        RejectLegacyInlineCollection(root, "statusEffects");
        RejectLegacyInlineCollection(root, "lootTables");
        RejectLegacyInlineCollection(root, "recipes");
        RejectLegacyInlineCollection(root, "progressTracks");
        RejectLegacyInlineCollection(root, "factions");

        GameplayContentSnapshot snapshot = JsonSerializer.Deserialize<GameplayContentSnapshot>(json, _json);
        if (snapshot == null)
            throw new InvalidOperationException("GameplayContent.json did not produce a content snapshot.");

        return snapshot;
    }

    private void LoadItems(CatalogFileSpec spec, List<ItemDefinition> output)
    {
        ItemCatalogDocument document = ReadRequiredDocument<ItemCatalogDocument>(spec.RelativePath);
        ItemAuthoringDefinition[] definitions = document.items ?? Array.Empty<ItemAuthoringDefinition>();

        for (int i = 0; i < definitions.Length; ++i)
        {
            ItemAuthoringDefinition authored = definitions[i]
                ?? throw new InvalidOperationException($"{spec.RelativePath}: item {i} is null.");

            if (authored.kind != spec.ExpectedKind || authored.subtype != spec.ExpectedSubtype)
            {
                throw new InvalidOperationException(
                    $"{spec.RelativePath}: item '{authored.definitionId ?? "<missing>"}' must use " +
                    $"kind '{spec.ExpectedKind}' and subtype '{spec.ExpectedSubtype}'.");
            }

            output.Add(ConvertItem(spec.RelativePath, authored));
        }
    }

    private static ItemDefinition ConvertItem(string sourcePath, ItemAuthoringDefinition authored)
    {
        bool isEquipment = authored.kind == ItemKind.Equipment;
        bool isWeapon = isEquipment && authored.subtype == EquipmentItemSubtype.Weapon;
        bool isConsumable = authored.kind == ItemKind.Consumable;
        bool isAmmo = authored.kind == ItemKind.Ammo;

        if (isEquipment != (authored.equipment != null))
            throw new InvalidOperationException(
                $"{sourcePath}: item '{authored.definitionId}' must {(isEquipment ? "define" : "not define")} an equipment section.");

        if (isWeapon != (authored.weapon != null))
            throw new InvalidOperationException(
                $"{sourcePath}: item '{authored.definitionId}' must {(isWeapon ? "define" : "not define")} a weapon section.");

        if (isConsumable != (authored.consumable != null))
            throw new InvalidOperationException(
                $"{sourcePath}: item '{authored.definitionId}' must {(isConsumable ? "define" : "not define")} a consumable section.");

        if (isAmmo != (authored.ammo != null))
            throw new InvalidOperationException(
                $"{sourcePath}: item '{authored.definitionId}' must {(isAmmo ? "define" : "not define")} an ammo section.");

        EquipmentItemAuthoringSection equipment = authored.equipment;
        WeaponItemAuthoringSection weapon = authored.weapon;
        ConsumableItemAuthoringSection consumable = authored.consumable;
        AmmoItemAuthoringSection ammo = authored.ammo;

        return new ItemDefinition
        {
            definitionId = authored.definitionId,
            displayName = authored.displayName,
            kind = authored.kind,
            subtype = authored.subtype,
            presentationId = authored.presentationId,
            maxStack = authored.maxStack,
            weight = authored.weight,
            maxDurability = authored.maxDurability,
            tags = authored.tags ?? Array.Empty<string>(),
            harvestCapabilities = authored.harvestCapabilities,
            harvestToolTier = authored.harvestToolTier,
            allowedEquipmentSlots = equipment?.allowedEquipmentSlots ?? Array.Empty<string>(),
            statModifiers = equipment?.statModifiers ?? Array.Empty<StatModifierDefinition>(),
            damageMin = weapon?.damageMin ?? ammo?.damageMin ?? 0f,
            damageMax = weapon?.damageMax ?? ammo?.damageMax ?? 0f,
            damageTypeId = weapon?.damageTypeId ?? ammo?.damageTypeId ?? 0,
            canCrit = weapon?.canCrit ?? ammo?.canCrit ?? true,
            armorPenetrationFlat = weapon?.armorPenetrationFlat ?? ammo?.armorPenetrationFlat ?? 0f,
            armorPenetrationPercent = weapon?.armorPenetrationPercent ?? ammo?.armorPenetrationPercent ?? 0f,
            ammoFamily = weapon?.ammoFamily ?? ammo?.ammoFamily ?? string.Empty,
            firearmMagazineCapacity = weapon?.firearmMagazineCapacity ?? 0,
            firearmFireMode = weapon?.firearmFireMode ?? FirearmFireMode.SemiAutomatic,
            firearmRoundsPerTrigger = weapon?.firearmRoundsPerTrigger ?? 0,
            firearmRoundsPerSecond = weapon?.firearmRoundsPerSecond ?? 0f,
            firearmBaseHitChance = weapon?.firearmBaseHitChance ?? 1f,
            firearmBloomPerShot = weapon?.firearmBloomPerShot ?? 0.035f,
            firearmMaximumBloom = weapon?.firearmMaximumBloom ?? 0.45f,
            firearmBloomRecoveryPerSecond = weapon?.firearmBloomRecoveryPerSecond ?? 0.35f,
            firearmAimBloomMultiplier = weapon?.firearmAimBloomMultiplier ?? 0.65f,
            basicAttackRange = weapon?.basicAttackRange ?? 0f,
            basicAttackInterval = weapon?.basicAttackInterval ?? 0f,
            consumeQuantity = consumable?.consumeQuantity ?? 0,
            useEffects = consumable?.useEffects ?? Array.Empty<ItemUseEffectDefinition>(),
        };
    }

    private void LoadOptionalItems(string relativePath, ItemKind expectedKind, EquipmentItemSubtype expectedSubtype, List<ItemDefinition> output)
    {
        string path = Resolve(relativePath);
        if (!File.Exists(path))
            return;

        ItemCatalogDocument document = ReadOptionalDocument<ItemCatalogDocument>(relativePath);
        ItemAuthoringDefinition[] definitions = document?.items ?? Array.Empty<ItemAuthoringDefinition>();
        for (int i = 0; i < definitions.Length; ++i)
        {
            ItemAuthoringDefinition authored = definitions[i]
                ?? throw new InvalidOperationException($"{relativePath}: item {i} is null.");
            if (authored.kind != expectedKind || authored.subtype != expectedSubtype)
                throw new InvalidOperationException($"{relativePath}: item '{authored.definitionId ?? "<missing>"}' must use kind '{expectedKind}' and subtype '{expectedSubtype}'.");
            output.Add(ConvertItem(relativePath, authored));
        }
    }

    private static T[] Concat<T>(T[] first, T[] second)
    {
        first ??= Array.Empty<T>();
        second ??= Array.Empty<T>();
        if (first.Length == 0) return second;
        if (second.Length == 0) return first;
        var result = new T[first.Length + second.Length];
        Array.Copy(first, 0, result, 0, first.Length);
        Array.Copy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static CharacterResourceDefinition[] MergeResources(CharacterResourceDefinition[] primary, CharacterResourceDefinition[] additive)
    {
        primary ??= Array.Empty<CharacterResourceDefinition>();
        additive ??= Array.Empty<CharacterResourceDefinition>();
        if (additive.Length == 0) return primary;
        var result = new List<CharacterResourceDefinition>(primary.Length + additive.Length);
        var existing = new HashSet<CharacterResourceId>();
        for (int i = 0; i < primary.Length; ++i)
        {
            CharacterResourceDefinition value = primary[i];
            if (value == null) continue;
            result.Add(value);
            existing.Add(value.id);
        }
        for (int i = 0; i < additive.Length; ++i)
        {
            CharacterResourceDefinition value = additive[i];
            if (value != null && value.id != CharacterResourceId.None && existing.Add(value.id))
                result.Add(value);
        }
        return result.ToArray();
    }

    private T ReadRequiredDocument<T>(string relativePath)
    {
        string path = Resolve(relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required Content Catalog V2 file '{relativePath}' was not found.", path);

        string json = ReadAllTextShared(path);
        T document = JsonSerializer.Deserialize<T>(json, _json);
        if (document == null)
            throw new InvalidOperationException($"{relativePath} did not contain a valid catalog document.");
        return document;
    }

    private T ReadOptionalDocument<T>(string relativePath) where T : class
    {
        string path = Resolve(relativePath);
        if (!File.Exists(path))
            return null;

        string json = ReadAllTextShared(path);
        T document = JsonSerializer.Deserialize<T>(json, _json);
        if (document == null)
            throw new InvalidOperationException($"{relativePath} did not contain a valid catalog document.");
        return document;
    }

    private string Resolve(string relativePath)
    {
        string combined = Path.GetFullPath(Path.Combine(
            _contentRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        string rootWithSeparator = _contentRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Catalog path '{relativePath}' escapes the content root.");

        return combined;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static bool TryReadCatalogVersion(JsonElement root, out int value)
    {
        value = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "catalogVersion", StringComparison.OrdinalIgnoreCase))
                continue;
            return property.Value.ValueKind == JsonValueKind.Number &&
                   property.Value.TryGetInt32(out value);
        }
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void RejectLegacyInlineCollection(JsonElement root, string propertyName)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GameplayContent.json must not contain legacy inline '{propertyName}' in Content Catalog V2.");
            }
        }
    }

    private readonly struct CatalogFileSpec
    {
        public CatalogFileSpec(string relativePath, ItemKind expectedKind, EquipmentItemSubtype expectedSubtype)
        {
            RelativePath = relativePath;
            ExpectedKind = expectedKind;
            ExpectedSubtype = expectedSubtype;
        }

        public string RelativePath { get; }
        public ItemKind ExpectedKind { get; }
        public EquipmentItemSubtype ExpectedSubtype { get; }
    }

    private sealed class ItemCatalogDocument
    {
        public ItemAuthoringDefinition[] items { get; set; }
    }

    private sealed class AbilityCatalogDocument
    {
        public AbilityDefinition[] abilities { get; set; }
    }

    private sealed class StatusEffectCatalogDocument
    {
        public StatusEffectDefinition[] statusEffects { get; set; }
    }

    private sealed class DamageTypeCatalogDocument
    {
        public DamageTypeDefinition[] damageTypes { get; set; }
    }

    private sealed class ResourceCatalogDocument
    {
        public CharacterResourceDefinition[] resources { get; set; }
    }

    private sealed class LootCatalogDocument
    {
        public LootTableDefinition[] lootTables { get; set; }
    }

    private sealed class HarvestCatalogDocument
    {
        public HarvestProfileDefinition[] harvestProfiles { get; set; }
    }

    private sealed class CraftingCatalogDocument
    {
        public RecipeDefinition[] recipes { get; set; }
        public CraftingStationDefinition[] stations { get; set; }
    }

    private sealed class ProgressCatalogDocument
    {
        public ProgressionRulesDefinition progression { get; set; }
        public ProgressTrackDefinition[] skills { get; set; }
    }

    private sealed class FactionCatalogDocument
    {
        public FactionDefinition[] factions { get; set; }
        public FactionRelationshipDefinition[] relationships { get; set; }
        public JurisdictionDefinition[] jurisdictions { get; set; }
        public IncidentDefinition[] incidents { get; set; }
    }

    private sealed class ItemAuthoringDefinition
    {
        public string definitionId { get; set; }
        public string displayName { get; set; }
        public ItemKind kind { get; set; }
        public EquipmentItemSubtype subtype { get; set; }
        public ushort presentationId { get; set; }
        public int maxStack { get; set; }
        public float weight { get; set; }
        public int maxDurability { get; set; }
        public string[] tags { get; set; }
        public HarvestCapability harvestCapabilities { get; set; }
        public byte harvestToolTier { get; set; }
        public EquipmentItemAuthoringSection equipment { get; set; }
        public WeaponItemAuthoringSection weapon { get; set; }
        public ConsumableItemAuthoringSection consumable { get; set; }
        public AmmoItemAuthoringSection ammo { get; set; }
    }

    private sealed class EquipmentItemAuthoringSection
    {
        public string[] allowedEquipmentSlots { get; set; }
        public StatModifierDefinition[] statModifiers { get; set; }
    }

    private sealed class WeaponItemAuthoringSection
    {
        public float damageMin { get; set; }
        public float damageMax { get; set; }
        public ushort damageTypeId { get; set; }
        public bool canCrit { get; set; } = true;
        public float armorPenetrationFlat { get; set; }
        public float armorPenetrationPercent { get; set; }
        public string ammoFamily { get; set; }
        public int firearmMagazineCapacity { get; set; }
        public FirearmFireMode firearmFireMode { get; set; } = FirearmFireMode.SemiAutomatic;
        public int firearmRoundsPerTrigger { get; set; }
        public float firearmRoundsPerSecond { get; set; }
        public float firearmBaseHitChance { get; set; } = 1f;
        public float firearmBloomPerShot { get; set; } = 0.035f;
        public float firearmMaximumBloom { get; set; } = 0.45f;
        public float firearmBloomRecoveryPerSecond { get; set; } = 0.35f;
        public float firearmAimBloomMultiplier { get; set; } = 0.65f;
        public float basicAttackRange { get; set; }
        public float basicAttackInterval { get; set; }
    }

    private sealed class ConsumableItemAuthoringSection
    {
        public int consumeQuantity { get; set; }
        public ItemUseEffectDefinition[] useEffects { get; set; }
    }

    private sealed class AmmoItemAuthoringSection
    {
        public string ammoFamily { get; set; }
        public float damageMin { get; set; }
        public float damageMax { get; set; }
        public ushort damageTypeId { get; set; }
        public bool canCrit { get; set; } = true;
        public float armorPenetrationFlat { get; set; }
        public float armorPenetrationPercent { get; set; }
    }
}
