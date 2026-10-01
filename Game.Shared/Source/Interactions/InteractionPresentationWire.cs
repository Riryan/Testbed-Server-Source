using System;

namespace Game.Shared.Interactions
{
    /// <summary>
    /// Compact semantic ids carried in the EXISTING PlayerEntitySnapshot.actionId byte
    /// whenever actionState == PlayerEntityActionState.Interacting.
    ///
    /// No new network message or snapshot field is introduced.
    ///
    /// 0       = explicit interaction presentation stop
    /// 1..127  = Harvest presentation ids (passed through when they fit)
    /// 128..   = shared Player<->Player interaction presentation semantics
    /// </summary>
    public static class InteractionPresentationWire
    {
        public const byte Stop = 0;

        public const byte HarvestGeneric = 1;
        public const byte MaximumHarvestPresentationId = 127;

        public const byte GenericInitiator = 128;
        public const byte GenericReceiver = 129;
        public const byte InspectInitiator = 130;
        public const byte InspectReceiver = 131;
        public const byte TradeInitiator = 132;
        public const byte TradeReceiver = 133;
        public const byte PartyInviteInitiator = 134;
        public const byte PartyInviteReceiver = 135;
        public const byte GuildInviteInitiator = 136;
        public const byte GuildInviteReceiver = 137;
        public const byte FeedInitiator = 138;
        public const byte FeedReceiver = 139;
        public const byte HugInitiator = 140;
        public const byte HugReceiver = 141;
        public const byte KissInitiator = 142;
        public const byte KissReceiver = 143;
        public const byte PartnerDanceInitiator = 144;
        public const byte PartnerDanceReceiver = 145;
        public const byte DuelInitiator = 146;
        public const byte DuelReceiver = 147;
        public const byte FriendInitiator = 148;
        public const byte FriendReceiver = 149;
        public const byte CoupleInitiator = 150;
        public const byte CoupleReceiver = 151;
        public const byte TargetedEmoteInitiator = 152;
        public const byte TargetedEmoteReceiver = 153;
        public const byte VampireInitiator = 154;
        public const byte VampireReceiver = 155;
        public const byte HunterInitiator = 156;
        public const byte HunterReceiver = 157;

        public static bool IsHarvest(byte presentationId) =>
            presentationId > 0 && presentationId <= MaximumHarvestPresentationId;

        /// <summary>
        /// Harvest definitions already own a ushort presentationId. V1 deliberately reserves
        /// the lower 127 values for a zero-extra-byte observer presentation path.
        /// Values above 127 fall back to Generic Harvest rather than truncating/colliding.
        /// </summary>
        public static byte EncodeHarvest(ushort presentationId)
        {
            if (presentationId > 0 && presentationId <= MaximumHarvestPresentationId)
                return (byte)presentationId;
            return HarvestGeneric;
        }

        public static byte EncodeInteraction(InteractionActionId actionId, bool receiver)
        {
            switch (actionId)
            {
                case InteractionActionId.Inspect:
                    return receiver ? InspectReceiver : InspectInitiator;

                case InteractionActionId.TradeRequest:
                    return receiver ? TradeReceiver : TradeInitiator;

                case InteractionActionId.PartyInvite:
                    return receiver ? PartyInviteReceiver : PartyInviteInitiator;

                case InteractionActionId.GuildInvite:
                    return receiver ? GuildInviteReceiver : GuildInviteInitiator;

                case InteractionActionId.Feed:
                    return receiver ? FeedReceiver : FeedInitiator;

                case InteractionActionId.Hug:
                    return receiver ? HugReceiver : HugInitiator;

                case InteractionActionId.Kiss:
                    return receiver ? KissReceiver : KissInitiator;

                case InteractionActionId.PartnerDance:
                    return receiver ? PartnerDanceReceiver : PartnerDanceInitiator;

                case InteractionActionId.DuelRequest:
                    return receiver ? DuelReceiver : DuelInitiator;

                case InteractionActionId.AddFriend:
                case InteractionActionId.RemoveFriend:
                case InteractionActionId.GiftFriend:
                    return receiver ? FriendReceiver : FriendInitiator;

                case InteractionActionId.CoupleInvite:
                case InteractionActionId.Divorce:
                    return receiver ? CoupleReceiver : CoupleInitiator;

                case InteractionActionId.OpenEmotes:
                case InteractionActionId.PlayTargetedEmote:
                    return receiver ? TargetedEmoteReceiver : TargetedEmoteInitiator;
            }

            ushort raw = (ushort)actionId;
            if (raw >= 400 && raw <= 427)
                return receiver ? VampireReceiver : VampireInitiator;
            if (raw >= 500 && raw <= 543)
                return receiver ? HunterReceiver : HunterInitiator;

            return receiver ? GenericReceiver : GenericInitiator;
        }

        /// <summary>
        /// Development-only visual hold used by the opt-in Player-model interaction dummy.
        /// Production paired sessions will own their authored duration/session lifetime.
        /// </summary>
        public static double DevelopmentDurationSeconds(InteractionActionId actionId)
        {
            switch (actionId)
            {
                case InteractionActionId.PartnerDance: return 6.0d;
                case InteractionActionId.Feed: return 3.5d;
                case InteractionActionId.Hug: return 3.0d;
                case InteractionActionId.Kiss: return 3.0d;
                case InteractionActionId.DuelRequest: return 2.0d;
                case InteractionActionId.TradeRequest: return 2.0d;
                case InteractionActionId.PartyInvite: return 2.0d;
                case InteractionActionId.GuildInvite: return 2.0d;
                default: return 2.5d;
            }
        }

        public static string DebugLabel(byte presentationId)
        {
            if (presentationId == Stop) return "Stop";
            if (IsHarvest(presentationId)) return $"Harvest {presentationId}";

            switch (presentationId)
            {
                case GenericInitiator: return "Generic Initiator";
                case GenericReceiver: return "Generic Receiver";
                case InspectInitiator: return "Inspect Initiator";
                case InspectReceiver: return "Inspect Receiver";
                case TradeInitiator: return "Trade Initiator";
                case TradeReceiver: return "Trade Receiver";
                case PartyInviteInitiator: return "Party Invite Initiator";
                case PartyInviteReceiver: return "Party Invite Receiver";
                case GuildInviteInitiator: return "Guild Invite Initiator";
                case GuildInviteReceiver: return "Guild Invite Receiver";
                case FeedInitiator: return "Feed Initiator";
                case FeedReceiver: return "Feed Receiver";
                case HugInitiator: return "Hug Initiator";
                case HugReceiver: return "Hug Receiver";
                case KissInitiator: return "Kiss Initiator";
                case KissReceiver: return "Kiss Receiver";
                case PartnerDanceInitiator: return "Partner Dance Initiator";
                case PartnerDanceReceiver: return "Partner Dance Receiver";
                case DuelInitiator: return "Duel Initiator";
                case DuelReceiver: return "Duel Receiver";
                case FriendInitiator: return "Friend Initiator";
                case FriendReceiver: return "Friend Receiver";
                case CoupleInitiator: return "Couple Initiator";
                case CoupleReceiver: return "Couple Receiver";
                case TargetedEmoteInitiator: return "Targeted Emote Initiator";
                case TargetedEmoteReceiver: return "Targeted Emote Receiver";
                case VampireInitiator: return "Vampire Initiator";
                case VampireReceiver: return "Vampire Receiver";
                case HunterInitiator: return "Hunter Initiator";
                case HunterReceiver: return "Hunter Receiver";
                default: return $"Interaction {presentationId}";
            }
        }
    }
}
