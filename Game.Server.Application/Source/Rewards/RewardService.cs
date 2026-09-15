using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Items;
using Game.Server.Application.Progression;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Protocol;

namespace Game.Server.Application.Rewards
{
    public readonly struct RewardGrantResult
    {
        public bool Success { get; }
        public PlayerItemOperationStatus Status { get; }
        public string Error { get; }
        public PlayerItemsSnapshot Items { get; }
        public RewardGrantResult(bool success, PlayerItemOperationStatus status, string error, PlayerItemsSnapshot items)
        { Success = success; Status = status; Error = error ?? string.Empty; Items = items; }
    }

    /// <summary>
    /// Canonical reward gateway. Durable item ownership changes pass through one atomic
    /// item-bundle transaction; progression/reputation/knowledge remain soft coalesced state.
    /// </summary>
    public sealed class RewardService
    {
        private readonly PlayerItemService _items;
        private readonly ProgressionService _progression;

        public RewardService(PlayerItemService items, ProgressionService progression)
        {
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
        }

        public async Task<RewardGrantResult> GrantAsync(
            PlayerRuntime runtime,
            RewardBundleDefinition reward,
            CancellationToken cancellationToken)
        {
            if (runtime == null) return new RewardGrantResult(false, PlayerItemOperationStatus.SessionUnavailable, "player runtime is unavailable", null);
            reward = reward ?? new RewardBundleDefinition();
            RewardItemDefinition[] itemRewards = reward.items ?? Array.Empty<RewardItemDefinition>();
            PlayerItemsSnapshot snapshot = _items.GetSnapshot(runtime);
            if (itemRewards.Length > 0)
            {
                PlayerItemOperationResult items = await _items.GrantBundleAsync(runtime, itemRewards, cancellationToken).ConfigureAwait(false);
                if (!items.Success) return new RewardGrantResult(false, items.Status, items.Error, items.Snapshot);
                snapshot = items.Snapshot;
            }
            GrantSoft(runtime, reward);
            return new RewardGrantResult(true, PlayerItemOperationStatus.Success, string.Empty, snapshot);
        }

        public void GrantSoft(PlayerRuntime runtime, RewardBundleDefinition reward)
        {
            if (runtime == null || reward == null) return;
            if (reward.experience > 0) _progression.GainExperience(runtime, reward.experience);

            RewardTrackDefinition[] tracks = reward.tracks ?? Array.Empty<RewardTrackDefinition>();
            for (int i = 0; i < tracks.Length; ++i)
            {
                RewardTrackDefinition value = tracks[i];
                if (value != null && value.trackDataId != 0 && value.amount > 0)
                    _progression.GainTrack(runtime, value.trackDataId, value.amount);
            }

            RewardReputationDefinition[] reputation = reward.reputation ?? Array.Empty<RewardReputationDefinition>();
            for (int i = 0; i < reputation.Length; ++i)
            {
                RewardReputationDefinition value = reputation[i];
                if (value != null && value.factionDataId != 0 && value.amount != 0)
                    _progression.AdjustReputation(runtime, value.factionDataId, value.amount);
            }

            ushort[] recipes = reward.knownRecipeDataIds ?? Array.Empty<ushort>();
            for (int i = 0; i < recipes.Length; ++i)
                if (recipes[i] != 0) _progression.LearnRecipe(runtime, recipes[i]);
        }
    }
}
