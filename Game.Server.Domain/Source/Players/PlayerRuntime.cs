using System;
using System.Collections.Generic;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Stats;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Server.Domain.Combat;
using Game.Server.Domain.Actions;
using Game.Shared.Resources;
using Game.Shared.Abilities;
using Game.Shared.Identity;
using Game.Shared.Characters;
using Game.Shared.Progression;

namespace Game.Server.Domain.Players
{
    public sealed class PlayerRuntime
    {
        private readonly object _gate = new object();
        private CharacterLocationState _location;
        private long _revision;
        private PlayerDirtyFlags _dirtyFlags;
        private InventoryState _inventory;
        private EquipmentState _equipment;
        private StatsState _stats;
        private CharacterResourcesState _resources;
        private CharacterStatusEffectsState _statusEffects = CharacterStatusEffectsState.Empty;
        private CombatState _combat = new CombatState(0, 0d, false);
        private CharacterActionState _actions = CharacterActionState.Empty;
        private CharacterAppearanceRecipe _appearance;
        private CharacterPresentationPreferences _presentationPreferences;
        private CharacterProgressionState _progression = CharacterProgressionState.CreateDefault();

        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public PlayerSessionId SessionId { get; }
        public CharacterState Character { get; }

        public CharacterLocationState Location
        {
            get { lock (_gate) return _location; }
        }

        public long Revision
        {
            get { lock (_gate) return _revision; }
        }

        public PlayerDirtyFlags DirtyFlags
        {
            get { lock (_gate) return _dirtyFlags; }
        }

        public bool IsDirty
        {
            get { lock (_gate) return _dirtyFlags != PlayerDirtyFlags.None; }
        }

        public bool HasPlayerItemSystems
        {
            get { lock (_gate) return _inventory != null && _equipment != null && _stats != null; }
        }

        public bool HasCharacterResources
        {
            get { lock (_gate) return _resources != null; }
        }

        public CombatState Combat
        {
            get { lock (_gate) return _combat; }
        }

        public StatusEffectMultipliers StatusMultipliers
        {
            get { lock (_gate) return _statusEffects.Multipliers; }
        }

        public CharacterActionState CaptureActionState()
        {
            lock (_gate) return _actions;
        }


        public CharacterAppearanceRecipe CaptureAppearance()
        {
            lock (_gate)
                return _appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
        }

        /// <summary>
        /// Commits a validated authoritative appearance revision. Presentation assets are
        /// intentionally not part of this state; only compact semantic ids/values persist.
        /// </summary>
        public bool TryCommitAppearance(uint expectedAppearanceRevision, CharacterAppearanceRecipe nextAppearance)
        {
            if (nextAppearance == null || !nextAppearance.IsValid(out _) ||
                nextAppearance.revision != expectedAppearanceRevision + 1)
                return false;

            bool becameDirty;
            lock (_gate)
            {
                uint currentRevision = _appearance?.revision ?? 0;
                if (currentRevision != expectedAppearanceRevision)
                    return false;

                _appearance = nextAppearance.Clone();
                becameDirty = MarkDirtyLocked(PlayerDirtyFlags.Appearance);
            }

            if (becameDirty)
                BecameDirty?.Invoke(this);
            return true;
        }


        public CharacterPresentationPreferences CapturePresentationPreferences()
        {
            lock (_gate)
                return _presentationPreferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
        }

        /// <summary>
        /// Commits character-owned movement/presentation preferences. The authoritative
        /// server persists only compact semantic values; clients map them to local
        /// Animator/controller presentation.
        /// </summary>
        public bool TryCommitPresentationPreferences(
            uint expectedPresentationRevision,
            CharacterPresentationPreferences nextPreferences)
        {
            if (nextPreferences == null || !nextPreferences.IsValid(out _) ||
                nextPreferences.revision != expectedPresentationRevision + 1)
                return false;

            bool becameDirty;
            lock (_gate)
            {
                uint currentRevision = _presentationPreferences?.revision ?? 0;
                if (currentRevision != expectedPresentationRevision)
                    return false;

                _presentationPreferences = nextPreferences.Clone();
                becameDirty = MarkDirtyLocked(PlayerDirtyFlags.Presentation);
            }

            if (becameDirty)
                BecameDirty?.Invoke(this);
            return true;
        }

