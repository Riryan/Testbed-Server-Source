using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMODashboard;

internal sealed class ExistingItemRow
{
    public string DefinitionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int MaxStack { get; init; }
    public int PresentationId { get; init; }
}

internal sealed class ItemStatModifierDraft
{
    public string StatId { get; set; } = string.Empty;
    public double Additive { get; set; }
    public double Multiplier { get; set; } = 1d;
}

internal sealed class ItemUseEffectDraft
{
    public string Kind { get; set; } = "RestoreResource";
    public string ResourceToken { get; set; } = "0";
    public int Amount { get; set; }
    public string StatusEffectId { get; set; } = string.Empty;
    public int Stacks { get; set; } = 1;
}

internal sealed class ResourceChoice
{
    public string Token { get; init; } = "0";
    public string Display { get; init; } = string.Empty;
}

internal sealed class ItemDraft
{
    public string DefinitionId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PresentationId { get; set; }
    public int MaxStack { get; set; } = 1;
    public double Weight { get; set; }
    public int MaxDurability { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string[] AllowedEquipmentSlots { get; set; } = Array.Empty<string>();
    public ItemStatModifierDraft[] StatModifiers { get; set; } = Array.Empty<ItemStatModifierDraft>();
    public double DamageMin { get; set; }
    public double DamageMax { get; set; }
    public string AmmoFamily { get; set; } = string.Empty;
    public double BasicAttackRange { get; set; }
    public double BasicAttackInterval { get; set; }
    public int ConsumeQuantity { get; set; }
    public ItemUseEffectDraft[] UseEffects { get; set; } = Array.Empty<ItemUseEffectDraft>();
}

internal sealed record ItemSaveAnalysis(bool RequiresRestart, string RestartReason);

internal sealed class GameplayItemEditor
{
    private readonly JsonObject _root;
    private readonly JsonArray _items;
    private readonly JsonArray _slots;
    private readonly JsonArray _resources;
    private readonly JsonArray _statuses;
    private readonly JsonArray _starters;

    public AdminDocument Document { get; private set; }

    private GameplayItemEditor(AdminDocument document, JsonObject root)
    {
        Document = document;
        _root = root;
        _items = EnsureArray(root, "items");
        _slots = EnsureArray(root, "equipmentSlots");
        _resources = EnsureArray(root, "resources");
        _statuses = EnsureArray(root, "statusEffects");
        _starters = EnsureArray(root, "starterItems");
    }

