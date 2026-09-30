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
    private readonly Dictionary<long, OwnerLiveInterestKind> _ownerLiveInterestsByCharacter = new();
    // Outbound transport baselines only. Authoritative state remains in SocialEconomyRuntime.
    private readonly Dictionary<long, StorageStateMessage> _lastSentStorageStateByCharacter = new();
    private readonly Dictionary<long, TradeStateMessage> _lastSentTradeStateByCharacter = new();
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
        RegisterRequest(handlers, FriendRequestTypes.OwnerLiveInterest, HandleOwnerLiveInterest);
        RegisterRequest(handlers, EconomyRequestTypes.TradeAction, HandleTradeAction);
        RegisterRequest(handlers, EconomyRequestTypes.TradeSnapshot, HandleTradeSnapshot);
        RegisterRequest(handlers, EconomyRequestTypes.StorageSnapshot, HandleStorageSnapshot);
        RegisterRequest(handlers, EconomyRequestTypes.StorageTransfer, HandleStorageTransfer);
    }

    private void HandleOwnerLiveInterest(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new OwnerLiveInterestRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId,
                SocialEconomyMutationResponseMessage.Failed(1, "character is not in world"));
            return;
        }

        const OwnerLiveInterestKind supported =
            OwnerLiveInterestKind.FriendsPresence | OwnerLiveInterestKind.GuildPresence;
        OwnerLiveInterestKind requested = (OwnerLiveInterestKind)request.interests & supported;
        long characterId = runtime.CharacterId.Value;
        _ownerLiveInterestsByCharacter.TryGetValue(characterId, out OwnerLiveInterestKind previous);

        if (requested == OwnerLiveInterestKind.None)
            _ownerLiveInterestsByCharacter.Remove(characterId);
        else
            _ownerLiveInterestsByCharacter[characterId] = requested;

        SendResponse(session, requestId, SocialEconomyMutationResponseMessage.Ok());

        // Enabling presence interest reconciles immediately from the already-hydrated
        // GameServer cache. This intentionally performs no Gateway/database reload.
        bool friendsEnabledNow = (requested & OwnerLiveInterestKind.FriendsPresence) != 0;
        bool friendsWasEnabled = (previous & OwnerLiveInterestKind.FriendsPresence) != 0;
        if (friendsEnabledNow && !friendsWasEnabled)
        {
            SendClientMessage(
                session,
                SocialEconomyMessageTypes.FriendsState,
                BuildFriendPresenceState(_socialEconomy.GetFriendView(characterId)),
                DeliveryMethod.ReliableOrdered);
        }
    }

    private bool HasOwnerLiveInterest(long characterId, OwnerLiveInterestKind interest) =>
        _ownerLiveInterestsByCharacter.TryGetValue(characterId, out OwnerLiveInterestKind value) &&
        (value & interest) != 0;

    private void PublishFriendPresenceToInterestedOwners(long changedCharacterId)
    {
        if (_socialEconomy == null || changedCharacterId <= 0 || _ownerLiveInterestsByCharacter.Count == 0)
            return;

        // Presence is optional live state. Only owners with an active consumer receive it.
        // Membership remains in the existing authoritative StateChanged path and is pushed
        // regardless of whether the Friends UI is open.
        var interestedOwners = new List<long>(_ownerLiveInterestsByCharacter.Count);
        foreach (KeyValuePair<long, OwnerLiveInterestKind> pair in _ownerLiveInterestsByCharacter)
        {
            if ((pair.Value & OwnerLiveInterestKind.FriendsPresence) != 0)
                interestedOwners.Add(pair.Key);
        }

        for (int i = 0; i < interestedOwners.Count; ++i)
        {
            long ownerCharacterId = interestedOwners[i];
            if (ownerCharacterId == changedCharacterId ||
                !HasOwnerLiveInterest(ownerCharacterId, OwnerLiveInterestKind.FriendsPresence))
                continue;

            FriendView view = _socialEconomy.GetFriendView(ownerCharacterId);
            BackendFriendEntryDto[] friends = view?.Friends ?? Array.Empty<BackendFriendEntryDto>();
            bool affected = false;
            for (int j = 0; j < friends.Length; ++j)
            {
                if (friends[j]?.characterId != changedCharacterId) continue;
                affected = true;
                break;
            }
            if (!affected) continue;

            ClientSession owner = FindReadySessionByCharacterId(ownerCharacterId);
            if (owner == null || owner.Entity?.Runtime == null) continue;
            SendClientMessage(
                owner,
                SocialEconomyMessageTypes.FriendsState,
                new FriendsStateMessage
                {
                    updateKind = FriendsStateUpdateKind.PresenceDelta,
                    presence = new[]
                    {
                        new FriendPresenceWire
                        {
                            characterId = changedCharacterId,
                            online = FindReadySessionByCharacterId(changedCharacterId) != null,
                        },
                    },
                },
                DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleFriendsSnapshot(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new EmptySocialRequestMessage();
        request.Deserialize(reader);

        if (TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            RunFriendsSnapshotAsync(session, requestId, runtime).Forget();
            return;
        }

        // Existing request 700 doubles as the pre-Ready membership-fingerprint probe.
        // It never grants authority: the GameServer hydrates durable membership first,
        // then records the proof only on an exact match.
        if (request.knownRevision != 0 &&
            TryGetAuthoritativeSession(session, out var authoritative) &&
            authoritative.State == Game.Shared.Sessions.PlayerSessionState.AwaitingWorldEntry &&
            authoritative.Runtime != null)
        {
            RunFriendsCacheProbeAsync(session, requestId, authoritative.Runtime, request.knownRevision).Forget();
            return;
        }

        SendResponse(session, requestId, new FriendsStateMessage { updateKind = FriendsStateUpdateKind.Full });
    }

    private async Task RunFriendsCacheProbeAsync(ClientSession session, uint requestId, PlayerRuntime runtime, long knownRevision)
    {
        FriendView view = null;
        try
        {
            view = await _socialEconomy.LoadFriendsAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }

        FriendsStateMessage state = BuildFriendsState(view);
        long authoritativeRevision = FriendsStateRevision.Compute(state.friends);
        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session)) return;
            session.FriendsKnownRevision = knownRevision == authoritativeRevision ? authoritativeRevision : 0L;
            // Probe response is intentionally tiny; a mismatch falls through to the normal
            // authoritative Ready baseline.
            SendResponse(session, requestId, new FriendsStateMessage
            {
                updateKind = FriendsStateUpdateKind.PresenceDelta,
                presence = Array.Empty<FriendPresenceWire>(),
            });
        });
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
        TradeStateMessage state = BuildTradeState(_socialEconomy.GetTradeView(runtime.CharacterId.Value));
        _lastSentTradeStateByCharacter[runtime.CharacterId.Value] = state;
        SendResponse(session, requestId, state);
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
            _lastSentStorageStateByCharacter.Remove(runtime.CharacterId.Value);
            SendResponse(session, requestId,
                new StorageStateMessage { updateKind = StorageStateUpdateKind.Full, detail = reason });
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
            // The outbound diff baseline is presentation-only transport state and must die
            // with the authoritative storage-access grant.
            _lastSentStorageStateByCharacter.Remove(runtime.CharacterId.Value);
            SendClientMessage(session, SocialEconomyMessageTypes.StorageState,
                new StorageStateMessage { updateKind = StorageStateUpdateKind.Full, detail = reason },
                DeliveryMethod.ReliableOrdered);
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
            if (state.capacity > 0)
                _lastSentStorageStateByCharacter[runtime.CharacterId.Value] = state;
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
                runtime,
                deposit,
                request.sourceSlot,
                request.quantity,
                request.expectedItemInstanceId,
                request.knownStorageRevision,
                CancellationToken.None).ConfigureAwait(false);
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
                session.FriendsKnownRevision = FriendsStateRevision.Compute(state.friends);
                SendClientMessage(session, SocialEconomyMessageTypes.FriendsState, state, DeliveryMethod.ReliableOrdered);
            }
            if ((kind & SocialEconomyStateKind.Trade) != 0)
            {
                TradeStateMessage current = BuildTradeState(_socialEconomy.GetTradeView(characterId));
                TradeStateMessage outbound = BuildTradeDelta(characterId, current);
                _lastSentTradeStateByCharacter[characterId] = current;
                SendClientMessage(session, SocialEconomyMessageTypes.TradeState, outbound, DeliveryMethod.ReliableOrdered);
            }
            if ((kind & SocialEconomyStateKind.Storage) != 0)
            {
                BackendStorageSnapshotDto storage = _socialEconomy.GetStorage(characterId);
                if (storage != null)
                {
                    StorageStateMessage current = BuildStorageState(storage);
                    StorageStateMessage outbound = BuildStorageDelta(characterId, current);
                    _lastSentStorageStateByCharacter[characterId] = current;
                    SendClientMessage(session, SocialEconomyMessageTypes.StorageState, outbound, DeliveryMethod.ReliableOrdered);
                }
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
            updateKind = FriendsStateUpdateKind.Full,
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
            changeMask = TradeStateChangeMask.Full,
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
            updateKind = StorageStateUpdateKind.Full,
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

    private FriendsStateMessage BuildFriendPresenceState(FriendView view)
    {
        BackendFriendEntryDto[] source = view?.Friends ?? Array.Empty<BackendFriendEntryDto>();
        var presence = new FriendPresenceWire[source.Length];
        for (int i = 0; i < source.Length; ++i)
        {
            long id = source[i]?.characterId ?? 0;
            presence[i] = new FriendPresenceWire
            {
                characterId = id,
                online = id > 0 && FindReadySessionByCharacterId(id) != null,
            };
        }
        return new FriendsStateMessage
        {
            updateKind = FriendsStateUpdateKind.PresenceDelta,
            presence = presence,
        };
    }

    private TradeStateMessage BuildTradeDelta(long characterId, TradeStateMessage current)
    {
        if (!_lastSentTradeStateByCharacter.TryGetValue(characterId, out TradeStateMessage previous) ||
            previous.sessionId != current.sessionId || current.sessionId == 0)
            return current;

        TradeStateChangeMask mask = TradeStateChangeMask.Full;
        bool metadataChanged =
            previous.partnerCharacterId != current.partnerCharacterId ||
            !string.Equals(previous.partnerName, current.partnerName, StringComparison.Ordinal) ||
            previous.phase != current.phase || previous.ownLocked != current.ownLocked ||
            previous.partnerLocked != current.partnerLocked || previous.ownConfirmed != current.ownConfirmed ||
            previous.partnerConfirmed != current.partnerConfirmed;
        bool ownChanged = !SameTradeOffers(previous.ownOffers, current.ownOffers);
        bool partnerChanged = !SameTradeOffers(previous.partnerOffers, current.partnerOffers);
        bool detailChanged = !string.Equals(previous.detail, current.detail, StringComparison.Ordinal);

        if (metadataChanged) mask |= TradeStateChangeMask.Metadata;
        if (ownChanged) mask |= TradeStateChangeMask.OwnOffers;
        if (partnerChanged) mask |= TradeStateChangeMask.PartnerOffers;
        if (detailChanged) mask |= TradeStateChangeMask.Detail;

        // Full is encoded as zero, so a zero-difference notification is represented by
        // Metadata with the current compact flags rather than accidentally serializing full.
        if (mask == TradeStateChangeMask.Full)
            mask = TradeStateChangeMask.Metadata;

        return new TradeStateMessage
        {
            changeMask = mask,
            sessionId = current.sessionId,
            partnerCharacterId = current.partnerCharacterId,
            partnerName = current.partnerName,
            phase = current.phase,
            ownLocked = current.ownLocked,
            partnerLocked = current.partnerLocked,
            ownConfirmed = current.ownConfirmed,
            partnerConfirmed = current.partnerConfirmed,
            ownOffers = ownChanged ? current.ownOffers : null,
            partnerOffers = partnerChanged ? current.partnerOffers : null,
            detail = detailChanged ? current.detail : null,
        };
    }

    private static bool SameTradeOffers(TradeOfferWire[] left, TradeOfferWire[] right)
    {
        left ??= Array.Empty<TradeOfferWire>();
        right ??= Array.Empty<TradeOfferWire>();
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; ++i)
        {
            if (left[i].sourceSlot != right[i].sourceSlot || left[i].quantity != right[i].quantity ||
                left[i].item.itemInstanceId != right[i].item.itemInstanceId ||
                left[i].item.quantity != right[i].item.quantity || left[i].item.durability != right[i].item.durability)
                return false;
        }
        return true;
    }

    private StorageStateMessage BuildStorageDelta(long characterId, StorageStateMessage current)
    {
        if (!_lastSentStorageStateByCharacter.TryGetValue(characterId, out StorageStateMessage previous) ||
            previous.capacity <= 0 || previous.capacity != current.capacity ||
            current.revision != previous.revision + 1)
            return current;

        var before = new Dictionary<long, PlayerItemWire>();
        PlayerItemWire[] previousItems = previous.items ?? Array.Empty<PlayerItemWire>();
        for (int i = 0; i < previousItems.Length; ++i)
            if (previousItems[i].itemInstanceId > 0) before[previousItems[i].itemInstanceId] = previousItems[i];

        var after = new Dictionary<long, PlayerItemWire>();
        PlayerItemWire[] currentItems = current.items ?? Array.Empty<PlayerItemWire>();
        for (int i = 0; i < currentItems.Length; ++i)
            if (currentItems[i].itemInstanceId > 0) after[currentItems[i].itemInstanceId] = currentItems[i];

        var changed = new List<PlayerItemWire>();
        foreach (KeyValuePair<long, PlayerItemWire> pair in after)
        {
            if (!before.TryGetValue(pair.Key, out PlayerItemWire oldItem) || !SameStorageItem(oldItem, pair.Value))
                changed.Add(pair.Value);
        }

        var removed = new List<long>();
        foreach (long id in before.Keys)
            if (!after.ContainsKey(id)) removed.Add(id);

        return new StorageStateMessage
        {
            updateKind = StorageStateUpdateKind.Delta,
            capacity = current.capacity,
            revision = current.revision,
            baseRevision = previous.revision,
            items = changed.ToArray(),
            removedItemInstanceIds = removed.ToArray(),
            detail = current.detail ?? string.Empty,
        };
    }

    private static bool SameStorageItem(PlayerItemWire a, PlayerItemWire b) =>
        a.inventorySlot == b.inventorySlot &&
        a.itemInstanceId == b.itemInstanceId && a.itemDataId == b.itemDataId &&
        a.quantity == b.quantity && a.durability == b.durability;

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
        PublishFriendPresenceToInterestedOwners(runtime.CharacterId.Value);
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
        _ownerLiveInterestsByCharacter.Remove(characterId);
        _storageAccessByCharacter.Remove(characterId);
        _lastSentStorageStateByCharacter.Remove(characterId);
        _lastSentTradeStateByCharacter.Remove(characterId);
        _socialEconomy?.CancelForCharacter(characterId);
        // GuildService owns only a reloadable runtime cache/invite state here; durable guild
        // membership remains repository-owned. Releasing on the existing disconnect lifecycle
        // prevents disconnected characters from being retained without adding any wire traffic.
        _guildService?.ReleaseCharacter(characterId);
        PublishFriendPresenceToInterestedOwners(characterId);
    }
}