        public CharacterProgressionState CaptureProgressionState()
        {
            lock (_gate) return _progression?.Clone() ?? CharacterProgressionState.CreateDefault();
        }

        public void InitializeProgressionState(CharacterProgressionState progression)
        {
            lock (_gate)
            {
                if (_progression != null && _progression.revision != 0)
                    throw new InvalidOperationException("Player progression is already initialized.");
                _progression = progression?.Clone() ?? CharacterProgressionState.CreateDefault();
                if (_progression.level < 1) _progression.level = 1;
            }
        }

        public bool TryCommitProgression(long expectedRevision, CharacterProgressionState nextState)
        {
            if (nextState == null || nextState.revision != expectedRevision + 1)
                return false;

            bool becameDirty;
            lock (_gate)
            {
                long currentRevision = _progression?.revision ?? 0;
                if (currentRevision != expectedRevision)
                    return false;
                _progression = nextState.Clone();
                becameDirty = MarkDirtyLocked(PlayerDirtyFlags.Progression);
            }
            if (becameDirty) BecameDirty?.Invoke(this);
            return true;
        }

        public event Action<CharacterActionChange> ActionStateChanged;

        public bool TryCommitActionState(
            long expectedRevision,
            CharacterActionState nextState,
            CharacterActionChangeReason reason,
            string abilityDefinitionId = "",
            long castId = 0,
            double deadline = 0d)
        {
            if (nextState == null || nextState.Revision != expectedRevision + 1)
                return false;

            CharacterActionChange change;
            lock (_gate)
            {
                if (_actions.Revision != expectedRevision)
                    return false;
                _actions = nextState;
                change = new CharacterActionChange(
                    CharacterId.Value,
                    nextState.Revision,
                    reason,
                    abilityDefinitionId,
                    castId,
                    deadline);
            }

            ActionStateChanged?.Invoke(change);
            return true;
        }

        public float GetStat(string statId, float fallback = 0f)
        {
            lock (_gate)
            {
                float baseValue = _stats == null ? fallback : _stats.Get(statId, fallback);
                return _statusEffects == null
                    ? baseValue
                    : _statusEffects.StatModifiers.Apply(statId, baseValue);
            }
        }

        public PlayerItemSystemsRuntimeSnapshot CapturePlayerItemSystems()
        {
            lock (_gate)
            {
                if (_inventory == null || _equipment == null || _stats == null)
                    return null;
                return new PlayerItemSystemsRuntimeSnapshot(
                    new InventoryState(_inventory.Capacity, _inventory.Revision, _inventory.CopySlots()),
                    new EquipmentState(_equipment.Revision, _equipment.Snapshot()),
                    new StatsState(_stats.Snapshot()));
            }
        }

        public void InitializePlayerItemSystems(InventoryState inventory, EquipmentState equipment, StatsState stats)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (equipment == null) throw new ArgumentNullException(nameof(equipment));
            if (stats == null) throw new ArgumentNullException(nameof(stats));
            lock (_gate)
            {
                if (_inventory != null || _equipment != null || _stats != null)
                    throw new InvalidOperationException("Player item systems are already initialized.");
                _inventory = inventory;
                _equipment = equipment;
                _stats = stats;
            }
        }

