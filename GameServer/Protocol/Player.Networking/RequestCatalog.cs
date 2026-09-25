using System;
using System.Collections.Generic;

namespace Player.Networking
{
    public static class CorePlayerRequestTypes
    {
        public const ushort EnterGame = 0;
        public const ushort ClientReady = 1;
    }

    public readonly struct PlayerRequestDescriptor
    {
        public ushort Id { get; }
        public string Name { get; }
        public RequestIdRange Range { get; }

        public PlayerRequestDescriptor(ushort id, string name)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? $"Request[{id}]" : name;
            Range = RequestIdRanges.Find(id);
        }

        public override string ToString() => $"{Id} {Name} ({Range.Name})";
    }

    public static class PlayerRequestCatalog
    {
        private static readonly Dictionary<ushort, PlayerRequestDescriptor> ById = Build();

        public static IReadOnlyCollection<PlayerRequestDescriptor> All => ById.Values;

        public static bool TryGet(ushort requestId, out PlayerRequestDescriptor descriptor) =>
            ById.TryGetValue(requestId, out descriptor);

        public static PlayerRequestDescriptor Get(ushort requestId)
        {
            if (!ById.TryGetValue(requestId, out PlayerRequestDescriptor descriptor))
                throw new InvalidOperationException($"Request id {requestId} is not present in the implemented protocol catalog.");
            return descriptor;
        }

        private static Dictionary<ushort, PlayerRequestDescriptor> Build()
        {
            var result = new Dictionary<ushort, PlayerRequestDescriptor>();

            Add(result, CorePlayerRequestTypes.EnterGame, "Core.EnterGame");
            Add(result, CorePlayerRequestTypes.ClientReady, "Core.ClientReady");

            Add(result, CharacterSessionRequestTypes.AuthenticateAdmission, "Character.AuthenticateAdmission");
            Add(result, CharacterSessionRequestTypes.CharacterList, "Character.List");
            Add(result, CharacterSessionRequestTypes.CreateCharacter, "Character.Create");
            Add(result, CharacterSessionRequestTypes.DeleteCharacter, "Character.Delete");
            Add(result, CharacterSessionRequestTypes.EnterCharacter, "Character.Enter");

            Add(result, PlayerItemRequestTypes.Snapshot, "Items.Snapshot");
            Add(result, PlayerItemRequestTypes.MoveInventory, "Items.MoveInventory");
            Add(result, PlayerItemRequestTypes.Equip, "Items.Equip");
            Add(result, PlayerItemRequestTypes.Unequip, "Items.Unequip");
            Add(result, PlayerItemRequestTypes.Use, "Items.Use");
            Add(result, PlayerItemRequestTypes.Drop, "Items.Drop");
            Add(result, WorldItemRequestTypes.Snapshot, "WorldItems.Snapshot");
            Add(result, WorldItemRequestTypes.Loot, "WorldItems.Loot");

            Add(result, PlayerResourceRequestTypes.Snapshot, "Resources.Snapshot");
            Add(result, ProgressionRequestTypes.Snapshot, "Progression.Snapshot");
            Add(result, PlayerStatusEffectRequestTypes.Snapshot, "StatusEffects.Snapshot");
            Add(result, GameplaySettingsRequestTypes.Snapshot, "System.GameplaySettingsSnapshot");

            Add(result, PlayerGameplayActionRequestTypes.BasicAttack, "Gameplay.BasicAttack");
            Add(result, PlayerGameplayActionRequestTypes.BeginAbility, "Gameplay.BeginAbility");
            Add(result, PlayerGameplayActionRequestTypes.CancelAbility, "Gameplay.CancelAbility");
            Add(result, PlayerGameplayActionRequestTypes.Interaction, "Gameplay.Interaction");
            Add(result, PlayerGameplayActionRequestTypes.Respawn, "Gameplay.Respawn");
            Add(result, PlayerGameplayActionRequestTypes.InteractionMenu, "Interactions.Menu");
            Add(result, PlayerGameplayActionRequestTypes.ContextInteraction, "Interactions.Context");
            Add(result, PlayerGameplayActionRequestTypes.WorldLootOpen, "Interactions.WorldLootOpen");
            Add(result, PlayerGameplayActionRequestTypes.WorldLootTake, "Interactions.WorldLootTake");
            Add(result, PlayerGameplayActionRequestTypes.WorldLootTakeAll, "Interactions.WorldLootTakeAll");
            Add(result, PlayerGameplayActionRequestTypes.Reload, "Gameplay.Reload");
            Add(result, PlayerGameplayActionRequestTypes.CombatOwnerState, "Gameplay.CombatOwnerState");

            Add(result, CraftingRequestTypes.Craft, "Crafting.Craft");

            Add(result, FriendRequestTypes.Snapshot, "Friends.Snapshot");
            Add(result, FriendRequestTypes.Action, "Friends.Action");
            Add(result, FriendRequestTypes.OwnerLiveInterest, "Social.OwnerLiveInterest");
            Add(result, GuildRequestTypes.Snapshot, "Guild.Snapshot");
            Add(result, GuildRequestTypes.Action, "Guild.Action");
            Add(result, EconomyRequestTypes.TradeAction, "Trade.Action");
            Add(result, EconomyRequestTypes.TradeSnapshot, "Trade.Snapshot");
            Add(result, EconomyRequestTypes.StorageSnapshot, "Storage.Snapshot");
            Add(result, EconomyRequestTypes.StorageTransfer, "Storage.Transfer");

            Add(result, StaffRequestTypes.Status, "Staff.Status");
            Add(result, StaffRequestTypes.SetVisibility, "Staff.SetVisibility");
            Add(result, StaffRequestTypes.Spectate, "Staff.Spectate");
            Add(result, StaffRequestTypes.StopSpectate, "Staff.StopSpectate");

            return result;
        }

        private static void Add(
            Dictionary<ushort, PlayerRequestDescriptor> catalog,
            ushort requestId,
            string name)
        {
            var descriptor = new PlayerRequestDescriptor(requestId, name);
            if (!catalog.TryAdd(requestId, descriptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate player request id {requestId} while registering '{name}'.");
            }
        }
    }
}
