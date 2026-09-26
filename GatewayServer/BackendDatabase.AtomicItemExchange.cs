using System;
using System.Collections.Generic;
using Game.Shared.Backend;
using Game.Shared.Content;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    public BackendPlayerItemExchangeResponse ExchangePlayerItems(
        BackendPlayerItemExchangeRequest request,
        GameplayContentSnapshot content)
    {
        string contentError = string.Empty;
        BackendRewardItemDto[] requested = request?.items ?? Array.Empty<BackendRewardItemDto>();
        if (request == null || request.accountId <= 0 || request.characterId <= 0 ||
            request.costItemDataId == 0 || request.costQuantity < 1 ||
            requested.Length == 0 || requested.Length > 64 ||
            !GameplayContentSnapshotCache.TryValidate(content, out contentError))
            return ExchangeFailed(false, 0, 0,
                string.IsNullOrWhiteSpace(contentError) ? "invalid item exchange transaction" : contentError);

        if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
            return ExchangeFailed(false, 0, 0, "character authority lease unavailable");

        ItemDefinition costDefinition = FindItemDefinition(content, request.costItemDataId);
        if (costDefinition == null)
            return ExchangeFailed(false, 0, 0, "exchange cost item is unavailable");

        if (!TryAggregateRewardItems(requested, content, out Dictionary<ushort, int> grants, out string grantError))
            return ExchangeFailed(false, 0, 0, grantError);

        return Execute(conn =>
        {
            BackendPlayerItemExchangeResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow character = FindOwnedCharacter(conn, request.accountId, request.characterId);
                if (character == null) { response = ExchangeFailed(false, 0, 0, "character not found"); return; }

                EnsurePlayerSystems(conn, request.characterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow stored = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (stored == null) { response = ExchangeFailed(false, 0, 0, "player-system state unavailable"); return; }
                if (request.expectedInventoryRevision != stored.inventoryRevision ||
                    request.expectedEquipmentRevision != stored.equipmentRevision)
                {
                    response = ExchangeFailed(true, stored.inventoryRevision, stored.equipmentRevision,
                        "stale player-system revision");
                    return;
                }

                List<CharacterItemRow> original = LoadInventoryRows(conn, request.characterId);
                List<CharacterItemRow> work = CloneInventoryRows(original);

                long available = 0;
                for (int i = 0; i < work.Count; ++i)
                {
                    CharacterItemRow row = work[i];
                    if (row != null && row.quantity > 0 &&
                        string.Equals(row.definitionId, costDefinition.definitionId, StringComparison.Ordinal))
                        available += row.quantity;
                }
                if (available < request.costQuantity)
                {
                    response = ExchangeFailed(false, stored.inventoryRevision, stored.equipmentRevision,
                        "required exchange items are unavailable");
                    return;
                }

                int remainingCost = request.costQuantity;
                for (int i = 0; i < work.Count && remainingCost > 0; ++i)
                {
                    CharacterItemRow row = work[i];
                    if (row == null || row.quantity <= 0 ||
                        !string.Equals(row.definitionId, costDefinition.definitionId, StringComparison.Ordinal))
                        continue;
                    int remove = Math.Min(row.quantity, remainingCost);
                    row.quantity -= remove;
                    remainingCost -= remove;
                }

                work.RemoveAll(row => row == null || row.quantity <= 0);

                // Important: grant planning runs AFTER cost consumption. Slots emptied by
                // payment are therefore immediately available to the purchased items.
                if (!TryApplyRewardGrants(work, stored.inventoryCapacity, grants, content,
                        request.characterId, out string planningError))
                {
                    response = ExchangeFailed(false, stored.inventoryRevision, stored.equipmentRevision, planningError);
                    return;
                }

                ApplyPlannedInventory(conn, request.characterId, original, work);
                stored.inventoryRevision = checked(stored.inventoryRevision + 1);
                stored.updatedUtcTicks = DateTime.UtcNow.Ticks;
                if (conn.Update(stored) != 1)
                    throw new InvalidOperationException("Failed to advance inventory revision after item exchange.");

                response = new BackendPlayerItemExchangeResponse
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
            return response ?? ExchangeFailed(false, 0, 0, "item exchange transaction unavailable");
        });
    }

    private static BackendPlayerItemExchangeResponse ExchangeFailed(
        bool stale, long inventoryRevision, long equipmentRevision, string error) => new()
    {
        success = true,
        accepted = false,
        stale = stale,
        storedInventoryRevision = inventoryRevision,
        storedEquipmentRevision = equipmentRevision,
        error = error ?? string.Empty,
        state = null,
    };
}