        public bool TryCommitPlayerItemSystems(
            long expectedInventoryRevision,
            long expectedEquipmentRevision,
            InventoryState inventory,
            EquipmentState equipment,
            StatsState stats)
        {
            if (inventory == null || equipment == null || stats == null)
                return false;
            lock (_gate)
            {
                if (_inventory == null || _equipment == null || _stats == null ||
                    _inventory.Revision != expectedInventoryRevision ||
                    _equipment.Revision != expectedEquipmentRevision)
                    return false;

                // Item/equipment persistence can be in flight while combat consumes a
                // round. Inventory/equipment revisions deliberately do not advance per
                // shot, so merge any newer authoritative magazine state before installing
                // the transaction result. This prevents an older item snapshot from
                // rolling a weapon magazine backward in live memory.
                inventory = MergeNewerMagazineStateLocked(inventory);
                equipment = MergeNewerMagazineStateLocked(equipment);

                _inventory = inventory;
                _equipment = equipment;
                _stats = stats;
                return true;
            }
        }

        /// <summary>
        /// Commits one authoritative weapon-instance magazine mutation without advancing
        /// inventory/equipment revisions. Shots use this path so they are memory-cheap and
        /// only mark the magazine persistence stream dirty.
        /// </summary>
        public bool TryCommitItemMagazine(
            ItemInstanceId itemInstanceId,
            long expectedMagazineRevision,
            string loadedAmmoDefinitionId,
            int loadedRounds,
            long nextMagazineRevision)
        {
            if (!itemInstanceId.IsValid ||
                expectedMagazineRevision < 0 ||
                nextMagazineRevision != expectedMagazineRevision + 1 ||
                loadedRounds < 0)
                return false;

            bool becameDirty = false;
            bool changed = false;
            lock (_gate)
            {
                if (_inventory == null || _equipment == null)
                    return false;

                // Firearm shot decrements overwhelmingly target an equipped weapon.
                // Search/copy the small equipment set first so a shot does not clone the
                // player's full inventory on the hot combat path. Inventory is only the
                // fallback for a magazine-bearing item that is not currently equipped.
                EquippedItemState[] equipped = _equipment.Snapshot();
                for (int i = 0; i < equipped.Length; ++i)
                {
                    EquippedItemState entry = equipped[i];
                    if (entry?.Item == null || entry.Item.ItemInstanceId != itemInstanceId)
                        continue;
                    if (entry.Item.MagazineRevision != expectedMagazineRevision)
                        return false;

                    equipped[i] = new EquippedItemState(
                        entry.SlotId,
                        entry.Item.WithMagazine(
                            loadedAmmoDefinitionId,
                            loadedRounds,
                            nextMagazineRevision));
                    _equipment = new EquipmentState(_equipment.Revision, equipped);
                    changed = true;
                    break;
                }

                if (!changed)
                {
                    ItemInstanceState[] slots = _inventory.CopySlots();
                    for (int i = 0; i < slots.Length; ++i)
                    {
                        ItemInstanceState item = slots[i];
                        if (item == null || item.ItemInstanceId != itemInstanceId)
                            continue;
                        if (item.MagazineRevision != expectedMagazineRevision)
                            return false;

                        slots[i] = item.WithMagazine(
                            loadedAmmoDefinitionId,
                            loadedRounds,
                            nextMagazineRevision);
                        _inventory = new InventoryState(_inventory.Capacity, _inventory.Revision, slots);
                        changed = true;
                        break;
                    }
                }

                if (!changed)
                    return false;

                becameDirty = MarkDirtyLocked(PlayerDirtyFlags.Magazine);
            }

            if (becameDirty)
                BecameDirty?.Invoke(this);
            return true;
        }

        private InventoryState MergeNewerMagazineStateLocked(InventoryState incoming)
        {
            ItemInstanceState[] slots = incoming.CopySlots();
            bool changed = false;
            for (int i = 0; i < slots.Length; ++i)
            {
                ItemInstanceState item = slots[i];
                if (item == null)
                    continue;

                ItemInstanceState current = FindItemInstanceLocked(item.ItemInstanceId);
                if (current == null || current.MagazineRevision <= item.MagazineRevision)
                    continue;

                slots[i] = item.WithMagazine(
                    current.LoadedAmmoDefinitionId,
                    current.LoadedRounds,
                    current.MagazineRevision);
                changed = true;
            }

            return changed
                ? new InventoryState(incoming.Capacity, incoming.Revision, slots)
                : incoming;
        }

