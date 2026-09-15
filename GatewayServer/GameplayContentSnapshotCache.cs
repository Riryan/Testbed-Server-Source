using System.Runtime.CompilerServices;
using Game.Shared.Content;

namespace Game.BackendServer;

/// <summary>
/// Process-local validation/index cache for immutable published gameplay-content snapshots.
/// ContentDefinitionStore already publishes snapshots by replacement rather than mutation,
/// so a snapshot reference can be validated/indexed once and safely reused by hot backend
/// transaction paths for the life of that revision.
/// </summary>
internal static class GameplayContentSnapshotCache
{
    private sealed class SnapshotIndex
    {
        public readonly bool IsValid;
        public readonly string Error;
        public readonly Dictionary<string, ItemDefinition> ItemsByDefinitionId;
        public readonly Dictionary<ushort, ItemDefinition> ItemsByDataId;
        public readonly Dictionary<ushort, RecipeDefinition> RecipesByDataId;

        public SnapshotIndex(GameplayContentSnapshot snapshot)
        {
            IsValid = GameplayContentValidation.TryValidate(snapshot, out string error);
            Error = error ?? string.Empty;
            ItemsByDefinitionId = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            ItemsByDataId = new Dictionary<ushort, ItemDefinition>();
            RecipesByDataId = new Dictionary<ushort, RecipeDefinition>();

            if (!IsValid || snapshot == null)
                return;

            ItemDefinition[] items = snapshot.items ?? Array.Empty<ItemDefinition>();
            for (int i = 0; i < items.Length; ++i)
            {
                ItemDefinition item = items[i];
                if (item == null)
                    continue;
                ItemsByDefinitionId[item.definitionId] = item;
                ItemsByDataId[item.dataId] = item;
            }

            RecipeDefinition[] recipes = snapshot.recipes ?? Array.Empty<RecipeDefinition>();
            for (int i = 0; i < recipes.Length; ++i)
            {
                RecipeDefinition recipe = recipes[i];
                if (recipe != null)
                    RecipesByDataId[recipe.dataId] = recipe;
            }
        }
    }

    private static readonly ConditionalWeakTable<GameplayContentSnapshot, SnapshotIndex> Cache = new();

    public static bool TryValidate(GameplayContentSnapshot snapshot, out string error)
    {
        if (snapshot == null)
        {
            error = "content snapshot is missing";
            return false;
        }

        SnapshotIndex index = Cache.GetValue(snapshot, static value => new SnapshotIndex(value));
        error = index.Error;
        return index.IsValid;
    }

    public static void Warm(GameplayContentSnapshot snapshot)
    {
        if (!TryValidate(snapshot, out string error))
            throw new InvalidOperationException("Gameplay content is invalid: " + error);
    }

    public static ItemDefinition FindItem(GameplayContentSnapshot snapshot, string definitionId)
    {
        if (snapshot == null || string.IsNullOrEmpty(definitionId))
            return null;

        SnapshotIndex index = Cache.GetValue(snapshot, static value => new SnapshotIndex(value));
        return index.IsValid && index.ItemsByDefinitionId.TryGetValue(definitionId, out ItemDefinition item)
            ? item
            : null;
    }

    public static ItemDefinition FindItem(GameplayContentSnapshot snapshot, ushort dataId)
    {
        if (snapshot == null || dataId == 0)
            return null;

        SnapshotIndex index = Cache.GetValue(snapshot, static value => new SnapshotIndex(value));
        return index.IsValid && index.ItemsByDataId.TryGetValue(dataId, out ItemDefinition item)
            ? item
            : null;
    }

    public static RecipeDefinition FindRecipe(GameplayContentSnapshot snapshot, ushort dataId)
    {
        if (snapshot == null || dataId == 0)
            return null;

        SnapshotIndex index = Cache.GetValue(snapshot, static value => new SnapshotIndex(value));
        return index.IsValid && index.RecipesByDataId.TryGetValue(dataId, out RecipeDefinition recipe)
            ? recipe
            : null;
    }
}
