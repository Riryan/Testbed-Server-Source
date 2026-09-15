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
    // Character-owned player systems transactions (inventory/equipment state, consume, reload).
    public BackendPlayerSystemsLoadResponse LoadPlayerSystems(
        long accountId,
        long characterId,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (accountId <= 0 || characterId <= 0 ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return PlayerSystemsLoadFailed(string.IsNullOrWhiteSpace(contentError) ? "invalid player-system request" : contentError);

        return Execute(conn =>
        {
            CharacterRow character = FindOwnedCharacter(conn, accountId, characterId);
            if (character == null)
            {
                return new BackendPlayerSystemsLoadResponse
                {
                    success = true,
                    found = false,
                    error = string.Empty,
                    state = null,
                };
            }

            conn.RunInTransaction(() => EnsurePlayerSystems(conn, characterId, content, DateTime.UtcNow.Ticks));
            return new BackendPlayerSystemsLoadResponse
            {
                success = true,
                found = true,
                error = string.Empty,
                state = ReadPlayerSystems(conn, characterId),
            };
        });
    }

    public BackendPlayerSystemsCommitResponse CommitPlayerSystems(
        BackendPlayerSystemsCommitRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.state == null ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return PlayerSystemsCommitFailed(false, 0, 0, string.IsNullOrWhiteSpace(contentError) ? "invalid item transaction" : contentError);

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerSystemsCommitFailed(false, 0, 0, "character authority lease unavailable");

        try
        {
            return Execute(conn =>
            {
                BackendPlayerSystemsCommitResponse response = null;
                conn.RunInTransaction(() =>
                {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null)
                {
                    response = PlayerSystemsCommitFailed(false, 0, 0, "character not found");
                    return;
                }

                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null)
                {
                    response = PlayerSystemsCommitFailed(false, 0, 0, "player-system state unavailable");
                    return;
                }

                if (request.expectedInventoryRevision != stored.inventoryRevision ||
                    request.expectedEquipmentRevision != stored.equipmentRevision)
                {
                    response = PlayerSystemsCommitFailed(
                        true,
                        stored.inventoryRevision,
                        stored.equipmentRevision,
                        "stale player-system revision");
                    return;
                }

                if (!ValidatePlayerSystemsMutation(conn, request.characterId, stored, request.state, content, out string error))
                {
                    response = PlayerSystemsCommitFailed(
                        false,
                        stored.inventoryRevision,
                        stored.equipmentRevision,
                        error);
                    return;
                }

                ApplyPlayerSystemsMutation(conn, request.characterId, request.state);
                stored.inventoryRevision = request.state.inventoryRevision;
                stored.equipmentRevision = request.state.equipmentRevision;
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                conn.Update(stored);

                response = new BackendPlayerSystemsCommitResponse
                {
                    success = true,
                    accepted = true,
                    stale = false,
                    storedInventoryRevision = stored.inventoryRevision,
                    storedEquipmentRevision = stored.equipmentRevision,
                    error = string.Empty,
                };
                });

                return response ?? PlayerSystemsCommitFailed(false, 0, 0, "item transaction unavailable");
            });
        }
        catch (SQLiteException)
        {
            // Persistence failures must be contained at the backend boundary. The caller
            // receives a normal rejected commit instead of a Kestrel request exception.
            return PlayerSystemsCommitUnavailable("player-system persistence unavailable");
        }
    }

    public BackendPlayerItemConsumeResponse ConsumePlayerItem(
        BackendPlayerItemConsumeRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        if (request == null || request.accountId <= 0 || request.characterId <= 0 ||
            request.itemInstanceId <= 0 || request.consumeQuantity < 1 || request.state == null ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
        {
            return PlayerItemConsumeFailed(false, 0, 0,
                string.IsNullOrWhiteSpace(contentError) ? "invalid consume transaction" : contentError);
        }

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerItemConsumeFailed(false, 0, 0, "character authority lease unavailable");

        return Execute(conn =>
        {
            BackendPlayerItemConsumeResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null)
                {
                    response = PlayerItemConsumeFailed(false, 0, 0, "character not found");
                    return;
                }

                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null)
                {
                    response = PlayerItemConsumeFailed(false, 0, 0, "player-system state unavailable");
                    return;
                }

                if (request.expectedInventoryRevision != stored.inventoryRevision ||
                    request.expectedEquipmentRevision != stored.equipmentRevision)
                {
                    response = PlayerItemConsumeFailed(
                        true,
                        stored.inventoryRevision,
                        stored.equipmentRevision,
                        "stale player-system revision");
                    return;
                }

                if (!ValidatePlayerItemConsumeMutation(
                        conn,
                        request.characterId,
                        stored,
                        request.itemInstanceId,
                        request.consumeQuantity,
                        request.state,
                        content,
                        out string error))
                {
                    response = PlayerItemConsumeFailed(
                        false,
                        stored.inventoryRevision,
                        stored.equipmentRevision,
                        error);
                    return;
                }

                ApplyPlayerSystemsMutation(conn, request.characterId, request.state);
                stored.inventoryRevision = request.state.inventoryRevision;
                stored.equipmentRevision = request.state.equipmentRevision;
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                conn.Update(stored);

                response = new BackendPlayerItemConsumeResponse
                {
                    success = true,
                    accepted = true,
                    stale = false,
                    storedInventoryRevision = stored.inventoryRevision,
                    storedEquipmentRevision = stored.equipmentRevision,
                    error = string.Empty,
                };
            });

            return response ?? PlayerItemConsumeFailed(false, 0, 0, "consume transaction unavailable");
        });
    }

    public BackendPlayerAmmoReloadResponse ConsumeAmmoForReload(
        BackendPlayerAmmoReloadRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        string ammoFamily = (request?.ammoFamily ?? string.Empty).Trim();
        string preferredDefinitionId = (request?.preferredAmmoDefinitionId ?? string.Empty).Trim();
        if (request == null || request.accountId <= 0 || request.characterId <= 0 ||
            string.IsNullOrWhiteSpace(ammoFamily) || request.maximumRounds < 1 || request.maximumRounds > 4096 ||
            request.weaponItemInstanceId <= 0 || request.expectedMagazineRevision < 0 ||
            request.currentLoadedRounds < 0 ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
        {
            return PlayerAmmoReloadFailed(
                false, false, 0, 0,
                string.IsNullOrWhiteSpace(contentError) ? "invalid ammo reload transaction" : contentError);
        }

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return PlayerAmmoReloadFailed(false, false, 0, 0, "character authority lease unavailable");

        try
        {
            return Execute(conn =>
            {
                BackendPlayerAmmoReloadResponse response = null;
                conn.RunInTransaction(() =>
                {
                    CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                    if (character == null)
                    {
                        response = PlayerAmmoReloadFailed(false, false, 0, 0, "character not found");
                        return;
                    }

                    EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                    CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                    if (stored == null)
                    {
                        response = PlayerAmmoReloadFailed(false, false, 0, 0, "player-system state unavailable");
                        return;
                    }

                    if (request.expectedInventoryRevision != stored.inventoryRevision ||
                        request.expectedEquipmentRevision != stored.equipmentRevision)
                    {
                        response = PlayerAmmoReloadFailed(
                            true, false, stored.inventoryRevision, stored.equipmentRevision,
                            "stale player-system revision");
                        return;
                    }

                    if (stored.inventoryRevision == long.MaxValue)
                    {
                        response = PlayerAmmoReloadFailed(
                            false, false, stored.inventoryRevision, stored.equipmentRevision,
                            "inventory revision is exhausted");
                        return;
                    }

                    CharacterItemRow weaponRow = conn.FindWithQuery<CharacterItemRow>(
                        "SELECT * FROM character_items WHERE characterId=? AND itemInstanceId=? LIMIT 1",
                        request.characterId,
                        request.weaponItemInstanceId);
                    if (weaponRow == null ||
                        weaponRow.containerKind != EquipmentContainer ||
                        !string.Equals(weaponRow.equipmentSlotId ?? string.Empty, "MainHand", StringComparison.Ordinal))
                    {
                        response = PlayerAmmoReloadFailed(
                            false, false, stored.inventoryRevision, stored.equipmentRevision,
                            "equipped firearm instance is unavailable");
                        return;
                    }

                    ItemDefinition weaponDefinition = FindItemDefinition(content, weaponRow.definitionId);
                    if (weaponDefinition == null ||
                        weaponDefinition.firearmMagazineCapacity <= 0 ||
                        !string.Equals(weaponDefinition.ammoFamily ?? string.Empty, ammoFamily, StringComparison.Ordinal))
                    {
                        response = PlayerAmmoReloadFailed(
                            false, false, stored.inventoryRevision, stored.equipmentRevision,
                            "equipped item is not a compatible firearm");
                        return;
                    }

                    long storedMagazineRevision = Math.Max(0, weaponRow.magazineRevision);
                    int storedLoadedRounds = Math.Max(0, weaponRow.loadedRounds);
                    string storedLoadedAmmo = NormalizeLoadedAmmo(weaponRow.loadedAmmoDefinitionId, storedLoadedRounds);
                    string currentLoadedAmmo = NormalizeLoadedAmmo(
                        request.currentLoadedAmmoDefinitionId,
                        request.currentLoadedRounds);

                    if (request.expectedMagazineRevision < storedMagazineRevision)
                    {
                        response = PlayerAmmoReloadFailed(
                            true, false, stored.inventoryRevision, stored.equipmentRevision,
                            "stale magazine revision");
                        return;
                    }

                    if (request.currentLoadedRounds > weaponDefinition.firearmMagazineCapacity)
                    {
                        response = PlayerAmmoReloadFailed(
                            false, false, stored.inventoryRevision, stored.equipmentRevision,
                            "current magazine exceeds weapon capacity");
                        return;
                    }

                    if (request.expectedMagazineRevision == storedMagazineRevision)
                    {
                        if (request.currentLoadedRounds != storedLoadedRounds ||
                            !string.Equals(currentLoadedAmmo, storedLoadedAmmo, StringComparison.Ordinal))
                        {
                            response = PlayerAmmoReloadFailed(
                                true, false, stored.inventoryRevision, stored.equipmentRevision,
                                "stale magazine state");
                            return;
                        }
                    }
                    else
                    {
                        // Higher GameServer magazine revisions represent unsaved shot
                        // decrements. They may move only downward; reload is the sole path
                        // allowed to increase loaded ammunition.
                        if (request.currentLoadedRounds > storedLoadedRounds ||
                            (request.currentLoadedRounds > 0 &&
                             storedLoadedRounds > 0 &&
                             !string.Equals(currentLoadedAmmo, storedLoadedAmmo, StringComparison.Ordinal)))
                        {
                            response = PlayerAmmoReloadFailed(
                                false, false, stored.inventoryRevision, stored.equipmentRevision,
                                "uncommitted magazine state would increase or transform ammunition");
                            return;
                        }
                    }

                    if (request.currentLoadedRounds > 0 &&
                        !ValidateMagazinePayload(
                            weaponDefinition,
                            currentLoadedAmmo,
                            request.currentLoadedRounds,
                            content,
                            out string magazineError))
                    {
                        response = PlayerAmmoReloadFailed(
                            false, false, stored.inventoryRevision, stored.equipmentRevision,
                            magazineError);
                        return;
                    }

                    int availableMagazineSpace = weaponDefinition.firearmMagazineCapacity - request.currentLoadedRounds;
                    if (availableMagazineSpace <= 0 || request.maximumRounds > availableMagazineSpace)
                    {
                        response = PlayerAmmoReloadFailed(
                            false, false, stored.inventoryRevision, stored.equipmentRevision,
                            "reload request exceeds available magazine capacity");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(preferredDefinitionId))
                    {
                        ItemDefinition preferredDefinition = FindItemDefinition(content, preferredDefinitionId);
                        if (preferredDefinition == null ||
                            preferredDefinition.kind != ItemKind.Ammo ||
                            !string.Equals(preferredDefinition.ammoFamily ?? string.Empty, ammoFamily, StringComparison.Ordinal))
                        {
                            response = PlayerAmmoReloadFailed(
                                false, true, stored.inventoryRevision, stored.equipmentRevision,
                                "compatible ammunition is unavailable");
                            return;
                        }
                    }

                    List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
                        "SELECT * FROM character_items WHERE characterId=? AND containerKind=? ORDER BY inventorySlot ASC, itemInstanceId ASC",
                        request.characterId,
                        InventoryContainer);

                    string selectedDefinitionId = preferredDefinitionId;
                    if (string.IsNullOrWhiteSpace(selectedDefinitionId) && request.currentLoadedRounds > 0)
                        selectedDefinitionId = currentLoadedAmmo;
                    if (string.IsNullOrWhiteSpace(selectedDefinitionId))
                    {
                        for (int i = 0; i < rows.Count; ++i)
                        {
                            CharacterItemRow row = rows[i];
                            if (row == null || row.quantity <= 0)
                                continue;
                            ItemDefinition definition = FindItemDefinition(content, row.definitionId);
                            if (definition == null || definition.kind != ItemKind.Ammo ||
                                !string.Equals(definition.ammoFamily ?? string.Empty, ammoFamily, StringComparison.Ordinal))
                                continue;
                            selectedDefinitionId = definition.definitionId;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(selectedDefinitionId))
                    {
                        response = PlayerAmmoReloadFailed(
                            false, true, stored.inventoryRevision, stored.equipmentRevision,
                            "compatible ammunition is unavailable");
                        return;
                    }

                    int remaining = request.maximumRounds;
                    int consumed = 0;
                    var consumptionPlan = new List<(CharacterItemRow Row, int Take)>();
                    for (int i = 0; i < rows.Count && remaining > 0; ++i)
                    {
                        CharacterItemRow row = rows[i];
                        if (row == null || row.quantity <= 0 ||
                            !string.Equals(row.definitionId, selectedDefinitionId, StringComparison.Ordinal))
                            continue;
                        if (row.revision == long.MaxValue)
                        {
                            response = PlayerAmmoReloadFailed(
                                false, false, stored.inventoryRevision, stored.equipmentRevision,
                                "ammo item revision is exhausted");
                            return;
                        }

                        int take = Math.Min(remaining, row.quantity);
                        if (take <= 0)
                            continue;
                        consumptionPlan.Add((row, take));
                        consumed += take;
                        remaining -= take;
                    }

                    if (consumed <= 0)
                    {
                        response = PlayerAmmoReloadFailed(
                            false, true, stored.inventoryRevision, stored.equipmentRevision,
                            "compatible ammunition is unavailable");
                        return;
                    }

                    if (request.currentLoadedRounds > 0 &&
                        !string.Equals(selectedDefinitionId, currentLoadedAmmo, StringComparison.Ordinal))
                    {
                        response = PlayerAmmoReloadFailed(
                            false, true, stored.inventoryRevision, stored.equipmentRevision,
                            "cannot mix ammunition types in a partially loaded magazine");
                        return;
                    }

                    for (int i = 0; i < consumptionPlan.Count; ++i)
                    {
                        CharacterItemRow row = consumptionPlan[i].Row;
                        int nextQuantity = row.quantity - consumptionPlan[i].Take;
                        if (nextQuantity == 0)
                        {
                            int deleted = conn.Execute(
                                "DELETE FROM character_items WHERE characterId=? AND itemInstanceId=?",
                                request.characterId,
                                row.itemInstanceId);
                            if (deleted != 1)
                                throw new InvalidOperationException("Failed to consume ammo item instance.");
                        }
                        else
                        {
                            row.quantity = nextQuantity;
                            row.revision++;
                            if (conn.Update(row) != 1)
                                throw new InvalidOperationException("Failed to update ammo item instance.");
                        }
                    }

                    int nextLoadedRounds = checked(request.currentLoadedRounds + consumed);
                    weaponRow.loadedAmmoDefinitionId = NormalizeLoadedAmmo(selectedDefinitionId, nextLoadedRounds);
                    weaponRow.loadedRounds = nextLoadedRounds;
                    weaponRow.magazineRevision = checked(request.expectedMagazineRevision + 1);
                    if (conn.Update(weaponRow) != 1)
                        throw new InvalidOperationException("Failed to persist firearm magazine during reload.");

                    stored.inventoryRevision++;
                    stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                    if (conn.Update(stored) != 1)
                        throw new InvalidOperationException("Failed to advance inventory revision for reload.");

                    response = new BackendPlayerAmmoReloadResponse
                    {
                        success = true,
                        accepted = true,
                        stale = false,
                        ammoUnavailable = false,
                        storedInventoryRevision = stored.inventoryRevision,
                        storedEquipmentRevision = stored.equipmentRevision,
                        ammoDefinitionId = selectedDefinitionId,
                        consumedRounds = consumed,
                        state = ReadPlayerSystems(conn, request.characterId),
                        error = string.Empty,
                    };
                });

                return response ?? PlayerAmmoReloadFailed(false, false, 0, 0, "ammo reload transaction unavailable");
            });
        }
        catch (SQLiteException)
        {
            return PlayerAmmoReloadUnavailable("ammo reload persistence unavailable");
        }
    }
}