        private EquipmentState MergeNewerMagazineStateLocked(EquipmentState incoming)
        {
            EquippedItemState[] equipped = incoming.Snapshot();
            bool changed = false;
            for (int i = 0; i < equipped.Length; ++i)
            {
                EquippedItemState entry = equipped[i];
                if (entry?.Item == null)
                    continue;

                ItemInstanceState current = FindItemInstanceLocked(entry.Item.ItemInstanceId);
                if (current == null || current.MagazineRevision <= entry.Item.MagazineRevision)
                    continue;

                equipped[i] = new EquippedItemState(
                    entry.SlotId,
                    entry.Item.WithMagazine(
                        current.LoadedAmmoDefinitionId,
                        current.LoadedRounds,
                        current.MagazineRevision));
                changed = true;
            }

            return changed
                ? new EquipmentState(incoming.Revision, equipped)
                : incoming;
        }

        private ItemInstanceState FindItemInstanceLocked(ItemInstanceId itemInstanceId)
        {
            if (_inventory != null)
            {
                ItemInstanceState[] slots = _inventory.CopySlots();
                for (int i = 0; i < slots.Length; ++i)
                    if (slots[i] != null && slots[i].ItemInstanceId == itemInstanceId)
                        return slots[i];
            }

            if (_equipment != null)
            {
                EquippedItemState[] equipped = _equipment.Snapshot();
                for (int i = 0; i < equipped.Length; ++i)
                    if (equipped[i]?.Item != null && equipped[i].Item.ItemInstanceId == itemInstanceId)
                        return equipped[i].Item;
            }

            return null;
        }


        public CharacterResourcesState CaptureCharacterResources()
        {
            lock (_gate)
            {
                if (_resources == null)
                    return null;
                return new CharacterResourcesState(_resources.Revision, _resources.Snapshot());
            }
        }

        /// <summary>
        /// Allocation-free authoritative read for hot-path resource mechanics. The
        /// returned CharacterResourceState is immutable; callers must still commit
        /// through TryCommitCharacterResource using the returned revision.
        /// </summary>
        public bool TryGetCharacterResource(
            CharacterResourceId id,
            out long resourceRevision,
            out CharacterResourceState resource)
        {
            lock (_gate)
            {
                if (_resources == null || !_resources.TryGet(id, out resource))
                {
                    resourceRevision = 0;
                    resource = default;
                    return false;
                }

                resourceRevision = _resources.Revision;
                return true;
            }
        }

        public void InitializeCharacterResources(CharacterResourcesState resources)
        {
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            lock (_gate)
            {
                if (_resources != null)
                    throw new InvalidOperationException("Character resources are already initialized.");
                _resources = resources;
            }
        }

        public event Action<CharacterResourceChange> ResourceChanged;

