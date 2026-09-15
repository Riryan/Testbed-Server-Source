using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Application.Progression;
using Game.Server.Application.World;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.World;

namespace Game.Server.Application.Interactions
{
    public enum WorldInteractablePhase : byte
    {
        Idle = 0,
        Enter = 1,
        Active = 2,
        Exit = 3,
    }

    public readonly struct WorldInteractableKey : IEquatable<WorldInteractableKey>
    {
        public string MapId { get; }
        public string InstanceId { get; }
        public long StableId { get; }

        public WorldInteractableKey(string mapId, string instanceId, long stableId)
        {
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            StableId = stableId;
        }

        public bool Equals(WorldInteractableKey other) =>
            StableId == other.StableId &&
            string.Equals(MapId, other.MapId, StringComparison.Ordinal) &&
            string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is WorldInteractableKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(MapId);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(InstanceId);
                hash = (hash * 397) ^ StableId.GetHashCode();
                return hash;
            }
        }
    }

    public sealed class WorldLootRuntimeEntry
    {
        public int EntryIndex { get; }
        public LootTableEntryDefinition Definition { get; }
        public int Quantity { get; internal set; }

        public WorldLootRuntimeEntry(int entryIndex, LootTableEntryDefinition definition, int quantity)
        {
            EntryIndex = entryIndex;
            Definition = definition;
            Quantity = Math.Max(0, quantity);
        }
    }

    public sealed class WorldInteractableRuntime
    {
        private readonly Dictionary<string, long> _occupantBySlot = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _slotByActor = new Dictionary<long, string>();
        private readonly List<WorldLootRuntimeEntry> _loot = new List<WorldLootRuntimeEntry>();
        private readonly Dictionary<int, int> _reservedLoot = new Dictionary<int, int>();

        public WorldInteractableKey Key { get; }
        public ServerWorldInteractableDefinition Definition { get; }
        public bool Enabled { get; set; }
        public bool Open { get; set; }
        public bool Depleted { get; private set; }
        public int HarvestCharges { get; private set; }
        public double HarvestReadyAt { get; private set; }
        // Transient depletion-cycle token. Scheduled harvest respawns capture this value so a
        // stale/duplicate callback from an older cycle can never revive a later depletion.
        // This is runtime-only state and intentionally resets on GameServer restart.
        public uint HarvestGeneration { get; private set; } = 1;
        public long Revision { get; private set; } = 1;
        public WorldInteractablePhase Phase { get; set; }
        public long ActiveSessionId { get; set; }

        public WorldInteractableRuntime(WorldInteractableKey key, ServerWorldInteractableDefinition definition)
        {
            if (key.StableId <= 0) throw new ArgumentException("World object key is invalid.", nameof(key));
            Key = key;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Enabled = definition.enabledByDefault;
        }

        public void Touch() => Revision = Revision == long.MaxValue ? 1 : Revision + 1;

        internal void InitializeHarvest(int charges)
        {
            if (Definition.kind != ServerWorldInteractableKind.HarvestNode) return;
            HarvestCharges = Math.Max(0, charges);
            HarvestReadyAt = 0d;
            HarvestGeneration = 1;
            Depleted = HarvestCharges <= 0;
        }

        internal bool TryConsumeHarvestCharge(out bool depleted)
        {
            depleted = Depleted;
            if (Definition.kind != ServerWorldInteractableKind.HarvestNode || Depleted || HarvestCharges <= 0)
                return false;

            HarvestCharges--;
            depleted = HarvestCharges <= 0;
            Depleted = depleted;
            if (depleted)
                AdvanceHarvestGeneration();
            else
                HarvestReadyAt = 0d;
            return true;
        }

        internal void SetHarvestRespawnAt(double readyAt)
        {
            if (Definition.kind != ServerWorldInteractableKind.HarvestNode || !Depleted) return;
            HarvestReadyAt = double.IsNaN(readyAt) || double.IsInfinity(readyAt) ? 0d : Math.Max(0d, readyAt);
        }

        internal void RespawnHarvest(int charges)
        {
            if (Definition.kind != ServerWorldInteractableKind.HarvestNode) return;
            AdvanceHarvestGeneration();
            HarvestCharges = Math.Max(0, charges);
            HarvestReadyAt = 0d;
            Depleted = HarvestCharges <= 0;
        }

        private void AdvanceHarvestGeneration()
        {
            HarvestGeneration = HarvestGeneration == uint.MaxValue ? 1u : HarvestGeneration + 1u;
        }

        internal void InitializeLoot(WorldLootRuntimeEntry[] entries)
        {
            _loot.Clear();
            _reservedLoot.Clear();
            entries ??= Array.Empty<WorldLootRuntimeEntry>();
            for (int i = 0; i < entries.Length; ++i)
                if (entries[i] != null && entries[i].Quantity > 0) _loot.Add(entries[i]);
            RefreshDepleted();
        }

        public WorldLootRuntimeEntry[] SnapshotLoot()
        {
            var result = new WorldLootRuntimeEntry[_loot.Count];
            for (int i = 0; i < _loot.Count; ++i)
            {
                WorldLootRuntimeEntry entry = _loot[i];
                result[i] = new WorldLootRuntimeEntry(entry.EntryIndex, entry.Definition, entry.Quantity);
            }
            return result;
        }

        internal bool TryReserveLoot(int entryIndex, int quantity, out LootTableEntryDefinition definition)
        {
            definition = null;
            if (quantity < 1) return false;
            for (int i = 0; i < _loot.Count; ++i)
            {
                WorldLootRuntimeEntry entry = _loot[i];
                if (entry.EntryIndex != entryIndex) continue;
                _reservedLoot.TryGetValue(entryIndex, out int reserved);
                if (entry.Quantity - reserved < quantity) return false;
                _reservedLoot[entryIndex] = checked(reserved + quantity);
                definition = entry.Definition;
                return definition != null;
            }
            return false;
        }

        internal bool TryReserveAllLoot(out WorldLootRuntimeEntry[] entries)
        {
            var result = new List<WorldLootRuntimeEntry>(_loot.Count);
            for (int i = 0; i < _loot.Count; ++i)
            {
                WorldLootRuntimeEntry entry = _loot[i];
                _reservedLoot.TryGetValue(entry.EntryIndex, out int reserved);
                int available = entry.Quantity - reserved;
                if (available > 0)
                    result.Add(new WorldLootRuntimeEntry(entry.EntryIndex, entry.Definition, available));
            }
            if (result.Count == 0)
            {
                entries = Array.Empty<WorldLootRuntimeEntry>();
                return false;
            }
            for (int i = 0; i < result.Count; ++i)
            {
                WorldLootRuntimeEntry entry = result[i];
                _reservedLoot.TryGetValue(entry.EntryIndex, out int reserved);
                _reservedLoot[entry.EntryIndex] = checked(reserved + entry.Quantity);
            }
            entries = result.ToArray();
            return true;
        }

        internal void CompleteLootReservation(int entryIndex, int quantity, bool success)
        {
            if (quantity < 1 || !_reservedLoot.TryGetValue(entryIndex, out int reserved)) return;
            int release = Math.Min(quantity, reserved);
            int nextReserved = reserved - release;
            if (nextReserved == 0) _reservedLoot.Remove(entryIndex);
            else _reservedLoot[entryIndex] = nextReserved;

            if (!success) return;
            for (int i = 0; i < _loot.Count; ++i)
            {
                WorldLootRuntimeEntry entry = _loot[i];
                if (entry.EntryIndex != entryIndex) continue;
                entry.Quantity = Math.Max(0, entry.Quantity - release);
                break;
            }
            RefreshDepleted();
        }

        internal void CompleteLootReservations(WorldLootRuntimeEntry[] entries, bool success)
        {
            entries ??= Array.Empty<WorldLootRuntimeEntry>();
            for (int i = 0; i < entries.Length; ++i)
            {
                WorldLootRuntimeEntry entry = entries[i];
                if (entry != null) CompleteLootReservation(entry.EntryIndex, entry.Quantity, success);
            }
        }

        private void RefreshDepleted()
        {
            if (Definition.kind == ServerWorldInteractableKind.HarvestNode)
            {
                Depleted = HarvestCharges <= 0;
                return;
            }
            for (int i = 0; i < _loot.Count; ++i)
                if (_loot[i].Quantity > 0) { Depleted = false; return; }
            Depleted = true;
        }

        public bool TryReserve(long actorId, string preferredRoleId, out ServerInteractionSlotDefinition slot)
        {
            slot = null;
            if (actorId <= 0) return false;
            if (_slotByActor.TryGetValue(actorId, out string existing))
                return TryGetSlot(existing, out slot);

            ServerInteractionSlotDefinition[] slots = Definition.slots ?? Array.Empty<ServerInteractionSlotDefinition>();
            for (int i = 0; i < slots.Length; ++i)
            {
                ServerInteractionSlotDefinition candidate = slots[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.slotId) || _occupantBySlot.ContainsKey(candidate.slotId))
                    continue;
                if (!string.IsNullOrWhiteSpace(preferredRoleId) &&
                    !string.Equals(candidate.roleId ?? string.Empty, preferredRoleId, StringComparison.Ordinal))
                    continue;

                _occupantBySlot.Add(candidate.slotId, actorId);
                _slotByActor.Add(actorId, candidate.slotId);
                slot = candidate;
                Touch();
                return true;
            }
            return false;
        }

        public bool Release(long actorId)
        {
            if (!_slotByActor.TryGetValue(actorId, out string slotId)) return false;
            _slotByActor.Remove(actorId);
            _occupantBySlot.Remove(slotId);
            Touch();
            return true;
        }

        public bool TryGetSlot(string slotId, out ServerInteractionSlotDefinition slot)
        {
            ServerInteractionSlotDefinition[] slots = Definition.slots ?? Array.Empty<ServerInteractionSlotDefinition>();
            for (int i = 0; i < slots.Length; ++i)
            {
                if (slots[i] != null && string.Equals(slots[i].slotId, slotId, StringComparison.Ordinal))
                {
                    slot = slots[i];
                    return true;
                }
            }
            slot = null;
            return false;
        }

        public bool IsSlotOccupied(string slotId) => _occupantBySlot.ContainsKey(slotId ?? string.Empty);
        public int OccupiedSlotCount => _occupantBySlot.Count;
    }

    /// <summary>
    /// Canonical standalone runtime for baked contextual world objects. Static definitions are
    /// immutable content; only compact authoritative state, reservations and loot quantities live here.
    /// </summary>
    public sealed class WorldInteractableService
    {
        private readonly Dictionary<WorldInteractableKey, WorldInteractableRuntime> _objects =
            new Dictionary<WorldInteractableKey, WorldInteractableRuntime>();
        private readonly HashSet<string> _loadedMaps = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<WorldInteractableKey> _transientRuntimeOnly = new HashSet<WorldInteractableKey>();
        private readonly ServerMapCatalog _maps;
        private readonly GameplayContentCatalog _content;
        private readonly ProgressionService _progression;
        private long _nextTransientStableId = long.MaxValue;

        /// <summary>
        /// Internal runtime-state notification including transient sources. General client world-object
        /// replication still uses Changed, which intentionally excludes transient loot sources.
        /// </summary>
        public event Action<WorldInteractableRuntime> RuntimeChanged;
        public event Action<WorldInteractableRuntime> Changed;
        public int Count => _objects.Count;

        public WorldInteractableService(ServerMapCatalog maps, GameplayContentCatalog content = null, ProgressionService progression = null)
        {
            _maps = maps ?? throw new ArgumentNullException(nameof(maps));
            _content = content;
            _progression = progression;
        }

        public void EnsureMapLoaded(string mapId, string instanceId)
        {
            mapId = (mapId ?? string.Empty).Trim();
            instanceId = (instanceId ?? string.Empty).Trim();
            string loadedKey = mapId + "\n" + instanceId;
            if (_loadedMaps.Contains(loadedKey)) return;
            if (!_maps.TryGet(mapId, instanceId, out ServerMapSnapshot map)) return;

            ServerWorldInteractableDefinition[] defs = map.interactables ?? Array.Empty<ServerWorldInteractableDefinition>();
            for (int i = 0; i < defs.Length; ++i)
            {
                ServerWorldInteractableDefinition def = defs[i];
                if (def == null || def.stableId <= 0) continue;
                var key = new WorldInteractableKey(mapId, instanceId, def.stableId);
                if (_objects.ContainsKey(key)) continue;
                var runtime = new WorldInteractableRuntime(key, def);
                InitializeLoot(runtime);
                InitializeHarvest(runtime);
                _objects.Add(key, runtime);
            }
            _loadedMaps.Add(loadedKey);
        }

        private void InitializeHarvest(WorldInteractableRuntime runtime)
        {
            if (runtime == null || runtime.Definition.kind != ServerWorldInteractableKind.HarvestNode) return;
            if (_content == null || string.IsNullOrWhiteSpace(runtime.Definition.gameplayProfileId) ||
                !_content.TryGetHarvestProfile(runtime.Definition.gameplayProfileId, out HarvestProfileDefinition profile) || profile == null)
            {
                // Fail closed when authored harvest content is unavailable. A malformed/missing profile
                // must never create an infinitely usable node.
                runtime.InitializeHarvest(0);
                return;
            }

            int minimum = Math.Max(1, profile.minimumCharges);
            int maximum = Math.Max(minimum, profile.maximumCharges);
            int charges = minimum;
            if (maximum > minimum)
            {
                uint random = StableSeed(runtime.Key, (profile.definitionId ?? runtime.Definition.gameplayProfileId) + "#harvest");
                int span = checked(maximum - minimum + 1);
                charges += Math.Min(span - 1, (int)(Next01(ref random) * span));
            }
            runtime.InitializeHarvest(charges);
        }

        private void InitializeLoot(WorldInteractableRuntime runtime)
        {
            if (runtime == null || _content == null || string.IsNullOrWhiteSpace(runtime.Definition.lootTableId) ||
                !_content.TryGetLootTable(runtime.Definition.lootTableId, out LootTableDefinition table) || table == null)
            {
                runtime?.InitializeLoot(Array.Empty<WorldLootRuntimeEntry>());
                return;
            }

            LootTableEntryDefinition[] defs = table.entries ?? Array.Empty<LootTableEntryDefinition>();
            var entries = new List<WorldLootRuntimeEntry>(defs.Length);
            uint random = StableSeed(runtime.Key, table.definitionId);
            for (int i = 0; i < defs.Length; ++i)
            {
                LootTableEntryDefinition entry = defs[i];
                if (entry == null || entry.itemDataId == 0) continue;
                float chance = entry.chance < 0f ? 0f : entry.chance > 1f ? 1f : entry.chance;
                if (Next01(ref random) > chance) continue;
                int min = Math.Max(1, entry.minQuantity);
                int max = Math.Max(min, entry.maxQuantity);
                int quantity = min == max ? min : min + (int)(Next01(ref random) * (max - min + 1));
                if (quantity > max) quantity = max;
                entries.Add(new WorldLootRuntimeEntry(i, entry, quantity));
            }
            runtime.InitializeLoot(entries.ToArray());
        }

        private static uint StableSeed(WorldInteractableKey key, string contentId)
        {
            unchecked
            {
                uint hash = 2166136261u;
                void Add(string value)
                {
                    value ??= string.Empty;
                    for (int i = 0; i < value.Length; ++i) { hash ^= value[i]; hash *= 16777619u; }
                }
                Add(key.MapId); Add(key.InstanceId); Add(contentId);
                ulong id = (ulong)key.StableId;
                for (int i = 0; i < 8; ++i) { hash ^= (byte)(id >> (i * 8)); hash *= 16777619u; }
                return hash == 0 ? 0x9E3779B9u : hash;
            }
        }

        private static float Next01(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0x00FFFFFFu) / 16777216f;
        }

        public bool TryGet(string mapId, string instanceId, long stableId, out WorldInteractableRuntime runtime)
        {
            EnsureMapLoaded(mapId, instanceId);
            return _objects.TryGetValue(new WorldInteractableKey(
                (mapId ?? string.Empty).Trim(), (instanceId ?? string.Empty).Trim(), stableId), out runtime);
        }

        public WorldInteractableRuntime CreateTransientLootSource(
            string mapId,
            string instanceId,
            string label,
            WorldPosition position,
            string lootTableId,
            WorldLootRuntimeEntry[] entries)
        {
            mapId = (mapId ?? string.Empty).Trim();
            instanceId = (instanceId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(mapId))
                throw new ArgumentException("MapId is required.", nameof(mapId));
            if (string.IsNullOrWhiteSpace(lootTableId))
                throw new ArgumentException("Loot table id is required.", nameof(lootTableId));

            long stableId = AllocateTransientStableId();
            var definition = new ServerWorldInteractableDefinition
            {
                stableId = stableId,
                label = string.IsNullOrWhiteSpace(label) ? "Loot" : label.Trim(),
                pose = new ServerPose(position.X, position.Y, position.Z, 0f),
                kind = ServerWorldInteractableKind.Searchable,
                lootTableId = lootTableId.Trim(),
                persistentState = false,
                enabledByDefault = true,
                interactionDefinitions = new[]
                {
                    new ServerContextualInteractionDefinition
                    {
                        definitionId = "transient_population_loot",
                        categoryId = InteractionCategoryId.Use,
                        actionId = InteractionActionId.Search,
                        displayLabel = "Loot",
                        maximumUseDistance = InteractionRangePolicy.WorldObjectUseRange,
                        maximumFacingAngle = 180f,
                        exclusiveOccupancy = false,
                        looping = false,
                        fixedDurationSeconds = 0f,
                        lockMovement = false,
                        lockRotation = false,
                        cancelOnDamage = false,
                        cancelOnMovement = false,
                        cancelOnTargetUnavailable = true,
                        consentMode = InteractionConsentMode.None,
                        contentLevel = InteractionContentLevel.General,
                        feature = InteractionFeature.Loot,
                    },
                },
                slots = Array.Empty<ServerInteractionSlotDefinition>(),
            };

            var key = new WorldInteractableKey(mapId, instanceId, stableId);
            var runtime = new WorldInteractableRuntime(key, definition);
            runtime.InitializeLoot(entries ?? Array.Empty<WorldLootRuntimeEntry>());
            _objects.Add(key, runtime);
            _transientRuntimeOnly.Add(key);
            return runtime;
        }

        public bool RemoveTransient(WorldInteractableRuntime runtime)
        {
            if (runtime == null || runtime.Definition == null || runtime.Definition.persistentState)
                return false;
            _transientRuntimeOnly.Remove(runtime.Key);
            return _objects.Remove(runtime.Key);
        }

        private long AllocateTransientStableId()
        {
            for (int attempt = 0; attempt < 1000000; ++attempt)
            {
                long candidate = _nextTransientStableId;
                _nextTransientStableId = candidate <= 1 ? long.MaxValue : candidate - 1;
                bool used = false;
                foreach (WorldInteractableKey key in _objects.Keys)
                {
                    if (key.StableId == candidate)
                    {
                        used = true;
                        break;
                    }
                }
                if (!used)
                    return candidate;
            }
            throw new InvalidOperationException("transient world-interactable identity space is exhausted");
        }

        public WorldInteractableRuntime[] SnapshotStates(string mapId, string instanceId, bool changedOnly)
        {
            EnsureMapLoaded(mapId, instanceId);
            mapId = (mapId ?? string.Empty).Trim();
            instanceId = (instanceId ?? string.Empty).Trim();
            var result = new List<WorldInteractableRuntime>();
            foreach (KeyValuePair<WorldInteractableKey, WorldInteractableRuntime> pair in _objects)
            {
                if (_transientRuntimeOnly.Contains(pair.Key)) continue;
                if (!string.Equals(pair.Key.MapId, mapId, StringComparison.Ordinal) ||
                    !string.Equals(pair.Key.InstanceId, instanceId, StringComparison.Ordinal)) continue;
                if (changedOnly && pair.Value.Revision <= 1) continue;
                result.Add(pair.Value);
            }
            return result.ToArray();
        }

        public InteractionActionSet Discover(PlayerRuntime source, long stableId, double now)
        {
            if (source == null)
                return new InteractionActionSet(default, string.Empty, Array.Empty<InteractionActionEntry>(), "source character is unavailable");
            if (!TryGet(source.Location.MapId, source.Location.InstanceId, stableId, out WorldInteractableRuntime target))
                return new InteractionActionSet(new InteractionTargetHandle(InteractionTargetKind.SceneObject, stableId), string.Empty, Array.Empty<InteractionActionEntry>(), "world object is unavailable");

            ServerContextualInteractionDefinition[] defs = target.Definition.interactionDefinitions ?? Array.Empty<ServerContextualInteractionDefinition>();
            var entries = new List<InteractionActionEntry>(defs.Length);
            for (int i = 0; i < defs.Length; ++i)
            {
                ServerContextualInteractionDefinition def = defs[i];
                if (def == null || def.actionId == InteractionActionId.None) continue;
                bool available = Evaluate(source, target, def, out string reason);
                entries.Add(new InteractionActionEntry(
                    def.categoryId == InteractionCategoryId.None ? InteractionCategoryId.Use : def.categoryId,
                    def.actionId,
                    string.IsNullOrWhiteSpace(def.displayLabel) ? def.actionId.ToString() : def.displayLabel,
                    available ? InteractionAvailability.Available : InteractionAvailability.Disabled,
                    reason, def.consentMode, def.contentLevel, def.feature, i <= short.MaxValue ? (short)i : short.MaxValue));
            }
            return new InteractionActionSet(new InteractionTargetHandle(InteractionTargetKind.SceneObject, stableId),
                target.Definition.label, entries.ToArray(), string.Empty);
        }

        public InteractionResult ExecuteInstant(PlayerRuntime source, long stableId, InteractionActionId actionId, uint sequence, double now)
        {
            if (source == null)
                return new InteractionResult(sequence, actionId, InteractionResultCode.InvalidState, default, "source character is unavailable");
            var handle = new InteractionTargetHandle(InteractionTargetKind.SceneObject, stableId);
            if (!TryGet(source.Location.MapId, source.Location.InstanceId, stableId, out WorldInteractableRuntime target))
                return new InteractionResult(sequence, actionId, InteractionResultCode.TargetUnavailable, handle, "world object is unavailable");

            ServerContextualInteractionDefinition def = FindDefinition(target.Definition, actionId);
            if (def == null)
                return new InteractionResult(sequence, actionId, InteractionResultCode.Unsupported, handle, "action is not supported by this object");
            if (!Evaluate(source, target, def, out string reason))
                return new InteractionResult(sequence, actionId, InteractionResultCode.Rejected, handle, reason);
            if (def.looping || def.fixedDurationSeconds > 0f || def.consentMode != InteractionConsentMode.None)
                return new InteractionResult(sequence, actionId, InteractionResultCode.ConsentRequired, handle, "action requires an interaction session");

            if (actionId == InteractionActionId.Open && target.Definition.dynamicBlockerId > 0)
            {
                target.Open = !target.Open;
                if (_maps.TryGetCollisionWorld(source.Location.MapId, source.Location.InstanceId, out ServerCollisionWorld collision))
                    collision.TrySetDynamicBlockerEnabled(target.Definition.dynamicBlockerId, !target.Open);
            }
            target.Phase = WorldInteractablePhase.Active;
            PublishChanged(target);
            return new InteractionResult(sequence, actionId, InteractionResultCode.Success, handle,
                actionId == InteractionActionId.Open ? (target.Open ? "opened" : "closed") : "interaction completed");
        }

        public bool Evaluate(PlayerRuntime source, WorldInteractableRuntime target, ServerContextualInteractionDefinition def, out string reason)
        {
            reason = string.Empty;
            if (source == null || target == null || def == null || !target.Enabled)
            {
                reason = "interaction unavailable";
                return false;
            }
            if (!string.Equals(source.Location.MapId, target.Key.MapId, StringComparison.Ordinal) ||
                !string.Equals(source.Location.InstanceId, target.Key.InstanceId, StringComparison.Ordinal))
            {
                reason = "target is in a different world";
                return false;
            }
            float dx = source.Location.Position.X - target.Definition.pose.x;
            float dy = source.Location.Position.Y - target.Definition.pose.y;
            float dz = source.Location.Position.Z - target.Definition.pose.z;
            float range = InteractionRangePolicy.ClampWorldUseRange(def.maximumUseDistance);
            if (dx * dx + dy * dy + dz * dz > range * range)
            {
                reason = "target is out of range";
                return false;
            }
            if (def.maximumFacingAngle < 179.9f)
            {
                float toTargetYaw = MathF.Atan2(dx, dz) * (180f / MathF.PI);
                float delta = Math.Abs(DeltaAngle(source.Location.YawDegrees, toTargetYaw));
                if (delta > Math.Max(0f, def.maximumFacingAngle))
                {
                    reason = "target is outside the allowed facing angle";
                    return false;
                }
            }
            if (def.exclusiveOccupancy && target.ActiveSessionId != 0)
            {
                reason = "interaction is already in use";
                return false;
            }
            if (def.exclusiveOccupancy && target.Definition.slots != null && target.Definition.slots.Length > 0)
            {
                bool free = false;
                for (int i = 0; i < target.Definition.slots.Length; ++i)
                {
                    ServerInteractionSlotDefinition slot = target.Definition.slots[i];
                    if (slot != null && !target.IsSlotOccupied(slot.slotId)) { free = true; break; }
                }
                if (!free) { reason = "all interaction slots are occupied"; return false; }
            }
            return true;
        }

        public bool TryResolveCraftingStation(PlayerRuntime source, long stableId, out WorldInteractableRuntime target,
            out CraftingStationDefinition station, out string reason)
        {
            target = null;
            station = null;
            reason = string.Empty;
            if (source == null) { reason = "player runtime is unavailable"; return false; }
            if (_content == null) { reason = "gameplay content is unavailable"; return false; }
            if (!TryGet(source.Location.MapId, source.Location.InstanceId, stableId, out target))
            { reason = "crafting station is unavailable"; return false; }
            if (string.IsNullOrWhiteSpace(target.Definition.craftingStationId) ||
                !_content.TryGetCraftingStation(target.Definition.craftingStationId, out station))
            { reason = "crafting station content is unavailable"; return false; }
            ServerContextualInteractionDefinition def = FindDefinition(target.Definition, InteractionActionId.Craft);
            if (def == null) { reason = "world object is not a crafting station"; return false; }
            return Evaluate(source, target, def, out reason);
        }

        public bool TryOpenLoot(PlayerRuntime source, long stableId, out WorldInteractableRuntime target, out string reason)
        {
            target = null;
            reason = string.Empty;
            if (source == null) { reason = "player runtime is unavailable"; return false; }
            if (!TryGet(source.Location.MapId, source.Location.InstanceId, stableId, out target))
            { reason = "loot source is unavailable"; return false; }
            if (target.Definition.kind != ServerWorldInteractableKind.Searchable || string.IsNullOrWhiteSpace(target.Definition.lootTableId))
            { reason = "world object is not searchable"; return false; }
            if (target.Depleted)
            { reason = "loot source is depleted"; return false; }
            ServerContextualInteractionDefinition def = FindDefinition(target.Definition, InteractionActionId.Search);
            if (def == null || !Evaluate(source, target, def, out reason)) return false;
            return true;
        }

        public bool TryBeginLootTake(PlayerRuntime source, long stableId, long lootRevision, int entryIndex, int quantity,
            out WorldInteractableRuntime target, out LootTableEntryDefinition definition, out string reason)
        {
            definition = null;
            if (!TryOpenLoot(source, stableId, out target, out reason)) return false;
            if (lootRevision != target.Revision) { reason = "loot state changed; reopen the source"; return false; }
            if (target.Depleted) { reason = "loot source is depleted"; return false; }
            if (!target.TryReserveLoot(entryIndex, quantity, out definition))
            { reason = "requested loot is unavailable"; return false; }
            return true;
        }

        public bool TryBeginLootTakeAll(PlayerRuntime source, long stableId, long lootRevision,
            out WorldInteractableRuntime target, out WorldLootRuntimeEntry[] entries, out string reason)
        {
            entries = Array.Empty<WorldLootRuntimeEntry>();
            if (!TryOpenLoot(source, stableId, out target, out reason)) return false;
            if (lootRevision != target.Revision) { reason = "loot state changed; reopen the source"; return false; }
            if (target.Depleted || !target.TryReserveAllLoot(out entries))
            { reason = "loot source is depleted or already being transferred"; return false; }
            return true;
        }

        public void CompleteLootTake(WorldInteractableRuntime target, int entryIndex, int quantity, bool success)
        {
            if (target == null) return;
            target.CompleteLootReservation(entryIndex, quantity, success);
            if (success) PublishChanged(target);
        }

        public void CompleteLootTakeAll(WorldInteractableRuntime target, WorldLootRuntimeEntry[] entries, bool success)
        {
            if (target == null) return;
            target.CompleteLootReservations(entries, success);
            if (success) PublishChanged(target);
        }

        public bool TrySetOpenState(string mapId, string instanceId, long stableId, bool open)
        {
            if (!TryGet(mapId, instanceId, stableId, out WorldInteractableRuntime target) || !target.Enabled) return false;
            if (target.Open == open) return true;
            target.Open = open;
            if (target.Definition.dynamicBlockerId > 0 && _maps.TryGetCollisionWorld(mapId, instanceId, out ServerCollisionWorld collision))
                collision.TrySetDynamicBlockerEnabled(target.Definition.dynamicBlockerId, !open);
            target.Phase = WorldInteractablePhase.Active;
            PublishChanged(target);
            return true;
        }

        public void PublishChanged(WorldInteractableRuntime target)
        {
            if (target == null) return;
            target.Touch();
            RuntimeChanged?.Invoke(target);
            // Runtime-only loot sources are owner-facing through the existing WorldLoot
            // response path. Do not leak them into general scene-object state replication.
            if (!_transientRuntimeOnly.Contains(target.Key))
                Changed?.Invoke(target);
        }

        public static ServerContextualInteractionDefinition FindDefinition(ServerWorldInteractableDefinition target, InteractionActionId actionId)
        {
            ServerContextualInteractionDefinition[] defs = target?.interactionDefinitions ?? Array.Empty<ServerContextualInteractionDefinition>();
            for (int i = 0; i < defs.Length; ++i)
                if (defs[i] != null && defs[i].actionId == actionId) return defs[i];
            return null;
        }

        public static ServerContextualInteractionDefinition FindDefinition(
            ServerWorldInteractableDefinition target,
            InteractionCategoryId categoryId,
            InteractionActionId actionId)
        {
            ServerContextualInteractionDefinition[] defs = target?.interactionDefinitions ?? Array.Empty<ServerContextualInteractionDefinition>();
            for (int i = 0; i < defs.Length; ++i)
            {
                ServerContextualInteractionDefinition def = defs[i];
                if (def == null || def.actionId != actionId) continue;
                InteractionCategoryId authored = def.categoryId == InteractionCategoryId.None
                    ? InteractionCategoryId.Use
                    : def.categoryId;
                if (authored == categoryId) return def;
            }
            return null;
        }

        private static float DeltaAngle(float current, float target)
        {
            float delta = (target - current) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            return delta;
        }
    }
}
