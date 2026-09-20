using System;
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
        /// Binds an existing positional threat to the player character that caused it.
        /// This remains transient runtime state: no persistence and no additional wire state.
        /// The old positional overload remains valid for non-player/scripted threats.
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

            bool sameActiveThreat =
                pop.ThreatCharacterId == threatCharacterId &&
                pop.ThreatUntil > now;
            WorldPosition anchor = sameActiveThreat
                ? pop.ThreatAnchorPosition
                : threatPosition;

            // Reuse the established fight/flee scoring, wake-up and threat deadline path.
            if (!NotifyThreat(populationActorId, threatPosition, severity, now))
                return false;

            pop.ThreatCharacterId = threatCharacterId;
            pop.ThreatAnchorPosition = anchor;
            pop.ThreatEngageRange = Math.Max(0.5f, engageRange);
            return true;
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
