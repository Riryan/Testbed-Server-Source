using System;
using Game.GameServer.Runtime;
using Game.Shared.Protocol;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    /// <summary>
    /// Push the owner-only authoritative baselines once after Ready. Snapshot request
    /// handlers remain registered strictly for reconciliation/recovery; a normal standalone
    /// client no longer needs to issue a burst of baseline requests after admission.
    /// </summary>
    private void SendOwnerBaselinesAfterReady(ClientSession session)
    {
        if (!TryGetInWorldRuntime(session, out var runtime))
            return;

        try
        {
            // Party is temporary GameServer-local state and has no catalog dependency.
            // Subscribe/push it first so an unrelated optional owner-baseline failure cannot
            // leave later Party mutations without their event-driven UI bridge.
            BeginPartyStateReady(session, runtime);
            BeginGuildStateReady(session, runtime);

            // Settings first unless this exact connection already proved it has the current
            // persistent public catalog. A stale/missing cache keeps the established full
            // baseline and ordering so compact owner-state IDs are always resolvable.
            long currentSettingsRevision = _runtime.Content.Revision;
            if (session.GameplaySettingsValidatedRevision != currentSettingsRevision)
            {
                SendClientMessage(
                    session,
                    GameplaySettingsMessageTypes.Snapshot,
                    BuildGameplaySettingsSnapshot(),
                    DeliveryMethod.ReliableOrdered);
            }

            var items = _runtime.PlayerItems.GetSnapshot(runtime);
            if (items != null &&
                (session.PlayerItemsValidatedContentRevision != items.contentRevision ||
                 session.PlayerItemsValidatedInventoryRevision != items.inventoryRevision ||
                 session.PlayerItemsValidatedEquipmentRevision != items.equipmentRevision))
            {
                SendClientMessage(
                    session,
                    PlayerItemMessageTypes.Snapshot,
                    ToWire(PlayerItemOperationResult.Succeeded(items)),
                    DeliveryMethod.ReliableOrdered);
            }

            if (runtime.HasCharacterResources)
            {
                var resources = _runtime.Resources.GetSnapshot(runtime);
                if (resources != null)
                {
                    SendClientMessage(
                        session,
                        PlayerResourceMessageTypes.Snapshot,
                        ToWire(resources),
                        DeliveryMethod.ReliableOrdered);
                }
            }

            var statuses = _runtime.StatusEffects.GetSnapshot(runtime);
            if (statuses != null)
            {
                SendClientMessage(
                    session,
                    PlayerStatusEffectMessageTypes.Snapshot,
                    ToWire(statuses),
                    DeliveryMethod.ReliableOrdered);
            }

            ProgressionSnapshotMessage progression = BuildProgressionSnapshot(runtime);
            if (session.ProgressionValidatedContentRevision != progression.contentRevision ||
                session.ProgressionValidatedRevision != progression.revision)
            {
                SendClientMessage(
                    session,
                    ProgressionMessageTypes.Snapshot,
                    progression,
                    DeliveryMethod.ReliableOrdered);
            }

            SendClientMessage(
                session,
                PlayerGameplayActionMessageTypes.CombatOwnerState,
                ToGameplayWire(_runtime.CombatLoadout.Capture(runtime, _scheduler.ServerTime)),
                DeliveryMethod.ReliableOrdered);

            // Friends are durable but low-frequency social state. Prime that cache once
            // after Ready; later mutations and same-GameServer presence changes are push-only.
            BeginCacheAwareFriendsReady(session, runtime);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Owner baseline push failed for peer {session?.Peer?.Id}: {ex}");
        }
    }
}
