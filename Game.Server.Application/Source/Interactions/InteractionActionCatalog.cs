using Game.Shared.Interactions;

namespace Game.Server.Application.Interactions
{
    /// <summary>
    /// Default presentation metadata for registered authoritative actions. Feature systems
    /// may override these values by implementing IInteractionActionDescriptorProvider.
    /// Registration remains the source of truth: an enum value alone never makes an action
    /// appear in discovery.
    /// </summary>
    public static class InteractionActionCatalog
    {
        public static InteractionActionEntry Default(InteractionActionId actionId)
        {
            switch (actionId)
            {
                case InteractionActionId.Inspect:
                    return Entry(actionId, "Inspect", InteractionFeature.Core, 10);
                case InteractionActionId.Talk:
                    return Entry(actionId, "Talk", InteractionFeature.Core, 20);
                case InteractionActionId.Use:
                    return Entry(actionId, "Use", InteractionFeature.WorldObjects, 20);
                case InteractionActionId.Open:
                    return Entry(actionId, "Open", InteractionFeature.WorldObjects, 30);
                case InteractionActionId.Loot:
                    return Entry(actionId, "Loot", InteractionFeature.Loot, 10);
                case InteractionActionId.Examine:
                    return Entry(actionId, "Examine", InteractionFeature.Loot, 20);
                case InteractionActionId.Search:
                    return Entry(actionId, "Search", InteractionFeature.Loot, 35);
                case InteractionActionId.Harvest:
                    return Entry(actionId, "Harvest", InteractionFeature.Harvesting, 40);
                case InteractionActionId.TradeRequest:
                    return Entry(actionId, "Trade", InteractionFeature.PlayerSocial, 30, InteractionConsentMode.TargetAcceptance);
                case InteractionActionId.PartyInvite:
                    return Entry(actionId, "Invite to Party", InteractionFeature.PlayerSocial, 40, InteractionConsentMode.TargetAcceptance);
                case InteractionActionId.GuildInvite:
                    return Entry(actionId, "Invite to Guild", InteractionFeature.PlayerSocial, 50, InteractionConsentMode.TargetAcceptance);
                case InteractionActionId.DuelRequest:
                    return Entry(actionId, "Duel", InteractionFeature.PlayerSocial, 60, InteractionConsentMode.TargetAcceptance);
                case InteractionActionId.Hug:
                    return Entry(actionId, "Hug", InteractionFeature.Emotes, 80, InteractionConsentMode.TargetAcceptance);
                case InteractionActionId.Kiss:
                    return Entry(actionId, "Kiss", InteractionFeature.Emotes, 90, InteractionConsentMode.TargetAcceptance);
                case InteractionActionId.PartnerDance:
                    return Entry(actionId, "Partner Dance", InteractionFeature.Emotes, 100, InteractionConsentMode.TargetAcceptance);
                default:
                    return Entry(actionId, actionId.ToString(), InteractionFeature.Core, 500);
            }
        }

        private static InteractionActionEntry Entry(
            InteractionActionId actionId,
            string label,
            InteractionFeature feature,
            short sortOrder,
            InteractionConsentMode consent = InteractionConsentMode.None,
            InteractionContentLevel content = InteractionContentLevel.General) =>
            new InteractionActionEntry(
                InteractionCategoryCatalog.DefaultForAction(actionId),
                actionId,
                label,
                InteractionAvailability.Available,
                string.Empty,
                consent,
                content,
                feature,
                sortOrder);
    }
}
