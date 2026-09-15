using System;

namespace Game.Shared.Interactions
{
    // Mirrors the canonical production interaction framework vocabulary while remaining
    // primitives-only so both Unity clients and the standalone .NET server can use it.
    public enum InteractionFeature : byte
    {
        Core = 0,
        Loot = 1,
        PlayerSocial = 2,
        Friendship = 3,
        Marriage = 4,
        Emotes = 5,
        Vampire = 6,
        Hunter = 7,
        AdultContent = 8,
        Harvesting = 9,
        WorldObjects = 10,
    }

    public enum InteractionAvailability : byte
    {
        Hidden = 0,
        Disabled = 1,
        Available = 2,
    }

    public readonly struct InteractionActionEntry
    {
        public InteractionCategoryId CategoryId { get; }
        public InteractionActionId ActionId { get; }
        public string Category { get; }
        public string Label { get; }
        public InteractionAvailability Availability { get; }
        public string DisabledReason { get; }
        public InteractionConsentMode ConsentMode { get; }
        public InteractionContentLevel ContentLevel { get; }
        public InteractionFeature Feature { get; }
        public short SortOrder { get; }

        public bool IsAvailable => Availability == InteractionAvailability.Available;

        public InteractionActionEntry(
            InteractionCategoryId categoryId,
            InteractionActionId actionId,
            string label,
            InteractionAvailability availability,
            string disabledReason = "",
            InteractionConsentMode consentMode = InteractionConsentMode.None,
            InteractionContentLevel contentLevel = InteractionContentLevel.General,
            InteractionFeature feature = InteractionFeature.Core,
            short sortOrder = 0)
        {
            CategoryId = categoryId == InteractionCategoryId.None
                ? InteractionCategoryCatalog.DefaultForAction(actionId)
                : categoryId;
            ActionId = actionId;
            Category = InteractionCategoryCatalog.Label(CategoryId);
            Label = string.IsNullOrWhiteSpace(label) ? actionId.ToString() : label;
            Availability = availability;
            DisabledReason = disabledReason ?? string.Empty;
            ConsentMode = consentMode;
            ContentLevel = contentLevel;
            Feature = feature;
            SortOrder = sortOrder;
        }

        public InteractionActionEntry WithAvailability(InteractionAvailability availability, string reason = "") =>
            new InteractionActionEntry(CategoryId, ActionId, Label, availability, reason, ConsentMode, ContentLevel, Feature, SortOrder);


    }

    public readonly struct InteractionActionSet
    {
        public InteractionTargetHandle Target { get; }
        public string TargetLabel { get; }
        public InteractionActionEntry[] Actions { get; }
        public string Detail { get; }

        public InteractionActionSet(
            InteractionTargetHandle target,
            string targetLabel,
            InteractionActionEntry[] actions,
            string detail = "")
        {
            Target = target;
            TargetLabel = targetLabel ?? string.Empty;
            Actions = actions ?? Array.Empty<InteractionActionEntry>();
            Detail = detail ?? string.Empty;
        }
    }

    // Session vocabulary is established now so consent-aware Player/prop interactions can
    // use one contract later instead of introducing a parallel social/prop session model.
    public enum InteractionSessionState : byte
    {
        None = 0,
        Pending = 1,
        Accepted = 2,
        Active = 3,
        Declined = 4,
        Cancelled = 5,
        Expired = 6,
        Completed = 7,
    }

    public readonly struct InteractionSessionSnapshot
    {
        public long SessionId { get; }
        public long InitiatorCharacterId { get; }
        public InteractionTargetHandle Target { get; }
        public InteractionCategoryId CategoryId { get; }
        public InteractionActionId ActionId { get; }
        public InteractionConsentMode ConsentMode { get; }
        public InteractionSessionState State { get; }
        public double ExpiresAt { get; }

        public InteractionSessionSnapshot(
            long sessionId,
            long initiatorCharacterId,
            InteractionTargetHandle target,
            InteractionActionId actionId,
            InteractionConsentMode consentMode,
            InteractionSessionState state,
            double expiresAt)
        {
            SessionId = sessionId;
            InitiatorCharacterId = initiatorCharacterId;
            Target = target;
            CategoryId = InteractionCategoryCatalog.ForTargetAction(target.Kind, actionId);
            ActionId = actionId;
            ConsentMode = consentMode;
            State = state;
            ExpiresAt = expiresAt;
        }
    }
}
