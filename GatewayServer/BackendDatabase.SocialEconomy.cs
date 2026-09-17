using Game.Shared.Backend;
using Game.Shared.Content;
using SQLite;

namespace Game.BackendServer;

internal sealed partial class BackendDatabase
{
    private const byte StorageContainer = 3;
    private const int DefaultStorageCapacity = 100;
    private bool _socialEconomySchemaReady;

    [Table("character_friends")]
    private sealed class CharacterFriendRow
    {
        [PrimaryKey, AutoIncrement] public long id { get; set; }
        public long characterId { get; set; }
        public long friendCharacterId { get; set; }
        public long createdUtcTicks { get; set; }
    }

    [Table("character_storage_state")]
    private sealed class CharacterStorageStateRow
    {
        [PrimaryKey] public long characterId { get; set; }
        public int capacity { get; set; }
        public long revision { get; set; }
        public long updatedUtcTicks { get; set; }
    }

    public void InitializeSocialEconomy() => Execute(EnsureSocialEconomySchema);

    private void EnsureSocialEconomySchema(SQLiteConnection conn)
    {
        if (_socialEconomySchemaReady) return;
        conn.CreateTable<CharacterFriendRow>();
        conn.CreateTable<CharacterStorageStateRow>();
        conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_character_friend_pair ON character_friends(characterId, friendCharacterId)");
        conn.CreateIndex("idx_character_friends_owner", "character_friends", "characterId", false);
        conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_character_storage_slot " +
                     "ON character_items(characterId, containerKind, inventorySlot) WHERE containerKind=3");
        conn.Execute(@"CREATE TRIGGER IF NOT EXISTS trg_social_character_delete
                       AFTER DELETE ON characters
                       BEGIN
                           DELETE FROM character_friends WHERE characterId=OLD.characterId OR friendCharacterId=OLD.characterId;
                           DELETE FROM character_storage_state WHERE characterId=OLD.characterId;
                       END");
        _socialEconomySchemaReady = true;
    }

    public BackendFriendResponse LoadFriends(long characterId)
    {
        if (characterId <= 0)
            return FriendFailed("invalid character");

        return Execute(conn =>
        {
            EnsureSocialEconomySchema(conn);
            if (conn.Find<CharacterRow>(characterId) == null)
                return FriendFailed("character not found");
            return FriendSucceeded(ReadFriends(conn, characterId));
        });
    }

