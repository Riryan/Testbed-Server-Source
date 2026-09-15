using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Application.Interactions;
using Game.Server.Application.Loot;
using Game.Server.Application.Progression;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Shared.Content;
using Game.Shared.Progression;
using Game.Shared.Resources;
using Game.Shared.World;

namespace Game.Server.Application.Harvesting
{
    public readonly struct HarvestAttemptPlan
    {
        public long CharacterId { get; }
        public WorldInteractableKey TargetKey { get; }
        public HarvestProfileDefinition Profile { get; }
        public HarvestMethodDefinition Method { get; }
        public LootTableDefinition LootTable { get; }
        public int ProfessionLevel { get; }
        public byte ToolTier { get; }
        public float DurationSeconds { get; }
        public ushort PresentationId { get; }

        public HarvestAttemptPlan(
            long characterId,
            WorldInteractableKey targetKey,
            HarvestProfileDefinition profile,
            HarvestMethodDefinition method,
            LootTableDefinition lootTable,
            int professionLevel,
            byte toolTier)
        {
            CharacterId = characterId;
            TargetKey = targetKey;
            Profile = profile;
            Method = method;
            LootTable = lootTable;
            ProfessionLevel = professionLevel;
            ToolTier = toolTier;
            DurationSeconds = Math.Max(0.05f, profile?.durationSeconds ?? 0.05f);
            PresentationId = profile?.presentationId ?? 0;
        }

        public byte RewardTier => Method?.rewardTier ?? 0;
    }

    public readonly struct PendingHarvestResolution
    {
        public long SessionId { get; }
        public long CharacterId { get; }
        public WorldInteractableKey TargetKey { get; }
        public HarvestProfileDefinition Profile { get; }
        public HarvestMethodDefinition Method { get; }
        public RewardBundleDefinition Reward { get; }
        public bool HarvestSucceeded { get; }
        public bool ConsumeCharge { get; }
        public int ProfessionReward { get; }
        public byte RewardTier => Method?.rewardTier ?? 0;
        public ushort PresentationId => Profile?.presentationId ?? 0;

        public PendingHarvestResolution(
            long sessionId,
            long characterId,
            WorldInteractableKey targetKey,
            HarvestProfileDefinition profile,
            HarvestMethodDefinition method,
            RewardBundleDefinition reward,
            bool harvestSucceeded,
            bool consumeCharge,
            int professionReward)
        {
            SessionId = sessionId;
            CharacterId = characterId;
            TargetKey = targetKey;
            Profile = profile;
            Method = method;
            Reward = reward;
            HarvestSucceeded = harvestSucceeded;
            ConsumeCharge = consumeCharge;
            ProfessionReward = professionReward;
        }
    }

