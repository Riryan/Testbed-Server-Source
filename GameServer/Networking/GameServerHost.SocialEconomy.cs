using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Backend;
using Game.GameServer.Economy;
using Game.GameServer.Runtime;
using Game.Server.Application.Interactions;
using Game.Server.Domain.Players;
using Game.Shared.Backend;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.World;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private sealed class StorageAccess
    {
        public string MapId = string.Empty;
        public string InstanceId = string.Empty;
        public long StableId;
    }

    private readonly Dictionary<long, StorageAccess> _storageAccessByCharacter = new();
    private SocialEconomyRuntime _socialEconomy;

    private void RegisterSocialEconomyRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        _socialEconomy ??= new SocialEconomyRuntime(
            new BackendSocialEconomyRepository(_runtime.Backend, _runtime.Leases),
            _runtime.PlayerItems,
            characterId => FindReadySessionByCharacterId(characterId)?.Entity?.Runtime);
        _socialEconomy.StateChanged -= OnSocialEconomyStateChanged;
        _socialEconomy.StateChanged += OnSocialEconomyStateChanged;

        if (!_runtime.Interactions.Register(new FriendInviteInteractionHandler(_socialEconomy)))
            throw new InvalidOperationException("Friend interaction action is already registered.");
        if (!_runtime.Interactions.Register(new TradeInviteInteractionHandler(_socialEconomy)))
            throw new InvalidOperationException("Trade interaction action is already registered.");

        RegisterRequest(handlers, FriendRequestTypes.Snapshot, HandleFriendsSnapshot);
        RegisterRequest(handlers, FriendRequestTypes.Action, HandleFriendAction);
        RegisterRequest(handlers, EconomyRequestTypes.TradeAction, HandleTradeAction);
        RegisterRequest(handlers, EconomyRequestTypes.TradeSnapshot, HandleTradeSnapshot);
        RegisterRequest(handlers, EconomyRequestTypes.StorageSnapshot, HandleStorageSnapshot);
        RegisterRequest(handlers, EconomyRequestTypes.StorageTransfer, HandleStorageTransfer);
    }

    private void HandleFriendsSnapshot(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new EmptySocialRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, new FriendsStateMessage());
            return;
        }

        RunFriendsSnapshotAsync(session, requestId, runtime).Forget();
    }

    private async Task RunFriendsSnapshotAsync(ClientSession session, uint requestId, PlayerRuntime runtime)
    {
        FriendView view;
        try
        {
            view = await _socialEconomy.LoadFriendsAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Friend snapshot failed for peer {session?.Peer?.Id}: {ex.Message}");
            view = _socialEconomy.GetFriendView(runtime.CharacterId.Value);
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, BuildFriendsState(view));
        });
    }

    private void HandleFriendAction(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new FriendActionRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(1, "character is not in world"));
            return;
        }

        FriendActionKind action = (FriendActionKind)request.action;
        if (action != FriendActionKind.Decline && !IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(2, BackendPersistenceUnavailableMessage));
            return;
        }

        RunFriendActionAsync(session, requestId, runtime, request).Forget();
    }

    private async Task RunFriendActionAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        FriendActionRequestMessage request)
    {
        SocialEconomyResult result;
        try
        {
            result = await _socialEconomy.ApplyFriendActionAsync(
                runtime, request.action, request.targetCharacterId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Friend action failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = SocialEconomyResult.Fail("friend action failed");
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, result.Success
                ? SocialEconomyMutationResponseMessage.Ok()
                : SocialEconomyMutationResponseMessage.Failed(3, result.Error));
            // A rejected accept/decline may have pruned an expired invite. Push the
            // current authoritative view so the client never keeps a stale prompt.
            if (!result.Success)
                SendClientMessage(session, SocialEconomyMessageTypes.FriendsState,
                    BuildFriendsState(_socialEconomy.GetFriendView(runtime.CharacterId.Value)),
                    DeliveryMethod.ReliableOrdered);
        });
    }

    private void HandleTradeSnapshot(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new EmptySocialRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, new TradeStateMessage { detail = "character is not in world" });
            return;
        }
        SendResponse(session, requestId, BuildTradeState(_socialEconomy.GetTradeView(runtime.CharacterId.Value)));
    }

    private void HandleTradeAction(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new TradeActionRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(1, "character is not in world"));
            return;
        }
        TradeActionKind tradeAction = (TradeActionKind)request.action;
        if (tradeAction != TradeActionKind.Cancel && tradeAction != TradeActionKind.Decline &&
            !TryValidateActiveTradePair(runtime, out string tradeReason))
        {
            _socialEconomy.CancelTradeForCharacter(runtime.CharacterId.Value);
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(3, tradeReason));
            return;
        }
        if (tradeAction == TradeActionKind.Confirm && !IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(2, BackendPersistenceUnavailableMessage));
            return;
        }

        RunTradeActionAsync(session, requestId, runtime, request).Forget();
    }

    private async Task RunTradeActionAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        TradeActionRequestMessage request)
    {
        SocialEconomyResult result;
        try
        {
            result = await _socialEconomy.ApplyTradeActionAsync(
                runtime,
                request.action,
                request.partnerCharacterId,
                request.inventorySlot,
                request.quantity,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Trade action failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = SocialEconomyResult.Fail("trade action failed");
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, result.Success
                ? SocialEconomyMutationResponseMessage.Ok()
                : SocialEconomyMutationResponseMessage.Failed(3, result.Error));
        });
    }

    private void HandleStorageSnapshot(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new EmptySocialRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, new StorageStateMessage { detail = "character is not in world" });
            return;
        }
        if (!TryValidateStorageAccess(runtime, out string reason))
        {
            SendResponse(session, requestId, new StorageStateMessage { detail = reason });
            return;
        }

        RunStorageSnapshotAsync(session, requestId, runtime, pushOnly: false).Forget();
    }

    private void HandleStorageTransfer(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new StorageTransferRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(1, "character is not in world"));
            return;
        }
        if (!TryValidateStorageAccess(runtime, out string reason))
        {
            // Reuse StorageState as the closure/invalidation signal. No extra bank-session
            // message is needed just to clear a cache the server already knows is stale.
            SendClientMessage(session, SocialEconomyMessageTypes.StorageState,
                new StorageStateMessage { detail = reason }, DeliveryMethod.ReliableOrdered);
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(3, reason));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(2, BackendPersistenceUnavailableMessage));
            return;
        }

        StorageTransferKind action = (StorageTransferKind)request.action;
        if (action != StorageTransferKind.Deposit && action != StorageTransferKind.Withdraw)
        {
            SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Failed(3, "storage action is invalid"));
            return;
        }

        RunStorageTransferAsync(session, requestId, runtime, action == StorageTransferKind.Deposit, request).Forget();
    }

    private async Task RunStorageSnapshotAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        bool pushOnly)
    {
        BackendStorageSnapshotDto storage = null;
        string error = string.Empty;
        try
        {
            storage = await _socialEconomy.LoadStorageAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            if (storage == null) error = "storage is unavailable";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Storage snapshot failed for peer {session?.Peer?.Id}: {ex.Message}");
            error = "storage is unavailable";
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            StorageStateMessage state = BuildStorageState(storage, error);
            if (pushOnly)
                SendClientMessage(session, SocialEconomyMessageTypes.StorageState, state, DeliveryMethod.ReliableOrdered);
            else
                SendResponse(session, requestId, state);
        });
    }

    private async Task RunStorageTransferAsync(
        ClientSession session,
        uint requestId,
        PlayerRuntime runtime,
        bool deposit,
        StorageTransferRequestMessage request)
    {
        SocialEconomyResult result;
        try
        {
            result = await _socialEconomy.TransferStorageAsync(
                runtime, deposit, request.sourceSlot, request.quantity, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Storage transfer failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = SocialEconomyResult.Fail("storage transfer failed");
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            SendResponse(session, requestId, result.Success
                ? SocialEconomyMutationResponseMessage.Ok()
                : SocialEconomyMutationResponseMessage.Failed(3, result.Error));
        });
    }

    private void OnSocialEconomyStateChanged(long characterId, SocialEconomyStateKind kind)
    {
        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession session = FindReadySessionByCharacterId(characterId);
            if (session == null || session.Entity?.Runtime == null) return;

            if ((kind & SocialEconomyStateKind.Friends) != 0)
            {
                FriendsStateMessage state = BuildFriendsState(_socialEconomy.GetFriendView(characterId));
                SendClientMessage(session, SocialEconomyMessageTypes.FriendsState, state, DeliveryMethod.ReliableOrdered);
            }
            if ((kind & SocialEconomyStateKind.Trade) != 0)
            {
                TradeStateMessage state = BuildTradeState(_socialEconomy.GetTradeView(characterId));
                SendClientMessage(session, SocialEconomyMessageTypes.TradeState, state, DeliveryMethod.ReliableOrdered);
            }
            if ((kind & SocialEconomyStateKind.Storage) != 0)
            {
                BackendStorageSnapshotDto storage = _socialEconomy.GetStorage(characterId);
                if (storage != null)
                    SendClientMessage(session, SocialEconomyMessageTypes.StorageState, BuildStorageState(storage), DeliveryMethod.ReliableOrdered);
            }
        });
    }

    private FriendsStateMessage BuildFriendsState(FriendView view)
    {
        BackendFriendEntryDto[] source = view?.Friends ?? Array.Empty<BackendFriendEntryDto>();
        var friends = new FriendEntryWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            BackendFriendEntryDto entry = source[i];
            long characterId = entry?.characterId ?? 0;
            friends[i] = new FriendEntryWire
            {
                characterId = characterId,
                name = entry?.name ?? string.Empty,
                online = characterId > 0 && FindReadySessionByCharacterId(characterId) != null,
            };
        }
        return new FriendsStateMessage
        {
            friends = friends,
            pendingInviterCharacterId = view?.PendingInviterCharacterId ?? 0,
            pendingInviterName = view?.PendingInviterName ?? string.Empty,
        };
    }

    private static TradeStateMessage BuildTradeState(TradeView view)
    {
        view ??= new TradeView();
        return new TradeStateMessage
        {
            sessionId = view.SessionId,
            partnerCharacterId = view.PartnerCharacterId,
            partnerName = view.PartnerName,
            phase = view.Phase,
            ownLocked = view.OwnLocked,
            partnerLocked = view.PartnerLocked,
            ownConfirmed = view.OwnConfirmed,
            partnerConfirmed = view.PartnerConfirmed,
            ownOffers = BuildTradeOffers(view.OwnOffers),
            partnerOffers = BuildTradeOffers(view.PartnerOffers),
            detail = view.Detail,
        };
    }

    private static TradeOfferWire[] BuildTradeOffers(TradeOfferView[] offers)
    {
        TradeOfferView[] source = offers ?? Array.Empty<TradeOfferView>();
        var result = new TradeOfferWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            TradeOfferView offer = source[i];
            result[i] = new TradeOfferWire
            {
                sourceSlot = offer?.SourceSlot ?? -1,
                item = offer?.Item == null ? default : ToWire(offer.Item),
                quantity = offer?.Quantity ?? 0,
            };
        }
        return result;
    }

    private StorageStateMessage BuildStorageState(BackendStorageSnapshotDto storage, string detail = "")
    {
        if (storage == null)
            return new StorageStateMessage { detail = detail ?? "storage is unavailable" };

        BackendPersistedItemDto[] source = storage.items ?? Array.Empty<BackendPersistedItemDto>();
        var items = new PlayerItemWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
            items[i] = BuildStorageItemWire(source[i]);
        return new StorageStateMessage
        {
            capacity = storage.capacity,
            revision = storage.revision,
            items = items,
            detail = detail ?? string.Empty,
        };
    }

    private PlayerItemWire BuildStorageItemWire(BackendPersistedItemDto item)
    {
        if (item == null) return default;
        _runtime.Content.TryGetItem(item.definitionId, out ItemDefinition definition);
        return new PlayerItemWire
        {
            inventorySlot = item.inventorySlot,
            equipmentSlotDataId = 0,
            equipmentSlotId = string.Empty,
            itemInstanceId = item.itemInstanceId,
            itemDataId = definition?.dataId ?? 0,
            definitionId = item.definitionId ?? string.Empty,
            displayName = definition?.displayName ?? item.definitionId ?? string.Empty,
            quantity = item.quantity,
            durability = item.durability,
            maxDurability = definition?.maxDurability ?? 0,
            unitWeight = definition?.weight ?? 0f,
            canUse = definition != null && definition.consumeQuantity > 0 &&
                     (definition.useEffects ?? Array.Empty<ItemUseEffectDefinition>()).Length > 0,
            consumeQuantity = definition?.consumeQuantity ?? 0,
            allowedEquipmentSlots = definition?.allowedEquipmentSlots ?? Array.Empty<string>(),
        };
    }

    private bool TryValidateActiveTradePair(PlayerRuntime runtime, out string reason)
    {
        reason = string.Empty;
        if (runtime == null) { reason = "character is unavailable"; return false; }
        TradeView view = _socialEconomy.GetTradeView(runtime.CharacterId.Value);
        if (view.SessionId == 0 || view.PartnerCharacterId <= 0)
        {
            reason = "there is no active trade";
            return false;
        }
        ClientSession partnerSession = FindReadySessionByCharacterId(view.PartnerCharacterId);
        PlayerRuntime partner = partnerSession?.Entity?.Runtime;
        if (partner == null) { reason = "trade partner is unavailable"; return false; }
        if (_runtime.Lifecycle.IsDead(runtime) || _runtime.Lifecycle.IsDead(partner))
        {
            reason = "dead characters cannot continue a trade";
            return false;
        }
        if (!string.Equals(runtime.Location.MapId, partner.Location.MapId, StringComparison.Ordinal) ||
            !string.Equals(runtime.Location.InstanceId, partner.Location.InstanceId, StringComparison.Ordinal))
        {
            reason = "trade partner is in another world";
            return false;
        }
        float dx = runtime.Location.Position.X - partner.Location.Position.X;
        float dz = runtime.Location.Position.Z - partner.Location.Position.Z;
        float range = _runtime.Interactions.PlayerInteractionRange;
        if (dx * dx + dz * dz > range * range)
        {
            reason = "trade partner is out of range";
            return false;
        }
        return true;
    }

    private void AuthorizeStorage(PlayerRuntime runtime, long stableId)
    {
        if (runtime == null || stableId <= 0) return;
        _storageAccessByCharacter[runtime.CharacterId.Value] = new StorageAccess
        {
            MapId = runtime.Location.MapId ?? string.Empty,
            InstanceId = runtime.Location.InstanceId ?? string.Empty,
            StableId = stableId,
        };
    }

    private bool TryValidateStorageAccess(PlayerRuntime runtime, out string reason)
    {
        reason = "storage is not open";
        if (runtime == null || !_storageAccessByCharacter.TryGetValue(runtime.CharacterId.Value, out StorageAccess access))
            return false;
        if (!string.Equals(access.MapId, runtime.Location.MapId, StringComparison.Ordinal) ||
            !string.Equals(access.InstanceId, runtime.Location.InstanceId, StringComparison.Ordinal))
        {
            _storageAccessByCharacter.Remove(runtime.CharacterId.Value);
            reason = "storage access expired after changing world";
            return false;
        }
        if (!_runtime.WorldInteractables.TryGet(access.MapId, access.InstanceId, access.StableId, out WorldInteractableRuntime target))
        {
            _storageAccessByCharacter.Remove(runtime.CharacterId.Value);
            reason = "storage target is unavailable";
            return false;
        }
        ServerContextualInteractionDefinition definition = WorldInteractableService.FindDefinition(
            target.Definition,
            InteractionActionId.OpenStorage);
        if (definition == null || !_runtime.WorldInteractables.Evaluate(runtime, target, definition, out reason))
        {
            _storageAccessByCharacter.Remove(runtime.CharacterId.Value);
            if (string.IsNullOrWhiteSpace(reason)) reason = "storage access is no longer valid";
            return false;
        }
        return true;
    }

    private void OpenStorageFor(ClientSession session, PlayerRuntime runtime, long stableId)
    {
        AuthorizeStorage(runtime, stableId);
        RunStorageSnapshotAsync(session, 0, runtime, pushOnly: true).Forget();
    }

    private void BeginSocialEconomyReady(ClientSession session, PlayerRuntime runtime)
    {
        if (_socialEconomy == null || runtime == null) return;
        _socialEconomy.NotifyPresenceChanged(runtime.CharacterId.Value);
        RunFriendsReadyAsync(session, runtime).Forget();
    }

    private async Task RunFriendsReadyAsync(ClientSession session, PlayerRuntime runtime)
    {
        try
        {
            FriendView view = await _socialEconomy.LoadFriendsAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            _mainThreadCompletions.Enqueue(() =>
            {
                if (!IsCurrent(session) || !session.Ready) return;
                SendClientMessage(session, SocialEconomyMessageTypes.FriendsState, BuildFriendsState(view), DeliveryMethod.ReliableOrdered);
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Friend ready snapshot failed for peer {session?.Peer?.Id}: {ex.Message}");
        }
    }

    private void CloseSocialEconomyForCharacter(long characterId)
    {
        _storageAccessByCharacter.Remove(characterId);
        _socialEconomy?.CancelForCharacter(characterId);
        _socialEconomy?.NotifyPresenceChanged(characterId);
    }
}
