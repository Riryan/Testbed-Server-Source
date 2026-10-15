using System;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    public sealed partial class PopulationSimulationService
    {
        /// <summary>
        /// Cheap, authoritative ambient route motion. Keep the existing route target,
        /// lane selection, actor publication, AOI and threat/recovery behavior. Only
        /// skip the full CharacterMotor for a grounded non-combat follower when every
        /// position traversed this tick passes the existing collision world's ground,
        /// standing-static and live dynamic-blocker checks.
        ///
        /// Important: validatedClear currently proves an authored CENTERLINE, not
        /// the lateral route lane. Never infer a collision-free lane from that bit.
        /// The bounded capsule sweep below is the independent runtime proof.
        /// </summary>
        private bool TryTickAmbientRouteFast(
            MapGraph graph,
            PopulationActorRuntime pop,
            WorldPosition target,
            float dt,
            double now)
        {
            if (graph?.Collision == null || pop?.Actor == null ||
                pop.Motor == null || pop.MotorState == null ||
                pop.MovementBehavior != SharedAiMovementMode.Route ||
                pop.AwaitingPlayerActivation || !pop.Actor.Alive ||
                pop.PortalPhase != PopulationPortalSequencePhase.None ||
                pop.CurrentNodeId <= 0 || pop.NextNodeId <= 0 ||
                pop.CurrentNodeId == pop.NextNodeId ||
                (pop.AiState != PopulationAiState.FollowingRoute &&
                 pop.AiState != PopulationAiState.Waiting) ||
                pop.ThreatUntil > now ||
                pop.Actor.MovementMode != ActorMovementMode.Grounded ||
                pop.MotorState.Mode != ActorMovementMode.Grounded ||
                !pop.MotorState.Grounded ||
                !float.IsFinite(dt) || dt <= 0f)
            {
                return false;
            }

            // For an authored but invalid edge, the ordinary graph already has
            // a fail-closed filter in BuildGraphs. Check it again before the fast
            // movement branch rather than relaxing any edge admission contract.
            if (!graph.Outgoing.TryGetValue(pop.CurrentNodeId,
                    out System.Collections.Generic.List<ServerPopulationRouteEdge> outgoing))
                return false;
            bool admitted = false;
            for (int i = 0; i < outgoing.Count; ++i)
            {
                ServerPopulationRouteEdge edge = outgoing[i];
                if (edge != null && edge.toNodeId == pop.NextNodeId && edge.validatedClear)
                {
                    admitted = true;
                    break;
                }
            }
            if (!admitted)
                return false;

            var settings = pop.Motor.Settings;
            if (settings == null)
                return false;

            WorldPosition start = pop.Actor.Position;
            float dx = target.X - start.X;
            float dz = target.Z - start.Z;
            float distanceSq = dx * dx + dz * dz;
            if (!float.IsFinite(distanceSq) || distanceSq <= 0.000001f)
                return false;

            float distance = MathF.Sqrt(distanceSq);
            float distanceToMove = Math.Min(distance, settings.WalkSpeed * dt);
            if (!float.IsFinite(distanceToMove) || distanceToMove <= 0f)
                return false;

            var capsule = new ServerCapsule(settings.Radius, settings.Height);
            // The sampling distance is intentionally SMALLER than capsule radius.
            // A thin closed door or wall cannot be jumped over between samples.
            // A highly accelerated/unreasonable move retains the existing motor.
            float sweepInterval = Math.Max(0.05f, capsule.Radius * 0.5f);
            int samples = (int)MathF.Ceiling(distanceToMove / sweepInterval);
            if (samples < 1 || samples > 24)
                return false;

            float inv = 1f / distance;
            float moveX = dx * inv * distanceToMove;
            float moveZ = dz * inv * distanceToMove;
            WorldPosition cursor = start;
            long surfaceId = pop.MotorState.GroundSurfaceId;

            // No Actor or MotorState mutations until the WHOLE candidate motion
            // has been validated. On any failure the original motor executes and
            // owns blocked-route recovery with no partial movement or double publish.
            for (int i = 1; i <= samples; ++i)
            {
                float t = i / (float)samples;
                WorldPosition probe = new WorldPosition(
                    start.X + moveX * t,
                    cursor.Y,
                    start.Z + moveZ * t);
                if (!graph.Collision.TryFindGround(
                        probe,
                        capsule.Radius,
                        settings.StepHeight + 0.05f,
                        settings.GroundSnapDistance + 0.05f,
                        settings.MaximumSlopeDegrees,
                        out ServerGroundHit ground))
                    return false;

                float rise = ground.Position.Y - cursor.Y;
                if (!float.IsFinite(rise) ||
                    rise > settings.StepHeight + 0.05f ||
                    rise < -settings.GroundSnapDistance - 0.05f)
                    return false;

                if (!graph.Collision.IsStandingCapsuleClearStatic(
                        ground.Position, capsule, settings.MaximumSlopeDegrees) ||
                    !graph.Collision.IsDynamicCapsuleClear(ground.Position, capsule))
                    return false;

                cursor = ground.Position;
                surfaceId = ground.SurfaceId;
            }

            pop.Actor.YawDegrees = NormalizeYaw(
                MathF.Atan2(dx, dz) * (180f / MathF.PI));
            pop.MotorState.Position = cursor;
            pop.MotorState.LastSafePosition = cursor;
            pop.MotorState.GroundSurfaceId = surfaceId;
            pop.MotorState.VerticalVelocity = 0f;
            pop.MotorState.Grounded = true;
            pop.MotorState.Mode = ActorMovementMode.Grounded;
            // Keep the original route-progress state synchronized for Active ->
            // CoarseRoute transitions. Existing coarse scheduling reconstructs
            // position from this value, rather than running a motor.
            if (graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode fromNode) &&
                graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode toNode))
            {
                float edgeX = toNode.pose.x - fromNode.pose.x;
                float edgeZ = toNode.pose.z - fromNode.pose.z;
                float edgeLengthSq = edgeX * edgeX + edgeZ * edgeZ;
                if (edgeLengthSq > 0.0001f)
                {
                    float along = ((cursor.X - fromNode.pose.x) * edgeX +
                                   (cursor.Z - fromNode.pose.z) * edgeZ) / edgeLengthSq;
                    pop.SegmentProgress = Math.Clamp(along, 0f, 1f);
                }
            }
            ApplyMotor(pop); // existing spatial/AOI/observer event route
            DetectStuck(graph, pop, now);
            return true;
        }
    }
}
