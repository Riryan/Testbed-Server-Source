using Game.Shared.Interactions;

namespace Game.Server.Application.Interactions
{
    /// <summary>
    /// Minimal non-mutating player interaction owned by the standalone authoritative core.
    /// Inspect deliberately reveals no private character data; it only proves that the
    /// canonical target/range/admission path resolved an authoritative live player.
    /// Feature-specific social/consent actions remain owned by their gameplay systems.
    /// </summary>
    public sealed class InspectPlayerInteractionHandler : IInteractionActionHandler
    {
        public InteractionActionId ActionId => InteractionActionId.Inspect;

        public InteractionResult Execute(in InteractionExecutionContext context)
        {
            if (context.Source == null || context.TargetPlayer == null)
            {
                return new InteractionResult(
                    context.Sequence,
                    context.ActionId,
                    InteractionResultCode.InvalidTarget,
                    context.Target,
                    "target is unavailable");
            }

            return new InteractionResult(
                context.Sequence,
                context.ActionId,
                InteractionResultCode.Success,
                context.Target,
                "target inspected");
        }
    }
    /// <summary>
    /// Minimal non-mutating non-player actor interaction. This proves that Population/NPC
    /// identity, generation, world/range and alive-state admission resolve through the
    /// same canonical InteractionService instead of a Population-specific dispatcher.
    /// </summary>
    public sealed class InspectActorInteractionHandler : IActorInteractionActionHandler
    {
        public InteractionActionId ActionId => InteractionActionId.Inspect;

        public InteractionResult Execute(in ActorInteractionExecutionContext context)
        {
            if (context.Source == null ||
                context.TargetActor == null ||
                !context.TargetActor.Alive)
            {
                return new InteractionResult(
                    context.Sequence,
                    context.ActionId,
                    InteractionResultCode.InvalidTarget,
                    context.Target,
                    "actor target is unavailable");
            }

            return new InteractionResult(
                context.Sequence,
                context.ActionId,
                InteractionResultCode.Success,
                context.Target,
                "actor inspected");
        }
    }

}