        public bool TryCommitCharacterResource(
            long expectedResourceRevision,
            CharacterResourceState nextResource,
            CharacterResourceChangeReason reason)
        {
            if (nextResource.Id == CharacterResourceId.None)
                return false;

            bool becameDirty;
            CharacterResourceChange change;
            lock (_gate)
            {
                if (_resources == null || _resources.Revision != expectedResourceRevision)
                    return false;

                if (!_resources.TryGet(nextResource.Id, out CharacterResourceState previous))
                    return false;
                if (previous.Current == nextResource.Current &&
                    previous.Minimum == nextResource.Minimum &&
                    previous.Maximum == nextResource.Maximum &&
                    previous.Enabled == nextResource.Enabled &&
                    previous.Persistence == nextResource.Persistence)
                    return true;

                if (!_resources.ReplaceInPlace(nextResource))
                    return false;

                // Session/transient resources still emit authoritative change events,
                // but they must not create Backend checkpoint traffic. If a hot content
                // update changes persistence mode, either side being Character is enough
                // to persist the transition/removal on the next checkpoint.
                becameDirty =
                    (previous.Persistence == CharacterResourcePersistenceMode.Character ||
                     nextResource.Persistence == CharacterResourcePersistenceMode.Character) &&
                    MarkDirtyLocked(PlayerDirtyFlags.Resources);
                change = new CharacterResourceChange(
                    CharacterId,
                    nextResource.Id,
                    _resources.Revision,
                    previous.Current,
                    nextResource.Current,
                    nextResource.Minimum,
                    nextResource.Maximum,
                    reason);
            }

            if (becameDirty)
                BecameDirty?.Invoke(this);
            ResourceChanged?.Invoke(change);
            return true;
        }

        /// <summary>
        /// Returns the immutable status-state object. The state never exposes its backing
        /// array, so hot-path readers can inspect revision/aggregates without cloning.
        /// </summary>
        public CharacterStatusEffectsState CaptureStatusEffects()
        {
            lock (_gate) return _statusEffects;
        }

        public event Action<StatusEffectChange> StatusEffectChanged;
        public event Action<CombatActivityChange> CombatActivityChanged;

        public bool TryCommitStatusEffects(
            long expectedRevision,
            CharacterStatusEffectsState nextState,
            StatusEffectChange change)
        {
            if (nextState == null || nextState.Revision != expectedRevision + 1 ||
                change.CharacterId != CharacterId || change.Revision != nextState.Revision)
                return false;

            lock (_gate)
            {
                if (_statusEffects.Revision != expectedRevision)
                    return false;
                _statusEffects = nextState;
            }

            StatusEffectChanged?.Invoke(change);
            return true;
        }

        public bool MarkCombatActivity(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0d)
                return false;

            CombatActivityChange change;
            lock (_gate)
            {
                if (now <= _combat.LastCombatTime)
                    return false;
                _combat = new CombatState(_combat.Revision + 1, now, _combat.Invincible);
                change = new CombatActivityChange(CharacterId, _combat.Revision, now);
            }

