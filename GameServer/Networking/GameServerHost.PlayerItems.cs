using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{

    private void RegisterPlayerItemRequests(
        Dictionary<ushort, Action<ClientSession, uint, NetDataReader>> handlers)
    {
        RegisterRequest(handlers, PlayerItemRequestTypes.Snapshot,
            (session, requestId, _) => HandlePlayerItemsSnapshot(session, requestId));
        RegisterRequest(handlers, PlayerItemRequestTypes.MoveInventory, HandleMoveInventory);
        RegisterRequest(handlers, PlayerItemRequestTypes.Equip, HandleEquipItem);
        RegisterRequest(handlers, PlayerItemRequestTypes.Unequip, HandleUnequipItem);
        RegisterRequest(handlers, PlayerItemRequestTypes.Use, HandleUseItem);
    }
    private void HandlePlayerItemsSnapshot(ClientSession session, uint requestId)
    {
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerItemsResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.CharacterUnavailable,
                "character is not in world"));
            return;
        }

        PlayerItemsSnapshot snapshot = _runtime.PlayerItems.GetSnapshot(runtime);
        if (snapshot == null)
        {
            SendResponse(session, requestId, PlayerItemsResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.CharacterUnavailable,
                "player item state is unavailable"));
            return;
        }

        SendResponse(session, requestId, ToWire(PlayerItemOperationResult.Succeeded(snapshot)));
    }

    private void HandleMoveInventory(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new MoveInventoryRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.CharacterUnavailable,
                "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.PersistenceRejected,
                BackendPersistenceUnavailableMessage));
            return;
        }

        RunPlayerItemOperationAsync(
            session,
            requestId,
            _runtime.PlayerItems.MoveInventoryAsync(runtime, request.fromIndex, request.toIndex, CancellationToken.None)).Forget();
    }

    private void HandleEquipItem(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new EquipItemRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.CharacterUnavailable,
                "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.PersistenceRejected,
                BackendPersistenceUnavailableMessage));
            return;
        }

        if (!_runtime.Content.TryGetEquipmentSlot(
                request.equipmentSlotDataId,
                out EquipmentSlotDefinition slot))
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.EquipmentSlotInvalid,
                "equipment slot is invalid"));
            return;
        }

        RunPlayerItemOperationAsync(
            session,
            requestId,
            _runtime.PlayerItems.EquipAsync(
                runtime,
                request.inventoryIndex,
                slot.slotId,
                CancellationToken.None)).Forget();
    }

    private void HandleUnequipItem(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new UnequipItemRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.CharacterUnavailable,
                "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.PersistenceRejected,
                BackendPersistenceUnavailableMessage));
            return;
        }

        if (!_runtime.Content.TryGetEquipmentSlot(
                request.equipmentSlotDataId,
                out EquipmentSlotDefinition slot))
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.EquipmentSlotInvalid,
                "equipment slot is invalid"));
            return;
        }

        RunPlayerItemOperationAsync(
            session,
            requestId,
            _runtime.PlayerItems.UnequipAsync(
                runtime,
                slot.slotId,
                request.preferredInventoryIndex,
                CancellationToken.None)).Forget();
    }

    private void HandleUseItem(ClientSession session, uint requestId, NetDataReader reader)
    {
        var request = new UseItemRequestMessage();
        request.Deserialize(reader);
        if (!TryGetInWorldRuntime(session, out PlayerRuntime runtime))
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.CharacterUnavailable,
                "character is not in world"));
            return;
        }
        if (!IsBackendPersistenceMutationAvailable)
        {
            SendResponse(session, requestId, PlayerItemMutationResponseMessage.Failed(
                (byte)PlayerItemOperationStatus.PersistenceRejected,
                BackendPersistenceUnavailableMessage));
            return;
        }

        // Resource/Status dependencies are part of the standalone composition root.
        // UseAsync remains authoritative: persist consume first, then apply only the
        // content-defined Resource/Status effects and emit their revisioned deltas.
        RunPlayerItemOperationAsync(
            session,
            requestId,
            _runtime.PlayerItems.UseAsync(runtime, request.inventoryIndex, _scheduler.ServerTime, CancellationToken.None)).Forget();
    }

    private async Task RunPlayerItemOperationAsync(
        ClientSession session,
        uint requestId,
        Task<PlayerItemOperationResult> operation)
    {
        PlayerItemOperationResult result;
        try
        {
            result = await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Player item operation failed for peer {session?.Peer?.Id}: {ex.Message}");
            result = PlayerItemOperationResult.Failed(
                PlayerItemOperationStatus.PersistenceRejected,
                "player item operation failed");
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!IsCurrent(session))
                return;
            SendResponse(session, requestId, ToMutationAck(result));
        });
    }

    private bool TryGetInWorldRuntime(ClientSession session, out PlayerRuntime runtime)
    {
        runtime = null;
        if (!IsCurrent(session) || !session.Ready || session.Entity == null)
            return false;
        if (!TryGetAuthoritativeSession(session, out var authoritative) ||
            authoritative.State != PlayerSessionState.InWorld ||
            authoritative.Runtime == null ||
            !ReferenceEquals(authoritative.Runtime, session.Entity.Runtime))
        {
            return false;
        }

        runtime = authoritative.Runtime;
        return runtime.HasPlayerItemSystems;
    }

    private void OnAuthoritativePlayerItemsChanged(
        PlayerRuntime runtime,
        PlayerItemsSnapshot previous,
        PlayerItemsSnapshot current)
    {
        if (runtime == null || previous == null || current == null)
            return;

        PlayerItemsDeltaMessage delta = ToDelta(previous, current);
        bool equipmentPresentationChanged = current.equipmentRevision != previous.equipmentRevision;
        PlayerCombatOwnerStateMessage combatOwnerState = default;
        if (equipmentPresentationChanged)
        {
            _runtime.CombatLoadout.ResetForEquipmentChange(runtime);
            combatOwnerState = ToGameplayWire(_runtime.CombatLoadout.Capture(runtime, _scheduler.ServerTime));
        }
        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession owner = FindReadySession(runtime);
            if (owner == null)
                return;

            SendClientMessage(owner, PlayerItemMessageTypes.Delta, delta, DeliveryMethod.ReliableOrdered);

            if (equipmentPresentationChanged && owner.Entity != null)
            {
                // Combat loadout state is owner-only; observers receive presentation only.
                SendClientMessage(
                    owner,
                    PlayerGameplayActionMessageTypes.CombatOwnerState,
                    combatOwnerState,
                    DeliveryMethod.ReliableOrdered);

                // Equipment presentation is rare authoritative definition state. Bump the
                // appearance sequence and replicate one reliable appearance delta only to
                // current AOI observers. Movement snapshots remain unchanged.
                owner.Entity.MarkAppearanceChanged();
                BroadcastAppearanceDelta(owner);
            }
        });
    }

    private ClientSession FindReadySession(PlayerRuntime runtime)
    {
        if (runtime == null)
            return null;

        ClientSession session = FindIndexedReadySessionByCharacterId(runtime.CharacterId.Value);
        return session != null && ReferenceEquals(session.Entity?.Runtime, runtime)
            ? session
            : null;
    }

    private void SendClientMessage<TMessage>(
        ClientSession session,
        ushort messageType,
        TMessage message,
        DeliveryMethod deliveryMethod)
        where TMessage : struct, INetSerializable
    {
        if (!IsCurrent(session))
            return;

        _writer.Reset();
        _writer.PutPackedUShort(messageType);
        message.Serialize(_writer);
        TrackGameplayWire(messageType, _writer.Length);
        Send(
            session,
            _writer,
            deliveryMethod,
            PriorityForClientMessage(messageType, deliveryMethod),
            FamilyForClientMessage(messageType));
    }

    private void TrackGameplayWire(ushort messageType, int bytes)
    {
        if (!_options.CombatWireDiagnostics)
            return;

        int payloadBytes = Math.Max(0, bytes);
        switch (messageType)
        {
            case PlayerGameplayActionMessageTypes.CombatDamage:
                _wireCombatDamageMessages++;
                _wireCombatDamageBytes += payloadBytes;
                break;
            case PlayerGameplayActionMessageTypes.AbilityCastState:
                _wireAbilityStateMessages++;
                _wireAbilityStateBytes += payloadBytes;
                break;
            case PlayerResourceMessageTypes.Delta:
                _wireResourceDeltaMessages++;
                _wireResourceDeltaBytes += payloadBytes;
                break;
            case PlayerStatusEffectMessageTypes.Delta:
                _wireStatusDeltaMessages++;
                _wireStatusDeltaBytes += payloadBytes;
                break;
        }
    }

    private static PlayerItemMutationResponseMessage ToMutationAck(PlayerItemOperationResult operation) =>
        new PlayerItemMutationResponseMessage
        {
            success = operation.Success,
            status = (byte)operation.Status,
            error = operation.Error ?? string.Empty,
        };

    private static PlayerItemsResponseMessage ToWire(PlayerItemOperationResult operation)
    {
        PlayerItemsSnapshot snapshot = operation.Snapshot;
        if (snapshot == null)
            return PlayerItemsResponseMessage.Failed((byte)operation.Status, operation.Error);

        PlayerItemView[] sourceInventory = snapshot.inventory ?? Array.Empty<PlayerItemView>();
        var inventory = new PlayerItemWire[sourceInventory.Length];
        for (int i = 0; i < inventory.Length; ++i)
            inventory[i] = ToWire(sourceInventory[i]);

        EquipmentSlotView[] sourceEquipment = snapshot.equipment ?? Array.Empty<EquipmentSlotView>();
        var equipment = new EquipmentSlotWire[sourceEquipment.Length];
        for (int i = 0; i < equipment.Length; ++i)
        {
            EquipmentSlotView slot = sourceEquipment[i];
            equipment[i] = new EquipmentSlotWire
            {
                slotDataId = slot?.slotDataId ?? 0,
                slotId = slot?.slotId ?? string.Empty,
                displayName = slot?.displayName ?? string.Empty,
                order = slot?.order ?? 0,
                hasItem = slot?.item != null,
                item = slot?.item == null ? default : ToWire(slot.item),
            };
        }

        return new PlayerItemsResponseMessage
        {
            success = operation.Success,
            status = (byte)operation.Status,
            error = operation.Error,
            contentRevision = snapshot.contentRevision,
            inventoryRevision = snapshot.inventoryRevision,
            equipmentRevision = snapshot.equipmentRevision,
            inventoryCapacity = snapshot.inventoryCapacity,
            inventoryWeight = snapshot.inventoryWeight,
            armor = snapshot.armor,
            attackPower = snapshot.attackPower,
            inventory = inventory,
            equipment = equipment,
        };
    }

    private static PlayerItemWire ToWire(PlayerItemView item) => new PlayerItemWire
    {
        inventorySlot = item.inventorySlot,
        equipmentSlotDataId = item.equipmentSlotDataId,
        equipmentSlotId = item.equipmentSlotId,
        itemInstanceId = item.itemInstanceId,
        itemDataId = item.itemDataId,
        definitionId = item.definitionId,
        displayName = item.displayName,
        quantity = item.quantity,
        durability = item.durability,
        maxDurability = item.maxDurability,
        unitWeight = item.unitWeight,
        canUse = item.canUse,
        consumeQuantity = item.consumeQuantity,
        allowedEquipmentSlots = item.allowedEquipmentSlots,
    };

    private static PlayerItemsDeltaMessage ToDelta(PlayerItemsSnapshot previous, PlayerItemsSnapshot current)
    {
        var beforeInventory = new Dictionary<int, PlayerItemView>();
        PlayerItemView[] oldInventory = previous.inventory ?? Array.Empty<PlayerItemView>();
        for (int i = 0; i < oldInventory.Length; ++i)
            if (oldInventory[i] != null && oldInventory[i].inventorySlot >= 0)
                beforeInventory[oldInventory[i].inventorySlot] = oldInventory[i];

        var afterInventory = new Dictionary<int, PlayerItemView>();
        PlayerItemView[] newInventory = current.inventory ?? Array.Empty<PlayerItemView>();
        for (int i = 0; i < newInventory.Length; ++i)
            if (newInventory[i] != null && newInventory[i].inventorySlot >= 0)
                afterInventory[newInventory[i].inventorySlot] = newInventory[i];

        var changedSlots = new HashSet<int>(beforeInventory.Keys);
        changedSlots.UnionWith(afterInventory.Keys);
        var orderedSlots = new List<int>(changedSlots);
        orderedSlots.Sort();
        var inventoryChanges = new List<InventorySlotDeltaWire>();
        for (int i = 0; i < orderedSlots.Count; ++i)
        {
            int slot = orderedSlots[i];
            beforeInventory.TryGetValue(slot, out PlayerItemView before);
            afterInventory.TryGetValue(slot, out PlayerItemView after);
            if (SameItemView(before, after))
                continue;
            inventoryChanges.Add(new InventorySlotDeltaWire
            {
                inventorySlot = slot,
                hasItem = after != null,
                item = after == null ? default : ToWire(after),
            });
        }

        EquipmentSlotView[] oldEquipment = previous.equipment ?? Array.Empty<EquipmentSlotView>();
        EquipmentSlotView[] newEquipment = current.equipment ?? Array.Empty<EquipmentSlotView>();
        var equipmentChanges = new List<EquipmentSlotDeltaWire>();
        for (int i = 0; i < newEquipment.Length; ++i)
        {
            EquipmentSlotView afterSlot = newEquipment[i];
            if (afterSlot == null || string.IsNullOrWhiteSpace(afterSlot.slotId))
                continue;
            EquipmentSlotView beforeSlot = FindEquipmentSlot(oldEquipment, afterSlot.slotId);
            if (beforeSlot != null && SameItemView(beforeSlot.item, afterSlot.item))
                continue;
            equipmentChanges.Add(new EquipmentSlotDeltaWire
            {
                slotDataId = afterSlot.slotDataId,
                slotId = afterSlot.slotId,
                hasItem = afterSlot.item != null,
                item = afterSlot.item == null ? default : ToWire(afterSlot.item),
            });
        }

        PlayerItemsDeltaFlags flags = PlayerItemsDeltaFlags.None;
        if (current.contentRevision != previous.contentRevision) flags |= PlayerItemsDeltaFlags.ContentRevision;
        if (current.inventoryWeight != previous.inventoryWeight) flags |= PlayerItemsDeltaFlags.InventoryWeight;
        if (current.armor != previous.armor || current.attackPower != previous.attackPower) flags |= PlayerItemsDeltaFlags.CombatStats;

        return new PlayerItemsDeltaMessage
        {
            contentRevision = current.contentRevision,
            inventoryRevision = current.inventoryRevision,
            equipmentRevision = current.equipmentRevision,
            changeMask = (byte)flags,
            inventoryWeight = current.inventoryWeight,
            armor = current.armor,
            attackPower = current.attackPower,
            inventoryChanges = inventoryChanges.ToArray(),
            equipmentChanges = equipmentChanges.ToArray(),
        };
    }

    private static bool SameItemView(PlayerItemView a, PlayerItemView b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return false;
        return a.inventorySlot == b.inventorySlot &&
               string.Equals(a.equipmentSlotId ?? string.Empty, b.equipmentSlotId ?? string.Empty, StringComparison.Ordinal) &&
               a.itemInstanceId == b.itemInstanceId &&
               string.Equals(a.definitionId, b.definitionId, StringComparison.Ordinal) &&
               a.quantity == b.quantity &&
               a.durability == b.durability;
    }

    private static EquipmentSlotView FindEquipmentSlot(EquipmentSlotView[] slots, string slotId)
    {
        for (int i = 0; i < slots.Length; ++i)
            if (slots[i] != null && string.Equals(slots[i].slotId, slotId, StringComparison.Ordinal))
                return slots[i];
        return null;
    }
}
