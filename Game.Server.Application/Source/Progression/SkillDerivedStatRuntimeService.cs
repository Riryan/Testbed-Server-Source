using System;
using Game.Server.Application.Content;
using Game.Server.Application.Items;
using Game.Server.Application.Resources;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Shared.Protocol;
using Game.Shared.Resources;

namespace Game.Server.Application.Progression
{
    /// <summary>
    /// Sparse event-driven bridge from authoritative skill progression/equipment changes
    /// into hidden attributes and the already-existing resource maximum replication path.
    /// No polling, no new scheduler, and no new network message.
    /// </summary>
    public sealed class SkillDerivedStatRuntimeService : IDisposable
    {
        private readonly GameplayContentCatalog _content;
        private readonly ProgressionService _progression;
        private readonly CharacterResourceService _resources;
        private readonly PlayerItemService _items;

        public SkillDerivedStatRuntimeService(
            GameplayContentCatalog content,
            ProgressionService progression,
            CharacterResourceService resources,
            PlayerItemService items)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _items = items ?? throw new ArgumentNullException(nameof(items));

            _progression.Changed += OnProgressionChanged;
            _items.Changed += OnItemsChanged;
        }

        public void Dispose()
        {
            _progression.Changed -= OnProgressionChanged;
            _items.Changed -= OnItemsChanged;
        }

        private void OnProgressionChanged(PlayerRuntime runtime, ProgressionDelta delta)
        {
            if (delta.Kind != ProgressionDeltaKind.Track)
                return;

            Rebuild(runtime);
        }

        private void OnItemsChanged(
            PlayerRuntime runtime,
            PlayerItemsSnapshot previous,
            PlayerItemsSnapshot current)
        {
            // PlayerItemService deliberately rebuilds StatsState during ordinary item
            // commits as well as equipment commits. Re-apply the skill layer after every
            // item-system commit so an inventory-only operation cannot erase it.
            Rebuild(runtime);
        }

        private void Rebuild(PlayerRuntime runtime)
        {
            if (runtime == null)
                return;

            // Concurrent item/progression work can race this sparse rebuild. Retry against
            // a fresh immutable snapshot instead of installing stats over a newer equipment revision.
            for (int attempt = 0; attempt < 4; ++attempt)
            {
                PlayerItemSystemsRuntimeSnapshot itemState = runtime.CapturePlayerItemSystems();
                if (itemState == null)
                    return;

                StatsState equipmentStats = EquipmentStatCalculator.Calculate(_content, itemState.Equipment);
                StatsState derived = SkillDerivedStatCalculator.Apply(
                    _content,
                    runtime.CaptureProgressionState(),
                    equipmentStats);

                if (!runtime.TryReplaceCalculatedStats(itemState.Equipment.Revision, derived))
                    continue;

                _resources.RecalculateMaximums(
                    runtime,
                    CharacterResourceChangeReason.Administrative);
                return;
            }
        }
    }
}
