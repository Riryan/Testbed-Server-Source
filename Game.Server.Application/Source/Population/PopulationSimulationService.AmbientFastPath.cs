using System;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.Server.Application.Population
{
    public sealed partial class PopulationSimulationService
    {
        private double _diagnosticAmbientCollisionMs;
        private int _diagnosticAmbientSamples;
        private int _diagnosticAmbientFallbacks;

        // Existing startup-only certification against immutable baked geometry.
        // No extra periodic work, new runtime manager, or wire message.
        private static void CertifyBakedRouteLanes(MapGraph graph)
        {
            if (graph?.Collision == null || graph.Nodes.Count == 0)
                return;
            float radius = Math.Max(0.20f,
                graph.Map.navMesh != null ? graph.Map.navMesh.agentRadius : 0.35f);
            float height = Math.Max(radius * 2f,
                graph.Map.navMesh != null ? graph.Map.navMesh.agentHeight : 1.8f);
            ServerSpawnAnchor[] anchors = graph.Map.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
            for (int i = 0; i < anchors.Length; ++i)
            {
                ServerSpawnAnchor anchor = anchors[i];
                if (anchor == null || !anchor.enabled || anchor.kind != ServerSpawnKind.Population)
                    continue;
                radius = Math.Max(radius, anchor.capsuleRadius);
                height = Math.Max(height, anchor.capsuleHeight);
            }
            var capsule = new ServerCapsule(radius, height);
            int accepted = 0, rejected = 0;
            foreach (var pair in graph.Outgoing)
            {
                var edges = pair.Value;
                for (int i = 0; i < edges.Count; ++i)
                {
                    ServerPopulationRouteEdge edge = edges[i];
                    if (edge == null || !edge.validatedClear ||
                        !graph.Nodes.TryGetValue(edge.fromNodeId, out ServerPopulationRouteNode from) ||
                        !graph.Nodes.TryGetValue(edge.toNodeId, out ServerPopulationRouteNode to))
                        continue;
                    for (int lane = -1; lane <= 1; ++lane)
                    {
                        float laneOffset = lane * Math.Max(0f, edge.width * 0.35f);
                        bool certified = graph.Collision.IsStaticPopulationLaneCertified(
                            from.pose.ToWorldPosition(), to.pose.ToWorldPosition(),
                            laneOffset, capsule, maximumSlopeDegrees: 50f);
                        if (certified)
                        {
                            graph.CertifiedLanes.Add((edge.fromNodeId, edge.toNodeId, lane));
                            ++accepted;
                        }
                        else ++rejected;
                    }
                }
            }
        }

        // Authored route travel does not examine dynamic doors or props each step.
        // Certified lanes preserve the current lane offset; uncatalogued or off-lane
        // ordinary travel reuses the existing baked-graph rail helper rather than the
        // full player-grade collision motor. Threat/flee handling stays separate.
        private bool TryTickAmbientRouteFast(
            MapGraph graph, PopulationActorRuntime pop, WorldPosition target,
            float dt, double now, bool fleeing = false)
        {
            if (fleeing)
                return TryTickAmbientRouteCertified(graph, pop, dt, now, fleeing: true);

            if (pop?.Actor == null || pop.MotorState == null ||
                pop.MovementBehavior != SharedAiMovementMode.Route ||
                !pop.Actor.Alive || pop.AwaitingPlayerActivation ||
                pop.PortalPhase != PopulationPortalSequencePhase.None ||
                (pop.AiState != PopulationAiState.FollowingRoute &&
                 pop.AiState != PopulationAiState.Waiting) ||
                pop.ThreatUntil > now)
                return false;

            if (TryTickAmbientRouteCertified(graph, pop, dt, now, fleeing: false))
                return true;

            // Existing dormant rail helper: valid baked routes only, no live door
            // checks, no movement motor. It owns normal route arrival and recovery.
            return TryTickValidatedRouteCorridor(pop, graph, dt, now);
        }

        private bool TryTickAmbientRouteCertified(
            MapGraph graph, PopulationActorRuntime pop, float dt, double now, bool fleeing)
        {
            if (graph?.Collision == null || pop?.Actor == null ||
                pop.Motor == null || pop.MotorState == null ||
                pop.MovementBehavior != SharedAiMovementMode.Route ||
                !pop.Actor.Alive || pop.AwaitingPlayerActivation ||
                pop.PortalPhase != PopulationPortalSequencePhase.None ||
                pop.CurrentNodeId <= 0 || pop.NextNodeId <= 0 ||
                pop.CurrentNodeId == pop.NextNodeId ||
                pop.Actor.MovementMode != ActorMovementMode.Grounded ||
                pop.MotorState.Mode != ActorMovementMode.Grounded ||
                !pop.MotorState.Grounded ||
                (!fleeing && (pop.AiState != PopulationAiState.FollowingRoute &&
                              pop.AiState != PopulationAiState.Waiting || pop.ThreatUntil > now)) ||
                (fleeing && pop.AiState != PopulationAiState.Fleeing) ||
                !float.IsFinite(dt) || dt <= 0f)
                return false;
            if (!graph.Outgoing.TryGetValue(pop.CurrentNodeId,
                    out System.Collections.Generic.List<ServerPopulationRouteEdge> edges))
                return false;
            ServerPopulationRouteEdge selected = null;
            for (int i = 0; i < edges.Count; ++i)
                if (edges[i] != null && edges[i].toNodeId == pop.NextNodeId && edges[i].validatedClear)
                { selected = edges[i]; break; }
            if (selected == null ||
                !graph.Nodes.TryGetValue(pop.CurrentNodeId, out ServerPopulationRouteNode from) ||
                !graph.Nodes.TryGetValue(pop.NextNodeId, out ServerPopulationRouteNode to))
                return false;

            float standardOffset = Math.Max(0f, selected.width * 0.35f);
            int lane = 0;
            if (Math.Abs(pop.LaneOffset) > 0.03f)
            {
                if (standardOffset <= 0.03f ||
                    Math.Abs(Math.Abs(pop.LaneOffset) - standardOffset) > 0.03f)
                    return false;
                lane = pop.LaneOffset < 0f ? -1 : 1;
            }
            if (!graph.CertifiedLanes.Contains((pop.CurrentNodeId, pop.NextNodeId, lane)))
                return false;

            float dx = to.pose.x - from.pose.x;
            float dz = to.pose.z - from.pose.z;
            float edgeSq = dx * dx + dz * dz;
            if (!float.IsFinite(edgeSq) || edgeSq <= 0.0001f)
                return false;
            float edgeLength = MathF.Sqrt(edgeSq);
            WorldPosition start = pop.Actor.Position;
            float sideX = -dz / edgeLength;
            float sideZ = dx / edgeLength;
            float laneX = from.pose.x + sideX * pop.LaneOffset;
            float laneZ = from.pose.z + sideZ * pop.LaneOffset;
            float projected = Math.Clamp(((start.X - laneX) * dx +
                (start.Z - laneZ) * dz) / edgeSq, 0f, 1f);
            float expectedX = laneX + dx * projected;
            float expectedZ = laneZ + dz * projected;
            float expectedY = from.pose.y + (to.pose.y - from.pose.y) * projected;
            float offX = start.X - expectedX;
            float offZ = start.Z - expectedZ;
            if (offX * offX + offZ * offZ > 0.15f * 0.15f ||
                Math.Abs(start.Y - expectedY) > 0.12f)
                return false;

            float speed = fleeing ? pop.Motor.Settings.SprintSpeed : pop.Motor.Settings.WalkSpeed;
            if (!float.IsFinite(speed) || speed <= 0f)
                return false;
            float progress = Math.Min(1f, projected + speed * dt / edgeLength);
            float newX = laneX + dx * progress;
            float newZ = laneZ + dz * progress;
            float newY = from.pose.y + (to.pose.y - from.pose.y) * progress;
            var next = new WorldPosition(newX, newY, newZ);

            // The authored/certified lane is authoritative. No live door/prop query.
            pop.Actor.YawDegrees = NormalizeYaw(MathF.Atan2(dx, dz) * (180f / MathF.PI));
            pop.SegmentProgress = progress;
            pop.MotorState.Position = next;
            pop.MotorState.LastSafePosition = next;
            pop.MotorState.VerticalVelocity = 0f;
            pop.MotorState.Grounded = true;
            pop.MotorState.Mode = ActorMovementMode.Grounded;
            ApplyMotor(pop);
            if (progress >= 0.99999f ||
                Math.Abs(newX - (to.pose.x + sideX * pop.LaneOffset)) +
                Math.Abs(newZ - (to.pose.z + sideZ * pop.LaneOffset)) <= 0.15f)
                ArriveNode(graph, pop, now);
            else
                DetectStuck(graph, pop, now);
            return true;
        }
    }
}
