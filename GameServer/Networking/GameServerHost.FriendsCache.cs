using System;
using System.Threading;
using System.Threading.Tasks;
using Game.GameServer.Economy;
using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void BeginCacheAwareFriendsReady(ClientSession session, PlayerRuntime runtime)
    {
        if (_socialEconomy == null || runtime == null)
            return;

        PublishFriendPresenceToInterestedOwners(runtime.CharacterId.Value);
        RunCacheAwareFriendsReadyAsync(session, runtime).Forget();
    }

    private async Task RunCacheAwareFriendsReadyAsync(ClientSession session, PlayerRuntime runtime)
    {
        try
        {
            FriendView view = await _socialEconomy
                .LoadFriendsAsync(runtime, CancellationToken.None)
                .ConfigureAwait(false);
            FriendsStateMessage state = BuildFriendsState(view);
            long durableRevision = FriendsStateRevision.Compute(state.friends);

            _mainThreadCompletions.Enqueue(() =>
            {
                if (!IsCurrent(session) || !session.Ready)
                    return;

                if (session.FriendsKnownRevision == durableRevision)
                    return;

                session.FriendsKnownRevision = durableRevision;
                SendClientMessage(
                    session,
                    SocialEconomyMessageTypes.FriendsState,
                    state,
                    DeliveryMethod.ReliableOrdered);
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Friend ready snapshot failed for peer {session?.Peer?.Id}: {ex.Message}");
        }
    }
}