            CombatActivityChanged?.Invoke(change);
            return true;
        }

        public bool SetInvincible(bool value)
        {
            lock (_gate)
            {
                if (_combat.Invincible == value)
                    return false;
                _combat = new CombatState(_combat.Revision + 1, _combat.LastCombatTime, value);
                return true;
            }
        }

        public bool TryReplaceCalculatedStats(long expectedEquipmentRevision, StatsState stats)
        {
            if (stats == null) return false;
            lock (_gate)
            {
                if (_equipment == null || _equipment.Revision != expectedEquipmentRevision)
                    return false;
                _stats = stats;
                return true;
            }
        }

        // Fires only on clean -> dirty. Persistence can therefore track changed players
        // without polling every active runtime each checkpoint.
        public event Action<PlayerRuntime> BecameDirty;

        public PlayerRuntime(
            AccountId accountId,
            CharacterId characterId,
            PlayerSessionId sessionId,
            CharacterState character,
            CharacterLocationState location,
            long persistedRevision,
            CharacterAppearanceRecipe appearance = null,
            CharacterPresentationPreferences presentationPreferences = null)
        {
            if (!accountId.IsValid)
                throw new ArgumentException("AccountId is invalid.", nameof(accountId));
            if (!characterId.IsValid)
                throw new ArgumentException("CharacterId is invalid.", nameof(characterId));
            if (!sessionId.IsValid)
                throw new ArgumentException("SessionId is invalid.", nameof(sessionId));
            if (character == null)
                throw new ArgumentNullException(nameof(character));
            if (persistedRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(persistedRevision));

            AccountId = accountId;
            CharacterId = characterId;
            SessionId = sessionId;
            Character = character;
            _location = location;
            _revision = persistedRevision;
            _dirtyFlags = PlayerDirtyFlags.None;
            _appearance = appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            _presentationPreferences = presentationPreferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
        }

        public bool UpdateLocation(CharacterLocationState location)
        {
            bool becameDirty;
            lock (_gate)
            {
                if (_location.Equals(location))
                    return false;

                becameDirty = MarkDirtyLocked(PlayerDirtyFlags.Location);
                _location = location;
            }

            if (becameDirty)
                BecameDirty?.Invoke(this);
            return true;
        }

        public void MarkDirty(PlayerDirtyFlags flags)
        {
            if (flags == PlayerDirtyFlags.None)
                return;

            bool becameDirty;
            lock (_gate)
                becameDirty = MarkDirtyLocked(flags);

            if (becameDirty)
                BecameDirty?.Invoke(this);
        }

        private bool MarkDirtyLocked(PlayerDirtyFlags flags)
        {
            if (_revision == long.MaxValue)
                throw new InvalidOperationException("Player runtime revision exhausted.");

            bool wasClean = _dirtyFlags == PlayerDirtyFlags.None;
            _dirtyFlags |= flags;
            _revision++;
            return wasClean;
        }

        /// <summary>
        /// Legacy/all-flags acknowledgement. Prefer the flagged overload for persistence
        /// streams that only commit a subset of runtime state.
        /// </summary>
        public bool AcknowledgePersisted(long persistedRevision) =>
            AcknowledgePersisted(persistedRevision, (PlayerDirtyFlags)ushort.MaxValue);

        /// <summary>
        /// Clears only the state categories actually committed, and only if no newer
        /// mutation happened after the immutable snapshot was captured.
        /// </summary>
        public bool AcknowledgePersisted(
            long persistedRevision,
            PlayerDirtyFlags persistedFlags)
        {
            if (persistedFlags == PlayerDirtyFlags.None)
                return false;

            lock (_gate)
            {
                if (persistedRevision != _revision)
                    return false;

                _dirtyFlags &= ~persistedFlags;
                return true;
            }
        }

        public PlayerRuntimePersistenceSnapshot CapturePersistenceSnapshot()
        {
            lock (_gate)
            {
                ItemMagazineState[] magazines = null;
                if ((_dirtyFlags & PlayerDirtyFlags.Magazine) != 0 &&
                    _inventory != null &&
                    _equipment != null)
                {
                    var captured = new List<ItemMagazineState>();
                    ItemInstanceState[] slots = _inventory.CopySlots();
                    for (int i = 0; i < slots.Length; ++i)
                    {
                        ItemInstanceState item = slots[i];
                        if (item == null || item.MagazineRevision <= 0)
                            continue;
                        captured.Add(new ItemMagazineState(
                            item.ItemInstanceId,
                            item.LoadedAmmoDefinitionId,
                            item.LoadedRounds,
                            item.MagazineRevision));
                    }

                    EquippedItemState[] equipped = _equipment.Snapshot();
                    for (int i = 0; i < equipped.Length; ++i)
                    {
                        ItemInstanceState item = equipped[i]?.Item;
                        if (item == null || item.MagazineRevision <= 0)
                            continue;
                        captured.Add(new ItemMagazineState(
                            item.ItemInstanceId,
                            item.LoadedAmmoDefinitionId,
                            item.LoadedRounds,
                            item.MagazineRevision));
                    }
                    magazines = captured.ToArray();
                }

                return new PlayerRuntimePersistenceSnapshot(
                    AccountId,
                    CharacterId,
                    Character.Name,
                    _location,
                    _revision,
                    _dirtyFlags,
                    _resources == null
                        ? null
                        : new CharacterResourcesState(_resources.Revision, _resources.Snapshot()),
                    _appearance,
                    _presentationPreferences,
                    magazines,
                    _progression);
            }
        }
    }
}
