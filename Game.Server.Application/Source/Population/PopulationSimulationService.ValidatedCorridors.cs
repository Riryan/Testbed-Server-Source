using System;
using System.Collections.Generic;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    /// <summary>
    /// TEMPORARY TEST MODE: routed Population follows the EXISTING baked graph directly.
    /// No static triangle queries, per-segment clearance warming, or character motor calls
    /// during normal routed movement. This deliberately prioritizes inexpensive deterministic
    /// routing over physical precision, and should not be regarded as a production-safe
    /// replacement for fully validated corridors.
    ///
    /// Uses the pre-existing TickActive hook from Population Validated Route Corridors V1.
    /// Nothing here creates network messages, actors, new schedules, or a second route graph.
    /// Existing dynamic WorldInteractable blockers are still checked along each movement step.
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

            // Never allow the rail shortcut to override authoritative incapacitation.
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
                return false; // preserve the existing explicit portal sequence

            if (pop.CurrentNodeId <= 0 || pop.NextNodeId <= 0 ||
                !graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode to))
                return true; // no valid rail target; do not run off-route via the motor

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
                // Spawn/portal anchor -> its assigned first route marker. This is a
                // direct, bounded-speed join, not a teleport and not an ordinary edge.
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
                // Fail closed to movement, NOT over to the full motor, if the current
                // segment is absent from the validated baked graph.
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
                    // Existing coarse simulation owns SegmentProgress. Projection also
                    // permits a seamless transition if an actor was previously motor-driven.
                    float projected = ((previous.X - a.X) * dx +
                                       (previous.Z - a.Z) * dz) / lengthSquared;
                    float startT = Math.Clamp(
                        Math.Max(pop.SegmentProgress, projected), 0f, 1f);
                    float endT = Math.Min(1f, startT + travel / length);
                    // V2 debug mode uses only the validated CENTERLINE. Previous lane
                    // offsets are deliberately ignored: lateral width wasn't proven safe.
                    destination = new WorldPosition(
                        a.X + dx * endT,
                        a.Y + (b.Y - a.Y) * endT,
                        a.Z + dz * endT);
                    yaw = MathF.Atan2(dx, dz) * (180f / MathF.PI);
                    reached = endT >= 1f;
                    // Commit SegmentProgress only after live blocker checks succeed.
                    // The candidate is stored below on success via the same projection.
                }
            }

            var capsule = new ServerCapsule(pop.CapsuleRadius, pop.CapsuleHeight);
            if (!IsRailStepDynamicallyClear(graph.Collision, previous, destination, capsule))
            {
                // Never try a motor detour around a door/obstruction in rail-only mode.
                pop.MotorState.Position = previous;
                pop.MotorState.LastSafePosition = previous;
                pop.MotorState.VerticalVelocity = 0f;
                pop.MotorState.Grounded = true;
                pop.MotorState.Mode = ActorMovementMode.Grounded;
                ApplyMotor(pop);
                RecoverBlockedRoute(pop, now); // existing route avoidance/cooldown
                return true;
            }

            pop.Actor.YawDegrees = NormalizeYaw(yaw);
            pop.LaneOffset = 0f;
            pop.MotorState.Position = destination;
            pop.MotorState.LastSafePosition = destination;
            pop.MotorState.VerticalVelocity = 0f;
            pop.MotorState.Grounded = true;
            pop.MotorState.Mode = ActorMovementMode.Grounded;
            ApplyMotor(pop); // existing authority, AOI, spatial index and presentation hooks

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

        private static bool IsRailStepDynamicallyClear(
            ServerCollisionWorld world,
            WorldPosition from,
            WorldPosition to,
            ServerCapsule capsule)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float dz = to.Z - from.Z;
            float horizontal = MathF.Sqrt(dx * dx + dz * dz);
            // These are CHEAP dynamic-blocker queries, never static triangle queries.
            // Divide the movement chord so a single long step cannot skip a closed door.
            int checks = Math.Clamp(
                (int)Math.Ceiling(horizontal / Math.Max(0.15f, capsule.Radius * 0.75f)),
                1, 16);
            for (int i = 1; i <= checks; ++i)
            {
                float t = i / (float)checks;
                WorldPosition probe = new WorldPosition(
                    from.X + dx * t, from.Y + dy * t, from.Z + dz * t);
                if (!world.IsDynamicCapsuleClear(probe, capsule))
                    return false;
            }
            return true;
        }
    }
}
