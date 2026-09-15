using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Content;
using Game.Server.Application.Items;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Content;
using Game.Shared.Protocol;

namespace Game.Server.Application.Combat
{
    public enum CombatReloadResultCode : byte
    {
        Success = 0,
        InvalidState = 1,
        NotFirearm = 2,
        MagazineFull = 3,
        AmmoUnavailable = 4,
        AmmoMismatch = 5,
        PersistenceRejected = 6,
    }

    public readonly struct CombatReloadResult
    {
        public bool Success { get; }
        public CombatReloadResultCode Code { get; }
        public string Detail { get; }
        public CombatOwnerStateSnapshot State { get; }

        public CombatReloadResult(bool success, CombatReloadResultCode code, string detail, CombatOwnerStateSnapshot state)
        {
            Success = success;
            Code = code;
            Detail = detail ?? string.Empty;
            State = state;
        }
    }

    /// <summary>
    /// Authoritative reload transaction: resolve equipped firearm on the server, consume
    /// matching inventory ammunition transactionally, then update the authoritative weapon-instance magazine.
    /// Client-supplied ammo definition is only a preference and is never trusted as payload.
    /// </summary>
    public sealed class CombatReloadService
    {
        private readonly GameplayContentCatalog _content;
        private readonly PlayerItemService _items;
        private readonly CombatLoadoutService _loadout;

        public CombatReloadService(GameplayContentCatalog content, PlayerItemService items, CombatLoadoutService loadout)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _loadout = loadout ?? throw new ArgumentNullException(nameof(loadout));
        }

        public async Task<CombatReloadResult> ReloadAsync(
            PlayerRuntime runtime,
            string preferredAmmoDefinitionId,
            double now,
            CancellationToken cancellationToken)
        {
            CombatOwnerStateSnapshot state = _loadout.Capture(runtime, now);
            if (runtime == null || !state.Available)
                return new CombatReloadResult(false, CombatReloadResultCode.InvalidState, "combat loadout is unavailable", state);
            if (state.Mode != BasicAttackMode.Firearm || string.IsNullOrWhiteSpace(state.WeaponDefinitionId) ||
                !_content.TryGetItem(state.WeaponDefinitionId, out ItemDefinition weapon))
                return new CombatReloadResult(false, CombatReloadResultCode.NotFirearm, "equipped main hand is not a firearm", state);
            if (state.LoadedRounds >= state.MagazineCapacity)
                return new CombatReloadResult(false, CombatReloadResultCode.MagazineFull, "magazine is already full", state);

            string preferred = (preferredAmmoDefinitionId ?? string.Empty).Trim();
            if (state.LoadedRounds > 0 &&
                !string.IsNullOrWhiteSpace(preferred) &&
                !string.Equals(preferred, state.LoadedAmmoDefinitionId, StringComparison.Ordinal))
            {
                return new CombatReloadResult(false, CombatReloadResultCode.AmmoMismatch, "cannot mix ammunition types in a partially loaded magazine", state);
            }

            string requiredDefinition = state.LoadedRounds > 0 ? state.LoadedAmmoDefinitionId : preferred;
            int needed = Math.Max(0, state.MagazineCapacity - state.LoadedRounds);
            AmmoReloadInventoryResult consumed = await _items.ConsumeAmmoForReloadAsync(
                runtime,
                weapon.ammoFamily,
                requiredDefinition,
                needed,
                cancellationToken).ConfigureAwait(false);

            if (!consumed.Success)
            {
                CombatReloadResultCode code = consumed.Items.Status == PlayerItemOperationStatus.ItemUnavailable
                    ? CombatReloadResultCode.AmmoUnavailable
                    : CombatReloadResultCode.PersistenceRejected;
                string detail = string.IsNullOrWhiteSpace(consumed.Items.Error)
                    ? "reload failed"
                    : consumed.Items.Error;
                return new CombatReloadResult(false, code, detail, _loadout.Capture(runtime, now));
            }

            CombatOwnerStateSnapshot next = _loadout.ApplyReload(
                runtime,
                consumed.AmmoDefinitionId,
                state.LoadedRounds + consumed.ConsumedRounds,
                now);
            return new CombatReloadResult(
                true,
                CombatReloadResultCode.Success,
                $"reloaded {consumed.ConsumedRounds} round(s)",
                next);
        }
    }
}
