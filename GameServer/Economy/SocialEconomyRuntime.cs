using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Backend;
using Game.Server.Application.Interactions;
using Game.Server.Application.Items;
using Game.Server.Domain.Players;
using Game.Shared.Backend;
using Game.Shared.Interactions;
using Game.Shared.Protocol;

namespace Game.GameServer.Economy
{
    [Flags]
    internal enum SocialEconomyStateKind : byte
    {
        None = 0,
        Friends = 1,
        Trade = 2,
        Storage = 4,
    }

    internal readonly struct SocialEconomyResult
    {
        public bool Success { get; }
        public string Error { get; }
        public long OtherCharacterId { get; }
        public bool Completed { get; }

        public SocialEconomyResult(bool success, string error = "", long otherCharacterId = 0, bool completed = false)
        {
            Success = success;
            Error = error ?? string.Empty;
            OtherCharacterId = otherCharacterId;
            Completed = completed;
        }

        public static SocialEconomyResult Ok(long otherCharacterId = 0, bool completed = false) =>
            new SocialEconomyResult(true, string.Empty, otherCharacterId, completed);
        public static SocialEconomyResult Fail(string error, long otherCharacterId = 0) =>
            new SocialEconomyResult(false, error, otherCharacterId, false);
    }

    internal sealed class FriendView
    {
        public BackendFriendEntryDto[] Friends { get; init; } = Array.Empty<BackendFriendEntryDto>();
        public long PendingInviterCharacterId { get; init; }
        public string PendingInviterName { get; init; } = string.Empty;
    }

    internal sealed class TradeOfferView
    {
        public int SourceSlot { get; init; }
        public long ItemInstanceId { get; init; }
        public int Quantity { get; init; }
        public PlayerItemView Item { get; init; }
    }

    internal sealed class TradeView
    {
        public ulong SessionId { get; init; }
        public long PartnerCharacterId { get; init; }
        public string PartnerName { get; init; } = string.Empty;
        public byte Phase { get; init; }
        public bool OwnLocked { get; init; }
        public bool PartnerLocked { get; init; }
        public bool OwnConfirmed { get; init; }
        public bool PartnerConfirmed { get; init; }
        public TradeOfferView[] OwnOffers { get; init; } = Array.Empty<TradeOfferView>();
        public TradeOfferView[] PartnerOffers { get; init; } = Array.Empty<TradeOfferView>();
        public string Detail { get; init; } = string.Empty;
    }

    /// <summary>
    /// Small GameServer-local coordinator for Friends, P2P Trade and personal Storage.
    /// Durable mutations are delegated to Gateway; item mutations share PlayerItemService's
    /// existing serialization gates so ordinary inventory requests cannot race Trade/Storage.
    /// </summary>
    internal sealed class SocialEconomyRuntime
    {
        private const byte TradePhasePending = 1;
        private const byte TradePhaseActive = 2;
        private const byte TradePhaseCommitting = 3;
        private const int MaxTradeOffers = 64;
        private static readonly TimeSpan InviteLifetime = TimeSpan.FromSeconds(60);

        private sealed class FriendInvite
        {
            public long InviterCharacterId;
            public string InviterName;
            public DateTime ExpiresUtc;
        }

        private sealed class TradeOffer
        {
            public int SourceSlot;
            public long ItemInstanceId;
            public int Quantity;
        }

        private sealed class TradeSession
        {
            public ulong Id;
            public long LeftCharacterId;
            public string LeftName;
            public long RightCharacterId;
            public string RightName;
            public byte Phase;
            public bool LeftLocked;
            public bool RightLocked;
            public bool LeftConfirmed;
            public bool RightConfirmed;
            public readonly List<TradeOffer> LeftOffers = new List<TradeOffer>();
            public readonly List<TradeOffer> RightOffers = new List<TradeOffer>();
        }

        private readonly object _gate = new object();
        private readonly BackendSocialEconomyRepository _repository;
        private readonly PlayerItemService _items;
        private readonly Func<long, PlayerRuntime> _resolvePlayer;
        private readonly Dictionary<long, BackendFriendEntryDto[]> _friends = new Dictionary<long, BackendFriendEntryDto[]>();
        private readonly Dictionary<long, FriendInvite> _friendInvites = new Dictionary<long, FriendInvite>();
        private readonly Dictionary<long, TradeSession> _tradeByCharacter = new Dictionary<long, TradeSession>();
        private readonly Dictionary<long, BackendStorageSnapshotDto> _storage = new Dictionary<long, BackendStorageSnapshotDto>();
        private ulong _nextTradeId = 1;

