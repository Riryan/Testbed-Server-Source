using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Content;
using Game.Server.Application.Interactions;
using Game.Server.Application.Items;
using Game.Server.Application.Progression;
using Game.Server.Application.Rewards;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Protocol;

namespace Game.Server.Application.Crafting
{
    public readonly struct CraftingResult
    {
        public bool Success { get; }
        public string Error { get; }
        public PlayerItemsSnapshot Items { get; }
        public CraftingResult(bool success, string error, PlayerItemsSnapshot items)
        { Success = success; Error = error ?? string.Empty; Items = items; }
    }

    /// <summary>
    /// Validates recipe knowledge, predicates, station and tool requirements on GameServer,
    /// then delegates the ingredient/output ownership transition to one atomic Gateway commit.
    /// </summary>
    public sealed class CraftingService
    {
        private readonly GameplayContentCatalog _content;
        private readonly ProgressionService _progression;
        private readonly PlayerItemService _items;
        private readonly RewardService _rewards;
        private readonly WorldInteractableService _world;

        public CraftingService(
            GameplayContentCatalog content,
            ProgressionService progression,
            PlayerItemService items,
            RewardService rewards,
            WorldInteractableService world)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        public async Task<CraftingResult> CraftAsync(
            PlayerRuntime runtime,
            long stationStableId,
            ushort recipeDataId,
            CancellationToken cancellationToken)
        {
            if (runtime == null) return new CraftingResult(false, "player runtime is unavailable", null);
            if (!_content.TryGetRecipe(recipeDataId, out RecipeDefinition recipe))
                return new CraftingResult(false, "recipe is unavailable", _items.GetSnapshot(runtime));
            if (!_progression.IsRecipeKnown(runtime, recipeDataId))
                return new CraftingResult(false, "recipe is not known", _items.GetSnapshot(runtime));
            if (!_progression.MeetsPredicates(runtime, recipe.unlockPredicates))
                return new CraftingResult(false, "recipe unlock requirements are not met", _items.GetSnapshot(runtime));
            if (!_world.TryResolveCraftingStation(runtime, stationStableId, out _, out CraftingStationDefinition station, out string stationError))
                return new CraftingResult(false, stationError, _items.GetSnapshot(runtime));
            if (!string.IsNullOrWhiteSpace(recipe.stationTag) && !HasTag(station.tags, recipe.stationTag))
                return new CraftingResult(false, "recipe cannot be crafted at this station", _items.GetSnapshot(runtime));
            if (!string.IsNullOrWhiteSpace(recipe.toolTag) && !_items.HasItemTag(runtime, recipe.toolTag))
                return new CraftingResult(false, "required crafting tool is unavailable", _items.GetSnapshot(runtime));

            PlayerItemOperationResult transaction = await _items.CraftRecipeItemsAsync(runtime, recipeDataId, cancellationToken).ConfigureAwait(false);
            if (!transaction.Success) return new CraftingResult(false, transaction.Error, transaction.Snapshot);

            _rewards.GrantSoft(runtime, recipe.rewards);
            return new CraftingResult(true, string.Empty, transaction.Snapshot);
        }

        private static bool HasTag(string[] tags, string expected)
        {
            tags = tags ?? Array.Empty<string>();
            for (int i = 0; i < tags.Length; ++i)
                if (string.Equals(tags[i], expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
