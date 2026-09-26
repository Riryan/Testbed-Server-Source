using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Interactions;
using Game.Server.Application.Social;
using Game.Shared.Interactions;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private bool _canonicalPlayerSocialHandlersRegistered;

    private void EnsureCanonicalPlayerSocialInteractionHandlers()
    {
        if (_canonicalPlayerSocialHandlersRegistered)
            return;

        if (!_runtime.Interactions.Register(new PartyInviteCanonicalInteractionHandler(this)))
            throw new InvalidOperationException("Party invite interaction action is already registered.");
        if (!_runtime.Interactions.RegisterAsyncPlayer(new GuildInviteCanonicalInteractionHandler(this)))
            throw new InvalidOperationException("Guild invite interaction action is already registered.");

        _canonicalPlayerSocialHandlersRegistered = true;
    }

    private sealed class PartyInviteCanonicalInteractionHandler : IInteractionActionHandler, IInteractionActionDescriptorProvider
    {
        private readonly GameServerHost _owner;
        public PartyInviteCanonicalInteractionHandler(GameServerHost owner) => _owner = owner;
        public InteractionActionId ActionId => InteractionActionId.PartyInvite;
        public InteractionActionEntry DescribeAction() => InteractionActionCatalog.Default(ActionId);

        public InteractionResult Execute(in InteractionExecutionContext context)
        {
            PartyOperationResult operation = _owner.PartySocial.Invite(context.Source, context.TargetPlayer);
            if (operation.Success && context.TargetPlayer != null)
            {
                var targetSession = _owner.FindIndexedReadySessionByCharacterId(context.TargetPlayer.CharacterId.Value);
                if (targetSession != null)
                {
                    string actorName = context.Source?.Character?.Name ?? "Player";
                    _owner.SendSystemChat(targetSession, $"{actorName} invited you to a party. Type /party accept or /party decline.");
                }
            }

            return new InteractionResult(
                context.Sequence,
                ActionId,
                operation.Success ? InteractionResultCode.Success : InteractionResultCode.Rejected,
                context.Target,
                operation.Message);
        }
    }

    private sealed class GuildInviteCanonicalInteractionHandler : IAsyncPlayerInteractionActionHandler, IInteractionActionDescriptorProvider
    {
        private readonly GameServerHost _owner;
        public GuildInviteCanonicalInteractionHandler(GameServerHost owner) => _owner = owner;
        public InteractionActionId ActionId => InteractionActionId.GuildInvite;
        public InteractionActionEntry DescribeAction() => InteractionActionCatalog.Default(ActionId);

        public async Task<InteractionResult> ExecuteAsync(InteractionExecutionContext context, CancellationToken cancellationToken)
        {
            GuildOperationResult operation;
            try
            {
                operation = await _owner.GuildSocial.InviteAsync(
                    context.Source,
                    context.TargetPlayer,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Guild interaction invite failed for character {context.Source?.CharacterId.Value ?? 0}: {ex.Message}");
                return new InteractionResult(
                    context.Sequence,
                    ActionId,
                    InteractionResultCode.Rejected,
                    context.Target,
                    "guild service is temporarily unavailable");
            }

            if (operation.Success && context.TargetPlayer != null)
            {
                long targetCharacterId = context.TargetPlayer.CharacterId.Value;
                string actorName = context.Source?.Character?.Name ?? "Player";
                string guildName = operation.Guild?.Name ?? "the guild";
                _owner.QueueMainThreadCompletion(() =>
                {
                    var targetSession = _owner.FindIndexedReadySessionByCharacterId(targetCharacterId);
                    if (targetSession != null && _owner.IsCurrent(targetSession))
                        _owner.SendSystemChat(targetSession, $"{actorName} invited you to guild '{guildName}'. Type /guild accept or /guild decline.");
                });
            }

            return new InteractionResult(
                context.Sequence,
                ActionId,
                operation.Success ? InteractionResultCode.Success : InteractionResultCode.Rejected,
                context.Target,
                operation.Message);
        }
    }
}
