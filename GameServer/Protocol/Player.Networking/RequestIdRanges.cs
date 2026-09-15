using System;

namespace Player.Networking
{
    /// <summary>
    /// One reserved range in the client-to-GameServer request-id namespace.
    /// Request ids describe protocol operations, not content definitions.
    /// </summary>
    public readonly struct RequestIdRange
    {
        public readonly ushort Start;
        public readonly ushort End;
        public readonly string Name;

        public RequestIdRange(ushort start, ushort end, string name)
        {
            if (end < start)
                throw new ArgumentOutOfRangeException(nameof(end));

            Start = start;
            End = end;
            Name = name ?? string.Empty;
        }

        public bool Contains(ushort requestId) => requestId >= Start && requestId <= End;
        public override string ToString() => $"{Start}-{End} {Name}";
    }

    /// <summary>
    /// Canonical allocation policy for client-to-GameServer request ids.
    ///
    /// Important:
    /// - These are request operation ids, not item/quest/effect/interaction definition ids.
    /// - Existing ids are grandfathered and must not be renumbered for cosmetic cleanup.
    /// - New interaction/social/adult protocol operations belong in 1300-1399.
    /// - Content definitions use their own ids and do not consume request ids.
    /// - Top-level LiteNetLib message ids are a separate namespace.
    /// </summary>
    public static class RequestIdRanges
    {
        public static class Core
        {
            public const ushort Start = 0;
            public const ushort End = 99;
        }

        public static class CharacterAccount
        {
            public const ushort Start = 100;
            public const ushort End = 199;
        }

        public static class InventoryEquipmentWorldItems
        {
            public const ushort Start = 200;
            public const ushort End = 299;
        }

        public static class CharacterVitals
        {
            public const ushort Start = 300;
            public const ushort End = 349;
        }

        public static class Progression
        {
            public const ushort Start = 350;
            public const ushort End = 399;
        }

        public static class StatusEffects
        {
            public const ushort Start = 400;
            public const ushort End = 449;
        }

        public static class SystemReserved
        {
            public const ushort Start = 450;
            public const ushort End = 499;
        }

        public static class CharacterActions
        {
            public const ushort Start = 500;
            public const ushort End = 599;
        }

        public static class Staff
        {
            public const ushort Start = 600;
            public const ushort End = 649;
        }

        public static class WorldSession
        {
            public const ushort Start = 650;
            public const ushort End = 699;
        }

        public static class PartyFriends
        {
            public const ushort Start = 700;
            public const ushort End = 749;
        }

        public static class GuildCommunity
        {
            public const ushort Start = 750;
            public const ushort End = 849;
        }

        public static class Economy
        {
            public const ushort Start = 850;
            public const ushort End = 949;
        }

        public static class Quests
        {
            public const ushort Start = 950;
            public const ushort End = 999;
        }

        public static class Crafting
        {
            public const ushort Start = 1000;
            public const ushort End = 1049;
        }

        public static class Vehicles
        {
            public const ushort Start = 1050;
            public const ushort End = 1099;
        }

        public static class Housing
        {
            public const ushort Start = 1100;
            public const ushort End = 1249;
        }

        public static class NpcWorld
        {
            public const ushort Start = 1250;
            public const ushort End = 1299;
        }

        public static class Interactions
        {
            public const ushort Start = 1300;
            public const ushort End = 1399;
        }

        public static class Relationships
        {
            public const ushort Start = 1400;
            public const ushort End = 1449;
        }

        public static class Companions
        {
            public const ushort Start = 1450;
            public const ushort End = 1499;
        }

        public static class Activities
        {
            public const ushort Start = 1500;
            public const ushort End = 1549;
        }

        public static class FutureFirstParty
        {
            public const ushort Start = 1550;
            public const ushort End = 1999;
        }

        public static class FirstPartyExpansion
        {
            public const ushort Start = 2000;
            public const ushort End = 4095;
        }

        public static class Plugins
        {
            public const ushort Start = 4096;
            public const ushort End = 8191;
        }

        public static class ScriptsAndMods
        {
            public const ushort Start = 8192;
            public const ushort End = 16383;
        }

        public static class FutureReserved
        {
            public const ushort Start = 16384;
            public const ushort End = ushort.MaxValue;
        }

        /// <summary>
        /// Ordered, complete coverage of the ushort request-id namespace.
        /// Intended for tooling, validation, diagnostics, and developer UI.
        /// </summary>
        public static readonly RequestIdRange[] All =
        {
            new RequestIdRange(Core.Start, Core.End, "Core networking/session"),
            new RequestIdRange(CharacterAccount.Start, CharacterAccount.End, "Character/account session"),
            new RequestIdRange(InventoryEquipmentWorldItems.Start, InventoryEquipmentWorldItems.End, "Inventory/equipment/world items"),
            new RequestIdRange(CharacterVitals.Start, CharacterVitals.End, "Character vitals/resources"),
            new RequestIdRange(Progression.Start, Progression.End, "Progression/stats/talents"),
            new RequestIdRange(StatusEffects.Start, StatusEffects.End, "Status/effects"),
            new RequestIdRange(SystemReserved.Start, SystemReserved.End, "System expansion reserve"),
            new RequestIdRange(CharacterActions.Start, CharacterActions.End, "Combat/abilities/character actions"),
            new RequestIdRange(Staff.Start, Staff.End, "Staff/admin"),
            new RequestIdRange(WorldSession.Start, WorldSession.End, "Instance/matchmaking/world-session"),
            new RequestIdRange(PartyFriends.Start, PartyFriends.End, "Party/friends"),
            new RequestIdRange(GuildCommunity.Start, GuildCommunity.End, "Guild/community"),
            new RequestIdRange(Economy.Start, Economy.End, "Economy/trade/vendor/bank/mail/auction"),
            new RequestIdRange(Quests.Start, Quests.End, "Quests/journal"),
            new RequestIdRange(Crafting.Start, Crafting.End, "Crafting/gathering/orders"),
            new RequestIdRange(Vehicles.Start, Vehicles.End, "Vehicles/transport"),
            new RequestIdRange(Housing.Start, Housing.End, "Housing/property/neighborhood"),
            new RequestIdRange(NpcWorld.Start, NpcWorld.End, "NPC/world gameplay"),
            new RequestIdRange(Interactions.Start, Interactions.End, "Interaction/social/adult interaction protocol"),
            new RequestIdRange(Relationships.Start, Relationships.End, "Relationships/household/family"),
            new RequestIdRange(Companions.Start, Companions.End, "Pets/companions"),
            new RequestIdRange(Activities.Start, Activities.End, "Activities/venues/events"),
            new RequestIdRange(FutureFirstParty.Start, FutureFirstParty.End, "Future first-party systems"),
            new RequestIdRange(FirstPartyExpansion.Start, FirstPartyExpansion.End, "First-party expansion reserve"),
            new RequestIdRange(Plugins.Start, Plugins.End, "Extension/plugin API"),
            new RequestIdRange(ScriptsAndMods.Start, ScriptsAndMods.End, "Script/mod reserve"),
            new RequestIdRange(FutureReserved.Start, FutureReserved.End, "Future reserved"),
        };

        public static RequestIdRange Find(ushort requestId)
        {
            for (int i = 0; i < All.Length; ++i)
            {
                if (All[i].Contains(requestId))
                    return All[i];
            }

            throw new InvalidOperationException($"Request id {requestId} is outside the configured request-id namespace.");
        }
    }
}
