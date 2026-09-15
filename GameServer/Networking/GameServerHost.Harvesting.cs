using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Harvesting;
using Game.Server.Application.Items;
using Game.Server.Application.Interactions;
using Game.Server.Application.Rewards;
using Game.Server.Domain.Players;
using Game.GameServer.Runtime;
using Game.Shared.Interactions;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void SendHarvestStarted(ClientSession session, long stableId, HarvestAttemptPlan plan)
    {
        if (!IsCurrent(session)) return;
        double centiseconds = Math.Ceiling(Math.Max(0.05d, plan.DurationSeconds) * 100d);
        uint duration = centiseconds >= uint.MaxValue ? uint.MaxValue : (uint)centiseconds;
        SendClientMessage(
            session,
            PlayerHarvestMessageTypes.HarvestEvent,
            new PlayerHarvestEventMessage
            {
                phase = (byte)PlayerHarvestEventPhase.Started,
                stableId = stableId,
                presentationId = plan.PresentationId,
                durationCentiseconds = duration,
                rewardTier = plan.RewardTier,
                flags = 0,
            },
            DeliveryMethod.ReliableOrdered);
    }

    private void OnInteractionSessionChanged(ServerInteractionSession session)
    {
        if (session == null || session.ActionId != InteractionActionId.Harvest)
            return;

        switch (session.State)
        {
            case InteractionSessionState.Completed:
                BeginHarvestResolution(session);
                break;
            case InteractionSessionState.Cancelled:
            case InteractionSessionState.Declined:
            case InteractionSessionState.Expired:
                if (_runtime.Harvesting.CancelSession(session.SessionId))
                {
                    PlayerRuntime cancelledRuntime = ResolveReadyRuntime(session.InitiatorCharacterId);
                    ClientSession owner = FindReadySession(cancelledRuntime);
                    if (owner != null)
                        SendHarvestTerminal(owner, session.WorldObjectId, PlayerHarvestEventPhase.Cancelled, 0, 0, false);
                }
                break;
        }
    }

    private void BeginHarvestResolution(ServerInteractionSession session)
    {
        PlayerRuntime runtime = ResolveReadyRuntime(session.InitiatorCharacterId);
        if (!_runtime.Harvesting.TryBeginResolution(
                session.SessionId,
                runtime,
                Random.Shared.NextDouble,
                out PendingHarvestResolution pending,
                out string detail))
        {
            Console.Error.WriteLine($"[Harvest] Session {session.SessionId} could not resolve: {detail}");
            ClientSession failedOwner = FindReadySession(runtime);
            if (failedOwner != null)
                SendHarvestTerminal(failedOwner, session.WorldObjectId, PlayerHarvestEventPhase.Cancelled, 0, 0, false);
            return;
        }

        _ = CommitHarvestRewardAsync(runtime, pending);
    }

    private async Task CommitHarvestRewardAsync(PlayerRuntime runtime, PendingHarvestResolution pending)
    {
        PlayerItemPartialGrantResult grant;
        try
        {
            grant = await _runtime.PlayerItems.GrantBundlePartiallyAsync(
                runtime,
                pending.Reward?.items,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _mainThreadCompletions.Enqueue(() => CompleteHarvestReward(runtime, pending, false, ex.Message));
            return;
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!grant.Success)
            {
                CompleteHarvestReward(runtime, pending, false, grant.ItemResult.Error);
                return;
            }

            int overflowDrops = 0;
            try
            {
                overflowDrops = _runtime.WorldItems.SpawnGeneratedRewardDrops(runtime, grant.OverflowItems);
            }
            catch (Exception ex)
            {
                // Inventory acceptance has already committed. Never make the harvest retryable
                // after that point or the accepted portion could duplicate.
                Console.Error.WriteLine(
                    $"[Harvest] Overflow world-drop creation failed after inventory commit for character {pending.CharacterId}, node {pending.TargetKey.StableId}: {ex.Message}");
            }

            try
            {
                // Soft progression/reputation is independent of overflow world-drop creation.
                // A transient drop failure must not silently skip the rest of the committed reward.
                _runtime.Rewards.GrantSoft(runtime, pending.Reward);
            }
            catch (Exception ex)
            {
                // As above, durable inventory acceptance has already committed. Report the
                // soft-tail failure without making the harvest retryable and duplicating items.
                Console.Error.WriteLine(
                    $"[Harvest] Soft reward grant failed after inventory commit for character {pending.CharacterId}, node {pending.TargetKey.StableId}: {ex.Message}");
            }

            if (overflowDrops > 0)
            {
                Console.WriteLine(
                    $"[Harvest] Character {pending.CharacterId} overflowed {overflowDrops} transient reward stack(s) at node {pending.TargetKey.StableId}.");
            }
            CompleteHarvestReward(runtime, pending, true, string.Empty);
        });
    }

    private void CompleteHarvestReward(
        PlayerRuntime runtime,
        PendingHarvestResolution pending,
        bool rewardCommitted,
        string rewardError)
    {
        HarvestResolveResult result = _runtime.Harvesting.CompleteResolution(
            pending,
            rewardCommitted,
            rewardError,
            _scheduler.ServerTime);

        ClientSession owner = FindReadySession(runtime);
        if (owner != null)
        {
            PlayerHarvestEventPhase phase = result.Committed
                ? (result.HarvestSucceeded ? PlayerHarvestEventPhase.Succeeded : PlayerHarvestEventPhase.Failed)
                : PlayerHarvestEventPhase.Cancelled;
            SendHarvestTerminal(
                owner,
                pending.TargetKey.StableId,
                phase,
                result.PresentationId,
                result.RewardTier,
                result.Depleted);
        }

        if (!result.Committed)
        {
            Console.Error.WriteLine(
                $"[Harvest] Reward commit failed for character {pending.CharacterId}, node {pending.TargetKey.StableId}: {result.Detail}");
            return;
        }

        if (result.Depleted && result.HarvestReadyAt > 0d && result.HarvestGeneration != 0u)
        {
            // HarvestingService owns the canonical ready time. Do not recompute profile timing here;
            // scheduling from the recorded deadline keeps runtime state and delayed work in lockstep.
            double delay = Math.Max(0d, result.HarvestReadyAt - _scheduler.ServerTime);
            uint harvestGeneration = result.HarvestGeneration;
            _scheduler.Schedule(delay, () =>
                _runtime.Harvesting.TryRespawn(
                    pending.TargetKey,
                    harvestGeneration,
                    _scheduler.ServerTime,
                    Random.Shared.NextDouble));
        }
    }

    private void SendHarvestTerminal(
        ClientSession session,
        long stableId,
        PlayerHarvestEventPhase phase,
        ushort presentationId,
        byte rewardTier,
        bool depleted)
    {
        SendClientMessage(
            session,
            PlayerHarvestMessageTypes.HarvestEvent,
            new PlayerHarvestEventMessage
            {
                phase = (byte)phase,
                stableId = stableId,
                presentationId = presentationId,
                durationCentiseconds = 0,
                rewardTier = rewardTier,
                flags = depleted ? (byte)PlayerHarvestEventFlags.Depleted : (byte)PlayerHarvestEventFlags.None,
            },
            DeliveryMethod.ReliableOrdered);
    }
}
