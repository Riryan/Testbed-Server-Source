using System.Text.Json;
using Game.Shared.Authentication;
using Game.Shared.Backend;
using Game.Shared.Characters;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.Progression;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    // Item/world-item transaction domain and shared item mutation validation helpers.
    public BackendWorldItemsLoadResponse LoadWorldItems()
    {
        return Execute(conn =>
        {
            List<WorldItemWorldRow> worldRows = conn.Query<WorldItemWorldRow>("SELECT * FROM world_item_worlds ORDER BY worldKey ASC");
            var worlds = new BackendWorldItemWorldDto[worldRows.Count];
            for (int i = 0; i < worldRows.Count; ++i)
            {
                WorldItemWorldRow world = worldRows[i];
                List<WorldItemRow> items = conn.Query<WorldItemRow>("SELECT * FROM world_items WHERE worldKey=? ORDER BY itemInstanceId ASC", world.worldKey);
                var dtoItems = new BackendWorldItemDto[items.Count];
                for (int j = 0; j < items.Count; ++j) dtoItems[j] = ToWorldItemDto(items[j]);
                worlds[i] = new BackendWorldItemWorldDto { mapId = world.mapId, instanceId = world.instanceId, revision = world.revision, items = dtoItems };
            }
            return new BackendWorldItemsLoadResponse { success = true, error = string.Empty, worlds = worlds };
        });
    }

    public BackendPlayerItemDropResponse DropPlayerItem(BackendPlayerItemDropRequest request, GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.sourceItemInstanceId <= 0 || request.dropQuantity < 1 ||
            (request.splitWorldItemInstanceId != 0 && request.splitWorldItemInstanceId < BackendServiceContracts.TransientWorldItemIdFloor) ||
            request.sourceLoadedRounds < 0 || request.sourceMagazineRevision < 0 || request.state == null ||
            !IsValidWorldItemLocation(request.mapId, request.instanceId, request.positionX, request.positionY, request.positionZ) ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return PlayerItemDropFailed(false, 0, 0, 0, string.IsNullOrWhiteSpace(contentError) ? "invalid drop transaction" : contentError);

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerItemDropFailed(false, 0, 0, 0, "character authority lease unavailable");

        return Execute(conn =>
        {
            BackendPlayerItemDropResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null) { response = PlayerItemDropFailed(false, 0, 0, 0, "character not found"); return; }
                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null) { response = PlayerItemDropFailed(false, 0, 0, 0, "player-system state unavailable"); return; }
                if (request.expectedInventoryRevision != stored.inventoryRevision || request.expectedEquipmentRevision != stored.equipmentRevision)
                { response = PlayerItemDropFailed(true, stored.inventoryRevision, stored.equipmentRevision, 0, "stale player-system revision"); return; }

                if (!ValidatePlayerItemDropMutation(conn, request.characterId, stored, request.sourceItemInstanceId, request.dropQuantity, request.state, content, out CharacterItemRow source, out string error))
                { response = PlayerItemDropFailed(false, stored.inventoryRevision, stored.equipmentRevision, 0, error); return; }

                ItemDefinition sourceDefinition = FindItemDefinition(content, source.definitionId);
                var sourceMagazine = new BackendPersistedItemDto
                {
                    magazineRevision = request.sourceMagazineRevision,
                    loadedAmmoDefinitionId = request.sourceLoadedAmmoDefinitionId,
                    loadedRounds = request.sourceLoadedRounds,
                };
                if (!ValidateNonIncreasingMagazineTransition(source, sourceMagazine, sourceDefinition, content, out error))
                { response = PlayerItemDropFailed(false, stored.inventoryRevision, stored.equipmentRevision, 0, error); return; }

                bool requestMagazineIsNewerOrEqual = request.sourceMagazineRevision >= Math.Max(0, source.magazineRevision);
                string sourceLoadedAmmo = requestMagazineIsNewerOrEqual
                    ? NormalizeLoadedAmmo(request.sourceLoadedAmmoDefinitionId, request.sourceLoadedRounds)
                    : NormalizeLoadedAmmo(source.loadedAmmoDefinitionId, source.loadedRounds);
                int sourceLoadedRounds = requestMagazineIsNewerOrEqual ? request.sourceLoadedRounds : Math.Max(0, source.loadedRounds);
                long sourceMagazineRevision = requestMagazineIsNewerOrEqual ? request.sourceMagazineRevision : Math.Max(0, source.magazineRevision);

                // Ground drops are transient GameServer runtime state. Backend still validates
                // and persists the inventory removal atomically. Split stacks arrive with a
                // GameServer-owned transient id so a lost HTTP response cannot orphan the
                // committed drop. No durable world_items row or world revision is created.
                bool fullStack = request.dropQuantity == source.quantity;
                if (fullStack && request.splitWorldItemInstanceId != 0)
                {
                    response = PlayerItemDropFailed(false, stored.inventoryRevision, stored.equipmentRevision, 0, "full-stack drop cannot supply a split transient id");
                    return;
                }
                if (!fullStack && request.splitWorldItemInstanceId < BackendServiceContracts.TransientWorldItemIdFloor)
                {
                    response = PlayerItemDropFailed(false, stored.inventoryRevision, stored.equipmentRevision, 0, "split-stack drop requires a transient world item id");
                    return;
                }

                long worldItemId = fullStack ? source.itemInstanceId : request.splitWorldItemInstanceId;
                if (!fullStack && conn.Find<CharacterItemRow>(worldItemId) != null)
                {
                    response = PlayerItemDropFailed(false, stored.inventoryRevision, stored.equipmentRevision, 0, "transient world item id collides with durable inventory identity");
                    return;
                }
                long worldItemRevision = fullStack ? checked(source.revision + 1) : 0;

                ApplyPlayerSystemsMutation(conn, request.characterId, request.state);
                var worldRow = new WorldItemRow
                {
                    itemInstanceId = worldItemId, worldKey = string.Empty, mapId = request.mapId, instanceId = request.instanceId ?? string.Empty,
                    definitionId = source.definitionId, quantity = request.dropQuantity, durability = source.durability, revision = worldItemRevision,
                    loadedAmmoDefinitionId = sourceLoadedAmmo,
                    loadedRounds = sourceLoadedRounds,
                    magazineRevision = sourceMagazineRevision,
                    positionX = request.positionX, positionY = request.positionY, positionZ = request.positionZ,
                };

                stored.inventoryRevision = request.state.inventoryRevision;
                stored.equipmentRevision = request.state.equipmentRevision;
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                conn.Update(stored);

                response = new BackendPlayerItemDropResponse
                {
                    success = true, accepted = true, stale = false,
                    storedInventoryRevision = stored.inventoryRevision, storedEquipmentRevision = stored.equipmentRevision,
                    worldRevision = 0, worldItem = ToWorldItemDto(worldRow), error = string.Empty,
                };
            });
            return response ?? PlayerItemDropFailed(false, 0, 0, 0, "drop transaction unavailable");
        });
    }

    public BackendPlayerItemGrantResponse GrantPlayerItem(
        BackendPlayerItemGrantRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (request == null || request.accountId <= 0 || request.characterId <= 0 ||
            string.IsNullOrWhiteSpace(request.definitionId) || request.quantity < 1 ||
            request.quantity > 1000000 ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
        {
            return PlayerItemGrantFailed(
                false, 0, 0,
                string.IsNullOrWhiteSpace(contentError) ? "invalid grant transaction" : contentError);
        }

        ItemDefinition definition = FindItemDefinition(content, request.definitionId);
        if (definition == null)
            return PlayerItemGrantFailed(false, 0, 0, "item definition is unavailable");

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerItemGrantFailed(false, 0, 0, "character authority lease unavailable");

        return Execute(conn =>
        {
            BackendPlayerItemGrantResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null)
                {
                    response = PlayerItemGrantFailed(false, 0, 0, "character not found");
                    return;
                }

                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null)
                {
                    response = PlayerItemGrantFailed(false, 0, 0, "player-system state unavailable");
                    return;
                }

                if (request.expectedInventoryRevision != stored.inventoryRevision ||
                    request.expectedEquipmentRevision != stored.equipmentRevision)
                {
                    response = PlayerItemGrantFailed(
                        true,
                        stored.inventoryRevision,
                        stored.equipmentRevision,
                        "stale player-system revision");
                    return;
                }

                List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
                    "SELECT * FROM character_items WHERE characterId=? AND containerKind=? ORDER BY inventorySlot ASC, itemInstanceId ASC",
                    request.characterId,
                    InventoryContainer);

                bool[] occupied = new bool[stored.inventoryCapacity];
                for (int i = 0; i < rows.Count; ++i)
                {
                    CharacterItemRow row = rows[i];
                    if (row.inventorySlot >= 0 && row.inventorySlot < occupied.Length)
                        occupied[row.inventorySlot] = true;
                }

                int remaining = request.quantity;
                int maxStack = Math.Max(1, definition.maxStack);
                for (int i = 0; i < rows.Count && remaining > 0; ++i)
                {
                    CharacterItemRow row = rows[i];
                    if (!string.Equals(row.definitionId, definition.definitionId, StringComparison.Ordinal) ||
                        row.durability != definition.maxDurability ||
                        row.quantity >= maxStack)
                        continue;

                    int room = maxStack - row.quantity;
                    int add = Math.Min(room, remaining);
                    remaining -= add;
                }

                int emptySlots = 0;
                for (int i = 0; i < occupied.Length; ++i)
                    if (!occupied[i]) emptySlots++;

                int requiredNewStacks = remaining <= 0 ? 0 : (remaining + maxStack - 1) / maxStack;
                if (requiredNewStacks > emptySlots)
                {
                    response = PlayerItemGrantFailed(
                        false,
                        stored.inventoryRevision,
                        stored.equipmentRevision,
                        "inventory cannot hold the granted item");
                    return;
                }

                remaining = request.quantity;
                for (int i = 0; i < rows.Count && remaining > 0; ++i)
                {
                    CharacterItemRow row = rows[i];
                    if (!string.Equals(row.definitionId, definition.definitionId, StringComparison.Ordinal) ||
                        row.durability != definition.maxDurability ||
                        row.quantity >= maxStack)
                        continue;

                    int room = maxStack - row.quantity;
                    int add = Math.Min(room, remaining);
                    if (add <= 0) continue;

                    row.quantity = checked(row.quantity + add);
                    row.revision = checked(row.revision + 1);
                    if (conn.Update(row) != 1)
                        throw new InvalidOperationException("Failed to update granted item stack.");
                    remaining -= add;
                }

                for (int slot = 0; slot < occupied.Length && remaining > 0; ++slot)
                {
                    if (occupied[slot]) continue;
                    int add = Math.Min(maxStack, remaining);
                    var row = new CharacterItemRow
                    {
                        itemInstanceId = AllocateItemInstanceId(conn),
                        characterId = request.characterId,
                        containerKind = InventoryContainer,
                        inventorySlot = slot,
                        equipmentSlotId = string.Empty,
                        definitionId = definition.definitionId,
                        quantity = add,
                        durability = definition.maxDurability,
                        revision = 0,
                    };
                    if (conn.Insert(row) != 1 || row.itemInstanceId <= 0)
                        throw new InvalidOperationException("Failed to create granted item instance.");
                    occupied[slot] = true;
                    remaining -= add;
                }

                if (remaining != 0)
                    throw new InvalidOperationException("Grant inventory planning mismatch.");

                stored.inventoryRevision = checked(stored.inventoryRevision + 1);
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                if (conn.Update(stored) != 1)
                    throw new InvalidOperationException("Failed to advance inventory revision after grant.");

                response = new BackendPlayerItemGrantResponse
                {
                    success = true,
                    accepted = true,
                    stale = false,
                    storedInventoryRevision = stored.inventoryRevision,
                    storedEquipmentRevision = stored.equipmentRevision,
                    error = string.Empty,
                    state = ReadPlayerSystems(conn, request.characterId),
                };
            });

            return response ?? PlayerItemGrantFailed(false, 0, 0, "grant transaction unavailable");
        });
    }

    public BackendPlayerItemBundleGrantResponse GrantPlayerItemBundle(
        BackendPlayerItemBundleGrantRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        BackendRewardItemDto[] requested = request?.items ?? Array.Empty<BackendRewardItemDto>();
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || requested.Length == 0 || requested.Length > 64 ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return PlayerItemBundleGrantFailed(false, 0, 0, string.IsNullOrWhiteSpace(contentError) ? "invalid reward bundle transaction" : contentError);
        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerItemBundleGrantFailed(false, 0, 0, "character authority lease unavailable");

        if (!TryAggregateRewardItems(requested, content, out Dictionary<ushort, int> grants, out string grantError))
            return PlayerItemBundleGrantFailed(false, 0, 0, grantError);

        return Execute(conn =>
        {
            BackendPlayerItemBundleGrantResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null) { response = PlayerItemBundleGrantFailed(false, 0, 0, "character not found"); return; }
                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null) { response = PlayerItemBundleGrantFailed(false, 0, 0, "player-system state unavailable"); return; }
                if (request.expectedInventoryRevision != stored.inventoryRevision || request.expectedEquipmentRevision != stored.equipmentRevision)
                { response = PlayerItemBundleGrantFailed(true, stored.inventoryRevision, stored.equipmentRevision, "stale player-system revision"); return; }

                List<CharacterItemRow> original = LoadInventoryRows(conn, request.characterId);
                List<CharacterItemRow> work = CloneInventoryRows(original);
                if (!TryApplyRewardGrants(work, stored.inventoryCapacity, grants, content, request.characterId, out string planningError))
                { response = PlayerItemBundleGrantFailed(false, stored.inventoryRevision, stored.equipmentRevision, planningError); return; }

                ApplyPlannedInventory(conn, request.characterId, original, work);
                stored.inventoryRevision = checked(stored.inventoryRevision + 1);
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                if (conn.Update(stored) != 1) throw new InvalidOperationException("Failed to advance inventory revision after reward bundle.");
                response = new BackendPlayerItemBundleGrantResponse
                {
                    success = true, accepted = true, stale = false,
                    storedInventoryRevision = stored.inventoryRevision,
                    storedEquipmentRevision = stored.equipmentRevision,
                    error = string.Empty,
                    state = ReadPlayerSystems(conn, request.characterId),
                };
            });
            return response ?? PlayerItemBundleGrantFailed(false, 0, 0, "reward bundle transaction unavailable");
        });
    }

    public BackendPlayerCraftResponse CraftPlayerItem(
        BackendPlayerCraftRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.recipeDataId == 0 ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return PlayerCraftFailed(false, 0, 0, string.IsNullOrWhiteSpace(contentError) ? "invalid craft transaction" : contentError);
        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerCraftFailed(false, 0, 0, "character authority lease unavailable");

        RecipeDefinition recipe = FindRecipeDefinition(content, request.recipeDataId);
        if (recipe == null) return PlayerCraftFailed(false, 0, 0, "recipe is unavailable");
        RewardItemDefinition[] outputItems = recipe.rewards?.items ?? Array.Empty<RewardItemDefinition>();
        var outputDtos = new BackendRewardItemDto[outputItems.Length];
        for (int i = 0; i < outputItems.Length; ++i)
            outputDtos[i] = new BackendRewardItemDto { itemDataId = outputItems[i]?.itemDataId ?? (ushort)0, quantity = outputItems[i]?.quantity ?? 0 };
        Dictionary<ushort, int> grants = new Dictionary<ushort, int>();
        if (outputDtos.Length > 0 && !TryAggregateRewardItems(outputDtos, content, out grants, out string grantError))
            return PlayerCraftFailed(false, 0, 0, grantError);

        return Execute(conn =>
        {
            BackendPlayerCraftResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null) { response = PlayerCraftFailed(false, 0, 0, "character not found"); return; }
                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null) { response = PlayerCraftFailed(false, 0, 0, "player-system state unavailable"); return; }
                if (request.expectedInventoryRevision != stored.inventoryRevision || request.expectedEquipmentRevision != stored.equipmentRevision)
                { response = PlayerCraftFailed(true, stored.inventoryRevision, stored.equipmentRevision, "stale player-system revision"); return; }

                List<CharacterItemRow> original = LoadInventoryRows(conn, request.characterId);
                List<CharacterItemRow> work = CloneInventoryRows(original);
                if (!TryConsumeRecipeIngredients(work, recipe, content, out string consumeError))
                { response = PlayerCraftFailed(false, stored.inventoryRevision, stored.equipmentRevision, consumeError); return; }
                work.RemoveAll(row => row == null || row.quantity <= 0);
                if (!TryApplyRewardGrants(work, stored.inventoryCapacity, grants, content, request.characterId, out string planningError))
                { response = PlayerCraftFailed(false, stored.inventoryRevision, stored.equipmentRevision, planningError); return; }

                ApplyPlannedInventory(conn, request.characterId, original, work);
                stored.inventoryRevision = checked(stored.inventoryRevision + 1);
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                if (conn.Update(stored) != 1) throw new InvalidOperationException("Failed to advance inventory revision after craft.");
                response = new BackendPlayerCraftResponse
                {
                    success = true, accepted = true, stale = false,
                    storedInventoryRevision = stored.inventoryRevision,
                    storedEquipmentRevision = stored.equipmentRevision,
                    error = string.Empty,
                    state = ReadPlayerSystems(conn, request.characterId),
                };
            });
            return response ?? PlayerCraftFailed(false, 0, 0, "craft transaction unavailable");
        });
    }

    private static List<CharacterItemRow> LoadInventoryRows(SQLiteConnection conn, long characterId) =>
        conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? AND containerKind=? ORDER BY inventorySlot ASC, itemInstanceId ASC",
            characterId,
            InventoryContainer);

    private static List<CharacterItemRow> CloneInventoryRows(List<CharacterItemRow> source)
    {
        var result = new List<CharacterItemRow>(source?.Count ?? 0);
        if (source == null) return result;
        for (int i = 0; i < source.Count; ++i)
        {
            CharacterItemRow row = source[i];
            if (row == null) continue;
            result.Add(new CharacterItemRow
            {
                itemInstanceId = row.itemInstanceId, characterId = row.characterId, containerKind = row.containerKind,
                inventorySlot = row.inventorySlot, equipmentSlotId = row.equipmentSlotId, definitionId = row.definitionId,
                quantity = row.quantity, durability = row.durability, revision = row.revision,
                loadedAmmoDefinitionId = row.loadedAmmoDefinitionId, loadedRounds = row.loadedRounds, magazineRevision = row.magazineRevision,
            });
        }
        return result;
    }

    private static bool TryAggregateRewardItems(
        BackendRewardItemDto[] items,
        GameplayContentSnapshot content,
        out Dictionary<ushort, int> grants,
        out string error)
    {
        grants = new Dictionary<ushort, int>();
        long total = 0;
        items = items ?? Array.Empty<BackendRewardItemDto>();
        for (int i = 0; i < items.Length; ++i)
        {
            BackendRewardItemDto item = items[i];
            if (item == null || item.itemDataId == 0 || item.quantity < 1 || item.quantity > 1000000 || FindItemDefinition(content, item.itemDataId) == null)
            { error = "reward bundle contains an invalid item"; return false; }
            long next = (long)(grants.TryGetValue(item.itemDataId, out int existing) ? existing : 0) + item.quantity;
            total += item.quantity;
            if (next > 1000000 || total > 4000000) { error = "reward bundle quantity is too large"; return false; }
            grants[item.itemDataId] = (int)next;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryConsumeRecipeIngredients(
        List<CharacterItemRow> work,
        RecipeDefinition recipe,
        GameplayContentSnapshot content,
        out string error)
    {
        var required = new Dictionary<ushort, int>();
        RecipeIngredientDefinition[] ingredients = recipe?.ingredients ?? Array.Empty<RecipeIngredientDefinition>();
        for (int i = 0; i < ingredients.Length; ++i)
        {
            RecipeIngredientDefinition ingredient = ingredients[i];
            if (ingredient == null || ingredient.itemDataId == 0 || ingredient.quantity < 1 || FindItemDefinition(content, ingredient.itemDataId) == null)
            { error = "recipe contains an invalid ingredient"; return false; }
            long next = (long)(required.TryGetValue(ingredient.itemDataId, out int existing) ? existing : 0) + ingredient.quantity;
            if (next > 1000000) { error = "recipe ingredient quantity is too large"; return false; }
            required[ingredient.itemDataId] = (int)next;
        }

        foreach (KeyValuePair<ushort, int> entry in required)
        {
            ItemDefinition definition = FindItemDefinition(content, entry.Key);
            int available = 0;
            for (int i = 0; i < work.Count; ++i)
                if (work[i] != null && string.Equals(work[i].definitionId, definition.definitionId, StringComparison.Ordinal))
                    available = checked(available + Math.Max(0, work[i].quantity));
            if (available < entry.Value) { error = "required crafting ingredients are unavailable"; return false; }

            int remaining = entry.Value;
            for (int i = 0; i < work.Count && remaining > 0; ++i)
            {
                CharacterItemRow row = work[i];
                if (row == null || !string.Equals(row.definitionId, definition.definitionId, StringComparison.Ordinal)) continue;
                int remove = Math.Min(Math.Max(0, row.quantity), remaining);
                row.quantity -= remove;
                remaining -= remove;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool TryApplyRewardGrants(
        List<CharacterItemRow> work,
        int inventoryCapacity,
        Dictionary<ushort, int> grants,
        GameplayContentSnapshot content,
        long characterId,
        out string error)
    {
        if (inventoryCapacity < 0) { error = "inventory capacity is invalid"; return false; }
        bool[] occupied = new bool[inventoryCapacity];
        for (int i = 0; i < work.Count; ++i)
        {
            CharacterItemRow row = work[i];
            if (row != null && row.quantity > 0 && row.inventorySlot >= 0 && row.inventorySlot < occupied.Length)
                occupied[row.inventorySlot] = true;
        }

        foreach (KeyValuePair<ushort, int> grant in grants)
        {
            ItemDefinition definition = FindItemDefinition(content, grant.Key);
            if (definition == null) { error = "reward item is unavailable"; return false; }
            int remaining = grant.Value;
            int maxStack = Math.Max(1, definition.maxStack);
            for (int i = 0; i < work.Count && remaining > 0; ++i)
            {
                CharacterItemRow row = work[i];
                if (row == null || row.quantity <= 0 || !string.Equals(row.definitionId, definition.definitionId, StringComparison.Ordinal) ||
                    row.durability != definition.maxDurability || row.quantity >= maxStack) continue;
                int add = Math.Min(maxStack - row.quantity, remaining);
                row.quantity = checked(row.quantity + add);
                remaining -= add;
            }
            while (remaining > 0)
            {
                int slot = -1;
                for (int i = 0; i < occupied.Length; ++i) if (!occupied[i]) { slot = i; break; }
                if (slot < 0) { error = "inventory cannot hold the reward bundle"; return false; }
                int add = Math.Min(maxStack, remaining);
                work.Add(new CharacterItemRow
                {
                    itemInstanceId = 0, characterId = characterId, containerKind = InventoryContainer,
                    inventorySlot = slot, equipmentSlotId = string.Empty, definitionId = definition.definitionId,
                    quantity = add, durability = definition.maxDurability, revision = 0,
                    loadedAmmoDefinitionId = string.Empty, loadedRounds = 0, magazineRevision = 0,
                });
                occupied[slot] = true;
                remaining -= add;
            }
        }
        error = string.Empty;
        return true;
    }

    private static void ApplyPlannedInventory(
        SQLiteConnection conn,
        long characterId,
        List<CharacterItemRow> original,
        List<CharacterItemRow> work)
    {
        var finalById = new Dictionary<long, CharacterItemRow>();
        for (int i = 0; i < work.Count; ++i)
            if (work[i] != null && work[i].itemInstanceId > 0 && work[i].quantity > 0)
                finalById[work[i].itemInstanceId] = work[i];

        for (int i = 0; i < original.Count; ++i)
        {
            CharacterItemRow old = original[i];
            if (!finalById.TryGetValue(old.itemInstanceId, out CharacterItemRow next))
            {
                if (conn.Execute("DELETE FROM character_items WHERE itemInstanceId=? AND characterId=?", old.itemInstanceId, characterId) != 1)
                    throw new InvalidOperationException("Failed to remove consumed crafting ingredient.");
                continue;
            }
            if (next.quantity == old.quantity) continue;
            old.quantity = next.quantity;
            old.revision = checked(old.revision + 1);
            if (conn.Update(old) != 1) throw new InvalidOperationException("Failed to update item quantity.");
        }

        for (int i = 0; i < work.Count; ++i)
        {
            CharacterItemRow row = work[i];
            if (row == null || row.itemInstanceId > 0 || row.quantity <= 0) continue;
            row.itemInstanceId = AllocateItemInstanceId(conn);
            if (conn.Insert(row) != 1) throw new InvalidOperationException("Failed to create reward item stack.");
        }
    }


    public BackendPlayerItemPickupResponse PickupPlayerItem(BackendPlayerItemPickupRequest request, GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.worldItemInstanceId <= 0 || request.expectedWorldItemRevision < 0 || request.state == null ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return PlayerItemPickupFailed(false, 0, 0, 0, string.Empty, string.Empty, string.IsNullOrWhiteSpace(contentError) ? "invalid pickup transaction" : contentError);

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerItemPickupFailed(false, 0, 0, 0, string.Empty, string.Empty, "character authority lease unavailable");

        return Execute(conn =>
        {
            BackendPlayerItemPickupResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null) { response = PlayerItemPickupFailed(false, 0, 0, 0, string.Empty, string.Empty, "character not found"); return; }
                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null) { response = PlayerItemPickupFailed(false, 0, 0, 0, string.Empty, string.Empty, "player-system state unavailable"); return; }
                if (request.expectedInventoryRevision != stored.inventoryRevision || request.expectedEquipmentRevision != stored.equipmentRevision)
                { response = PlayerItemPickupFailed(true, stored.inventoryRevision, stored.equipmentRevision, 0, string.Empty, string.Empty, "stale player-system revision"); return; }

                // The GameServer owns transient ground-item existence. Backend validates only
                // the authoritative inventory addition and never reads/writes a world_items row.
                if (!ValidateTransientPlayerItemPickupMutation(
                        conn, request.characterId, stored, request.worldItemInstanceId,
                        request.expectedWorldItemRevision, request.state, content, out string error))
                { response = PlayerItemPickupFailed(false, stored.inventoryRevision, stored.equipmentRevision, 0, string.Empty, string.Empty, error); return; }

                ApplyTransientPlayerItemPickupMutation(conn, request.characterId, request.worldItemInstanceId, request.state);

                stored.inventoryRevision = request.state.inventoryRevision;
                stored.equipmentRevision = request.state.equipmentRevision;
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                conn.Update(stored);
                response = new BackendPlayerItemPickupResponse
                {
                    success = true, accepted = true, stale = false,
                    storedInventoryRevision = stored.inventoryRevision, storedEquipmentRevision = stored.equipmentRevision,
                    worldRevision = 0, mapId = string.Empty, instanceId = string.Empty,
                    state = ReadPlayerSystems(conn, request.characterId), error = string.Empty,
                };
            });
            return response ?? PlayerItemPickupFailed(false, 0, 0, 0, string.Empty, string.Empty, "pickup transaction unavailable");
        });
    }

    private static bool ValidatePlayerItemDropMutation(SQLiteConnection conn, long characterId, CharacterPlayerSystemsRow storedState, long sourceId, int quantity, BackendPlayerSystemsSnapshotDto next, GameplayContentSnapshot content, out CharacterItemRow source, out string error)
    {
        source = null;
        if (next.inventoryCapacity != storedState.inventoryCapacity || storedState.inventoryRevision == long.MaxValue || next.inventoryRevision != storedState.inventoryRevision + 1 || next.equipmentRevision != storedState.equipmentRevision)
            return FailMutation("drop must advance only the inventory revision", out error);
        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>("SELECT * FROM character_items WHERE characterId=? ORDER BY itemInstanceId ASC", characterId);
        var storedById = new Dictionary<long, CharacterItemRow>();
        for (int i = 0; i < rows.Count; ++i) { storedById[rows[i].itemInstanceId] = rows[i]; if (rows[i].itemInstanceId == sourceId) source = rows[i]; }
        if (source == null || source.containerKind != InventoryContainer || quantity > source.quantity) return FailMutation("drop source or quantity is invalid", out error);
        if (source.revision == long.MaxValue) return FailMutation("item revision is exhausted", out error);

        var incoming = BuildIncomingById(next, storedState, storedById, content, false, out error);
        if (incoming == null) return false;
        if (incoming.Count > storedById.Count) return FailMutation("drop transaction cannot introduce item instances", out error);
        int expectedSourceQuantity = source.quantity - quantity;
        foreach (CharacterItemRow stored in rows)
        {
            bool isSource = stored.itemInstanceId == sourceId;
            if (!incoming.TryGetValue(stored.itemInstanceId, out BackendPersistedItemDto item))
            {
                if (isSource && expectedSourceQuantity == 0) continue;
                return FailMutation("drop transaction would remove an unrelated item", out error);
            }
            if (!SamePersistedLocationAndIdentity(stored, item)) return FailMutation("drop transaction cannot move or transform owned items", out error);
            int expectedQuantity = isSource ? expectedSourceQuantity : stored.quantity;
            long expectedRevision = isSource ? stored.revision + 1 : stored.revision;
            if (item.quantity != expectedQuantity || item.revision != expectedRevision) return FailMutation("drop quantity or item revision transition is invalid", out error);
        }
        foreach (long id in incoming.Keys) if (!storedById.ContainsKey(id)) return FailMutation("drop transaction cannot introduce item instances", out error);
        error = string.Empty; return true;
    }

    private static bool ValidateTransientPlayerItemPickupMutation(
        SQLiteConnection conn,
        long characterId,
        CharacterPlayerSystemsRow storedState,
        long worldItemInstanceId,
        long expectedWorldItemRevision,
        BackendPlayerSystemsSnapshotDto next,
        GameplayContentSnapshot content,
        out string error)
    {
        if (worldItemInstanceId <= 0 || expectedWorldItemRevision < 0)
            return FailMutation("transient world item identity is invalid", out error);
        if (next.inventoryCapacity != storedState.inventoryCapacity ||
            storedState.inventoryRevision == long.MaxValue ||
            next.inventoryRevision != storedState.inventoryRevision + 1 ||
            next.equipmentRevision != storedState.equipmentRevision)
            return FailMutation("pickup must advance only the inventory revision", out error);
        if (expectedWorldItemRevision == long.MaxValue)
            return FailMutation("world item revision is exhausted", out error);

        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? ORDER BY itemInstanceId ASC",
            characterId);
        var storedById = new Dictionary<long, CharacterItemRow>();
        for (int i = 0; i < rows.Count; ++i)
            storedById[rows[i].itemInstanceId] = rows[i];

        // A runtime-only drop id may be reintroduced into inventory, but it must not already
        // belong to any persisted character. Full-stack drops retire the source row before
        // GameServer exposes the ground item; split-stack ids come from the global sequence.
        if (conn.Find<CharacterItemRow>(worldItemInstanceId) != null)
            return FailMutation("transient world item id is already persisted", out error);

        var incoming = BuildIncomingById(next, storedState, storedById, content, true, out error);
        if (incoming == null)
            return false;

        string acquiredDefinitionId = string.Empty;
        int acquiredDurability = -1;
        long acquiredQuantity = 0;
        ItemDefinition acquiredDefinition = null;
        int introducedInstances = 0;

        foreach (CharacterItemRow stored in rows)
        {
            if (!incoming.TryGetValue(stored.itemInstanceId, out BackendPersistedItemDto item))
                return FailMutation("pickup transaction cannot remove owned items", out error);
            if (!SamePersistedLocationAndIdentity(stored, item))
                return FailMutation("pickup transaction cannot move or transform owned items", out error);
            if (item.quantity < stored.quantity)
                return FailMutation("pickup transaction cannot reduce owned stacks", out error);

            int increase = item.quantity - stored.quantity;
            if (increase > 0)
            {
                if (stored.containerKind != InventoryContainer)
                    return FailMutation("pickup may only increase inventory stacks", out error);
                if (stored.revision == long.MaxValue || item.revision != stored.revision + 1)
                    return FailMutation("merged stack revision transition is invalid", out error);
                if (!AccumulateTransientPickup(
                        content,
                        stored.definitionId,
                        stored.durability,
                        increase,
                        ref acquiredDefinitionId,
                        ref acquiredDurability,
                        ref acquiredDefinition,
                        ref acquiredQuantity,
                        out error))
                    return false;
            }
            else if (item.revision != stored.revision)
            {
                return FailMutation("unchanged item revision cannot advance during pickup", out error);
            }
        }

        foreach (KeyValuePair<long, BackendPersistedItemDto> pair in incoming)
        {
            if (storedById.ContainsKey(pair.Key))
                continue;

            BackendPersistedItemDto item = pair.Value;
            introducedInstances++;
            if (introducedInstances > 1 ||
                pair.Key != worldItemInstanceId ||
                item.inventorySlot < 0 ||
                !string.IsNullOrEmpty(item.equipmentSlotId) ||
                item.revision != checked(expectedWorldItemRevision + 1))
                return FailMutation("pickup transaction introduced an invalid transient item instance", out error);

            ItemDefinition definition = FindItemDefinition(content, item.definitionId);
            if (definition == null || item.quantity < 1 || item.quantity > definition.maxStack)
                return FailMutation("transient world item definition or stack is invalid", out error);
            if (!ValidateMagazinePayload(
                    definition,
                    NormalizeLoadedAmmo(item.loadedAmmoDefinitionId, item.loadedRounds),
                    item.loadedRounds,
                    content,
                    out error))
                return false;

            if (!AccumulateTransientPickup(
                    content,
                    item.definitionId,
                    item.durability,
                    item.quantity,
                    ref acquiredDefinitionId,
                    ref acquiredDurability,
                    ref acquiredDefinition,
                    ref acquiredQuantity,
                    out error))
                return false;
        }

        if (acquiredQuantity < 1 || acquiredDefinition == null)
            return FailMutation("pickup transaction did not acquire an item", out error);
        if (acquiredQuantity > acquiredDefinition.maxStack)
            return FailMutation("pickup quantity exceeds the item stack limit", out error);

        error = string.Empty;
        return true;
    }

    private static bool AccumulateTransientPickup(
        GameplayContentSnapshot content,
        string definitionId,
        int durability,
        int quantity,
        ref string acquiredDefinitionId,
        ref int acquiredDurability,
        ref ItemDefinition acquiredDefinition,
        ref long acquiredQuantity,
        out string error)
    {
        if (quantity < 1)
            return FailMutation("pickup quantity is invalid", out error);
        ItemDefinition definition = FindItemDefinition(content, definitionId);
        if (definition == null)
            return FailMutation("pickup item definition is unavailable", out error);

        if (acquiredQuantity == 0)
        {
            acquiredDefinitionId = definition.definitionId;
            acquiredDurability = durability;
            acquiredDefinition = definition;
        }
        else if (!string.Equals(acquiredDefinitionId, definition.definitionId, StringComparison.Ordinal) ||
                 acquiredDurability != durability)
        {
            return FailMutation("one transient pickup cannot introduce multiple item types", out error);
        }

        acquiredQuantity = checked(acquiredQuantity + quantity);
        error = string.Empty;
        return true;
    }

    private static void ApplyTransientPlayerItemPickupMutation(
        SQLiteConnection conn,
        long characterId,
        long worldItemInstanceId,
        BackendPlayerSystemsSnapshotDto next)
    {
        List<CharacterItemRow> storedRows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=?",
            characterId);
        var storedIds = new HashSet<long>();
        for (int i = 0; i < storedRows.Count; ++i)
            storedIds.Add(storedRows[i].itemInstanceId);

        BackendPersistedItemDto[] inventory = next.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
        BackendPersistedItemDto[] equipment = next.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();
        for (int i = 0; i < inventory.Length; ++i)
        {
            BackendPersistedItemDto item = inventory[i];
            if (storedIds.Contains(item.itemInstanceId))
            {
                UpdatePersistedItem(conn, characterId, InventoryContainer, item);
                continue;
            }

            if (item.itemInstanceId != worldItemInstanceId)
                throw new InvalidOperationException("Unexpected transient pickup item id.");

            long durableItemId = item.itemInstanceId >= BackendServiceContracts.TransientWorldItemIdFloor
                ? AllocateItemInstanceId(conn)
                : item.itemInstanceId;
            var row = new CharacterItemRow
            {
                itemInstanceId = durableItemId,
                characterId = characterId,
                containerKind = InventoryContainer,
                inventorySlot = item.inventorySlot,
                equipmentSlotId = string.Empty,
                definitionId = item.definitionId,
                quantity = item.quantity,
                durability = item.durability,
                revision = item.revision,
                loadedAmmoDefinitionId = NormalizeLoadedAmmo(item.loadedAmmoDefinitionId, item.loadedRounds),
                loadedRounds = Math.Max(0, item.loadedRounds),
                magazineRevision = Math.Max(0, item.magazineRevision),
            };
            if (conn.Insert(row) != 1)
                throw new InvalidOperationException("Failed to transfer transient world item into inventory.");
        }

        for (int i = 0; i < equipment.Length; ++i)
            UpdatePersistedItem(conn, characterId, EquipmentContainer, equipment[i]);
    }

    private static bool ValidatePlayerItemPickupMutation(SQLiteConnection conn, long characterId, CharacterPlayerSystemsRow storedState, WorldItemRow world, BackendPlayerSystemsSnapshotDto next, GameplayContentSnapshot content, out string error)
    {
        if (next.inventoryCapacity != storedState.inventoryCapacity || storedState.inventoryRevision == long.MaxValue || next.inventoryRevision != storedState.inventoryRevision + 1 || next.equipmentRevision != storedState.equipmentRevision)
            return FailMutation("pickup must advance only the inventory revision", out error);
        ItemDefinition worldDefinition = FindItemDefinition(content, world.definitionId);
        if (worldDefinition == null || world.quantity < 1 || world.quantity > worldDefinition.maxStack) return FailMutation("world item definition or stack is invalid", out error);
        if (world.revision == long.MaxValue) return FailMutation("world item revision is exhausted", out error);

        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>("SELECT * FROM character_items WHERE characterId=? ORDER BY itemInstanceId ASC", characterId);
        var storedById = new Dictionary<long, CharacterItemRow>();
        for (int i = 0; i < rows.Count; ++i) storedById[rows[i].itemInstanceId] = rows[i];
        if (storedById.ContainsKey(world.itemInstanceId)) return FailMutation("world item id is already owned by character", out error);
        var incoming = BuildIncomingById(next, storedState, storedById, content, true, out error);
        if (incoming == null) return false;

        long acquired = 0;
        foreach (CharacterItemRow stored in rows)
        {
            if (!incoming.TryGetValue(stored.itemInstanceId, out BackendPersistedItemDto item)) return FailMutation("pickup transaction cannot remove owned items", out error);
            if (!SamePersistedLocationAndIdentity(stored, item)) return FailMutation("pickup transaction cannot move or transform owned items", out error);
            if (item.quantity < stored.quantity) return FailMutation("pickup transaction cannot reduce owned stacks", out error);
            int increase = item.quantity - stored.quantity;
            if (increase > 0)
            {
                if (stored.containerKind != InventoryContainer || !string.Equals(stored.definitionId, world.definitionId, StringComparison.Ordinal) || stored.durability != world.durability)
                    return FailMutation("pickup may only merge into compatible inventory stacks", out error);
                if (stored.revision == long.MaxValue || item.revision != stored.revision + 1) return FailMutation("merged stack revision transition is invalid", out error);
                acquired = checked(acquired + increase);
            }
            else if (item.revision != stored.revision) return FailMutation("unchanged item revision cannot advance during pickup", out error);
        }

        foreach (KeyValuePair<long, BackendPersistedItemDto> pair in incoming)
        {
            if (storedById.ContainsKey(pair.Key)) continue;
            BackendPersistedItemDto item = pair.Value;
            if (pair.Key != world.itemInstanceId ||
                item.inventorySlot < 0 ||
                !string.IsNullOrEmpty(item.equipmentSlotId) ||
                !string.Equals(item.definitionId, world.definitionId, StringComparison.Ordinal) ||
                item.durability != world.durability ||
                item.revision != checked(world.revision + 1) ||
                item.magazineRevision != Math.Max(0, world.magazineRevision) ||
                item.loadedRounds != Math.Max(0, world.loadedRounds) ||
                !string.Equals(
                    NormalizeLoadedAmmo(item.loadedAmmoDefinitionId, item.loadedRounds),
                    NormalizeLoadedAmmo(world.loadedAmmoDefinitionId, world.loadedRounds),
                    StringComparison.Ordinal))
                return FailMutation("pickup transaction introduced an invalid item instance", out error);
            acquired = checked(acquired + item.quantity);
        }
        if (acquired != world.quantity) return FailMutation("pickup must transfer the complete world-item quantity", out error);
        error = string.Empty; return true;
    }

    private static Dictionary<long, BackendPersistedItemDto> BuildIncomingById(BackendPlayerSystemsSnapshotDto next, CharacterPlayerSystemsRow storedState, Dictionary<long, CharacterItemRow> storedById, GameplayContentSnapshot content, bool allowWorldId, out string error)
    {
        BackendPersistedItemDto[] inventory = next.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
        BackendPersistedItemDto[] equipment = next.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();
        if (inventory.Length > storedState.inventoryCapacity || equipment.Length > 128) { FailMutation("item collection is too large", out error); return null; }
        var result = new Dictionary<long, BackendPersistedItemDto>();
        var invSlots = new HashSet<int>();
        var eqSlots = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < inventory.Length; ++i)
        {
            BackendPersistedItemDto item = inventory[i];
            if (item == null || item.itemInstanceId <= 0 || item.quantity < 1 || item.durability < 0 || item.revision < 0 || item.inventorySlot < 0 || item.inventorySlot >= storedState.inventoryCapacity || !string.IsNullOrEmpty(item.equipmentSlotId) || !invSlots.Add(item.inventorySlot) || !result.TryAdd(item.itemInstanceId, item))
            { FailMutation("invalid or duplicate inventory item", out error); return null; }
            ItemDefinition definition = FindItemDefinition(content, item.definitionId);
            if (definition == null || item.quantity > definition.maxStack) { FailMutation("item definition or stack is invalid", out error); return null; }
            if (storedById.TryGetValue(item.itemInstanceId, out CharacterItemRow storedItem) &&
                !ValidateNonIncreasingMagazineTransition(storedItem, item, definition, content, out error))
                return null;
        }
        for (int i = 0; i < equipment.Length; ++i)
        {
            BackendPersistedItemDto item = equipment[i];
            if (item == null || item.itemInstanceId <= 0 || item.quantity < 1 || item.durability < 0 || item.revision < 0 || item.inventorySlot != -1 || !GameplayContentValidation.IsStableId(item.equipmentSlotId) || !eqSlots.Add(item.equipmentSlotId) || !result.TryAdd(item.itemInstanceId, item))
            { FailMutation("invalid or duplicate equipment item", out error); return null; }
            ItemDefinition definition = FindItemDefinition(content, item.definitionId);
            if (definition == null || item.quantity > definition.maxStack || !AllowsEquipmentSlot(definition, item.equipmentSlotId)) { FailMutation("equipment item is invalid", out error); return null; }
            if (storedById.TryGetValue(item.itemInstanceId, out CharacterItemRow storedItem) &&
                !ValidateNonIncreasingMagazineTransition(storedItem, item, definition, content, out error))
                return null;
        }
        error = string.Empty; return result;
    }

    private static bool SamePersistedLocationAndIdentity(CharacterItemRow stored, BackendPersistedItemDto item) =>
        string.Equals(stored.definitionId, item.definitionId, StringComparison.Ordinal) && stored.durability == item.durability &&
        item.inventorySlot == (stored.containerKind == InventoryContainer ? stored.inventorySlot : -1) &&
        string.Equals(item.equipmentSlotId ?? string.Empty, stored.containerKind == EquipmentContainer ? stored.equipmentSlotId : string.Empty, StringComparison.Ordinal);

    private static void ApplyPlayerItemPickupMutation(SQLiteConnection conn, long characterId, WorldItemRow world, BackendPlayerSystemsSnapshotDto next)
    {
        BackendPersistedItemDto[] inventory = next.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
        BackendPersistedItemDto[] equipment = next.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();
        for (int i = 0; i < inventory.Length; ++i)
        {
            BackendPersistedItemDto item = inventory[i];
            if (item.itemInstanceId == world.itemInstanceId)
            {
                var row = new CharacterItemRow
                {
                    itemInstanceId = item.itemInstanceId,
                    characterId = characterId,
                    containerKind = InventoryContainer,
                    inventorySlot = item.inventorySlot,
                    equipmentSlotId = string.Empty,
                    definitionId = item.definitionId,
                    quantity = item.quantity,
                    durability = item.durability,
                    revision = item.revision,
                    loadedAmmoDefinitionId = NormalizeLoadedAmmo(world.loadedAmmoDefinitionId, world.loadedRounds),
                    loadedRounds = Math.Max(0, world.loadedRounds),
                    magazineRevision = Math.Max(0, world.magazineRevision),
                };
                if (conn.Insert(row) != 1) throw new InvalidOperationException("Failed to transfer stable world item id into inventory.");
            }
            else UpdatePersistedItem(conn, characterId, InventoryContainer, item);
        }
        for (int i = 0; i < equipment.Length; ++i) UpdatePersistedItem(conn, characterId, EquipmentContainer, equipment[i]);
    }

    private static bool IsValidWorldItemLocation(string mapId, string instanceId, float x, float y, float z) =>
        !string.IsNullOrWhiteSpace(mapId) && mapId.Length <= 128 && (instanceId ?? string.Empty).Length <= 128 && mapId.IndexOf('\u001f') < 0 && (instanceId ?? string.Empty).IndexOf('\u001f') < 0 && IsFinite(x) && IsFinite(y) && IsFinite(z);

    private static string BuildWorldKey(string mapId, string instanceId) => mapId + "\u001f" + (instanceId ?? string.Empty);

    private static string NormalizeLoadedAmmo(string definitionId, int loadedRounds) =>
        loadedRounds > 0 ? (definitionId ?? string.Empty).Trim() : string.Empty;

    private static long AdvanceWorldRevision(SQLiteConnection conn, string worldKey, string mapId, string instanceId)
    {
        WorldItemWorldRow row = conn.Find<WorldItemWorldRow>(worldKey);
        if (row == null)
        {
            row = new WorldItemWorldRow { worldKey = worldKey, mapId = mapId, instanceId = instanceId ?? string.Empty, revision = 1 };
            if (conn.Insert(row) != 1) throw new InvalidOperationException("Failed to create world-item revision row.");
            return 1;
        }
        if (row.revision == long.MaxValue) throw new InvalidOperationException("World-item revision is exhausted.");
        row.revision++;
        row.mapId = mapId; row.instanceId = instanceId ?? string.Empty;
        if (conn.Update(row) != 1) throw new InvalidOperationException("Failed to advance world-item revision.");
        return row.revision;
    }

    private static BackendWorldItemDto ToWorldItemDto(WorldItemRow row) => new()
    {
        itemInstanceId = row.itemInstanceId, definitionId = row.definitionId, quantity = row.quantity, durability = row.durability, revision = row.revision,
        loadedAmmoDefinitionId = NormalizeLoadedAmmo(row.loadedAmmoDefinitionId, row.loadedRounds),
        loadedRounds = Math.Max(0, row.loadedRounds),
        magazineRevision = Math.Max(0, row.magazineRevision),
        mapId = row.mapId, instanceId = row.instanceId, positionX = row.positionX, positionY = row.positionY, positionZ = row.positionZ,
    };

    private static bool ValidatePlayerItemConsumeMutation(
        SQLiteConnection conn,
        long characterId,
        CharacterPlayerSystemsRow storedState,
        long itemInstanceId,
        int consumeQuantity,
        BackendPlayerSystemsSnapshotDto next,
        GameplayContentSnapshot content,
        out string error)
    {
        if (next.inventoryCapacity != storedState.inventoryCapacity)
            return FailMutation("inventory capacity is server-owned", out error);
        if (storedState.inventoryRevision == long.MaxValue ||
            next.inventoryRevision != storedState.inventoryRevision + 1 ||
            next.equipmentRevision != storedState.equipmentRevision)
        {
            return FailMutation("consume must advance only the inventory revision", out error);
        }

        List<CharacterItemRow> storedRows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? ORDER BY itemInstanceId ASC",
            characterId);
        CharacterItemRow source = null;
        var storedById = new Dictionary<long, CharacterItemRow>();
        for (int i = 0; i < storedRows.Count; ++i)
        {
            CharacterItemRow row = storedRows[i];
            storedById[row.itemInstanceId] = row;
            if (row.itemInstanceId == itemInstanceId)
                source = row;
        }

        if (source == null || source.containerKind != InventoryContainer)
            return FailMutation("consume source is not an owned inventory item", out error);
        ItemDefinition sourceDefinition = FindItemDefinition(content, source.definitionId);
        if (sourceDefinition == null || sourceDefinition.consumeQuantity < 1 ||
            (sourceDefinition.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length == 0)
        {
            return FailMutation("item is not usable", out error);
        }
        if (consumeQuantity != sourceDefinition.consumeQuantity || source.quantity < consumeQuantity)
            return FailMutation("consume quantity is invalid", out error);

        BackendPersistedItemDto[] inventory = next.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
        BackendPersistedItemDto[] equipment = next.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();
        if (inventory.Length > storedState.inventoryCapacity || equipment.Length > 128)
            return FailMutation("item collection is too large", out error);

        var incomingById = new Dictionary<long, BackendPersistedItemDto>();
        for (int i = 0; i < inventory.Length; ++i)
        {
            BackendPersistedItemDto item = inventory[i];
            if (item == null || item.itemInstanceId <= 0 || incomingById.ContainsKey(item.itemInstanceId))
                return FailMutation("invalid or duplicate inventory item", out error);
            incomingById.Add(item.itemInstanceId, item);
        }
        for (int i = 0; i < equipment.Length; ++i)
        {
            BackendPersistedItemDto item = equipment[i];
            if (item == null || item.itemInstanceId <= 0 || incomingById.ContainsKey(item.itemInstanceId))
                return FailMutation("invalid or duplicate equipment item", out error);
            incomingById.Add(item.itemInstanceId, item);
        }
        if (incomingById.Count > storedById.Count)
            return FailMutation("consume transaction cannot create item instances", out error);
        foreach (long incomingId in incomingById.Keys)
        {
            if (!storedById.ContainsKey(incomingId))
                return FailMutation("consume transaction cannot introduce item instances", out error);
        }

        int expectedSourceQuantity = source.quantity - consumeQuantity;
        for (int i = 0; i < storedRows.Count; ++i)
        {
            CharacterItemRow stored = storedRows[i];
            bool isSource = stored.itemInstanceId == itemInstanceId;
            if (!incomingById.TryGetValue(stored.itemInstanceId, out BackendPersistedItemDto incoming))
            {
                if (isSource && expectedSourceQuantity == 0)
                    continue;
                return FailMutation("consume transaction would remove an unrelated item", out error);
            }

            int expectedQuantity = isSource ? expectedSourceQuantity : stored.quantity;
            if (isSource && stored.revision == long.MaxValue)
                return FailMutation("item revision is exhausted", out error);
            long expectedRevision = isSource ? stored.revision + 1 : stored.revision;
            int expectedInventorySlot = stored.containerKind == InventoryContainer ? stored.inventorySlot : -1;
            string expectedEquipmentSlot = stored.containerKind == EquipmentContainer ? stored.equipmentSlotId : string.Empty;
            if (expectedQuantity < 1 ||
                incoming.quantity != expectedQuantity ||
                incoming.revision != expectedRevision ||
                incoming.durability != stored.durability ||
                !string.Equals(incoming.definitionId, stored.definitionId, StringComparison.Ordinal) ||
                incoming.inventorySlot != expectedInventorySlot ||
                !string.Equals(incoming.equipmentSlotId ?? string.Empty, expectedEquipmentSlot ?? string.Empty, StringComparison.Ordinal))
            {
                return FailMutation("consume transaction changed unrelated item state", out error);
            }
        }

        error = string.Empty;
        return true;
    }

    private static CharacterRow FindOwnedCharacter(SQLiteConnection conn, long accountId, long characterId) =>
        conn.FindWithQuery<CharacterRow>(
            "SELECT * FROM characters WHERE characterId=? AND accountId=? LIMIT 1",
            characterId,
            accountId);

    private static void EnsurePlayerSystems(
        SQLiteConnection conn,
        long characterId,
        GameplayContentSnapshot content,
        long utcNowTicks)
    {
        if (conn.Find<CharacterPlayerSystemsRow>(characterId) != null)
            return;
        InitializePlayerSystems(conn, characterId, content, utcNowTicks);
    }

    private static void InitializePlayerSystems(
        SQLiteConnection conn,
        long characterId,
        GameplayContentSnapshot content,
        long utcNowTicks)
    {
        if (characterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        if (!GameplayContentSnapshotCache.TryValidate(content, out string error))
            throw new InvalidOperationException("Cannot initialize player systems from invalid content: " + error);
        if (conn.Find<CharacterPlayerSystemsRow>(characterId) != null)
            return;

        var state = new CharacterPlayerSystemsRow
        {
            characterId = characterId,
            inventoryCapacity = content.baseInventoryCapacity,
            inventoryRevision = 0,
            equipmentRevision = 0,
            updatedUtcTicks = utcNowTicks,
        };
        if (conn.Insert(state) != 1)
            throw new InvalidOperationException("Failed to initialize player-system state.");

        StarterItemDefinition[] starters = content.starterItems ?? Array.Empty<StarterItemDefinition>();
        for (int i = 0; i < starters.Length; ++i)
        {
            StarterItemDefinition starter = starters[i];
            ItemDefinition definition = FindItemDefinition(content, starter.definitionId);
            if (definition == null)
                throw new InvalidOperationException("Starter item definition disappeared during initialization.");

            var row = new CharacterItemRow
            {
                itemInstanceId = AllocateItemInstanceId(conn),
                characterId = characterId,
                containerKind = InventoryContainer,
                inventorySlot = i,
                equipmentSlotId = string.Empty,
                definitionId = definition.definitionId,
                quantity = starter.quantity,
                durability = definition.maxDurability,
                revision = 0,
            };
            if (conn.Insert(row) != 1 || row.itemInstanceId <= 0)
                throw new InvalidOperationException("Failed to create starter item instance.");
        }
    }

    private static BackendPlayerSystemsSnapshotDto ReadPlayerSystems(SQLiteConnection conn, long characterId)
    {
        CharacterPlayerSystemsRow state = conn.Find<CharacterPlayerSystemsRow>(characterId);
        if (state == null)
            return null;

        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? ORDER BY containerKind ASC, inventorySlot ASC, equipmentSlotId ASC, itemInstanceId ASC",
            characterId);
        var inventory = new List<BackendPersistedItemDto>();
        var equipment = new List<BackendPersistedItemDto>();
        for (int i = 0; i < rows.Count; ++i)
        {
            CharacterItemRow row = rows[i];
            BackendPersistedItemDto dto = ToItemDto(row);
            if (row.containerKind == InventoryContainer)
                inventory.Add(dto);
            else if (row.containerKind == EquipmentContainer)
                equipment.Add(dto);
        }

        return new BackendPlayerSystemsSnapshotDto
        {
            inventoryCapacity = state.inventoryCapacity,
            inventoryRevision = state.inventoryRevision,
            equipmentRevision = state.equipmentRevision,
            inventoryItems = inventory.ToArray(),
            equipmentItems = equipment.ToArray(),
        };
    }

    private static bool ValidatePlayerSystemsMutation(
        SQLiteConnection conn,
        long characterId,
        CharacterPlayerSystemsRow storedState,
        BackendPlayerSystemsSnapshotDto next,
        GameplayContentSnapshot content,
        out string error)
    {
        if (next.inventoryCapacity != storedState.inventoryCapacity)
            return FailMutation("inventory capacity is server-owned", out error);

        if (!IsSingleRevisionStep(storedState.inventoryRevision, next.inventoryRevision) ||
            !IsSingleRevisionStep(storedState.equipmentRevision, next.equipmentRevision) ||
            (next.inventoryRevision == storedState.inventoryRevision && next.equipmentRevision == storedState.equipmentRevision))
            return FailMutation("invalid player-system revision transition", out error);

        BackendPersistedItemDto[] incomingInventory = next.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
        BackendPersistedItemDto[] incomingEquipment = next.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();
        if (incomingInventory.Length > storedState.inventoryCapacity || incomingEquipment.Length > 128)
            return FailMutation("item collection is too large", out error);

        List<CharacterItemRow> storedRows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? ORDER BY itemInstanceId ASC",
            characterId);
        var storedById = new Dictionary<long, CharacterItemRow>();
        var storedTotals = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < storedRows.Count; ++i)
        {
            CharacterItemRow row = storedRows[i];
            storedById[row.itemInstanceId] = row;
            AddAggregate(storedTotals, row.definitionId, row.durability, row.quantity);
        }

        var incomingIds = new HashSet<long>();
        var inventorySlots = new HashSet<int>();
        var equipmentSlots = new HashSet<string>(StringComparer.Ordinal);
        var incomingTotals = new Dictionary<string, long>(StringComparer.Ordinal);

        for (int i = 0; i < incomingInventory.Length; ++i)
        {
            BackendPersistedItemDto item = incomingInventory[i];
            if (!ValidateIncomingItem(item, storedById, content, out ItemDefinition definition, out error))
                return false;
            if (item.inventorySlot < 0 || item.inventorySlot >= storedState.inventoryCapacity || !inventorySlots.Add(item.inventorySlot))
                return FailMutation("invalid or duplicate inventory slot", out error);
            if (!string.IsNullOrEmpty(item.equipmentSlotId))
                return FailMutation("inventory item contains equipment-slot state", out error);
            if (!incomingIds.Add(item.itemInstanceId))
                return FailMutation("duplicate item instance", out error);
            AddAggregate(incomingTotals, definition.definitionId, item.durability, item.quantity);
        }

        for (int i = 0; i < incomingEquipment.Length; ++i)
        {
            BackendPersistedItemDto item = incomingEquipment[i];
            if (!ValidateIncomingItem(item, storedById, content, out ItemDefinition definition, out error))
                return false;
            if (item.inventorySlot != -1 || !GameplayContentValidation.IsStableId(item.equipmentSlotId) || !equipmentSlots.Add(item.equipmentSlotId))
                return FailMutation("invalid or duplicate equipment slot", out error);
            if (!AllowsEquipmentSlot(definition, item.equipmentSlotId))
                return FailMutation("item is not allowed in requested equipment slot", out error);
            if (!incomingIds.Add(item.itemInstanceId))
                return FailMutation("duplicate item instance", out error);
            AddAggregate(incomingTotals, definition.definitionId, item.durability, item.quantity);
        }

        if (!SameAggregates(storedTotals, incomingTotals))
            return FailMutation("item transaction would create, destroy, or transform item quantity", out error);

        error = string.Empty;
        return true;
    }

    private static bool ValidateIncomingItem(
        BackendPersistedItemDto item,
        Dictionary<long, CharacterItemRow> storedById,
        GameplayContentSnapshot content,
        out ItemDefinition definition,
        out string error)
    {
        definition = null;
        if (item == null || item.itemInstanceId <= 0 || item.quantity < 1 || item.durability < 0 || item.revision < 0)
            return FailMutation("invalid item instance", out error);
        if (!storedById.TryGetValue(item.itemInstanceId, out CharacterItemRow stored))
            return FailMutation("item instance is not owned by this character", out error);
        if (!string.Equals(stored.definitionId, item.definitionId, StringComparison.Ordinal))
            return FailMutation("item definition cannot change during inventory/equipment movement", out error);
        definition = FindItemDefinition(content, item.definitionId);
        if (definition == null || item.quantity > definition.maxStack)
            return FailMutation("item definition or stack is invalid", out error);
        if (item.revision < stored.revision || item.revision > checked(stored.revision + 1))
            return FailMutation("item instance revision transition is invalid", out error);
        if (!ValidateNonIncreasingMagazineTransition(stored, item, definition, content, out error))
            return false;

        error = string.Empty;
        return true;
    }

    private static bool ValidateNonIncreasingMagazineTransition(
        CharacterItemRow stored,
        BackendPersistedItemDto incoming,
        ItemDefinition itemDefinition,
        GameplayContentSnapshot content,
        out string error)
    {
        if (incoming.magazineRevision < 0 || incoming.loadedRounds < 0)
            return FailMutation("invalid magazine state", out error);

        long storedRevision = Math.Max(0, stored.magazineRevision);
        int storedRounds = Math.Max(0, stored.loadedRounds);
        string storedAmmo = NormalizeLoadedAmmo(stored.loadedAmmoDefinitionId, storedRounds);
        string incomingAmmo = NormalizeLoadedAmmo(incoming.loadedAmmoDefinitionId, incoming.loadedRounds);

        if (incoming.magazineRevision < storedRevision)
        {
            // A concurrent checkpoint may already have persisted a newer shot decrement.
            // The movement/equipment mutation may proceed; UpdatePersistedItem preserves
            // the newer stored magazine state.
            error = string.Empty;
            return true;
        }

        if (incoming.magazineRevision == storedRevision)
        {
            if (incoming.loadedRounds != storedRounds ||
                !string.Equals(incomingAmmo, storedAmmo, StringComparison.Ordinal))
                return FailMutation("magazine state changed without advancing magazine revision", out error);
            error = string.Empty;
            return true;
        }

        if (!ValidateMagazinePayload(itemDefinition, incomingAmmo, incoming.loadedRounds, content, out error))
            return false;

        // Ordinary item/equipment transactions may piggyback shot decrements but can
        // never create loaded ammunition. Reload is the sole increase path.
        if (incoming.loadedRounds > storedRounds)
            return FailMutation("non-reload transaction cannot increase loaded ammunition", out error);
        if (incoming.loadedRounds > 0 &&
            storedRounds > 0 &&
            !string.Equals(incomingAmmo, storedAmmo, StringComparison.Ordinal))
            return FailMutation("non-reload transaction cannot change loaded ammunition type", out error);

        error = string.Empty;
        return true;
    }

    private static bool ValidateMagazinePayload(
        ItemDefinition itemDefinition,
        string loadedAmmoDefinitionId,
        int loadedRounds,
        GameplayContentSnapshot content,
        out string error)
    {
        if (loadedRounds < 0)
            return FailMutation("loaded rounds cannot be negative", out error);
        if (loadedRounds == 0)
        {
            error = string.Empty;
            return true;
        }

        if (itemDefinition == null ||
            itemDefinition.firearmMagazineCapacity <= 0 ||
            string.IsNullOrWhiteSpace(itemDefinition.ammoFamily) ||
            loadedRounds > itemDefinition.firearmMagazineCapacity)
            return FailMutation("magazine state is invalid for item definition", out error);

        ItemDefinition ammo = FindItemDefinition(content, loadedAmmoDefinitionId);
        if (ammo == null ||
            ammo.kind != ItemKind.Ammo ||
            !string.Equals(ammo.ammoFamily ?? string.Empty, itemDefinition.ammoFamily ?? string.Empty, StringComparison.Ordinal))
            return FailMutation("loaded ammunition is incompatible with weapon", out error);

        error = string.Empty;
        return true;
    }

    private static void ApplyPlayerSystemsMutation(
        SQLiteConnection conn,
        long characterId,
        BackendPlayerSystemsSnapshotDto next)
    {
        // ItemInstanceId is permanent identity. Ordinary inventory/equipment operations
        // must never delete/reinsert surviving rows because character_items uses an
        // AUTOINCREMENT primary key and SQLite-net may allocate a replacement ID.
        //
        // Most mutations (consume, durability/revision changes, magazine piggyback) do not
        // move slots. Keep those proportional to the rows that actually changed. Arbitrary
        // swaps/reorders still use the existing safe staging strategy because the slot
        // indexes are UNIQUE and a direct row-by-row swap can fail transiently.
        BackendPersistedItemDto[] inventory = next.inventoryItems ?? Array.Empty<BackendPersistedItemDto>();
        BackendPersistedItemDto[] equipment = next.equipmentItems ?? Array.Empty<BackendPersistedItemDto>();

        var retainedIds = new HashSet<long>();
        for (int i = 0; i < inventory.Length; ++i)
            retainedIds.Add(inventory[i].itemInstanceId);
        for (int i = 0; i < equipment.Length; ++i)
            retainedIds.Add(equipment[i].itemInstanceId);

        List<CharacterItemRow> storedRows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=?",
            characterId);
        var storedById = new Dictionary<long, CharacterItemRow>(storedRows.Count);
        for (int i = 0; i < storedRows.Count; ++i)
            storedById[storedRows[i].itemInstanceId] = storedRows[i];

        // Retired item instances (for example a fully merged source stack) must vacate
        // their old unique slots before retained rows are moved to their final locations.
        for (int i = 0; i < storedRows.Count; ++i)
        {
            CharacterItemRow row = storedRows[i];
            if (retainedIds.Contains(row.itemInstanceId))
                continue;

            int deleted = conn.Execute(
                "DELETE FROM character_items WHERE characterId=? AND itemInstanceId=?",
                characterId,
                row.itemInstanceId);
            if (deleted != 1)
                throw new InvalidOperationException("Failed to retire item instance during transaction.");
            storedById.Remove(row.itemInstanceId);
        }

        bool requiresStaging = false;
        for (int i = 0; i < inventory.Length && !requiresStaging; ++i)
        {
            BackendPersistedItemDto item = inventory[i];
            if (!storedById.TryGetValue(item.itemInstanceId, out CharacterItemRow current))
                throw new InvalidOperationException("Stable item instance disappeared during transaction.");
            requiresStaging = !HasSamePersistedLocation(current, InventoryContainer, item);
        }
        for (int i = 0; i < equipment.Length && !requiresStaging; ++i)
        {
            BackendPersistedItemDto item = equipment[i];
            if (!storedById.TryGetValue(item.itemInstanceId, out CharacterItemRow current))
                throw new InvalidOperationException("Stable item instance disappeared during transaction.");
            requiresStaging = !HasSamePersistedLocation(current, EquipmentContainer, item);
        }

        if (requiresStaging)
        {
            // containerKind=0 participates in neither partial UNIQUE slot index. One
            // staging statement makes arbitrary swaps/reorders safe without changing IDs.
            int staged = conn.Execute(
                "UPDATE character_items SET containerKind=0, inventorySlot=-1, equipmentSlotId='' WHERE characterId=?",
                characterId);
            if (staged != retainedIds.Count)
                throw new InvalidOperationException("Failed to stage stable item instances during transaction.");

            // Staging changed every retained row's location, so every row must be restored.
            for (int i = 0; i < inventory.Length; ++i)
            {
                BackendPersistedItemDto item = inventory[i];
                UpdatePersistedItem(conn, characterId, InventoryContainer, item, storedById[item.itemInstanceId]);
            }
            for (int i = 0; i < equipment.Length; ++i)
            {
                BackendPersistedItemDto item = equipment[i];
                UpdatePersistedItem(conn, characterId, EquipmentContainer, item, storedById[item.itemInstanceId]);
            }
            return;
        }

        // No locations changed, so avoid touching unchanged rows entirely. This turns
        // ordinary consume/durability/revision updates from O(inventory size) writes into
        // O(changed rows) writes while preserving the authoritative full-snapshot checks.
        for (int i = 0; i < inventory.Length; ++i)
        {
            BackendPersistedItemDto item = inventory[i];
            CharacterItemRow current = storedById[item.itemInstanceId];
            if (NeedsPersistedItemUpdate(current, InventoryContainer, item))
                UpdatePersistedItem(conn, characterId, InventoryContainer, item, current);
        }
        for (int i = 0; i < equipment.Length; ++i)
        {
            BackendPersistedItemDto item = equipment[i];
            CharacterItemRow current = storedById[item.itemInstanceId];
            if (NeedsPersistedItemUpdate(current, EquipmentContainer, item))
                UpdatePersistedItem(conn, characterId, EquipmentContainer, item, current);
        }
    }

    private static bool HasSamePersistedLocation(
        CharacterItemRow current,
        byte container,
        BackendPersistedItemDto item) =>
        current.containerKind == container &&
        current.inventorySlot == (container == InventoryContainer ? item.inventorySlot : -1) &&
        string.Equals(
            current.equipmentSlotId ?? string.Empty,
            container == EquipmentContainer ? item.equipmentSlotId ?? string.Empty : string.Empty,
            StringComparison.Ordinal);

    private static bool NeedsPersistedItemUpdate(
        CharacterItemRow current,
        byte container,
        BackendPersistedItemDto item)
    {
        bool incomingMagazineIsNewerOrEqual = item.magazineRevision >= current.magazineRevision;
        string loadedAmmoDefinitionId = incomingMagazineIsNewerOrEqual
            ? NormalizeLoadedAmmo(item.loadedAmmoDefinitionId, item.loadedRounds)
            : NormalizeLoadedAmmo(current.loadedAmmoDefinitionId, current.loadedRounds);
        int loadedRounds = incomingMagazineIsNewerOrEqual ? item.loadedRounds : current.loadedRounds;
        long magazineRevision = incomingMagazineIsNewerOrEqual ? item.magazineRevision : current.magazineRevision;

        return !HasSamePersistedLocation(current, container, item) ||
               !string.Equals(current.definitionId, item.definitionId, StringComparison.Ordinal) ||
               current.quantity != item.quantity ||
               current.durability != item.durability ||
               current.revision != item.revision ||
               !string.Equals(current.loadedAmmoDefinitionId ?? string.Empty, loadedAmmoDefinitionId, StringComparison.Ordinal) ||
               current.loadedRounds != loadedRounds ||
               current.magazineRevision != magazineRevision;
    }

    private static void UpdatePersistedItem(
        SQLiteConnection conn,
        long characterId,
        byte container,
        BackendPersistedItemDto item)
    {
        CharacterItemRow current = conn.FindWithQuery<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? AND itemInstanceId=? LIMIT 1",
            characterId,
            item.itemInstanceId);
        if (current == null)
            throw new InvalidOperationException("Stable item instance disappeared during transaction.");

        UpdatePersistedItem(conn, characterId, container, item, current);
    }

    private static void UpdatePersistedItem(
        SQLiteConnection conn,
        long characterId,
        byte container,
        BackendPersistedItemDto item,
        CharacterItemRow current)
    {
        // Item operations may have captured their snapshot before a concurrent soft
        // magazine checkpoint completed. Preserve the newest magazine revision instead
        // of allowing a value-neutral move/equip transaction to roll it backward.
        bool incomingMagazineIsNewerOrEqual = item.magazineRevision >= current.magazineRevision;
        string loadedAmmoDefinitionId = incomingMagazineIsNewerOrEqual
            ? NormalizeLoadedAmmo(item.loadedAmmoDefinitionId, item.loadedRounds)
            : NormalizeLoadedAmmo(current.loadedAmmoDefinitionId, current.loadedRounds);
        int loadedRounds = incomingMagazineIsNewerOrEqual ? item.loadedRounds : current.loadedRounds;
        long magazineRevision = incomingMagazineIsNewerOrEqual ? item.magazineRevision : current.magazineRevision;

        int updated = conn.Execute(
            @"UPDATE character_items
              SET containerKind=?,
                  inventorySlot=?,
                  equipmentSlotId=?,
                  quantity=?,
                  durability=?,
                  revision=?,
                  loadedAmmoDefinitionId=?,
                  loadedRounds=?,
                  magazineRevision=?
              WHERE characterId=? AND itemInstanceId=? AND definitionId=?",
            container,
            container == InventoryContainer ? item.inventorySlot : -1,
            container == EquipmentContainer ? item.equipmentSlotId : string.Empty,
            item.quantity,
            item.durability,
            item.revision,
            loadedAmmoDefinitionId,
            loadedRounds,
            magazineRevision,
            characterId,
            item.itemInstanceId,
            item.definitionId);

        if (updated != 1)
            throw new InvalidOperationException("Failed to update stable item instance during transaction.");
    }

    private static BackendPersistedItemDto ToItemDto(CharacterItemRow row) =>
        new()
        {
            itemInstanceId = row.itemInstanceId,
            definitionId = row.definitionId,
            quantity = row.quantity,
            durability = row.durability,
            revision = row.revision,
            inventorySlot = row.containerKind == InventoryContainer ? row.inventorySlot : -1,
            equipmentSlotId = row.containerKind == EquipmentContainer ? row.equipmentSlotId : string.Empty,
            loadedAmmoDefinitionId = NormalizeLoadedAmmo(row.loadedAmmoDefinitionId, row.loadedRounds),
            loadedRounds = Math.Max(0, row.loadedRounds),
            magazineRevision = Math.Max(0, row.magazineRevision),
        };

    private static ItemDefinition FindItemDefinition(GameplayContentSnapshot content, string id) =>
        GameplayContentSnapshotCache.FindItem(content, id);

    private static ItemDefinition FindItemDefinition(GameplayContentSnapshot content, ushort dataId) =>
        GameplayContentSnapshotCache.FindItem(content, dataId);

    private static RecipeDefinition FindRecipeDefinition(GameplayContentSnapshot content, ushort dataId) =>
        GameplayContentSnapshotCache.FindRecipe(content, dataId);

    private static bool AllowsEquipmentSlot(ItemDefinition definition, string slotId)
    {
        string[] slots = definition?.allowedEquipmentSlots ?? Array.Empty<string>();
        for (int i = 0; i < slots.Length; ++i)
            if (string.Equals(slots[i], slotId, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool IsSingleRevisionStep(long current, long next) =>
        current >= 0 && (next == current || (current < long.MaxValue && next == current + 1));

    private static void AddAggregate(Dictionary<string, long> totals, string definitionId, int durability, int quantity)
    {
        string key = definitionId + "\u001f" + durability.ToString(System.Globalization.CultureInfo.InvariantCulture);
        totals[key] = totals.TryGetValue(key, out long current) ? checked(current + quantity) : quantity;
    }

    private static bool SameAggregates(Dictionary<string, long> a, Dictionary<string, long> b)
    {
        if (a.Count != b.Count) return false;
        foreach (KeyValuePair<string, long> pair in a)
            if (!b.TryGetValue(pair.Key, out long value) || value != pair.Value)
                return false;
        return true;
    }

    private static bool FailMutation(string message, out string error)
    {
        error = message;
        return false;
    }

    private static BackendPlayerSystemsLoadResponse PlayerSystemsLoadFailed(string error) =>
        new()
        {
            success = false,
            found = false,
            error = error,
            state = null,
        };

    private static BackendPlayerSystemsCommitResponse PlayerSystemsCommitUnavailable(string error) =>
        new()
        {
            success = false,
            accepted = false,
            stale = false,
            storedInventoryRevision = 0,
            storedEquipmentRevision = 0,
            error = error ?? "player-system persistence unavailable",
        };

    private static BackendPlayerItemDropResponse PlayerItemDropFailed(bool stale, long storedInventoryRevision, long storedEquipmentRevision, long worldRevision, string error) => new()
    { success = true, accepted = false, stale = stale, storedInventoryRevision = storedInventoryRevision, storedEquipmentRevision = storedEquipmentRevision, worldRevision = worldRevision, worldItem = null, error = error };

    private static BackendPlayerItemGrantResponse PlayerItemGrantFailed(
        bool stale,
        long storedInventoryRevision,
        long storedEquipmentRevision,
        string error) =>
        new()
        {
            success = true,
            accepted = false,
            stale = stale,
            storedInventoryRevision = storedInventoryRevision,
            storedEquipmentRevision = storedEquipmentRevision,
            error = error ?? string.Empty,
            state = null,
        };

    private static BackendPlayerItemBundleGrantResponse PlayerItemBundleGrantFailed(bool stale, long inventoryRevision, long equipmentRevision, string error) => new()
    { success = true, accepted = false, stale = stale, storedInventoryRevision = inventoryRevision, storedEquipmentRevision = equipmentRevision, error = error ?? string.Empty, state = null };

    private static BackendPlayerCraftResponse PlayerCraftFailed(bool stale, long inventoryRevision, long equipmentRevision, string error) => new()
    { success = true, accepted = false, stale = stale, storedInventoryRevision = inventoryRevision, storedEquipmentRevision = equipmentRevision, error = error ?? string.Empty, state = null };

    private static BackendPlayerItemPickupResponse PlayerItemPickupFailed(bool stale, long storedInventoryRevision, long storedEquipmentRevision, long worldRevision, string mapId, string instanceId, string error) => new()
    { success = true, accepted = false, stale = stale, storedInventoryRevision = storedInventoryRevision, storedEquipmentRevision = storedEquipmentRevision, worldRevision = worldRevision, mapId = mapId ?? string.Empty, instanceId = instanceId ?? string.Empty, error = error };

    private static void InitializeItemIdSequence(SQLiteConnection conn)
    {
        long maxCharacter = conn.ExecuteScalar<long>("SELECT COALESCE(MAX(itemInstanceId), 0) FROM character_items");
        long maxWorld = conn.ExecuteScalar<long>("SELECT COALESCE(MAX(itemInstanceId), 0) FROM world_items");
        long high = Math.Max(maxCharacter, maxWorld);
        ItemIdSequenceRow row = conn.Find<ItemIdSequenceRow>(1);
        if (row == null)
        {
            row = new ItemIdSequenceRow { sequenceId = 1, lastItemInstanceId = high };
            if (conn.Insert(row) != 1) throw new InvalidOperationException("Failed to initialize item id sequence.");
        }
        else if (row.lastItemInstanceId < high)
        {
            row.lastItemInstanceId = high;
            if (conn.Update(row) != 1) throw new InvalidOperationException("Failed to reconcile item id sequence.");
        }
    }

    private static long AllocateItemInstanceId(SQLiteConnection conn)
    {
        ItemIdSequenceRow row = conn.Find<ItemIdSequenceRow>(1) ?? throw new InvalidOperationException("Item id sequence is unavailable.");
        if (row.lastItemInstanceId >= BackendServiceContracts.TransientWorldItemIdFloor - 1)
            throw new InvalidOperationException("Durable item instance id space is exhausted.");
        row.lastItemInstanceId++;
        if (conn.Update(row) != 1) throw new InvalidOperationException("Failed to allocate item instance id.");
        return row.lastItemInstanceId;
    }

    private static BackendPlayerAmmoReloadResponse PlayerAmmoReloadFailed(
        bool stale,
        bool ammoUnavailable,
        long storedInventoryRevision,
        long storedEquipmentRevision,
        string error) =>
        new()
        {
            success = true,
            accepted = false,
            stale = stale,
            ammoUnavailable = ammoUnavailable,
            storedInventoryRevision = storedInventoryRevision,
            storedEquipmentRevision = storedEquipmentRevision,
            ammoDefinitionId = string.Empty,
            consumedRounds = 0,
            state = null,
            error = error ?? string.Empty,
        };

    private static BackendPlayerAmmoReloadResponse PlayerAmmoReloadUnavailable(string error) =>
        new()
        {
            success = false,
            accepted = false,
            stale = false,
            ammoUnavailable = false,
            storedInventoryRevision = 0,
            storedEquipmentRevision = 0,
            ammoDefinitionId = string.Empty,
            consumedRounds = 0,
            state = null,
            error = error ?? "ammo reload persistence unavailable",
        };

    private static BackendPlayerItemConsumeResponse PlayerItemConsumeFailed(
        bool stale,
        long storedInventoryRevision,
        long storedEquipmentRevision,
        string error) =>
        new()
        {
            success = true,
            accepted = false,
            stale = stale,
            storedInventoryRevision = storedInventoryRevision,
            storedEquipmentRevision = storedEquipmentRevision,
            error = error,
        };

    private static BackendPlayerSystemsCommitResponse PlayerSystemsCommitFailed(
        bool stale,
        long storedInventoryRevision,
        long storedEquipmentRevision,
        string error) =>
        new()
        {
            success = true,
            accepted = false,
            stale = stale,
            storedInventoryRevision = storedInventoryRevision,
            storedEquipmentRevision = storedEquipmentRevision,
            error = error,
        };

}