        public event Action<long, SocialEconomyStateKind> StateChanged;

        public SocialEconomyRuntime(
            BackendSocialEconomyRepository repository,
            PlayerItemService items,
            Func<long, PlayerRuntime> resolvePlayer)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _resolvePlayer = resolvePlayer ?? throw new ArgumentNullException(nameof(resolvePlayer));
        }

        public SocialEconomyResult InviteFriend(PlayerRuntime inviter, PlayerRuntime target)
        {
            if (!TryIdentity(inviter, out long inviterId, out string inviterName) ||
                !TryIdentity(target, out long targetId, out _))
                return SocialEconomyResult.Fail("Friend target is unavailable.");
            if (inviterId == targetId)
                return SocialEconomyResult.Fail("You cannot add yourself as a friend.");

            lock (_gate)
            {
                if (CachedFriendsContains(inviterId, targetId))
                    return SocialEconomyResult.Fail("That player is already your friend.");

                _friendInvites[targetId] = new FriendInvite
                {
                    InviterCharacterId = inviterId,
                    InviterName = inviterName,
                    ExpiresUtc = DateTime.UtcNow + InviteLifetime,
                };
            }
            Publish(SocialEconomyStateKind.Friends, inviterId, targetId);
            return SocialEconomyResult.Ok(targetId);
        }

