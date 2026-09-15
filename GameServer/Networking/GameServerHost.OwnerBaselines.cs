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
            // Settings first: status/ability presentation metadata is available before the
            // following owner-state baselines are consumed on the same reliable stream.
            SendClientMessage(
                session,
                GameplaySettingsMessageTypes.Snapshot,
                BuildGameplaySettingsSnapshot(),
                DeliveryMethod.ReliableOrdered);

            var items = _runtime.PlayerItems.GetSnapshot(runtime);
            if (items != null)
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

            SendClientMessage(
                session,
                ProgressionMessageTypes.Snapshot,
                BuildProgressionSnapshot(runtime),
                DeliveryMethod.ReliableOrdered);

            SendClientMessage(
                session,
                PlayerGameplayActionMessageTypes.CombatOwnerState,
                ToGameplayWire(_runtime.CombatLoadout.Capture(runtime, _scheduler.ServerTime)),
                DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex)
        {
            // Admission is already authoritative at this point. A failed optional baseline
            // must not disconnect/quarantine the character; existing reconciliation requests
            // remain available when a cache is later found missing or revision-gapped.
            Console.Error.WriteLine($"Owner baseline push failed for peer {session?.Peer?.Id}: {ex}");
        }
    }
}
