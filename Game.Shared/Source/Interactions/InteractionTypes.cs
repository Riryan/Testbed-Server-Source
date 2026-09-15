using System;

namespace Game.Shared.Interactions
{
    // Numeric values intentionally preserve the uMMORPG interaction contract.
    public enum InteractionTargetKind : byte
    {
        None = 0,
        NetworkEntity = 1,
        PlayerEntity = 2,
        PopulationEntity = 3,
        SceneObject = 4,
        CombatTestTarget = 5,
        NetworkWorldObject = 6,
    }


    /// <summary>
    /// Stable top-level interaction intent. Keep this list deliberately small.
    /// Specific operations (Open, Hack, Chop, Trade, etc.) remain sub-action/definition IDs.
    /// Both category and action IDs are ushort on the wire for a compact, expandable contract.
    /// </summary>
    public enum InteractionCategoryId : ushort
    {
        None = 0,
        Use = 1,
        Interact = 2,
    }

    /// <summary>
    /// Shared immutable category rules. Clients use these for presentation/preflight;
    /// the authoritative server independently validates the same category/action pair.
    /// </summary>
    public static class InteractionCategoryCatalog
    {
        public static string Label(InteractionCategoryId categoryId)
        {
            switch (categoryId)
            {
                case InteractionCategoryId.Use: return "Use";
                case InteractionCategoryId.Interact: return "Interact";
                default: return string.Empty;
            }
        }

        public static InteractionCategoryId DefaultForAction(InteractionActionId actionId)
        {
            switch (actionId)
            {
                case InteractionActionId.Use:
                case InteractionActionId.Open:
                case InteractionActionId.Loot:
                case InteractionActionId.OpenContainer:
                case InteractionActionId.Unlock:
                case InteractionActionId.Examine:
                case InteractionActionId.Search:
                case InteractionActionId.Harvest:
                case InteractionActionId.OpenStorage:
                case InteractionActionId.OpenQuests:
                case InteractionActionId.Teleport:
                case InteractionActionId.Craft:
                    return InteractionCategoryId.Use;
                case InteractionActionId.None:
                    return InteractionCategoryId.None;
                default:
                    return InteractionCategoryId.Interact;
            }
        }

        public static InteractionCategoryId ForTargetAction(InteractionTargetKind targetKind, InteractionActionId actionId)
        {
            if (actionId == InteractionActionId.None)
                return InteractionCategoryId.None;

            switch (targetKind)
            {
                case InteractionTargetKind.SceneObject:
                case InteractionTargetKind.NetworkWorldObject:
                case InteractionTargetKind.CombatTestTarget:
                    return InteractionCategoryId.Use;
                case InteractionTargetKind.PlayerEntity:
                case InteractionTargetKind.PopulationEntity:
                case InteractionTargetKind.NetworkEntity:
                    return InteractionCategoryId.Interact;
                default:
                    return DefaultForAction(actionId);
            }
        }

        public static bool IsCompatible(
            InteractionTargetKind targetKind,
            InteractionCategoryId categoryId,
            InteractionActionId actionId) =>
            categoryId != InteractionCategoryId.None &&
            actionId != InteractionActionId.None &&
            categoryId == ForTargetAction(targetKind, actionId);


    }

    /// <summary>
    /// Client-safe immutable subset of an authored interaction definition. This can be baked
    /// into the client so supported actions, labels and obvious range checks do not require
    /// an interaction-menu round trip. Dynamic state remains server-owned and is replicated
    /// only when it changes.
    /// </summary>
    [Serializable]
    public struct InteractionPublicDefinition
    {
        public InteractionCategoryId categoryId;
        public InteractionActionId actionId;
        public string label;
        public float maximumUseDistance;
        public InteractionConsentMode consentMode;
        public InteractionContentLevel contentLevel;
        public short sortOrder;

        public bool IsValid => categoryId != InteractionCategoryId.None && actionId != InteractionActionId.None;
    }