        public async Task<SocialEconomyResult> ApplyFriendActionAsync(
            PlayerRuntime actor,
            byte action,
            long targetCharacterId,
            CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long actorId, out _))
                return SocialEconomyResult.Fail("Character is unavailable.");

            switch (action)
            {
                case 1: // Accept
                {
                    FriendInvite invite;
                    lock (_gate)
                    {
                        PruneFriendInvite(actorId);
                        if (!_friendInvites.TryGetValue(actorId, out invite) ||
                            (targetCharacterId > 0 && invite.InviterCharacterId != targetCharacterId))
                            return SocialEconomyResult.Fail("Friend invite is no longer available.");
                    }

                    BackendFriendResponse response = await _repository
                        .AddFriendAsync(actor, invite.InviterCharacterId, cancellationToken)
                        .ConfigureAwait(false);
                    if (response == null || !response.success)
                        return SocialEconomyResult.Fail(response?.error ?? "Friend update failed.");

                    lock (_gate)
                    {
                        if (_friendInvites.TryGetValue(actorId, out FriendInvite current) &&
                            ReferenceEquals(current, invite))
                            _friendInvites.Remove(actorId);
                        _friends[actorId] = response.friends ?? Array.Empty<BackendFriendEntryDto>();
                    }
                    await RefreshFriendsAsync(invite.InviterCharacterId, cancellationToken).ConfigureAwait(false);
                    Publish(SocialEconomyStateKind.Friends, actorId, invite.InviterCharacterId);
                    return SocialEconomyResult.Ok(invite.InviterCharacterId);
                }
                case 2: // Decline
                {
                    FriendInvite invite;
                    lock (_gate)
                    {
                        if (!TryTakeFriendInvite(actorId, targetCharacterId, out invite))
                            return SocialEconomyResult.Fail("Friend invite is no longer available.");
                    }
                    Publish(SocialEconomyStateKind.Friends, actorId, invite.InviterCharacterId);
                    return SocialEconomyResult.Ok(invite.InviterCharacterId);
                }
                case 3: // Remove
                {
                    if (targetCharacterId <= 0)
                        return SocialEconomyResult.Fail("Friend target is invalid.");
                    BackendFriendResponse response = await _repository
                        .RemoveFriendAsync(actor, targetCharacterId, cancellationToken)
                        .ConfigureAwait(false);
                    if (response == null || !response.success)
                        return SocialEconomyResult.Fail(response?.error ?? "Friend removal failed.");
                    lock (_gate)
                        _friends[actorId] = response.friends ?? Array.Empty<BackendFriendEntryDto>();
                    await RefreshFriendsAsync(targetCharacterId, cancellationToken).ConfigureAwait(false);
                    Publish(SocialEconomyStateKind.Friends, actorId, targetCharacterId);
                    return SocialEconomyResult.Ok(targetCharacterId);
                }
                default:
                    return SocialEconomyResult.Fail("Friend action is invalid.");
            }
        }

        public async Task<FriendView> LoadFriendsAsync(PlayerRuntime actor, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long characterId, out _))
                return new FriendView();
            await RefreshFriendsAsync(characterId, cancellationToken).ConfigureAwait(false);
            return GetFriendView(characterId);
        }

        public FriendView GetFriendView(long characterId)
        {
            lock (_gate)
            {
                PruneFriendInvite(characterId);
                _friends.TryGetValue(characterId, out BackendFriendEntryDto[] friends);
                _friendInvites.TryGetValue(characterId, out FriendInvite invite);
                return new FriendView
                {
                    Friends = friends ?? Array.Empty<BackendFriendEntryDto>(),
                    PendingInviterCharacterId = invite?.InviterCharacterId ?? 0,
                    PendingInviterName = invite?.InviterName ?? string.Empty,
                };
            }
        }

        public SocialEconomyResult InviteTrade(PlayerRuntime inviter, PlayerRuntime target)
        {
            if (!TryIdentity(inviter, out long leftId, out string leftName) ||
                !TryIdentity(target, out long rightId, out string rightName))
                return SocialEconomyResult.Fail("Trade target is unavailable.");
            if (leftId == rightId)
                return SocialEconomyResult.Fail("You cannot trade with yourself.");

            lock (_gate)
            {
                if (_tradeByCharacter.ContainsKey(leftId))
                    return SocialEconomyResult.Fail("You already have an active trade.");
                if (_tradeByCharacter.ContainsKey(rightId))
                    return SocialEconomyResult.Fail("That player is already trading.");

                var session = new TradeSession
                {
                    Id = AllocateTradeId(),
                    LeftCharacterId = leftId,
                    LeftName = leftName,
                    RightCharacterId = rightId,
                    RightName = rightName,
                    Phase = TradePhasePending,
                };
                _tradeByCharacter[leftId] = session;
                _tradeByCharacter[rightId] = session;
            }
            Publish(SocialEconomyStateKind.Trade, leftId, rightId);
            return SocialEconomyResult.Ok(rightId);
        }

        public async Task<SocialEconomyResult> ApplyTradeActionAsync(
            PlayerRuntime actor,
            byte action,
            long partnerCharacterId,
            int inventorySlot,
            int quantity,
            CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long actorId, out _))
                return SocialEconomyResult.Fail("Character is unavailable.");

            TradeSession session;
            long otherId;
            bool commit = false;
            lock (_gate)
            {
                if (!_tradeByCharacter.TryGetValue(actorId, out session))
                    return SocialEconomyResult.Fail("There is no active trade.");
                otherId = Other(session, actorId);
                if (partnerCharacterId > 0 && partnerCharacterId != otherId)
                    return SocialEconomyResult.Fail("Trade partner does not match the active session.");

                switch (action)
                {
                    case 1: // Accept pending invitation. Only invite target may accept.
                        if (session.Phase != TradePhasePending || actorId != session.RightCharacterId)
                            return SocialEconomyResult.Fail("Trade invitation cannot be accepted.", otherId);
                        if (_resolvePlayer(session.LeftCharacterId) == null || _resolvePlayer(session.RightCharacterId) == null)
                        {
                            RemoveTrade(session);
                            Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                            return SocialEconomyResult.Fail("Trade partner is unavailable.", otherId);
                        }
                        session.Phase = TradePhaseActive;
                        ResetConfirmations(session);
                        Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        return SocialEconomyResult.Ok(otherId);

                    case 2: // Decline
                        if (session.Phase != TradePhasePending || actorId != session.RightCharacterId)
                            return SocialEconomyResult.Fail("Trade invitation cannot be declined.", otherId);
                        RemoveTrade(session);
                        Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        return SocialEconomyResult.Ok(otherId, completed: true);

                    case 3: // Offer
                        if (session.Phase != TradePhaseActive)
                            return SocialEconomyResult.Fail("Trade is not active.", otherId);
                        if (OwnLocked(session, actorId))
                            return SocialEconomyResult.Fail("Unlock your offer before changing it.", otherId);
                        if (quantity <= 0)
                            return SocialEconomyResult.Fail("Trade quantity must be positive.", otherId);
                        PlayerItemView item = FindInventoryItem(_items.GetSnapshot(actor), inventorySlot);
                        if (item == null || item.quantity < quantity)
                            return SocialEconomyResult.Fail("Offered inventory item is unavailable.", otherId);
                        List<TradeOffer> own = OwnOffers(session, actorId);
                        TradeOffer existing = own.Find(x => x.SourceSlot == inventorySlot);
                        if (existing == null && own.Count >= MaxTradeOffers)
                            return SocialEconomyResult.Fail("Trade offer is full.", otherId);
                        if (existing == null)
                        {
                            existing = new TradeOffer();
                            own.Add(existing);
                        }
                        existing.SourceSlot = inventorySlot;
                        existing.ItemInstanceId = item.itemInstanceId;
                        existing.Quantity = quantity;
                        ResetAllLocksAndConfirmations(session);
                        Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        return SocialEconomyResult.Ok(otherId);

                    case 4: // Remove offer by source slot
                        if (session.Phase != TradePhaseActive)
                            return SocialEconomyResult.Fail("Trade is not active.", otherId);
                        if (OwnLocked(session, actorId))
                            return SocialEconomyResult.Fail("Unlock your offer before changing it.", otherId);
                        if (OwnOffers(session, actorId).RemoveAll(x => x.SourceSlot == inventorySlot) > 0)
                        {
                            ResetAllLocksAndConfirmations(session);
                            Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        }
                        return SocialEconomyResult.Ok(otherId);

                    case 5: // Lock
                        if (session.Phase != TradePhaseActive)
                            return SocialEconomyResult.Fail("Trade is not active.", otherId);
                        SetOwnLocked(session, actorId, true);
                        SetOwnConfirmed(session, actorId, false);
                        Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        return SocialEconomyResult.Ok(otherId);

                    case 6: // Unlock
                        if (session.Phase != TradePhaseActive)
                            return SocialEconomyResult.Fail("Trade is not active.", otherId);
                        SetOwnLocked(session, actorId, false);
                        ResetConfirmations(session);
                        Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        return SocialEconomyResult.Ok(otherId);

                    case 7: // Confirm
                        if (session.Phase != TradePhaseActive || !session.LeftLocked || !session.RightLocked)
                            return SocialEconomyResult.Fail("Both offers must be locked before confirming.", otherId);
                        SetOwnConfirmed(session, actorId, true);
                        if (!session.LeftConfirmed || !session.RightConfirmed)
                        {
                            Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                            return SocialEconomyResult.Ok(otherId);
                        }
                        session.Phase = TradePhaseCommitting;
                        commit = true;
                        break;

                    case 8: // Cancel
                        if (session.Phase == TradePhaseCommitting)
                            return SocialEconomyResult.Fail("Trade is already committing.", otherId);
                        RemoveTrade(session);
                        Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
                        return SocialEconomyResult.Ok(otherId, completed: true);

                    default:
                        return SocialEconomyResult.Fail("Trade action is invalid.", otherId);
                }
            }

            if (!commit)
                return SocialEconomyResult.Fail("Trade action could not be completed.", otherId);

            Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
            return await CommitTradeAsync(session, cancellationToken).ConfigureAwait(false);
        }

        public TradeView GetTradeView(long viewerCharacterId, string emptyDetail = "")
        {
            lock (_gate)
            {
                if (!_tradeByCharacter.TryGetValue(viewerCharacterId, out TradeSession session))
                    return new TradeView { Detail = emptyDetail ?? string.Empty };

                bool left = viewerCharacterId == session.LeftCharacterId;
                long partnerId = left ? session.RightCharacterId : session.LeftCharacterId;
                string partnerName = left ? session.RightName : session.LeftName;
                PlayerRuntime ownRuntime = _resolvePlayer(viewerCharacterId);
                PlayerRuntime partnerRuntime = _resolvePlayer(partnerId);
                return new TradeView
                {
                    SessionId = session.Id,
                    PartnerCharacterId = partnerId,
                    PartnerName = partnerName,
                    Phase = session.Phase,
                    OwnLocked = left ? session.LeftLocked : session.RightLocked,
                    PartnerLocked = left ? session.RightLocked : session.LeftLocked,
                    OwnConfirmed = left ? session.LeftConfirmed : session.RightConfirmed,
                    PartnerConfirmed = left ? session.RightConfirmed : session.LeftConfirmed,
                    OwnOffers = ResolveOffers(ownRuntime, left ? session.LeftOffers : session.RightOffers),
                    PartnerOffers = ResolveOffers(partnerRuntime, left ? session.RightOffers : session.LeftOffers),
                };
            }
        }

        public async Task<BackendStorageSnapshotDto> LoadStorageAsync(PlayerRuntime actor, CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long characterId, out _))
                return null;
            BackendStorageLoadResponse response = await _repository.LoadStorageAsync(actor, cancellationToken).ConfigureAwait(false);
            if (response == null || !response.success || response.storage == null)
                return null;
            lock (_gate)
                _storage[characterId] = response.storage;
            return response.storage;
        }

        public BackendStorageSnapshotDto GetStorage(long characterId)
        {
            lock (_gate)
                return _storage.TryGetValue(characterId, out BackendStorageSnapshotDto value) ? value : null;
        }

        public async Task<SocialEconomyResult> TransferStorageAsync(
            PlayerRuntime actor,
            bool deposit,
            int sourceSlot,
            int quantity,
            CancellationToken cancellationToken)
        {
            if (!TryIdentity(actor, out long characterId, out _))
                return SocialEconomyResult.Fail("Character is unavailable.");
            if (quantity <= 0)
                return SocialEconomyResult.Fail("Storage quantity must be positive.");

            BackendStorageSnapshotDto storage;
            lock (_gate)
                _storage.TryGetValue(characterId, out storage);
            if (storage == null)
            {
                storage = await LoadStorageAsync(actor, cancellationToken).ConfigureAwait(false);
                if (storage == null)
                    return SocialEconomyResult.Fail("Storage is unavailable.");
            }

            SocialEconomyResult result = await _items.RunExternalSerializedAsync(
                actor,
                async token =>
                {
                    PlayerItemSystemsRuntimeSnapshot current = actor.CapturePlayerItemSystems();
                    if (current == null)
                        return SocialEconomyResult.Fail("Player item state is unavailable.");

                    long instanceId;
                    if (deposit)
                    {
                        PlayerItemView item = FindInventoryItem(_items.GetSnapshot(actor), sourceSlot);
                        if (item == null || item.quantity < quantity)
                            return SocialEconomyResult.Fail("Inventory item is unavailable.");
                        instanceId = item.itemInstanceId;
                    }
                    else
                    {
                        BackendPersistedItemDto item = (storage.items ?? Array.Empty<BackendPersistedItemDto>())
                            .FirstOrDefault(x => x != null && x.inventorySlot == sourceSlot);
                        if (item == null || item.quantity < quantity)
                            return SocialEconomyResult.Fail("Storage item is unavailable.");
                        instanceId = item.itemInstanceId;
                    }

                    BackendStorageTransferResponse response = await _repository.TransferStorageAsync(
                        actor,
                        current.Inventory.Revision,
                        current.Equipment.Revision,
                        storage.revision,
                        deposit,
                        instanceId,
                        quantity,
                        token).ConfigureAwait(false);
                    if (response == null || !response.success || response.playerState == null || response.storage == null)
                    {
                        if (response?.stale == true)
                        {
                            BackendStorageLoadResponse refreshed = await _repository.LoadStorageAsync(actor, token).ConfigureAwait(false);
                            if (refreshed?.success == true && refreshed.storage != null)
                            {
                                lock (_gate)
                                    _storage[characterId] = refreshed.storage;
                            }
                        }
                        return SocialEconomyResult.Fail(response?.error ?? "Storage transfer failed.");
                    }

                    PlayerItemOperationResult installed = _items.InstallExternalPersistedState(
                        actor,
                        current,
                        BackendSocialEconomyRepository.ToPlayerState(response.playerState),
                        "storage transfer");
                    if (!installed.Success)
                        return SocialEconomyResult.Fail(installed.Error);

                    lock (_gate)
                        _storage[characterId] = response.storage;
                    return SocialEconomyResult.Ok();
                },
                cancellationToken).ConfigureAwait(false);

            Publish(SocialEconomyStateKind.Storage, characterId);
            return result;
        }

        public void OnPlayerItemsChanged(PlayerRuntime runtime)
        {
            if (runtime == null) return;
            long left = 0;
            long right = 0;
            lock (_gate)
            {
                if (!_tradeByCharacter.TryGetValue(runtime.CharacterId.Value, out TradeSession session) ||
                    session.Phase == TradePhaseCommitting)
                    return;
                // Any inventory mutation can invalidate an offered slot/quantity. Keep the trade
                // session but clear the mutated side's offer and both parties' readiness.
                OwnOffers(session, runtime.CharacterId.Value).Clear();
                ResetAllLocksAndConfirmations(session);
                left = session.LeftCharacterId;
                right = session.RightCharacterId;
            }
            Publish(SocialEconomyStateKind.Trade, left, right);
        }

        public void NotifyPresenceChanged(long characterId)
        {
            if (characterId <= 0) return;
            var affected = new List<long>();
            lock (_gate)
            {
                foreach (KeyValuePair<long, BackendFriendEntryDto[]> pair in _friends)
                {
                    BackendFriendEntryDto[] values = pair.Value ?? Array.Empty<BackendFriendEntryDto>();
                    for (int i = 0; i < values.Length; ++i)
                    {
                        if (values[i]?.characterId != characterId) continue;
                        affected.Add(pair.Key);
                        break;
                    }
                }
            }
            if (affected.Count > 0)
                Publish(SocialEconomyStateKind.Friends, affected.ToArray());
        }

        public void CancelTradeForCharacter(long characterId)
        {
            long left = 0;
            long right = 0;
            lock (_gate)
            {
                if (_tradeByCharacter.TryGetValue(characterId, out TradeSession session))
                {
                    left = session.LeftCharacterId;
                    right = session.RightCharacterId;
                    RemoveTrade(session);
                }
            }
            if (left > 0 || right > 0)
                Publish(SocialEconomyStateKind.Trade, left, right);
        }

        public void CancelForCharacter(long characterId, string detail = "Trade partner disconnected.")
        {
            long left = 0;
            long right = 0;
            var inviteTargets = new List<long>();
            lock (_gate)
            {
                if (_tradeByCharacter.TryGetValue(characterId, out TradeSession session))
                {
                    left = session.LeftCharacterId;
                    right = session.RightCharacterId;
                    RemoveTrade(session);
                }
                _friendInvites.Remove(characterId);
                foreach (KeyValuePair<long, FriendInvite> pair in _friendInvites)
                    if (pair.Value?.InviterCharacterId == characterId) inviteTargets.Add(pair.Key);
                for (int i = 0; i < inviteTargets.Count; ++i) _friendInvites.Remove(inviteTargets[i]);
                _friends.Remove(characterId);
                _storage.Remove(characterId);
            }
            if (left > 0 || right > 0)
                Publish(SocialEconomyStateKind.Trade, left, right);
            if (inviteTargets.Count > 0)
                Publish(SocialEconomyStateKind.Friends, inviteTargets.ToArray());
        }

        private async Task<SocialEconomyResult> CommitTradeAsync(TradeSession session, CancellationToken cancellationToken)
        {
            PlayerRuntime left = _resolvePlayer(session.LeftCharacterId);
            PlayerRuntime right = _resolvePlayer(session.RightCharacterId);
            if (left == null || right == null)
            {
                CancelTrade(session, "Trade partner is unavailable.");
                return SocialEconomyResult.Fail("Trade partner is unavailable.");
            }

            SocialEconomyResult result = await _items.RunExternalPairSerializedAsync(
                left,
                right,
                async token =>
                {
                    PlayerItemSystemsRuntimeSnapshot leftState = left.CapturePlayerItemSystems();
                    PlayerItemSystemsRuntimeSnapshot rightState = right.CapturePlayerItemSystems();
                    if (leftState == null || rightState == null)
                        return SocialEconomyResult.Fail("Trade item state is unavailable.");

                    if (!ValidateOffers(_items.GetSnapshot(left), session.LeftOffers) ||
                        !ValidateOffers(_items.GetSnapshot(right), session.RightOffers))
                        return SocialEconomyResult.Fail("A trade offer changed before commit.");

                    BackendTradeCommitResponse response = await _repository.CommitTradeAsync(
                        left,
                        leftState,
                        ToBackendOffers(session.LeftOffers),
                        right,
                        rightState,
                        ToBackendOffers(session.RightOffers),
                        token).ConfigureAwait(false);
                    if (response == null || !response.success || response.leftState == null || response.rightState == null)
                        return SocialEconomyResult.Fail(response?.error ?? "Trade commit failed.");

                    PlayerItemOperationResult leftInstalled = _items.InstallExternalPersistedState(
                        left, leftState, BackendSocialEconomyRepository.ToPlayerState(response.leftState), "player trade");
                    PlayerItemOperationResult rightInstalled = _items.InstallExternalPersistedState(
                        right, rightState, BackendSocialEconomyRepository.ToPlayerState(response.rightState), "player trade");
                    if (!leftInstalled.Success || !rightInstalled.Success)
                        return SocialEconomyResult.Fail("Trade committed but runtime item reconciliation failed.");
                    return SocialEconomyResult.Ok(completed: true);
                },
                cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (result.Success)
                {
                    RemoveTrade(session);
                }
                else if (_tradeByCharacter.TryGetValue(session.LeftCharacterId, out TradeSession current) &&
                         ReferenceEquals(current, session))
                {
                    session.Phase = TradePhaseActive;
                    ResetAllLocksAndConfirmations(session);
                }
            }
            Publish(SocialEconomyStateKind.Trade, session.LeftCharacterId, session.RightCharacterId);
            return result.Success
                ? new SocialEconomyResult(true, string.Empty, 0, true)
                : result;
        }

        private async Task RefreshFriendsAsync(long characterId, CancellationToken cancellationToken)
        {
            if (characterId <= 0) return;
            BackendFriendResponse response = await _repository.LoadFriendsAsync(characterId, cancellationToken).ConfigureAwait(false);
            if (response?.success == true)
            {
                lock (_gate)
                    _friends[characterId] = response.friends ?? Array.Empty<BackendFriendEntryDto>();
            }
        }

        private bool TryTakeFriendInvite(long targetId, long expectedInviterId, out FriendInvite invite)
        {
            PruneFriendInvite(targetId);
            if (!_friendInvites.TryGetValue(targetId, out invite)) return false;
            if (expectedInviterId > 0 && invite.InviterCharacterId != expectedInviterId) return false;
            _friendInvites.Remove(targetId);
            return true;
        }

        private void PruneFriendInvite(long targetId)
        {
            if (_friendInvites.TryGetValue(targetId, out FriendInvite invite) && invite.ExpiresUtc <= DateTime.UtcNow)
                _friendInvites.Remove(targetId);
        }

        private bool CachedFriendsContains(long characterId, long otherCharacterId)
        {
            if (!_friends.TryGetValue(characterId, out BackendFriendEntryDto[] values)) return false;
            for (int i = 0; i < values.Length; ++i)
                if (values[i]?.characterId == otherCharacterId) return true;
            return false;
        }

        private static bool TryIdentity(PlayerRuntime runtime, out long characterId, out string name)
        {
            characterId = runtime?.CharacterId.Value ?? 0;
            name = runtime?.Character?.Name ?? string.Empty;
            return characterId > 0;
        }

        private ulong AllocateTradeId()
        {
            ulong value = _nextTradeId++;
            if (value == 0) value = _nextTradeId++;
            return value;
        }

        private static long Other(TradeSession session, long actorId) =>
            actorId == session.LeftCharacterId ? session.RightCharacterId : session.LeftCharacterId;
        private static List<TradeOffer> OwnOffers(TradeSession session, long actorId) =>
            actorId == session.LeftCharacterId ? session.LeftOffers : session.RightOffers;
        private static bool OwnLocked(TradeSession session, long actorId) =>
            actorId == session.LeftCharacterId ? session.LeftLocked : session.RightLocked;
        private static void SetOwnLocked(TradeSession session, long actorId, bool value)
        {
            if (actorId == session.LeftCharacterId) session.LeftLocked = value; else session.RightLocked = value;
        }
        private static void SetOwnConfirmed(TradeSession session, long actorId, bool value)
        {
            if (actorId == session.LeftCharacterId) session.LeftConfirmed = value; else session.RightConfirmed = value;
        }
        private static void ResetConfirmations(TradeSession session)
        {
            session.LeftConfirmed = false; session.RightConfirmed = false;
        }
        private static void ResetAllLocksAndConfirmations(TradeSession session)
        {
            session.LeftLocked = false; session.RightLocked = false; ResetConfirmations(session);
        }

        private void CancelTrade(TradeSession session, string detail)
        {
            long left = session.LeftCharacterId;
            long right = session.RightCharacterId;
            RemoveTrade(session);
            Publish(SocialEconomyStateKind.Trade, left, right);
        }

        private void RemoveTrade(TradeSession session)
        {
            if (_tradeByCharacter.TryGetValue(session.LeftCharacterId, out TradeSession left) && ReferenceEquals(left, session))
                _tradeByCharacter.Remove(session.LeftCharacterId);
            if (_tradeByCharacter.TryGetValue(session.RightCharacterId, out TradeSession right) && ReferenceEquals(right, session))
                _tradeByCharacter.Remove(session.RightCharacterId);
        }

        private static PlayerItemView FindInventoryItem(PlayerItemsSnapshot snapshot, int slot)
        {
            PlayerItemView[] values = snapshot?.inventory ?? Array.Empty<PlayerItemView>();
            for (int i = 0; i < values.Length; ++i)
                if (values[i] != null && values[i].inventorySlot == slot) return values[i];
            return null;
        }

        private TradeOfferView[] ResolveOffers(PlayerRuntime runtime, List<TradeOffer> offers)
        {
            if (runtime == null || offers == null || offers.Count == 0) return Array.Empty<TradeOfferView>();
            PlayerItemsSnapshot snapshot = _items.GetSnapshot(runtime);
            PlayerItemView[] inventory = snapshot?.inventory ?? Array.Empty<PlayerItemView>();
            var result = new List<TradeOfferView>(offers.Count);
            for (int i = 0; i < offers.Count; ++i)
            {
                TradeOffer offer = offers[i];
                PlayerItemView item = null;
                for (int j = 0; j < inventory.Length; ++j)
                    if (inventory[j] != null && inventory[j].itemInstanceId == offer.ItemInstanceId) { item = inventory[j]; break; }
                if (item == null) continue;
                result.Add(new TradeOfferView { SourceSlot = offer.SourceSlot, ItemInstanceId = offer.ItemInstanceId, Quantity = offer.Quantity, Item = item });
            }
            return result.ToArray();
        }

        private static bool ValidateOffers(PlayerItemsSnapshot snapshot, List<TradeOffer> offers)
        {
            PlayerItemView[] inventory = snapshot?.inventory ?? Array.Empty<PlayerItemView>();
            for (int i = 0; i < offers.Count; ++i)
            {
                TradeOffer offer = offers[i];
                bool found = false;
                for (int j = 0; j < inventory.Length; ++j)
                {
                    PlayerItemView item = inventory[j];
                    if (item != null && item.itemInstanceId == offer.ItemInstanceId && item.quantity >= offer.Quantity)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }
            return true;
        }

        private static BackendTradeOfferDto[] ToBackendOffers(List<TradeOffer> offers)
        {
            var result = new BackendTradeOfferDto[offers.Count];
            for (int i = 0; i < offers.Count; ++i)
                result[i] = new BackendTradeOfferDto { itemInstanceId = offers[i].ItemInstanceId, quantity = offers[i].Quantity };
            return result;
        }

        private void Publish(SocialEconomyStateKind kind, params long[] characterIds)
        {
            if (StateChanged == null || kind == SocialEconomyStateKind.None || characterIds == null) return;
            for (int i = 0; i < characterIds.Length; ++i)
                if (characterIds[i] > 0) StateChanged(characterIds[i], kind);
        }
    }

    internal sealed class FriendInviteInteractionHandler : IInteractionActionHandler, IInteractionActionDescriptorProvider
    {
        private readonly SocialEconomyRuntime _runtime;
        public FriendInviteInteractionHandler(SocialEconomyRuntime runtime) => _runtime = runtime;
        public InteractionActionId ActionId => InteractionActionId.AddFriend;
        public InteractionResult Execute(in InteractionExecutionContext context)
        {
            SocialEconomyResult result = _runtime.InviteFriend(context.Source, context.TargetPlayer);
            return new InteractionResult(context.Sequence, ActionId,
                result.Success ? InteractionResultCode.Success : InteractionResultCode.Rejected,
                context.Target, result.Error);
        }
        public InteractionActionEntry DescribeAction() => new InteractionActionEntry(
            InteractionCategoryId.Interact, ActionId, "Add Friend", InteractionAvailability.Available,
            consentMode: InteractionConsentMode.TargetAcceptance,
            feature: InteractionFeature.Friendship, sortOrder: 35);
    }

    internal sealed class TradeInviteInteractionHandler : IInteractionActionHandler
    {
        private readonly SocialEconomyRuntime _runtime;
        public TradeInviteInteractionHandler(SocialEconomyRuntime runtime) => _runtime = runtime;
        public InteractionActionId ActionId => InteractionActionId.TradeRequest;
        public InteractionResult Execute(in InteractionExecutionContext context)
        {
            SocialEconomyResult result = _runtime.InviteTrade(context.Source, context.TargetPlayer);
            return new InteractionResult(context.Sequence, ActionId,
                result.Success ? InteractionResultCode.Success : InteractionResultCode.Rejected,
                context.Target, result.Error);
        }
    }
}
