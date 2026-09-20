using System;
using System.Runtime.CompilerServices;
using Game.Server.Application.Movement;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    public sealed partial class PopulationSimulationService
    {
        /// <summary>
        /// Small server-only metadata for the currently bound reactive threat.
        /// It is intentionally not persisted or replicated. ConditionalWeakTable keeps the
        /// metadata lifetime bound to the existing PopulationActorRuntime without adding another
        /// permanent actor registry or changing the public/network contract.
        /// </summary>
        private sealed class ReactiveThreatMetadata
        {
            public float Severity;
            public bool LowHealthDecisionMade;
        }

        private readonly ConditionalWeakTable<PopulationActorRuntime, ReactiveThreatMetadata>
            _reactiveThreatMetadata =
                new ConditionalWeakTable<PopulationActorRuntime, ReactiveThreatMetadata>();

        /// <summary>
        /// A different attacker must exceed the currently bound threat by this normalized
        /// severity amount before an active encounter switches targets. This prevents ordinary
        /// multi-player chip damage from making an NPC ping-pong between attackers.
        /// </summary>
        public float ReactiveThreatSwitchSeverityAdvantage { get; set; } = 0.20f;

        /// <summary>
        /// Once per encounter, when current HP reaches this fraction of maximum HP or lower,
        /// the NPC re-evaluates Fight/Flee. Unlike the initial legacy roll, this resolve check
        /// intentionally allows a low-health NPC to choose Fighting and commit to the death.
        /// </summary>
        public float ReactiveLowHealthDecisionThreshold { get; set; } = 0.30f;

        /// <summary>
        /// Binds an existing positional threat to the player character that caused it.
        /// This remains transient runtime state: no persistence and no additional wire state.
        /// The old positional overload remains valid for non-player/scripted threats.
        ///
        /// Reactive V1.1 rules:
        /// - a new encounter anchors its leash to the NPC/home area, never to the attacker;
        /// - repeated hits from the current attacker refresh the encounter without re-rolling
        ///   fight/flee behavior;
        /// - a different attacker does not steal the target unless the current target is known
        ///   invalid or the incoming hit is materially more severe.
        /// </summary>
        public bool NotifyThreat(
            long populationActorId,
            long threatCharacterId,
            WorldPosition threatPosition,
            float severity,
            float engageRange,
            double now)
        {
            if (threatCharacterId <= 0)
                return NotifyThreat(populationActorId, threatPosition, severity, now);

            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) ||
                pop?.Actor == null ||
                !pop.Actor.Alive)
            {
                return false;
            }

            severity = Math.Clamp(severity, 0f, 1f);
            engageRange = Math.Max(0.5f, engageRange);

            bool activeReactiveThreat =
                pop.ThreatCharacterId > 0 &&
                pop.ThreatUntil > now &&
                (pop.AiState == PopulationAiState.Fighting ||
                 pop.AiState == PopulationAiState.Fleeing);

            ReactiveThreatMetadata metadata = _reactiveThreatMetadata.GetOrCreateValue(pop);

            if (activeReactiveThreat)
            {
                // Repeated authoritative contact from the same attacker refreshes the threat
                // without consuming another behavior RNG roll or flipping Fight <-> Flee.
                if (pop.ThreatCharacterId == threatCharacterId)
                {
                    pop.ThreatPosition = threatPosition;
                    pop.ThreatEngageRange = engageRange;
                    pop.ThreatUntil = ReactiveThreatDeadline(now, severity);
                    metadata.Severity = Math.Max(metadata.Severity, severity);
                    TryApplyLowHealthResolve(pop, metadata, metadata.Severity);
                    pop.SimulationLod = PopulationSimulationLod.Engaged;
                    ScheduleNow(pop, now);
                    return true;
                }

                // Preserve target stickiness during an active encounter. A target that is known
                // dead/gone from the current authoritative player partition can be replaced
                // immediately; otherwise require a material severity advantage.
                if (!ShouldSwitchReactiveThreat(pop, metadata.Severity, severity))
                {
                    metadata.Severity = Math.Max(metadata.Severity, severity);
                    TryApplyLowHealthResolve(pop, metadata, metadata.Severity);
                    return true;
                }

                // Switching attackers during the same encounter deliberately preserves both the
                // existing Fight/Flee choice and the original leash anchor. This prevents a chain
                // of attackers from dragging the NPC's permitted combat area across the map.
                pop.ThreatCharacterId = threatCharacterId;
                pop.ThreatPosition = threatPosition;
                pop.ThreatEngageRange = engageRange;
                pop.ThreatUntil = ReactiveThreatDeadline(now, severity);
                metadata.Severity = severity;
                TryApplyLowHealthResolve(pop, metadata, severity);
                pop.SimulationLod = PopulationSimulationLod.Engaged;
                ScheduleNow(pop, now);
                return true;
            }

            // Reuse the established fight/flee scoring, wake-up and threat-deadline path for a
            // genuinely new encounter. Then replace the legacy positional anchor with an NPC
            // owned leash anchor.
            if (!NotifyThreat(populationActorId, threatPosition, severity, now))
                return false;

            pop.ThreatCharacterId = threatCharacterId;
            pop.ThreatAnchorPosition = ResolveReactiveThreatAnchor(pop);
            pop.ThreatEngageRange = engageRange;
            metadata.Severity = severity;
            metadata.LowHealthDecisionMade = false;
            TryApplyLowHealthResolve(pop, metadata, severity);
            return true;
        }

        private void TryApplyLowHealthResolve(
            PopulationActorRuntime pop,
            ReactiveThreatMetadata metadata,
            float severity)
        {
            if (pop?.Actor == null ||
                metadata == null ||
                metadata.LowHealthDecisionMade ||
                pop.Actor.HealthMaximum <= 0)
            {
                return;
            }

            float threshold = Math.Clamp(ReactiveLowHealthDecisionThreshold, 0.05f, 0.95f);
            float healthFraction =
                (float)pop.Actor.HealthCurrent / Math.Max(1, pop.Actor.HealthMaximum);
            if (healthFraction > threshold)
                return;

            // This is deliberately a one-time resolve check for the encounter. Reuse the
            // established aggression/courage/combat-skill weighting, but do NOT reuse the
            // legacy "must be above 35% HP to fight" gate. At this threshold the point of the
            // decision is specifically whether this NPC breaks and flees or commits to fighting
            // until the encounter ends.
            metadata.LowHealthDecisionMade = true;

            float normalizedSeverity = Math.Clamp(severity, 0f, 1f);
            float fightScore =
                pop.Aggression * 0.55f +
                pop.Courage * 0.30f +
                pop.CombatSkill * 0.15f;
            float fleeScore =
                (1f - pop.Courage) * 0.65f +
                (1f - pop.Aggression) * 0.25f +
                normalizedSeverity * 0.10f;

            float totalScore = Math.Max(0.0001f, fightScore + fleeScore);
            float fightChance = Math.Clamp(fightScore / totalScore, 0f, 1f);
            double behaviorRoll = pop.Random?.NextDouble() ?? 0.5d;

            if (behaviorRoll < fightChance)
            {
                pop.AiState = PopulationAiState.Fighting;
                pop.RouteReason = PopulationRouteReason.Pursuing;
            }
            else
            {
                pop.AiState = PopulationAiState.Fleeing;
                pop.RouteReason = PopulationRouteReason.Fleeing;
            }
        }

        private bool ShouldSwitchReactiveThreat(
            PopulationActorRuntime pop,
            float currentSeverity,
            float incomingSeverity)
        {
            if (pop?.Actor == null || pop.ThreatCharacterId <= 0)
                return true;

            // Only treat absence as authoritative invalidation when this map partition has already
            // been prepared. If no partition exists yet, do not guess that the target disconnected.
            if (_playersByPartition.TryGetValue(
                    MapKey(pop.Actor.MapId, pop.Actor.InstanceId),
                    out PlayerSpatialPartition partition))
            {
                if (!partition.ByCharacterId.TryGetValue(
                        pop.ThreatCharacterId,
                        out PopulationPlayerView currentTarget) ||
                    !currentTarget.Alive)
                {
                    return true;
                }
            }

            float advantage = Math.Clamp(ReactiveThreatSwitchSeverityAdvantage, 0f, 1f);
            return incomingSeverity + 0.0001f >= currentSeverity + advantage;
        }

        private static WorldPosition ResolveReactiveThreatAnchor(PopulationActorRuntime pop)
        {
            if (pop?.Actor == null)
                return default;

            // Free-roam and stationary actors already own a canonical home anchor. Route actors
            // may have travelled a long distance from their authored spawn, so their encounter
            // begins at the authoritative position where combat actually started.
            return pop.MovementBehavior == SharedAiMovementMode.FreeRoam ||
                   pop.MovementBehavior == SharedAiMovementMode.Stationary
                ? pop.HomePosition
                : pop.Actor.Position;
        }

        private static double ReactiveThreatDeadline(double now, float severity) =>
            now + 5d + Math.Clamp(severity, 0f, 1f) * 8d;

        /// <summary>
        /// Refreshes the authoritative attack range used by an already-active reactive threat.
        /// The GameServer host calls this after combat/loadout activation so chase/attack
        /// positioning follows the current canonical BasicAttack range rather than a stale
        /// value captured when threat was first established.
        ///
        /// This is server-only transient state. It does not add persistence or wire state.
        /// </summary>
        public bool UpdateReactiveThreatEngageRange(long populationActorId, float engageRange)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) ||
                pop?.Actor == null ||
                !pop.Actor.Alive ||
                pop.ThreatCharacterId <= 0 ||
                pop.ThreatUntil <= 0d)
            {
                return false;
            }

            pop.ThreatEngageRange = Math.Max(0.5f, engageRange);
            return true;
        }

        /// <summary>
        /// Time-guarded range refresh used by the current GameServer host. The host passes its
        /// authoritative scheduler time so an expired reactive threat cannot have its chase/
        /// attack range refreshed after the encounter deadline has already elapsed.
        /// </summary>
        public bool UpdateReactiveThreatEngageRange(
            long populationActorId,
            float engageRange,
            double now)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) ||
                pop?.Actor == null ||
                !pop.Actor.Alive ||
                pop.ThreatCharacterId <= 0 ||
                pop.ThreatUntil <= now)
            {
                return false;
            }

            pop.ThreatEngageRange = Math.Max(0.5f, engageRange);
            return true;
        }

        /// <summary>
        /// Compatibility overload retained for branches that guard by target id instead of time.
        /// </summary>
        public bool UpdateReactiveThreatEngageRange(
            long populationActorId,
            long threatCharacterId,
            float engageRange)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) ||
                pop?.Actor == null ||
                !pop.Actor.Alive ||
                threatCharacterId <= 0 ||
                pop.ThreatCharacterId != threatCharacterId ||
                pop.ThreatUntil <= 0d)
            {
                return false;
            }

            pop.ThreatEngageRange = Math.Max(0.5f, engageRange);
            return true;
        }

        // Compatibility overload for callers that already have the Population runtime.
        public bool UpdateReactiveThreatEngageRange(
            PopulationActorRuntime pop,
            float engageRange)
        {
            if (pop?.Actor == null)
                return false;

            return UpdateReactiveThreatEngageRange(
                pop.Actor.Handle.actorId,
                engageRange);
        }

        public bool UpdateReactiveThreatEngageRange(
            PopulationActorRuntime pop,
            float engageRange,
            double now)
        {
            if (pop?.Actor == null)
                return false;

            return UpdateReactiveThreatEngageRange(
                pop.Actor.Handle.actorId,
                engageRange,
                now);
        }

        // Guarded runtime-object overload matching the newer three-argument contract.
        public bool UpdateReactiveThreatEngageRange(
            PopulationActorRuntime pop,
            long threatCharacterId,
            float engageRange)
        {
            if (pop?.Actor == null)
                return false;

            return UpdateReactiveThreatEngageRange(
                pop.Actor.Handle.actorId,
                threatCharacterId,
                engageRange);
        }

        public bool TryGetReactiveThreatTarget(PopulationActorRuntime pop, out long characterId)
        {
            characterId = pop?.ThreatCharacterId ?? 0L;
            return pop?.Actor != null && pop.Actor.Alive && characterId > 0;
        }

        /// <summary>
        /// Ends one reactive player threat immediately. Used when the target dies,
        /// disconnects, changes world, or otherwise becomes invalid between AI work items.
        /// </summary>
        public bool CancelReactiveThreat(long populationActorId, double now)
        {
            if (!_population.TryGetValue(populationActorId, out PopulationActorRuntime pop) ||
                pop?.Actor == null)
            {
                return false;
            }

            EndThreatBehavior(pop, now);
            ScheduleNow(pop, now);
            return true;
        }

        /// <summary>
        /// Reactive V1 threat work. Player-bound threats follow the live player position;
        /// legacy positional/scripted threats continue through the existing TickThreat path.
        /// </summary>
        private void TickReactiveThreat(PopulationActorRuntime pop, float dt, double now)
        {
            if (pop?.Actor == null)
                return;

            if (pop.ThreatCharacterId <= 0)
            {
                TickThreat(pop, dt, now);
                return;
            }

            if (!TryGetReactiveThreatPlayer(pop, out PopulationPlayerView target) || !target.Alive)
            {
                EndThreatBehavior(pop, now);
                return;
            }

            // Fleeing and fighting react to the attacker's CURRENT authoritative position.
            pop.ThreatPosition = target.Position;

            float leash = Math.Max(4f, pop.LeashRadius);
            if (DistanceXZ(target.Position, pop.ThreatAnchorPosition) > leash ||
                DistanceXZ(pop.Actor.Position, pop.ThreatAnchorPosition) > leash)
            {
                EndThreatBehavior(pop, now);
                return;
            }

            if (pop.AiState == PopulationAiState.Fleeing)
            {
                TickThreat(pop, dt, now);
                return;
            }

            if (pop.AiState != PopulationAiState.Fighting)
            {
                EndThreatBehavior(pop, now);
                return;
            }

            TickReactiveFight(pop, target, dt, now);
        }

        private bool TryGetReactiveThreatPlayer(
            PopulationActorRuntime pop,
            out PopulationPlayerView target)
        {
            target = default;
            if (pop?.Actor == null || pop.ThreatCharacterId <= 0)
                return false;

            if (!_playersByPartition.TryGetValue(
                    MapKey(pop.Actor.MapId, pop.Actor.InstanceId),
                    out PlayerSpatialPartition partition))
            {
                return false;
            }

            return partition.ByCharacterId.TryGetValue(pop.ThreatCharacterId, out target);
        }

        private void TickReactiveFight(
            PopulationActorRuntime pop,
            PopulationPlayerView target,
            float dt,
            double now)
        {
            if (!TryGetGraph(pop.Actor, out MapGraph graph))
            {
                EndThreatBehavior(pop, now);
                return;
            }

            float dx = target.Position.X - pop.Actor.Position.X;
            float dz = target.Position.Z - pop.Actor.Position.Z;
            float distanceSq = dx * dx + dz * dz;
            float engageRange = Math.Max(0.5f, pop.ThreatEngageRange);
            bool withinHorizontalRange = distanceSq <= engageRange * engageRange;
            bool withinVerticalRange =
                Math.Abs(target.Position.Y - pop.Actor.Position.Y) <= 1.5f;
            bool hasLineOfSight =
                HasReactiveCombatLineOfSight(graph.Collision, pop.Actor.Position, target.Position);

            if (withinHorizontalRange && withinVerticalRange && hasLineOfSight)
            {
                StopAndFaceReactiveTarget(pop, dx, dz);
                FightIntent?.Invoke(pop);
                return;
            }

            // No full pathfinder is introduced here. Reuse the existing authoritative motor,
            // collision and stuck-recovery path exactly as flee/free-roam movement already does.
            if (distanceSq < 0.001f)
            {
                StopAndFaceReactiveTarget(pop, dx, dz);
                return;
            }

            float inv = 1f / MathF.Sqrt(distanceSq);
            pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));

            if (graph.Collision != null && pop.Motor != null && pop.MotorState != null)
            {
                var intent = new CharacterMovementIntent(dx * inv, dz * inv, true, false);
                pop.Motor.Tick(pop.MotorState, intent, dt, graph.Collision);
                ApplyMotor(pop);
                DetectStuck(graph, pop, now);
                return;
            }

            // Preserve mapless/service-test compatibility without adding another movement owner.
            WorldPosition previous = pop.Actor.Position;
            float distance = MathF.Sqrt(distanceSq);
            float step = Math.Min(distance, Math.Max(0.1f, pop.RunSpeed) * dt);
            pop.Actor.Position = new WorldPosition(
                previous.X + dx * inv * step,
                previous.Y,
                previous.Z + dz * inv * step);
            pop.Actor.LastSafePosition = pop.Actor.Position;
            pop.Actor.MovementMode = ActorMovementMode.Grounded;
            pop.Actor.VelocityX = pop.Actor.Position.X - previous.X;
            pop.Actor.VelocityY = pop.Actor.Position.Y - previous.Y;
            pop.Actor.VelocityZ = pop.Actor.Position.Z - previous.Z;
            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);
        }

        private void StopAndFaceReactiveTarget(PopulationActorRuntime pop, float dx, float dz)
        {
            if (pop?.Actor == null)
                return;

            float nextYaw = pop.Actor.YawDegrees;
            if (dx * dx + dz * dz > 0.0001f)
                nextYaw = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));

            bool changed =
                Math.Abs(pop.Actor.YawDegrees - nextYaw) > 0.05f ||
                Math.Abs(pop.Actor.VelocityX) > 0.0001f ||
                Math.Abs(pop.Actor.VelocityY) > 0.0001f ||
                Math.Abs(pop.Actor.VelocityZ) > 0.0001f;

            pop.Actor.YawDegrees = nextYaw;
            pop.Actor.VelocityX = 0f;
            pop.Actor.VelocityY = 0f;
            pop.Actor.VelocityZ = 0f;

            if (!changed)
                return;

            _actors.PublishChanged(pop.Actor);
            Changed?.Invoke(pop);
        }

        private static bool HasReactiveCombatLineOfSight(
            ServerCollisionWorld collision,
            WorldPosition sourceFeet,
            WorldPosition targetFeet)
        {
            if (collision == null)
                return true;

            var sourceTorso = new WorldPosition(sourceFeet.X, sourceFeet.Y + 1.15f, sourceFeet.Z);
            var targetTorso = new WorldPosition(targetFeet.X, targetFeet.Y + 1.15f, targetFeet.Z);
            if (!collision.IsLineObstructed(sourceTorso, targetTorso))
                return true;

            var sourceHead = new WorldPosition(sourceFeet.X, sourceFeet.Y + 1.65f, sourceFeet.Z);
            var targetHead = new WorldPosition(targetFeet.X, targetFeet.Y + 1.65f, targetFeet.Z);
            return !collision.IsLineObstructed(sourceHead, targetHead);
        }

        private void EndThreatBehavior(PopulationActorRuntime pop, double now)
        {
            if (pop?.Actor == null)
                return;

            if (_reactiveThreatMetadata.TryGetValue(pop, out ReactiveThreatMetadata metadata))
            {
                metadata.Severity = 0f;
                metadata.LowHealthDecisionMade = false;
            }

            pop.ThreatUntil = 0d;
            pop.ThreatCharacterId = 0L;
            pop.ThreatPosition = default;
            pop.ThreatAnchorPosition = default;
            pop.ThreatEngageRange = 0f;

            if (pop.AiState != PopulationAiState.Fleeing &&
                pop.AiState != PopulationAiState.Fighting)
            {
                return;
            }

            pop.RouteReason = PopulationRouteReason.Recovery;
            if (pop.MovementBehavior == SharedAiMovementMode.Route)
            {
                pop.AiState = PopulationAiState.Recovering;
                ReattachToNearestRoute(pop);
            }
            else
            {
                pop.AiState = PopulationAiState.Idle;
                pop.HasRoamTarget = false;
                pop.NextRoamDecisionAt = now;
            }
        }
    }
}