    public enum InteractionActionId : ushort
    {
        None = 0,
        Inspect = 1,
        Talk = 2,
        Use = 3,
        Open = 4,
        Loot = 100,
        OpenContainer = 101,
        Unlock = 102,
        Examine = 103,
        Search = 104,
        Harvest = 105,
        TradeRequest = 200,
        PartyInvite = 201,
        GuildInvite = 202,
        DuelRequest = 203,
        Whisper = 204,
        ReportPlayer = 205,
        AddFriend = 220,
        RemoveFriend = 221,
        GiftFriend = 222,
        CoupleInvite = 230,
        Divorce = 231,
        OpenStorage = 301,
        OpenQuests = 302,
        Teleport = 303,
        Revive = 304,
        Craft = 305,
        SenseBlood = 400,
        InspectBlood = 401,
        RequestBlood = 402,
        OfferBlood = 403,
        OfferProtection = 404,
        Feed = 405,
        Intimidate = 406,
        Charm = 407,
        Dominate = 408,
        Recruit = 409,
        Enthrall = 410,
        BloodBond = 411,
        CreateGhoul = 412,
        ReleaseBond = 413,
        OfferTurning = 414,
        TurnIntoVampire = 415,
        MentorProgeny = 416,
        CommandProgeny = 417,
        ReleaseProgeny = 418,
        CommandFollow = 419,
        CommandStay = 420,
        CommandGuard = 421,
        CommandWork = 422,
        CommandFeed = 423,
        CommandReturnHome = 424,
        DismissServant = 425,
        Rescue = 426,
        VampireEmbrace = 427,
        Scan = 500,
        CollectEvidence = 501,
        CollectSample = 502,
        PhotographEvidence = 503,
        Question = 504,
        InterviewWitness = 505,
        MarkSuspect = 506,
        BeginTracking = 507,
        SearchTarget = 520,
        Restrain = 521,
        ReleaseRestraint = 522,
        ApplyWard = 523,
        TestSupernaturalTrace = 524,
        ConfiscateEvidence = 525,
        TransferToCell = 526,
        RecruitInformant = 540,
        RequestCooperation = 541,
        ShareIntel = 542,
        ReportToCell = 543,
        OpenEmotes = 600,
        PlayTargetedEmote = 601,
        PartnerDance = 602,
        Hug = 603,
        Kiss = 604,
        // Reserved developer/test-fixture action range. These actions are never persisted as gameplay content.
        CombatDummyResetHealth = 700,
        CombatDummyClearStatuses = 701,
        CombatDummyNormalDefense = 702,
        CombatDummyConductivePlate = 703,
        CombatDummyPoisonResistant = 704,
        CombatDummyPoisonImmune = 705,
        CombatDummyFireWeak = 706,
        CombatDummyInvulnerable = 707,
        CombatDummyShowStats = 708,
        RequestAdultSession = 1000,
        EndAdultSession = 1001,
    }

    public enum InteractionConsentMode : byte
    {
        None = 0,
        Confirmation = 1,
        TargetAcceptance = 2,
        MutualOptIn = 3,
    }

    public enum InteractionContentLevel : byte
    {
        General = 0,
        Mature = 1,
        Adult = 2,
    }

    public enum InteractionResultCode : byte
    {
        Success = 0,
        Rejected = 1,
        Disabled = 2,
        Unsupported = 3,
        InvalidTarget = 4,
        InvalidState = 5,
        OutOfRange = 6,
        Obstructed = 7,
        Cooldown = 8,
        ConsentRequired = 9,
        PermissionDenied = 10,
        RateLimited = 11,
        TargetUnavailable = 12,
        LocationRestricted = 13,
        SessionBusy = 14,
        Expired = 15,
    }

    /// <summary>
    /// Shared hard-range contract for interaction admission. Client checks are send
    /// suppression only; the GameServer always revalidates from authoritative positions.
    /// </summary>
    public static class InteractionRangePolicy
    {
        public const float WorldObjectUseRange = 3.25f;
        public const float PlayerInteractionRange = 4.0f;

        // Camera-space acquisition may be slightly longer than player-space use range
        // because third-person cameras sit behind the local actor. It must never be an
        // across-map selection ray.
        public const float ClientPointerRaycastDistance = 12.0f;

        public static float ClampWorldUseRange(float authoredRange)
        {
            if (float.IsNaN(authoredRange) || float.IsInfinity(authoredRange) || authoredRange <= 0f)
                return WorldObjectUseRange;
            return authoredRange < WorldObjectUseRange ? authoredRange : WorldObjectUseRange;
        }
    }

    public readonly struct InteractionTargetHandle : IEquatable<InteractionTargetHandle>
    {
        public InteractionTargetKind Kind { get; }
        public long PrimaryId { get; }
        public ushort Generation { get; }
        public long SecondaryId { get; }
        public bool IsValid => Kind != InteractionTargetKind.None && PrimaryId > 0;

        public InteractionTargetHandle(InteractionTargetKind kind, long primaryId, ushort generation = 0, long secondaryId = 0)
        {
            Kind = kind;
            PrimaryId = primaryId;
            Generation = generation;
            SecondaryId = secondaryId;
        }

        public static InteractionTargetHandle Player(long characterId) =>
            new InteractionTargetHandle(InteractionTargetKind.PlayerEntity, characterId);

        public bool Equals(InteractionTargetHandle other) =>
            Kind == other.Kind && PrimaryId == other.PrimaryId && Generation == other.Generation && SecondaryId == other.SecondaryId;

        public override bool Equals(object obj) => obj is InteractionTargetHandle other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = (hash * 397) ^ PrimaryId.GetHashCode();
                hash = (hash * 397) ^ Generation;
                hash = (hash * 397) ^ SecondaryId.GetHashCode();
                return hash;
            }
        }
    }
}