    public static GameplayItemEditor Parse(AdminDocument document)
    {
        JsonNode? node = JsonNode.Parse(document.Content, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        JsonObject root = node as JsonObject ?? throw new InvalidOperationException("GameplayContent.json must contain a JSON object.");
        if (ReadLong(root["revision"], 0) <= 0)
            throw new InvalidOperationException("GameplayContent.json has an invalid or missing positive revision.");
        return new GameplayItemEditor(document, root);
    }

    public IReadOnlyList<ExistingItemRow> GetItems()
    {
        var result = new List<ExistingItemRow>();
        foreach (JsonNode? node in _items)
        {
            if (node is not JsonObject item) continue;
            result.Add(new ExistingItemRow
            {
                DefinitionId = ReadString(item["definitionId"]),
                DisplayName = ReadString(item["displayName"]),
                MaxStack = ReadInt(item["maxStack"], 1),
                PresentationId = ReadInt(item["presentationId"], 0),
            });
        }
        result.Sort((a, b) => string.Compare(a.DefinitionId, b.DefinitionId, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public IReadOnlyList<string> GetEquipmentSlots()
    {
        var result = new List<(int Order, string Id)>();
        foreach (JsonNode? node in _slots)
        {
            if (node is not JsonObject slot) continue;
            string id = ReadString(slot["slotId"]);
            if (id.Length > 0) result.Add((ReadInt(slot["order"], 0), id));
        }
        return result.OrderBy(v => v.Order).ThenBy(v => v.Id, StringComparer.Ordinal).Select(v => v.Id).ToArray();
    }

    public IReadOnlyList<ResourceChoice> GetResourceChoices()
    {
        var result = new List<ResourceChoice> { new() { Token = "0", Display = "None (0)" } };
        foreach (JsonNode? node in _resources)
        {
            if (node is not JsonObject resource) continue;
            JsonNode? id = resource["id"];
            if (id is null) continue;
            string token = id.ToJsonString();
            string displayName = ReadString(resource["displayName"]);
            string friendlyToken = token.Trim('\"');
            result.Add(new ResourceChoice { Token = token, Display = $"{displayName} ({friendlyToken})" });
        }
        return result;
    }

    public IReadOnlyList<string> GetStatusEffectIds()
    {
        return _statuses
            .OfType<JsonObject>()
            .Select(s => ReadString(s["definitionId"]))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    public ItemDraft LoadDraft(string definitionId)
    {
        JsonObject item = FindItem(definitionId) ?? throw new InvalidOperationException($"Item '{definitionId}' no longer exists in the loaded content.");
        var draft = new ItemDraft
        {
            DefinitionId = ReadString(item["definitionId"]),
            DisplayName = ReadString(item["displayName"]),
            PresentationId = ReadInt(item["presentationId"], 0),
            MaxStack = ReadInt(item["maxStack"], 1),
            Weight = ReadDouble(item["weight"], 0),
            MaxDurability = ReadInt(item["maxDurability"], 0),
            Tags = ReadStringArray(item["tags"]),
            AllowedEquipmentSlots = ReadStringArray(item["allowedEquipmentSlots"]),
            DamageMin = ReadDouble(item["damageMin"], 0),
            DamageMax = ReadDouble(item["damageMax"], 0),
            AmmoFamily = ReadString(item["ammoFamily"]),
            BasicAttackRange = ReadDouble(item["basicAttackRange"], 0),
            BasicAttackInterval = ReadDouble(item["basicAttackInterval"], 0),
            ConsumeQuantity = ReadInt(item["consumeQuantity"], 0),
            StatModifiers = ReadModifiers(item["statModifiers"]),
            UseEffects = ReadEffects(item["useEffects"]),
        };
        return draft;
    }

    public ItemSaveAnalysis AnalyzeAndApply(ItemDraft draft, string? originalDefinitionId)
    {
        ValidateDraft(draft, originalDefinitionId);

        JsonObject? existing = string.IsNullOrWhiteSpace(originalDefinitionId) ? null : FindItem(originalDefinitionId!);
        bool requiresRestart = existing is not null && HasStructuralChange(existing, draft);
        string reason = requiresRestart
            ? "Existing item stack/durability, equipment-slot compatibility, or use-effect structure changed. The live Gateway rejects those structural hot-reload changes; restart Gateway/GameServer after saving."
            : string.Empty;

        JsonObject item = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        WriteDraft(item, draft);

        if (existing is null)
        {
            _items.Add(item);
        }
        else
        {
            int index = _items.IndexOf(existing);
            if (index < 0) throw new InvalidOperationException("The selected item could not be replaced.");
            _items[index] = item;
        }

        long revision = ReadLong(_root["revision"], 0);
        if (revision <= 0 || revision == long.MaxValue)
            throw new InvalidOperationException("Gameplay content revision cannot be incremented.");
        _root["revision"] = revision + 1;
        return new ItemSaveAnalysis(requiresRestart, reason);
    }

    public string BuildContent() => _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    public void ReplaceDocument(AdminDocument document) => Document = document;

    private void ValidateDraft(ItemDraft draft, string? originalDefinitionId)
    {
        if (!IsStableId(draft.DefinitionId)) throw new InvalidOperationException("Definition ID must be 1-96 characters using only letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(draft.DisplayName)) throw new InvalidOperationException("Display name is required.");
        if (draft.PresentationId < 0 || draft.PresentationId > ushort.MaxValue) throw new InvalidOperationException("Presentation ID must be 0-65535.");
        if (draft.MaxStack < 1 || draft.MaxStack > 1_000_000) throw new InvalidOperationException("Max stack must be 1-1,000,000.");
        if (!IsFiniteFloat(draft.Weight) || draft.Weight < 0) throw new InvalidOperationException("Weight must be a non-negative finite float value.");
        if (draft.MaxDurability < 0) throw new InvalidOperationException("Max durability cannot be negative.");
        if (!IsFiniteFloat(draft.DamageMin) || !IsFiniteFloat(draft.DamageMax) || draft.DamageMin < 0 || draft.DamageMax < draft.DamageMin) throw new InvalidOperationException("Damage range is invalid.");
        if (!IsFiniteFloat(draft.BasicAttackRange) || draft.BasicAttackRange < 0) throw new InvalidOperationException("Basic attack range must be a non-negative finite float value.");
        if (!IsFiniteFloat(draft.BasicAttackInterval) || draft.BasicAttackInterval < 0 || (draft.BasicAttackInterval > 0 && (draft.BasicAttackInterval < 1.25 || draft.BasicAttackInterval > 10))) throw new InvalidOperationException("Basic attack interval must be 0 (use default) or 1.25-10 seconds.");
        if (draft.ConsumeQuantity < 0 || draft.ConsumeQuantity > draft.MaxStack) throw new InvalidOperationException("Consume quantity must be 0 through max stack.");
        if ((draft.ConsumeQuantity == 0) != (draft.UseEffects.Length == 0)) throw new InvalidOperationException("Consumable items must define both Consume Quantity and at least one Use Effect; non-consumables must define neither.");
        if (draft.UseEffects.Length > 16) throw new InvalidOperationException("An item can have at most 16 use effects.");

        foreach (JsonNode? starterNode in _starters)
        {
            if (starterNode is not JsonObject starter ||
                !string.Equals(ReadString(starter["definitionId"]), draft.DefinitionId, StringComparison.Ordinal))
                continue;
            int starterQuantity = ReadInt(starter["quantity"], 0);
            if (starterQuantity > draft.MaxStack)
                throw new InvalidOperationException($"Max Stack cannot be lower than starter quantity {starterQuantity} for '{draft.DefinitionId}'.");
        }

        if (!string.IsNullOrWhiteSpace(originalDefinitionId) &&
            !string.Equals(draft.DefinitionId, originalDefinitionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Existing item Definition IDs are stable and cannot be renamed. Use Duplicate/New Item to create a new ID instead.");
        }

        JsonObject? duplicateId = FindItem(draft.DefinitionId);
        if (duplicateId is not null && !string.Equals(ReadString(duplicateId["definitionId"]), originalDefinitionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"An item with definition ID '{draft.DefinitionId}' already exists.");

        if (draft.PresentationId != 0)
        {
            foreach (JsonNode? node in _items)
            {
                if (node is not JsonObject item) continue;
                string id = ReadString(item["definitionId"]);
                if (string.Equals(id, originalDefinitionId, StringComparison.Ordinal)) continue;
                if (ReadInt(item["presentationId"], 0) == draft.PresentationId)
                    throw new InvalidOperationException($"Presentation ID {draft.PresentationId} is already used by '{id}'.");
            }
        }

        var slotIds = new HashSet<string>(GetEquipmentSlots(), StringComparer.Ordinal);
        foreach (string slot in draft.AllowedEquipmentSlots)
            if (!slotIds.Contains(slot)) throw new InvalidOperationException($"Unknown equipment slot '{slot}'.");

        foreach (ItemStatModifierDraft modifier in draft.StatModifiers)
        {
            if (!IsStableId(modifier.StatId)) throw new InvalidOperationException("Every stat modifier requires a valid stable Stat ID.");
            if (!IsFiniteFloat(modifier.Additive) || !IsFiniteFloat(modifier.Multiplier) || modifier.Multiplier < 0) throw new InvalidOperationException($"Stat modifier '{modifier.StatId}' has invalid float values.");
        }

        var resourceTokens = new HashSet<string>(GetResourceChoices().Select(r => r.Token), StringComparer.Ordinal);
        var statusIds = new HashSet<string>(GetStatusEffectIds(), StringComparer.Ordinal);
        foreach (ItemUseEffectDraft effect in draft.UseEffects)
        {
            if (effect.Stacks < 1 || effect.Stacks > byte.MaxValue) throw new InvalidOperationException("Use effect stacks must be 1-255.");
            if (effect.Amount < 0) throw new InvalidOperationException("Use effect amount cannot be negative.");
            if (string.Equals(effect.Kind, "RestoreResource", StringComparison.Ordinal))
            {
                if (effect.Amount <= 0) throw new InvalidOperationException("Restore Resource effects require Amount > 0.");
                if (effect.ResourceToken == "0" || !resourceTokens.Contains(effect.ResourceToken)) throw new InvalidOperationException("Restore Resource effects must select a resource defined by the server content.");
            }
            else if (string.Equals(effect.Kind, "ApplyStatusEffect", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(effect.StatusEffectId) || !statusIds.Contains(effect.StatusEffectId)) throw new InvalidOperationException("Apply Status Effect must select a status effect defined by the server content.");
            }
            else
            {
                throw new InvalidOperationException($"Unsupported use effect kind '{effect.Kind}'.");
            }
        }
    }

    private static void WriteDraft(JsonObject item, ItemDraft draft)
    {
        item["definitionId"] = draft.DefinitionId.Trim();
        item["displayName"] = draft.DisplayName.Trim();
        item["presentationId"] = draft.PresentationId;
        item["maxStack"] = draft.MaxStack;
        item["weight"] = draft.Weight;
        item["maxDurability"] = draft.MaxDurability;
        item["tags"] = ToStringArray(draft.Tags);
        item["allowedEquipmentSlots"] = ToStringArray(draft.AllowedEquipmentSlots);

        var modifiers = new JsonArray();
        foreach (ItemStatModifierDraft modifier in draft.StatModifiers)
        {
            modifiers.Add(new JsonObject
            {
                ["statId"] = modifier.StatId.Trim(),
                ["additive"] = modifier.Additive,
                ["multiplier"] = modifier.Multiplier,
            });
        }
        item["statModifiers"] = modifiers;
        item["damageMin"] = draft.DamageMin;
        item["damageMax"] = draft.DamageMax;
        item["ammoFamily"] = draft.AmmoFamily?.Trim() ?? string.Empty;
        item["basicAttackRange"] = draft.BasicAttackRange;
        item["basicAttackInterval"] = draft.BasicAttackInterval;
        item["consumeQuantity"] = draft.ConsumeQuantity;

        var effects = new JsonArray();
        foreach (ItemUseEffectDraft effect in draft.UseEffects)
        {
            JsonNode? resource = ParseToken(effect.ResourceToken);
            effects.Add(new JsonObject
            {
                ["kind"] = string.Equals(effect.Kind, "ApplyStatusEffect", StringComparison.Ordinal) ? 2 : 1,
                ["resourceId"] = resource,
                ["amount"] = effect.Amount,
                ["statusEffectId"] = effect.StatusEffectId?.Trim() ?? string.Empty,
                ["stacks"] = effect.Stacks,
            });
        }
        item["useEffects"] = effects;
    }

    private bool HasStructuralChange(JsonObject existing, ItemDraft draft)
    {
        if (ReadInt(existing["maxStack"], 1) != draft.MaxStack) return true;
        if (ReadInt(existing["maxDurability"], 0) != draft.MaxDurability) return true;
        if (!SameSet(ReadStringArray(existing["allowedEquipmentSlots"]), draft.AllowedEquipmentSlots)) return true;
        if (ReadInt(existing["consumeQuantity"], 0) != draft.ConsumeQuantity) return true;
        ItemUseEffectDraft[] oldEffects = ReadEffects(existing["useEffects"]);
        if (oldEffects.Length != draft.UseEffects.Length) return true;
        for (int i = 0; i < oldEffects.Length; ++i)
        {
            if (!string.Equals(oldEffects[i].Kind, draft.UseEffects[i].Kind, StringComparison.Ordinal) ||
                !string.Equals(oldEffects[i].ResourceToken, draft.UseEffects[i].ResourceToken, StringComparison.Ordinal) ||
                !string.Equals(oldEffects[i].StatusEffectId ?? string.Empty, draft.UseEffects[i].StatusEffectId ?? string.Empty, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private JsonObject? FindItem(string definitionId)
    {
        foreach (JsonNode? node in _items)
            if (node is JsonObject item && string.Equals(ReadString(item["definitionId"]), definitionId, StringComparison.Ordinal))
                return item;
        return null;
    }

    private static ItemStatModifierDraft[] ReadModifiers(JsonNode? node)
    {
        if (node is not JsonArray array) return Array.Empty<ItemStatModifierDraft>();
        return array.OfType<JsonObject>().Select(m => new ItemStatModifierDraft
        {
            StatId = ReadString(m["statId"]),
            Additive = ReadDouble(m["additive"], 0),
            Multiplier = ReadDouble(m["multiplier"], 1),
        }).ToArray();
    }

    private static ItemUseEffectDraft[] ReadEffects(JsonNode? node)
    {
        if (node is not JsonArray array) return Array.Empty<ItemUseEffectDraft>();
        return array.OfType<JsonObject>().Select(e => new ItemUseEffectDraft
        {
            Kind = ReadInt(e["kind"], 0) == 2 ? "ApplyStatusEffect" : "RestoreResource",
            ResourceToken = e["resourceId"]?.ToJsonString() ?? "0",
            Amount = ReadInt(e["amount"], 0),
            StatusEffectId = ReadString(e["statusEffectId"]),
            Stacks = ReadInt(e["stacks"], 1),
        }).ToArray();
    }

    private static JsonArray ToStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal))
            array.Add(value);
        return array;
    }

    private static JsonNode? ParseToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return JsonValue.Create(0);
        try { return JsonNode.Parse(token); }
        catch { return JsonValue.Create(token); }
    }

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);

    private static JsonArray EnsureArray(JsonObject root, string name)
    {
        if (root[name] is JsonArray array) return array;
        array = new JsonArray();
        root[name] = array;
        return array;
    }

    private static string[] ReadStringArray(JsonNode? node) => node is JsonArray array
        ? array.Select(v => v is null ? string.Empty : ReadString(v)).Where(v => v.Length > 0).ToArray()
        : Array.Empty<string>();

    private static string ReadString(JsonNode? node)
    {
        if (node is null) return string.Empty;
        try { return node.GetValue<string>() ?? string.Empty; }
        catch { return node.ToJsonString().Trim('"'); }
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null) return fallback;
        try { return node.GetValue<int>(); }
        catch
        {
            return int.TryParse(ReadString(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }
    }

    private static long ReadLong(JsonNode? node, long fallback)
    {
        if (node is null) return fallback;
        try { return node.GetValue<long>(); }
        catch
        {
            return long.TryParse(ReadString(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;
        }
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        if (node is null) return fallback;
        try { return node.GetValue<double>(); }
        catch
        {
            return double.TryParse(ReadString(node), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
        }
    }

    private static bool IsFiniteFloat(double value) => double.IsFinite(value) && value >= -float.MaxValue && value <= float.MaxValue;

    internal static bool IsStableId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96) return false;
        foreach (char c in value)
            if (!(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        return true;
    }
}
