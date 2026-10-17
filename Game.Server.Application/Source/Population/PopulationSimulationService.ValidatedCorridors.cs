using System;
using System.Collections.Generic;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    /// <summary>
    /// Authored-route-only movement for ordinary routed Population.
    /// Uses the existing baked graph; the map's validatedClear edges are the
    /// movement contract. No per-step dynamic door/prop checks or character
    /// motor calls occur on this path. Combat/off-route movement remains with
    /// the existing authoritative systems, and no network protocol is added.
    /// </summary>
    public sealed partial class PopulationSimulationService
    {
        private bool TryTickValidatedRouteCorridor(
            PopulationActorRuntime pop, MapGraph graph, float dt, double now)
        {
            if (pop == null || pop.Actor == null || pop.MotorState == null ||
                graph == null || graph.Collision == null ||
                pop.MovementBehavior != SharedAiMovementMode.Route)
                return false;

            // Do not override authoritative incapacitation or portal state.
            ActorMovementMode mode = pop.MotorState.Mode;
            if (mode == ActorMovementMode.Dead ||
                mode == ActorMovementMode.InteractionLocked ||
                mode == ActorMovementMode.Vaulting ||
                mode == ActorMovementMode.Ladder ||
                mode == ActorMovementMode.Mounted)
                return true;

            if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt))
                return true;
            if (pop.PortalPhase != PopulationPortalSequencePhase.None)
                return false;

            if (pop.CurrentNodeId <= 0 || pop.NextNodeId <= 0 ||
                !graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode to))
                return true; // no route target: do not wander through collision geometry

            WorldPosition previous = pop.Actor.Position;
            WorldPosition destination;
            float yaw;
            bool reached;
            float speed = pop.AiState == PopulationAiState.Fleeing
                ? Math.Max(0.1f, pop.RunSpeed)
                : Math.Max(0.1f, pop.WalkSpeed);
            float travel = speed * dt;

            if (pop.CurrentNodeId == pop.NextNodeId)
            {
                // Spawn/portal to first route marker; bounded by authored speed.
                WorldPosition waypoint = to.pose.ToWorldPosition();
                float dx = waypoint.X - previous.X;
                float dz = waypoint.Z - previous.Z;
                float distance = MathF.Sqrt(dx * dx + dz * dz);
                if (distance <= 0.001f)
                {
                    destination = waypoint;
                    yaw = pop.Actor.YawDegrees;
                    reached = true;
                }
                else
                {
                    float fraction = Math.Min(1f, travel / distance);
                    destination = new WorldPosition(
                        previous.X + dx * fraction,
                        previous.Y + (waypoint.Y - previous.Y) * fraction,
                        previous.Z + dz * fraction);
                    yaw = MathF.Atan2(dx, dz) * (180f / MathF.PI);
                    reached = fraction >= 1f;
                }
            }
            else
            {
                // An invalid authored edge must not silently enter the expensive
                // full player-grade collision motor. Reuse the route recovery.
                if (!graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode from) ||
                    !graph.Outgoing.TryGetValue(pop.CurrentNodeId,
                        out List<ServerPopulationRouteEdge> edges))
                    return true;

                bool validEdge = false;
                for (int i = 0; i < edges.Count; ++i)
                {
                    ServerPopulationRouteEdge edge = edges[i];
                    if (edge != null && edge.toNodeId == pop.NextNodeId && edge.validatedClear)
                    {
                        validEdge = true;
                        break;
                    }
                }
                if (!validEdge)
                {
                    RecoverBlockedRoute(pop, now);
                    return true;
                }

                WorldPosition a = from.pose.ToWorldPosition();
                WorldPosition b = to.pose.ToWorldPosition();
                float dx = b.X - a.X;
                float dz = b.Z - a.Z;
                float lengthSquared = dx * dx + dz * dz;
                if (lengthSquared < 0.0025f)
                {
                    destination = b;
                    yaw = pop.Actor.YawDegrees;
                    reached = true;
                    pop.SegmentProgress = 1f;
                }
                else
                {
                    float length = MathF.Sqrt(lengthSquared);
                    float projected = ((previous.X - a.X) * dx +
                                       (previous.Z - a.Z) * dz) / lengthSquared;
                    float startT = Math.Clamp(
                        Math.Max(pop.SegmentProgress, projected), 0f, 1f);
                    float endT = Math.Min(1f, startT + travel / length);
                    // Existing centerline rail. No door, dynamic prop, or spatial
                    // collision query: authored graph controls route movement.
                    destination = new WorldPosition(
                        a.X + dx * endT,
                        a.Y + (b.Y - a.Y) * endT,
                        a.Z + dz * endT);
                    yaw = MathF.Atan2(dx, dz) * (180f / MathF.PI);
                    reached = endT >= 1f;
                }
            }

            pop.Actor.YawDegrees = NormalizeYaw(yaw);
            pop.LaneOffset = 0f;
            pop.MotorState.Position = destination;
            pop.MotorState.LastSafePosition = destination;
            pop.MotorState.VerticalVelocity = 0f;
            pop.MotorState.Grounded = true;
            pop.MotorState.Mode = ActorMovementMode.Grounded;
            ApplyMotor(pop); // preserve canonical spatial/AOI/presentation publication

            if (reached)
            {
                ArriveNode(graph, pop, now);
            }
            else if (pop.CurrentNodeId != pop.NextNodeId &&
                graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode start))
            {
                WorldPosition a = start.pose.ToWorldPosition();
                WorldPosition b = to.pose.ToWorldPosition();
                float dx = b.X - a.X;
                float dz = b.Z - a.Z;
                float lengthSquared = dx * dx + dz * dz;
                if (lengthSquared >= 0.0025f)
                    pop.SegmentProgress = Math.Clamp(
                        ((destination.X - a.X) * dx + (destination.Z - a.Z) * dz) /
                        lengthSquared, 0f, 1f);
                DetectStuck(graph, pop, now);
            }

            return true;
        }
    }
}