    public readonly struct HarvestResolveResult
    {
        public bool Committed { get; }
        public bool HarvestSucceeded { get; }
        public bool Depleted { get; }
        public byte RewardTier { get; }
        public ushort PresentationId { get; }
        public int ProfessionReward { get; }
        public double HarvestReadyAt { get; }
        public uint HarvestGeneration { get; }
        public string Detail { get; }

        public HarvestResolveResult(
            bool committed,
            bool harvestSucceeded,
            bool depleted,
            byte rewardTier,
            ushort presentationId,
            int professionReward,
            double harvestReadyAt,
            uint harvestGeneration,
            string detail)
        {
            Committed = committed;
            HarvestSucceeded = harvestSucceeded;
            Depleted = depleted;
            RewardTier = rewardTier;
            PresentationId = presentationId;
            ProfessionReward = professionReward;
            HarvestReadyAt = harvestReadyAt;
            HarvestGeneration = harvestGeneration;
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>
    /// Event-driven authoritative harvesting. It owns no update loop: InteractionSessionService
    /// supplies timed completion/cancellation events, and depleted nodes schedule one respawn event.
    /// Tool capability/tier, profession state, success odds and loot tables stay server-side.
    /// </summary>
    public sealed class HarvestingService
    {
        private readonly object _gate = new object();
        private readonly GameplayContentCatalog _content;
        private readonly ProgressionService _progression;
        private readonly WorldInteractableService _worldObjects;
        private readonly Dictionary<long, HarvestAttemptPlan> _attempts = new Dictionary<long, HarvestAttemptPlan>();
        private readonly HashSet<WorldInteractableKey> _reservedNodes = new HashSet<WorldInteractableKey>();

        public HarvestingService(
            GameplayContentCatalog content,
            ProgressionService progression,
            WorldInteractableService worldObjects)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _worldObjects = worldObjects ?? throw new ArgumentNullException(nameof(worldObjects));
        }

        public bool TryPrepareAttempt(
            PlayerRuntime actor,
            long stableId,
            out HarvestAttemptPlan plan,
            out string detail)
        {
            plan = default;
            detail = string.Empty;
            if (!IsAlive(actor))
            {
                detail = "player cannot harvest while dead or unavailable";
                return false;
            }

            if (!_worldObjects.TryGet(actor.Location.MapId, actor.Location.InstanceId, stableId, out WorldInteractableRuntime target) ||
                target.Definition.kind != Game.Shared.World.ServerWorldInteractableKind.HarvestNode)
            {
                detail = "harvest node is unavailable";
                return false;
            }
            if (target.Depleted || target.HarvestCharges <= 0)
            {
                detail = "harvest node is depleted";
                return false;
            }

            lock (_gate)
            {
                if (_reservedNodes.Contains(target.Key))
                {
                    detail = "harvest node is already being resolved";
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(target.Definition.gameplayProfileId) ||
                !_content.TryGetHarvestProfile(target.Definition.gameplayProfileId, out HarvestProfileDefinition profile) ||
                profile == null)
            {
                detail = "harvest profile is unavailable";
                return false;
            }

            ServerContextualInteractionDefinition interaction = WorldInteractableService.FindDefinition(
                target.Definition, Game.Shared.Interactions.InteractionActionId.Harvest);
            if (interaction == null || !_worldObjects.Evaluate(actor, target, interaction, out detail))
                return false;
            if (!WithinProfileUseRules(actor, target, profile, out detail))
                return false;

            CharacterProgressionState progression = _progression.Snapshot(actor);
            int professionLevel = FindTrackValue(progression, profile.professionTrackDataId);
            if (professionLevel < Math.Max(0, profile.minimumProfessionLevel))
            {
                detail = "profession level is too low";
                return false;
            }

            if (!TrySelectMethod(actor, profile, out HarvestMethodDefinition method, out byte toolTier, out detail))
                return false;
            if (!TryResolveLootTable(profile, method, out LootTableDefinition lootTable))
            {
                detail = "harvest loot table is unavailable";
                return false;
            }

            plan = new HarvestAttemptPlan(
                actor.CharacterId.Value,
                target.Key,
                profile,
                method,
                lootTable,
                professionLevel,
                toolTier);
            return true;
        }

        public bool BindSession(long sessionId, HarvestAttemptPlan plan, out string detail)
        {
            detail = string.Empty;
            if (sessionId <= 0 || plan.CharacterId <= 0 || plan.Profile == null || plan.LootTable == null)
            {
                detail = "harvest attempt is invalid";
                return false;
            }

            lock (_gate)
            {
                if (_attempts.ContainsKey(sessionId) || !_reservedNodes.Add(plan.TargetKey))
                {
                    detail = "harvest node is already reserved";
                    return false;
                }
                _attempts.Add(sessionId, plan);
                return true;
            }
        }

        public bool CancelSession(long sessionId)
        {
            lock (_gate)
            {
                if (!_attempts.TryGetValue(sessionId, out HarvestAttemptPlan plan)) return false;
                _attempts.Remove(sessionId);
                _reservedNodes.Remove(plan.TargetKey);
                return true;
            }
        }

        /// <summary>
        /// Computes the outcome and reward bundle synchronously on the authoritative thread.
        /// The caller commits the reward through the authoritative item/progression paths, then
        /// calls CompleteResolution. The node remains reserved while persistence is in flight.
        /// </summary>
        public bool TryBeginResolution(
            long sessionId,
            PlayerRuntime actor,
            Func<double> random01,
            out PendingHarvestResolution pending,
            out string detail)
        {
            pending = default;
            detail = string.Empty;
            if (random01 == null)
            {
                detail = "harvest random source is unavailable";
                return false;
            }

            HarvestAttemptPlan plan;
            lock (_gate)
            {
                if (!_attempts.TryGetValue(sessionId, out plan))
                {
                    detail = "harvest attempt is unavailable";
                    return false;
                }
                _attempts.Remove(sessionId);
            }

            if (actor == null || actor.CharacterId.Value != plan.CharacterId || !IsAlive(actor))
            {
                ReleaseReservation(plan.TargetKey);
                detail = "player became unavailable before harvest completion";
                return false;
            }
            if (!_worldObjects.TryGet(plan.TargetKey.MapId, plan.TargetKey.InstanceId, plan.TargetKey.StableId, out WorldInteractableRuntime target) ||
                target.Depleted || target.HarvestCharges <= 0)
            {
                ReleaseReservation(plan.TargetKey);
                detail = "harvest node became unavailable";
                return false;
            }

            ServerContextualInteractionDefinition interaction = WorldInteractableService.FindDefinition(
                target.Definition, Game.Shared.Interactions.InteractionActionId.Harvest);
            if (interaction == null || !_worldObjects.Evaluate(actor, target, interaction, out detail) ||
                !WithinProfileUseRules(actor, target, plan.Profile, out detail))
            {
                ReleaseReservation(plan.TargetKey);
                return false;
            }

            // The method/tool is authoritative state, not a client claim. Revalidate at completion so
            // a player cannot start with a qualifying tool, unequip/remove it during the timed action,
            // and still receive the higher reward tier.
            if (!IsPlannedMethodStillAvailable(actor, plan.Profile, plan.Method, plan.ToolTier))
            {
                ReleaseReservation(plan.TargetKey);
                detail = "harvesting method or tool became unavailable";
                return false;
            }

            int odds = Math.Max(0, Math.Min(10000,
                (int)plan.Method.baseSuccessBasisPoints +
                (plan.ProfessionLevel * (int)plan.Method.skillBasisPointsPerLevel)));
            bool success = ClampUnit(random01()) * 10000d < odds;
            int professionReward = success
                ? Math.Max(0, plan.Profile.successProfessionReward)
                : Math.Max(0, plan.Profile.failureProfessionReward);

            RewardItemDefinition[] itemRewards = Array.Empty<RewardItemDefinition>();
            if (success)
            {
                if (!LootTableResolver.TryRollAll(plan.LootTable, random01, null, out LootRollResult[] rolls, out detail))
                {
                    ReleaseReservation(plan.TargetKey);
                    return false;
                }
                itemRewards = AggregateRewards(rolls);
            }

            RewardTrackDefinition[] trackRewards = professionReward > 0 && plan.Profile.professionTrackDataId != 0
                ? new[] { new RewardTrackDefinition { trackDataId = plan.Profile.professionTrackDataId, amount = professionReward } }
                : Array.Empty<RewardTrackDefinition>();

            var reward = new RewardBundleDefinition
            {
                items = itemRewards,
                tracks = trackRewards,
            };

            pending = new PendingHarvestResolution(
                sessionId,
                plan.CharacterId,
                plan.TargetKey,
                plan.Profile,
                plan.Method,
                reward,
                success,
                success || plan.Profile.consumeChargeOnFailure,
                professionReward);
            return true;
        }

        public HarvestResolveResult CompleteResolution(
            PendingHarvestResolution pending,
            bool rewardCommitted,
            string rewardError,
            double now)
        {
            if (!rewardCommitted)
            {
                ReleaseReservation(pending.TargetKey);
                return new HarvestResolveResult(
                    false, pending.HarvestSucceeded, false, pending.RewardTier, pending.PresentationId, 0, 0d, 0u,
                    string.IsNullOrWhiteSpace(rewardError) ? "harvest reward could not be committed" : rewardError);
            }

            bool depleted = false;
            double harvestReadyAt = 0d;
            uint harvestGeneration = 0u;
            if (pending.ConsumeCharge &&
                _worldObjects.TryGet(pending.TargetKey.MapId, pending.TargetKey.InstanceId, pending.TargetKey.StableId, out WorldInteractableRuntime target))
            {
                // Only a charge consumed by this resolution may create a depletion/respawn event.
                // This makes duplicate completion calls idempotent with respect to node lifecycle.
                if (target.TryConsumeHarvestCharge(out bool becameDepleted))
                {
                    depleted = becameDepleted;
                    if (depleted)
                    {
                        harvestGeneration = target.HarvestGeneration;
                        if (pending.Profile.respawnPolicy == HarvestRespawnPolicy.Timed)
                        {
                            double delay = Math.Max(0d, pending.Profile.emptyDelaySeconds) + Math.Max(0.05d, pending.Profile.respawnSeconds);
                            harvestReadyAt = now + delay;
                            target.SetHarvestRespawnAt(harvestReadyAt);
                        }
                        // Remaining charge count is authoritative server-only state. Replicate only the
                        // client-visible transition to Depleted; ordinary successful charges add no world-state message.
                        _worldObjects.PublishChanged(target);
                    }
                }
            }

            ReleaseReservation(pending.TargetKey);
            return new HarvestResolveResult(
                true,
                pending.HarvestSucceeded,
                depleted,
                pending.RewardTier,
                pending.PresentationId,
                pending.ProfessionReward,
                harvestReadyAt,
                harvestGeneration,
                pending.HarvestSucceeded ? "harvest succeeded" : "harvest failed");
        }

        public bool TryRespawn(WorldInteractableKey key, uint expectedHarvestGeneration, double now, Func<double> random01)
        {
            if (expectedHarvestGeneration == 0u || random01 == null || double.IsNaN(now) || double.IsInfinity(now) || now < 0d ||
                !_worldObjects.TryGet(key.MapId, key.InstanceId, key.StableId, out WorldInteractableRuntime target) ||
                target.Definition.kind != Game.Shared.World.ServerWorldInteractableKind.HarvestNode || !target.Depleted ||
                target.HarvestGeneration != expectedHarvestGeneration)
                return false;
            if (!_content.TryGetHarvestProfile(target.Definition.gameplayProfileId, out HarvestProfileDefinition profile) ||
                profile == null || profile.respawnPolicy != HarvestRespawnPolicy.Timed ||
                target.HarvestReadyAt <= 0d || now < target.HarvestReadyAt)
                return false;

            lock (_gate)
                if (_reservedNodes.Contains(key)) return false;

            int minimum = Math.Max(1, profile.minimumCharges);
            int maximum = Math.Max(minimum, profile.maximumCharges);
            int charges = minimum;
            if (maximum > minimum)
            {
                int span = checked(maximum - minimum + 1);
                charges += Math.Min(span - 1, (int)(ClampUnit(random01()) * span));
            }
            target.RespawnHarvest(charges);
            _worldObjects.PublishChanged(target);
            return true;
        }

        private bool TrySelectMethod(
            PlayerRuntime actor,
            HarvestProfileDefinition profile,
            out HarvestMethodDefinition selected,
            out byte selectedToolTier,
            out string detail)
        {
            selected = null;
            selectedToolTier = 0;
            detail = string.Empty;
            HarvestMethodDefinition[] methods = profile.methods ?? Array.Empty<HarvestMethodDefinition>();

            // Compatibility path for the old single-table recovery profile.
            if (methods.Length == 0)
            {
                if (!LegacyToolRequirementMet(actor, profile.requiredEquippedItemTag))
                {
                    detail = "required harvesting tool is not equipped";
                    return false;
                }
                selected = new HarvestMethodDefinition
                {
                    capability = HarvestCapability.Hands,
                    baseSuccessBasisPoints = 10000,
                    lootTableId = profile.lootTableId ?? string.Empty,
                    rewardTier = 0,
                };
                return true;
            }

            for (int i = 0; i < methods.Length; ++i)
            {
                HarvestMethodDefinition method = methods[i];
                if (method == null || method.capability == HarvestCapability.None) continue;
                if (!TryResolveAvailableToolTier(actor, method, out byte tier)) continue;

                if (selected == null ||
                    method.rewardTier > selected.rewardTier ||
                    (method.rewardTier == selected.rewardTier && tier > selectedToolTier) ||
                    (method.rewardTier == selected.rewardTier && tier == selectedToolTier && method.baseSuccessBasisPoints > selected.baseSuccessBasisPoints))
                {
                    selected = method;
                    selectedToolTier = tier;
                }
            }

            if (selected == null)
            {
                detail = "no usable harvesting method or tool is available";
                return false;
            }
            return true;
        }

        private bool IsPlannedMethodStillAvailable(
            PlayerRuntime actor,
            HarvestProfileDefinition profile,
            HarvestMethodDefinition planned,
            byte plannedToolTier)
        {
            if (actor == null || profile == null || planned == null) return false;

            HarvestMethodDefinition[] methods = profile.methods ?? Array.Empty<HarvestMethodDefinition>();
            if (methods.Length == 0)
                return LegacyToolRequirementMet(actor, profile.requiredEquippedItemTag);

            return TryResolveAvailableToolTier(actor, planned, out byte currentTier) && currentTier >= plannedToolTier;
        }

        private bool TryResolveAvailableToolTier(PlayerRuntime actor, HarvestMethodDefinition method, out byte tier)
        {
            tier = 0;
            if (actor == null || method == null || method.capability == HarvestCapability.None) return false;
            if (method.capability == HarvestCapability.Hands && method.minimumToolTier == 0) return true;
            if (method.toolAccess == HarvestToolAccess.None) return false;

            PlayerItemSystemsRuntimeSnapshot systems = actor.CapturePlayerItemSystems();
            if (systems == null) return false;

            bool eligible = false;
            if (method.toolAccess == HarvestToolAccess.Equipped || method.toolAccess == HarvestToolAccess.EquippedOrInventory)
            {
                EquippedItemState[] equipment = systems.Equipment?.Snapshot() ?? Array.Empty<EquippedItemState>();
                for (int i = 0; i < equipment.Length; ++i)
                    ConsiderTool(equipment[i]?.Item?.DefinitionId, method, ref eligible, ref tier);
            }

            if (method.toolAccess == HarvestToolAccess.Inventory || method.toolAccess == HarvestToolAccess.EquippedOrInventory)
            {
                ItemInstanceState[] slots = systems.Inventory?.CopySlots() ?? Array.Empty<ItemInstanceState>();
                for (int i = 0; i < slots.Length; ++i)
                    ConsiderTool(slots[i]?.DefinitionId, method, ref eligible, ref tier);
            }

            return eligible;
        }

        private void ConsiderTool(
            string definitionId,
            HarvestMethodDefinition method,
            ref bool eligible,
            ref byte tier)
        {
            if (string.IsNullOrWhiteSpace(definitionId) || !_content.TryGetItem(definitionId, out ItemDefinition item) || item == null)
                return;
            if ((item.harvestCapabilities & method.capability) != method.capability || item.harvestToolTier < method.minimumToolTier)
                return;
            eligible = true;
            if (item.harvestToolTier > tier) tier = item.harvestToolTier;
        }

        private bool TryResolveLootTable(
            HarvestProfileDefinition profile,
            HarvestMethodDefinition method,
            out LootTableDefinition lootTable)
        {
            lootTable = null;
            if (method != null && method.lootTableDataId != 0 && _content.TryGetLootTable(method.lootTableDataId, out lootTable))
                return true;
            string id = method?.lootTableId;
            if (string.IsNullOrWhiteSpace(id)) id = profile?.lootTableId;
            return !string.IsNullOrWhiteSpace(id) && _content.TryGetLootTable(id, out lootTable);
        }

        private bool LegacyToolRequirementMet(PlayerRuntime actor, string requiredTag)
        {
            if (string.IsNullOrWhiteSpace(requiredTag)) return true;
            EquippedItemState[] equipment = actor.CapturePlayerItemSystems()?.Equipment?.Snapshot() ?? Array.Empty<EquippedItemState>();
            for (int i = 0; i < equipment.Length; ++i)
            {
                string definitionId = equipment[i]?.Item?.DefinitionId;
                if (string.IsNullOrWhiteSpace(definitionId) || !_content.TryGetItem(definitionId, out ItemDefinition item)) continue;
                string[] tags = item?.tags ?? Array.Empty<string>();
                for (int t = 0; t < tags.Length; ++t)
                    if (string.Equals(tags[t], requiredTag, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool WithinProfileUseRules(
            PlayerRuntime actor,
            WorldInteractableRuntime target,
            HarvestProfileDefinition profile,
            out string detail)
        {
            detail = string.Empty;
            if (actor == null || target == null || profile == null) return false;
            float dx = actor.Location.Position.X - target.Definition.pose.x;
            float dy = actor.Location.Position.Y - target.Definition.pose.y;
            float dz = actor.Location.Position.Z - target.Definition.pose.z;
            float range = Math.Max(0.1f, profile.maximumUseDistance);
            if (dx * dx + dy * dy + dz * dz > range * range)
            {
                detail = "harvest node is out of range";
                return false;
            }
            if (profile.maximumFacingAngle < 179.9f)
            {
                float toTargetYaw = MathF.Atan2(dx, dz) * (180f / MathF.PI);
                float delta = Math.Abs(DeltaAngle(actor.Location.YawDegrees, toTargetYaw));
                if (delta > Math.Max(0f, profile.maximumFacingAngle))
                {
                    detail = "harvest node is outside the allowed facing angle";
                    return false;
                }
            }
            return true;
        }

        private static RewardItemDefinition[] AggregateRewards(LootRollResult[] rolls)
        {
            rolls ??= Array.Empty<LootRollResult>();
            var quantities = new Dictionary<ushort, RewardItemDefinition>();
            for (int i = 0; i < rolls.Length; ++i)
            {
                LootRollResult roll = rolls[i];
                if (roll.ItemDataId == 0 || roll.Quantity <= 0) continue;
                if (quantities.TryGetValue(roll.ItemDataId, out RewardItemDefinition existing))
                    existing.quantity = checked(existing.quantity + roll.Quantity);
                else
                    quantities.Add(roll.ItemDataId, new RewardItemDefinition
                    {
                        itemDataId = roll.ItemDataId,
                        itemDefinitionId = roll.ItemDefinitionId,
                        quantity = roll.Quantity,
                    });
            }
            var result = new RewardItemDefinition[quantities.Count];
            int index = 0;
            foreach (RewardItemDefinition value in quantities.Values) result[index++] = value;
            Array.Sort(result, (a, b) => a.itemDataId.CompareTo(b.itemDataId));
            return result;
        }

        private void ReleaseReservation(WorldInteractableKey key)
        {
            lock (_gate) _reservedNodes.Remove(key);
        }

        private static int FindTrackValue(CharacterProgressionState state, ushort dataId)
        {
            if (state == null || dataId == 0) return 0;
            ProgressTrackState[] tracks = state.tracks ?? Array.Empty<ProgressTrackState>();
            for (int i = 0; i < tracks.Length; ++i)
                if (tracks[i] != null && tracks[i].dataId == dataId) return tracks[i].value;
            return 0;
        }

        private static bool IsAlive(PlayerRuntime runtime) =>
            runtime != null &&
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) &&
            health.Enabled && health.Current > health.Minimum;

        private static float DeltaAngle(float current, float target)
        {
            float delta = (target - current) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            return delta;
        }

        private static double ClampUnit(double value)
        {
            if (double.IsNaN(value) || value <= 0d) return 0d;
            if (value >= 1d) return 0.99999999999999989d;
            return value;
        }
    }
}