    public BackendFriendResponse AddFriend(BackendFriendMutationRequest request)
    {
        if (!ValidateFriendMutation(request, out string error))
            return FriendFailed(error);

        return Execute(conn =>
        {
            EnsureSocialEconomySchema(conn);
            BackendFriendResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow actor = FindOwnedCharacter(conn, request.accountId, request.characterId);
                CharacterRow other = conn.Find<CharacterRow>(request.otherCharacterId);
                if (actor == null || other == null)
                {
                    response = FriendFailed("character not found");
                    return;
                }
                if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
                {
                    response = FriendFailed("character authority lease unavailable");
                    return;
                }
                if (request.characterId == request.otherCharacterId)
                {
                    response = FriendFailed("cannot friend yourself");
                    return;
                }

                InsertFriendPairIfMissing(conn, request.characterId, request.otherCharacterId);
                InsertFriendPairIfMissing(conn, request.otherCharacterId, request.characterId);
                response = FriendSucceeded(ReadFriends(conn, request.characterId));
            });
            return response ?? FriendFailed("friend transaction unavailable");
        });
    }

    public BackendFriendResponse RemoveFriend(BackendFriendMutationRequest request)
    {
        if (!ValidateFriendMutation(request, out string error))
            return FriendFailed(error);

        return Execute(conn =>
        {
            EnsureSocialEconomySchema(conn);
            BackendFriendResponse response = null;
            conn.RunInTransaction(() =>
            {
                if (FindOwnedCharacter(conn, request.accountId, request.characterId) == null)
                {
                    response = FriendFailed("character not found");
                    return;
                }
                if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
                {
                    response = FriendFailed("character authority lease unavailable");
                    return;
                }
                conn.Execute("DELETE FROM character_friends WHERE characterId=? AND friendCharacterId=?", request.characterId, request.otherCharacterId);
                conn.Execute("DELETE FROM character_friends WHERE characterId=? AND friendCharacterId=?", request.otherCharacterId, request.characterId);
                response = FriendSucceeded(ReadFriends(conn, request.characterId));
            });
            return response ?? FriendFailed("friend transaction unavailable");
        });
    }

    public BackendTradeCommitResponse CommitTrade(BackendTradeCommitRequest request, GameplayContentSnapshot content)
    {
        if (request == null || request.leftAccountId <= 0 || request.rightAccountId <= 0 ||
            request.leftCharacterId <= 0 || request.rightCharacterId <= 0 ||
            request.leftCharacterId == request.rightCharacterId)
            return TradeFailed(false, "invalid trade transaction");

        if (!GameplayContentValidation.TryValidate(content, out string contentError))
            return TradeFailed(false, string.IsNullOrWhiteSpace(contentError) ? "invalid gameplay content" : contentError);

        BackendTradeOfferDto[] leftOffers = request.leftOffers ?? Array.Empty<BackendTradeOfferDto>();
        BackendTradeOfferDto[] rightOffers = request.rightOffers ?? Array.Empty<BackendTradeOfferDto>();
        if (leftOffers.Length == 0 && rightOffers.Length == 0)
            return TradeFailed(false, "trade has no offered items");
        if (leftOffers.Length > 64 || rightOffers.Length > 64)
            return TradeFailed(false, "trade offer is too large");

        return Execute(conn =>
        {
            EnsureSocialEconomySchema(conn);
            BackendTradeCommitResponse response = null;
            conn.RunInTransaction(() =>
            {
                CharacterRow leftCharacter = FindOwnedCharacter(conn, request.leftAccountId, request.leftCharacterId);
                CharacterRow rightCharacter = FindOwnedCharacter(conn, request.rightAccountId, request.rightCharacterId);
                if (leftCharacter == null || rightCharacter == null)
                {
                    response = TradeFailed(false, "trade character not found");
                    return;
                }
                if (!_characterLeases.IsCurrentOwner(request.leftCharacterId, request.leftLeaseOwnerToken) ||
                    !_characterLeases.IsCurrentOwner(request.rightCharacterId, request.rightLeaseOwnerToken))
                {
                    response = TradeFailed(false, "character authority lease unavailable");
                    return;
                }

                EnsurePlayerSystems(conn, request.leftCharacterId, content, DateTime.UtcNow.Ticks);
                EnsurePlayerSystems(conn, request.rightCharacterId, content, DateTime.UtcNow.Ticks);
                CharacterPlayerSystemsRow leftState = conn.Find<CharacterPlayerSystemsRow>(request.leftCharacterId);
                CharacterPlayerSystemsRow rightState = conn.Find<CharacterPlayerSystemsRow>(request.rightCharacterId);
                if (leftState == null || rightState == null)
                {
                    response = TradeFailed(false, "player-system state unavailable");
                    return;
                }
                if (leftState.inventoryRevision != request.leftExpectedInventoryRevision ||
                    leftState.equipmentRevision != request.leftExpectedEquipmentRevision ||
                    rightState.inventoryRevision != request.rightExpectedInventoryRevision ||
                    rightState.equipmentRevision != request.rightExpectedEquipmentRevision)
                {
                    response = TradeFailed(true, "stale player-system revision");
                    return;
                }

                if (!TryPlanTradeSide(conn, request.leftCharacterId, leftState.inventoryCapacity, leftOffers, rightOffers.Length, out List<TradeSourcePlan> leftPlan, out List<int> leftIncomingSlots, out string error) ||
                    !TryPlanTradeSide(conn, request.rightCharacterId, rightState.inventoryCapacity, rightOffers, leftOffers.Length, out List<TradeSourcePlan> rightPlan, out List<int> rightIncomingSlots, out error))
                {
                    response = TradeFailed(false, error);
                    return;
                }

                ApplyTradeOutgoing(conn, leftPlan);
                ApplyTradeOutgoing(conn, rightPlan);
                ApplyTradeIncoming(conn, rightCharacter.characterId, rightIncomingSlots, leftPlan);
                ApplyTradeIncoming(conn, leftCharacter.characterId, leftIncomingSlots, rightPlan);

                leftState.inventoryRevision = checked(leftState.inventoryRevision + 1);
                rightState.inventoryRevision = checked(rightState.inventoryRevision + 1);
                leftState.updatedUtcTicks = rightState.updatedUtcTicks = DateTime.UtcNow.Ticks;
                conn.Update(leftState);
                conn.Update(rightState);

                response = new BackendTradeCommitResponse
                {
                    success = true,
                    stale = false,
                    error = string.Empty,
                    leftState = ReadPlayerSystems(conn, request.leftCharacterId),
                    rightState = ReadPlayerSystems(conn, request.rightCharacterId),
                };
            });
            return response ?? TradeFailed(false, "trade transaction unavailable");
        });
    }

    public BackendStorageLoadResponse LoadStorage(BackendStorageLoadRequest request)
    {
        if (request == null || request.accountId <= 0 || request.characterId <= 0)
            return StorageLoadFailed("invalid storage load request");
        return Execute(conn =>
        {
            EnsureSocialEconomySchema(conn);
            if (FindOwnedCharacter(conn, request.accountId, request.characterId) == null)
                return StorageLoadFailed("character not found");
            EnsureStorage(conn, request.characterId);
            return new BackendStorageLoadResponse { success = true, error = string.Empty, storage = ReadStorage(conn, request.characterId) };
        });
    }

    public BackendStorageTransferResponse TransferStorage(BackendStorageTransferRequest request)
    {
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.sourceItemInstanceId <= 0 || request.quantity <= 0)
            return StorageTransferFailed(false, "invalid storage transfer request");

        return Execute(conn =>
        {
            EnsureSocialEconomySchema(conn);
            BackendStorageTransferResponse response = null;
            conn.RunInTransaction(() =>
            {
                if (FindOwnedCharacter(conn, request.accountId, request.characterId) == null)
                {
                    response = StorageTransferFailed(false, "character not found"); return;
                }
                if (!_characterLeases.IsCurrentOwner(request.characterId, request.leaseOwnerToken))
                {
                    response = StorageTransferFailed(false, "character authority lease unavailable"); return;
                }
                CharacterPlayerSystemsRow playerState = conn.Find<CharacterPlayerSystemsRow>(request.characterId);
                if (playerState == null)
                {
                    response = StorageTransferFailed(false, "player-system state unavailable"); return;
                }
                CharacterStorageStateRow storageState = EnsureStorage(conn, request.characterId);
                if (playerState.inventoryRevision != request.expectedInventoryRevision ||
                    playerState.equipmentRevision != request.expectedEquipmentRevision ||
                    storageState.revision != request.expectedStorageRevision)
                {
                    response = StorageTransferFailed(true, "stale inventory/storage revision"); return;
                }

                byte sourceContainer = request.deposit ? InventoryContainer : StorageContainer;
                byte targetContainer = request.deposit ? StorageContainer : InventoryContainer;
                int targetCapacity = request.deposit ? storageState.capacity : playerState.inventoryCapacity;
                CharacterItemRow source = conn.Find<CharacterItemRow>(request.sourceItemInstanceId);
                if (source == null || source.characterId != request.characterId || source.containerKind != sourceContainer || request.quantity > source.quantity)
                {
                    response = StorageTransferFailed(false, "storage source item is unavailable"); return;
                }
                if (request.quantity < source.quantity && source.loadedRounds > 0)
                {
                    response = StorageTransferFailed(false, "loaded weapon instances cannot be split"); return;
                }

                int targetSlot = FindFirstEmptyContainerSlot(conn, request.characterId, targetContainer, targetCapacity);
                bool fullStack = request.quantity == source.quantity;
                if (targetSlot < 0 && !fullStack)
                {
                    response = StorageTransferFailed(false, request.deposit ? "storage is full" : "inventory is full"); return;
                }
                if (targetSlot < 0 && fullStack)
                {
                    // A full move frees the source slot only when source and target are the same container,
                    // which is never true here. Target capacity therefore still needs an empty slot.
                    response = StorageTransferFailed(false, request.deposit ? "storage is full" : "inventory is full"); return;
                }

                if (fullStack)
                {
                    source.containerKind = 0;
                    source.inventorySlot = -1;
                    source.equipmentSlotId = string.Empty;
                    conn.Update(source);
                    source.containerKind = targetContainer;
                    source.inventorySlot = targetSlot;
                    source.revision = checked(source.revision + 1);
                    conn.Update(source);
                }
                else
                {
                    source.quantity -= request.quantity;
                    source.revision = checked(source.revision + 1);
                    conn.Update(source);
                    CharacterItemRow split = CopyItem(source);
                    split.itemInstanceId = AllocateItemInstanceId(conn);
                    split.containerKind = targetContainer;
                    split.inventorySlot = targetSlot;
                    split.quantity = request.quantity;
                    split.revision = 0;
                    if (conn.Insert(split) != 1)
                        throw new InvalidOperationException("failed to create storage split item");
                }

                playerState.inventoryRevision = checked(playerState.inventoryRevision + 1);
                playerState.updatedUtcTicks = DateTime.UtcNow.Ticks;
                storageState.revision = checked(storageState.revision + 1);
                storageState.updatedUtcTicks = playerState.updatedUtcTicks;
                conn.Update(playerState);
                conn.Update(storageState);
                response = new BackendStorageTransferResponse
                {
                    success = true,
                    stale = false,
                    error = string.Empty,
                    playerState = ReadPlayerSystems(conn, request.characterId),
                    storage = ReadStorage(conn, request.characterId),
                };
            });
            return response ?? StorageTransferFailed(false, "storage transaction unavailable");
        });
    }

    private sealed class TradeSourcePlan
    {
        public CharacterItemRow Source;
        public int Quantity;
        public bool FullStack;
    }

    private static bool TryPlanTradeSide(SQLiteConnection conn, long characterId, int capacity, BackendTradeOfferDto[] outgoing, int incomingStackCount,
        out List<TradeSourcePlan> plan, out List<int> incomingSlots, out string error)
    {
        plan = new List<TradeSourcePlan>(outgoing.Length);
        incomingSlots = new List<int>(incomingStackCount);
        var offeredIds = new HashSet<long>();
        var occupied = new bool[capacity];
        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? AND containerKind=? ORDER BY inventorySlot ASC", characterId, InventoryContainer);
        foreach (CharacterItemRow row in rows)
            if (row.inventorySlot >= 0 && row.inventorySlot < capacity) occupied[row.inventorySlot] = true;

        foreach (BackendTradeOfferDto offer in outgoing)
        {
            if (offer == null || offer.itemInstanceId <= 0 || offer.quantity <= 0 || !offeredIds.Add(offer.itemInstanceId))
            { error = "trade offer is invalid or duplicated"; return false; }
            CharacterItemRow row = conn.Find<CharacterItemRow>(offer.itemInstanceId);
            if (row == null || row.characterId != characterId || row.containerKind != InventoryContainer || offer.quantity > row.quantity)
            { error = "offered item is unavailable"; return false; }
            bool full = offer.quantity == row.quantity;
            if (!full && row.loadedRounds > 0)
            { error = "loaded weapon instances cannot be split"; return false; }
            if (full && row.inventorySlot >= 0 && row.inventorySlot < capacity) occupied[row.inventorySlot] = false;
            plan.Add(new TradeSourcePlan { Source = row, Quantity = offer.quantity, FullStack = full });
        }

        for (int n = 0; n < incomingStackCount; ++n)
        {
            int slot = -1;
            for (int i = 0; i < occupied.Length; ++i) if (!occupied[i]) { slot = i; occupied[i] = true; break; }
            if (slot < 0) { error = "trade target inventory does not have enough empty slots"; return false; }
            incomingSlots.Add(slot);
        }
        error = string.Empty;
        return true;
    }

    private static void ApplyTradeOutgoing(SQLiteConnection conn, List<TradeSourcePlan> plans)
    {
        foreach (TradeSourcePlan plan in plans)
        {
            if (plan.FullStack)
            {
                plan.Source.containerKind = 0;
                plan.Source.inventorySlot = -1;
                plan.Source.equipmentSlotId = string.Empty;
                conn.Update(plan.Source);
            }
            else
            {
                plan.Source.quantity -= plan.Quantity;
                plan.Source.revision = checked(plan.Source.revision + 1);
                conn.Update(plan.Source);
            }
        }
    }

    private static void ApplyTradeIncoming(SQLiteConnection conn, long targetCharacterId, List<int> slots, List<TradeSourcePlan> plans)
    {
        for (int i = 0; i < plans.Count; ++i)
        {
            TradeSourcePlan plan = plans[i];
            if (plan.FullStack)
            {
                plan.Source.characterId = targetCharacterId;
                plan.Source.containerKind = InventoryContainer;
                plan.Source.inventorySlot = slots[i];
                plan.Source.revision = checked(plan.Source.revision + 1);
                conn.Update(plan.Source);
            }
            else
            {
                CharacterItemRow split = CopyItem(plan.Source);
                split.itemInstanceId = AllocateItemInstanceId(conn);
                split.characterId = targetCharacterId;
                split.containerKind = InventoryContainer;
                split.inventorySlot = slots[i];
                split.quantity = plan.Quantity;
                split.revision = 0;
                if (conn.Insert(split) != 1)
                    throw new InvalidOperationException("failed to create traded split item");
            }
        }
    }

    private static CharacterItemRow CopyItem(CharacterItemRow source) => new CharacterItemRow
    {
        itemInstanceId = source.itemInstanceId,
        characterId = source.characterId,
        containerKind = source.containerKind,
        inventorySlot = source.inventorySlot,
        equipmentSlotId = source.equipmentSlotId ?? string.Empty,
        definitionId = source.definitionId,
        quantity = source.quantity,
        durability = source.durability,
        revision = source.revision,
        loadedAmmoDefinitionId = source.loadedAmmoDefinitionId ?? string.Empty,
        loadedRounds = source.loadedRounds,
        magazineRevision = source.magazineRevision,
    };

    private static int FindFirstEmptyContainerSlot(SQLiteConnection conn, long characterId, byte containerKind, int capacity)
    {
        var used = new bool[Math.Max(1, capacity)];
        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? AND containerKind=?", characterId, containerKind);
        foreach (CharacterItemRow row in rows)
            if (row.inventorySlot >= 0 && row.inventorySlot < used.Length) used[row.inventorySlot] = true;
        for (int i = 0; i < used.Length; ++i) if (!used[i]) return i;
        return -1;
    }

    private static CharacterStorageStateRow EnsureStorage(SQLiteConnection conn, long characterId)
    {
        CharacterStorageStateRow row = conn.Find<CharacterStorageStateRow>(characterId);
        if (row != null) return row;
        row = new CharacterStorageStateRow { characterId = characterId, capacity = DefaultStorageCapacity, revision = 0, updatedUtcTicks = DateTime.UtcNow.Ticks };
        if (conn.Insert(row) != 1) throw new InvalidOperationException("failed to initialize character storage");
        return row;
    }

    private static BackendStorageSnapshotDto ReadStorage(SQLiteConnection conn, long characterId)
    {
        CharacterStorageStateRow state = EnsureStorage(conn, characterId);
        List<CharacterItemRow> rows = conn.Query<CharacterItemRow>(
            "SELECT * FROM character_items WHERE characterId=? AND containerKind=? ORDER BY inventorySlot ASC, itemInstanceId ASC", characterId, StorageContainer);
        var items = new BackendPersistedItemDto[rows.Count];
        for (int i = 0; i < rows.Count; ++i) items[i] = ToStorageItemDto(rows[i]);
        return new BackendStorageSnapshotDto { capacity = state.capacity, revision = state.revision, items = items };
    }

    private static BackendPersistedItemDto ToStorageItemDto(CharacterItemRow row)
    {
        BackendPersistedItemDto item = ToItemDto(row);
        // ToItemDto intentionally exposes an inventory slot only for container 1.
        // Storage uses the same durable item DTO, so restore the storage slot here.
        item.inventorySlot = row.inventorySlot;
        return item;
    }

    private static void InsertFriendPairIfMissing(SQLiteConnection conn, long characterId, long friendCharacterId)
    {
        long count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM character_friends WHERE characterId=? AND friendCharacterId=?", characterId, friendCharacterId);
        if (count > 0) return;
        conn.Insert(new CharacterFriendRow { characterId = characterId, friendCharacterId = friendCharacterId, createdUtcTicks = DateTime.UtcNow.Ticks });
    }

    private static BackendFriendEntryDto[] ReadFriends(SQLiteConnection conn, long characterId)
    {
        List<CharacterFriendRow> rows = conn.Query<CharacterFriendRow>(
            "SELECT * FROM character_friends WHERE characterId=? ORDER BY friendCharacterId ASC", characterId);
        var result = new BackendFriendEntryDto[rows.Count];
        for (int i = 0; i < rows.Count; ++i)
        {
            CharacterRow character = conn.Find<CharacterRow>(rows[i].friendCharacterId);
            result[i] = new BackendFriendEntryDto { characterId = rows[i].friendCharacterId, name = character?.name ?? string.Empty };
        }
        return result;
    }

    private bool ValidateFriendMutation(BackendFriendMutationRequest request, out string error)
    {
        if (request == null || request.accountId <= 0 || request.characterId <= 0 || request.otherCharacterId <= 0)
        { error = "invalid friend request"; return false; }
        error = string.Empty; return true;
    }

    private static BackendFriendResponse FriendSucceeded(BackendFriendEntryDto[] friends) => new BackendFriendResponse { success = true, error = string.Empty, friends = friends ?? Array.Empty<BackendFriendEntryDto>() };
    private static BackendFriendResponse FriendFailed(string error) => new BackendFriendResponse { success = false, error = error ?? "friend operation failed", friends = Array.Empty<BackendFriendEntryDto>() };
    private static BackendTradeCommitResponse TradeFailed(bool stale, string error) => new BackendTradeCommitResponse { success = false, stale = stale, error = error ?? "trade failed" };
    private static BackendStorageLoadResponse StorageLoadFailed(string error) => new BackendStorageLoadResponse { success = false, error = error ?? "storage load failed" };
    private static BackendStorageTransferResponse StorageTransferFailed(bool stale, string error) => new BackendStorageTransferResponse { success = false, stale = stale, error = error ?? "storage transfer failed" };
}
