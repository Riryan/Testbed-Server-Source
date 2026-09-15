using System;
using System.Collections.Generic;
using Game.Server.Application.Lifecycle;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.World;

namespace Game.Server.Application.Spawning
{
    /// <summary>
    /// Resolves baked first-spawn/respawn/non-player anchors and revalidates every chosen
    /// pose against authoritative collision before use.
    /// </summary>
    public sealed class ServerSpawnService : ICharacterRespawnResolver
    {
        private readonly ServerMapCatalog _maps;
        private readonly float _maximumSlopeDegrees;

        public ServerSpawnService(ServerMapCatalog maps, float maximumSlopeDegrees = 50f)
        {
            _maps = maps ?? new ServerMapCatalog(Array.Empty<ServerMapSnapshot>());
            _maximumSlopeDegrees = Math.Clamp(maximumSlopeDegrees, 0f, 89f);
        }

        /// <summary>
        /// Resolves the canonical baked PlayerFirstSpawn anchor for a newly-created character.
        /// Character creation is fail-closed when authoritative map data or a valid first-spawn
        /// anchor is unavailable; an unvalidated content transform must never be persisted.
        /// </summary>
        public bool TryResolveFirstSpawn(CharacterLocationState legacyLocation, out CharacterLocationState location, out string detail)
        {
            location = legacyLocation;
            detail = string.Empty;

            if (!_maps.TryGet(legacyLocation.MapId, legacyLocation.InstanceId, out ServerMapSnapshot map) ||
                !_maps.TryGetCollisionWorld(legacyLocation.MapId, legacyLocation.InstanceId, out ServerCollisionWorld collision))
            {
                detail = $"authoritative first-spawn map data is unavailable for map '{legacyLocation.MapId}' instance '{DisplayInstance(legacyLocation.InstanceId)}'";
                return false;
            }

            ServerSpawnAnchor anchor = SelectNearestValidAnchor(
                map,
                collision,
                ServerSpawnKind.PlayerFirstSpawn,
                legacyLocation.Position,
                out ServerPose resolved,
                out detail);
            if (anchor == null)
                return false;

            location = new CharacterLocationState(
                legacyLocation.MapId,
                legacyLocation.InstanceId,
                resolved.ToWorldPosition(),
                NormalizeYaw(resolved.yaw));
            return true;
        }

        public bool TryResolveRespawn(PlayerRuntime runtime, out CharacterLocationState location, out string detail)
        {
            location = default;
            detail = string.Empty;
            if (runtime == null)
            {
                detail = "character is unavailable";
                return false;
            }

            CharacterLocationState current = runtime.Location;
            if (!_maps.TryGet(current.MapId, current.InstanceId, out ServerMapSnapshot map) ||
                !_maps.TryGetCollisionWorld(current.MapId, current.InstanceId, out ServerCollisionWorld collision))
            {
                detail = "baked respawn map data is unavailable";
                return false;
            }

            ServerSpawnAnchor anchor = SelectNearestValidAnchor(map, collision, ServerSpawnKind.PlayerRespawn, current.Position, out ServerPose resolved, out _);
            if (anchor == null)
                anchor = SelectNearestValidAnchor(map, collision, ServerSpawnKind.EmergencyFallback, current.Position, out resolved, out _);
            if (anchor == null)
            {
                detail = "no valid baked respawn/fallback anchor is available";
                return false;
            }

            location = new CharacterLocationState(current.MapId, current.InstanceId, resolved.ToWorldPosition(), NormalizeYaw(resolved.yaw));
            return true;
        }

        public bool TryValidateEntryLocation(CharacterLocationState requested, out CharacterLocationState resolved, out string detail)
        {
            resolved = requested;
            detail = string.Empty;
            if (!_maps.TryGetCollisionWorld(requested.MapId, requested.InstanceId, out ServerCollisionWorld collision))
            {
                if (_maps.Count == 0)
                    return true; // Explicit mapless development mode only.

                detail = $"authoritative entry map data is unavailable for map '{requested.MapId}' instance '{DisplayInstance(requested.InstanceId)}'";
                return false;
            }

            var pose = new ServerPose(requested.Position.X, requested.Position.Y, requested.Position.Z, requested.YawDegrees);
            var capsule = new ServerCapsule(0.35f, 1.8f);
            if (collision.TryValidateSpawn(pose, capsule, 0.65f, _maximumSlopeDegrees, out ServerPose corrected, out detail))
            {
                resolved = new CharacterLocationState(requested.MapId, requested.InstanceId, corrected.ToWorldPosition(), NormalizeYaw(corrected.yaw));
                return true;
            }

            if (TryResolveNearestPlayerRecovery(requested, out resolved, out string recoveryDetail))
            {
                detail = string.IsNullOrWhiteSpace(recoveryDetail)
                    ? "requested location invalid; recovered to nearest safe spawn"
                    : $"requested location invalid; {recoveryDetail}";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Detects clear out-of-world conditions only. Ordinary movement remains governed by
        /// the character motor and baked walk/collision data.
        /// </summary>
        public bool IsOutsideRecoveryBounds(CharacterLocationState location, out string detail)
        {
            detail = string.Empty;
            if (!_maps.TryGetCollisionWorld(location.MapId, location.InstanceId, out ServerCollisionWorld collision))
                return false;
            if (!collision.IsOutsideRecoveryBounds(location.Position))
                return false;

            detail = $"position ({location.Position.X:0.##}, {location.Position.Y:0.##}, {location.Position.Z:0.##}) is outside baked world bounds";
            return true;
        }

        public bool TryResolveNearestPlayerRecovery(
            CharacterLocationState current,
            out CharacterLocationState location,
            out string detail)
        {
            location = current;
            detail = string.Empty;
            if (!_maps.TryGet(current.MapId, current.InstanceId, out ServerMapSnapshot map) ||
                !_maps.TryGetCollisionWorld(current.MapId, current.InstanceId, out ServerCollisionWorld collision))
            {
                detail = "baked recovery map data is unavailable";
                return false;
            }

            ServerSpawnAnchor anchor = SelectNearestValidPlayerRecoveryAnchor(
                map, collision, current.Position, out ServerPose resolved);
            if (anchor != null)
            {
                location = new CharacterLocationState(
                    current.MapId, current.InstanceId, resolved.ToWorldPosition(), NormalizeYaw(resolved.yaw));
                detail = $"recovered to spawn '{anchor.label}' ({anchor.kind})";
                return true;
            }

            var capsule = new ServerCapsule(0.35f, 1.8f);
            if (collision.TryFindNearestSafeRecoveryPose(
                    current.Position, capsule, _maximumSlopeDegrees, out ServerPose safePose))
            {
                location = new CharacterLocationState(
                    current.MapId, current.InstanceId, safePose.ToWorldPosition(), NormalizeYaw(safePose.yaw));
                detail = "no valid authored player spawn was available; recovered to nearest safe baked walk surface";
                return true;
            }

            detail = "no valid player spawn or safe walkable recovery point is available";
            return false;
        }

        public bool TryResolveAnchor(string mapId, string instanceId, ServerSpawnKind kind, long stableId, out ServerPose pose, out string detail)
        {
            pose = default;
            detail = string.Empty;
            if (!_maps.TryGet(mapId, instanceId, out ServerMapSnapshot map) || !_maps.TryGetCollisionWorld(mapId, instanceId, out ServerCollisionWorld collision))
            {
                detail = "map data unavailable";
                return false;
            }

            string rejection = string.Empty;
            ServerSpawnAnchor[] anchors = map.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
            for (int i = 0; i < anchors.Length; ++i)
            {
                ServerSpawnAnchor anchor = anchors[i];
                if (anchor == null || !anchor.enabled || anchor.kind != kind || (stableId > 0 && anchor.stableId != stableId))
                    continue;
                if (IsPlayerRecoveryKind(kind) && anchor.actorKind != Game.Shared.Actors.AuthoritativeActorKind.Player)
                {
                    rejection = $"spawn '{anchor.label}' id={anchor.stableId} has actorKind {anchor.actorKind}; Player is required";
                    continue;
                }
                if (TryValidateAnchor(collision, anchor, out pose, out string anchorDetail))
                    return true;
                rejection = $"spawn '{anchor.label}' id={anchor.stableId} is invalid: {anchorDetail}";
            }
            detail = string.IsNullOrWhiteSpace(rejection) ? "spawn anchor is unavailable" : rejection;
            return false;
        }

        private ServerSpawnAnchor SelectNearestValidPlayerRecoveryAnchor(
            ServerMapSnapshot map,
            ServerCollisionWorld collision,
            WorldPosition from,
            out ServerPose resolved)
        {
            resolved = default;
            ServerSpawnAnchor best = null;
            float bestScore = float.PositiveInfinity;
            ServerSpawnAnchor[] anchors = map.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
            for (int i = 0; i < anchors.Length; ++i)
            {
                ServerSpawnAnchor anchor = anchors[i];
                if (anchor == null || !anchor.enabled || !IsPlayerRecoveryKind(anchor.kind) ||
                    anchor.actorKind != Game.Shared.Actors.AuthoritativeActorKind.Player)
                    continue;
                if (!TryValidateAnchor(collision, anchor, out ServerPose candidate, out _))
                    continue;

                float dx = candidate.x - from.X;
                float dz = candidate.z - from.Z;
                float score = dx * dx + dz * dz - anchor.priority * 1000f;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = anchor;
                resolved = candidate;
            }
            return best;
        }

        private static bool IsPlayerRecoveryKind(ServerSpawnKind kind) =>
            kind == ServerSpawnKind.PlayerRespawn ||
            kind == ServerSpawnKind.EmergencyFallback ||
            kind == ServerSpawnKind.PlayerFirstSpawn;

        private ServerSpawnAnchor SelectNearestValidAnchor(
            ServerMapSnapshot map,
            ServerCollisionWorld collision,
            ServerSpawnKind kind,
            WorldPosition from,
            out ServerPose resolved,
            out string detail)
        {
            resolved = default;
            detail = string.Empty;
            ServerSpawnAnchor best = null;
            float bestScore = float.PositiveInfinity;
            int candidates = 0;
            var rejected = new List<string>();
            ServerSpawnAnchor[] anchors = map.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
            for (int i = 0; i < anchors.Length; ++i)
            {
                ServerSpawnAnchor anchor = anchors[i];
                if (anchor == null || !anchor.enabled || anchor.kind != kind)
                    continue;

                candidates++;
                if (IsPlayerRecoveryKind(kind) && anchor.actorKind != Game.Shared.Actors.AuthoritativeActorKind.Player)
                {
                    AddRejected(rejected, anchor, $"actorKind is {anchor.actorKind}; Player is required");
                    continue;
                }

                if (!TryValidateAnchor(collision, anchor, out ServerPose candidate, out string rejection))
                {
                    AddRejected(rejected, anchor, rejection);
                    continue;
                }

                float dx = candidate.x - from.X;
                float dz = candidate.z - from.Z;
                float score = dx * dx + dz * dz - anchor.priority * 1000f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = anchor;
                    resolved = candidate;
                }
            }

            if (best != null)
                return best;

            string mapName = map?.mapId ?? string.Empty;
            string instance = DisplayInstance(map?.instanceId);
            if (candidates == 0)
            {
                detail = $"no enabled {kind} anchors are baked for map '{mapName}' instance '{instance}'";
                return null;
            }

            detail = $"all {candidates} enabled {kind} anchor(s) are invalid for map '{mapName}' instance '{instance}': {string.Join(" | ", rejected)}";
            return null;
        }

        private bool TryValidateAnchor(ServerCollisionWorld collision, ServerSpawnAnchor anchor, out ServerPose resolved, out string detail)
        {
            resolved = default;
            detail = string.Empty;
            if (anchor == null)
            {
                detail = "anchor is null";
                return false;
            }
            if (!IsFinite(anchor.pose.x) || !IsFinite(anchor.pose.y) || !IsFinite(anchor.pose.z) || !IsFinite(anchor.pose.yaw))
            {
                detail = "anchor pose contains a non-finite value";
                return false;
            }
            if (!IsFinite(anchor.capsuleRadius) || !IsFinite(anchor.capsuleHeight) || !IsFinite(anchor.maximumGroundSnap) ||
                anchor.capsuleRadius < 0.05f || anchor.capsuleHeight < anchor.capsuleRadius * 2f || anchor.maximumGroundSnap < 0.05f)
            {
                detail = "anchor capsule/ground-snap settings are invalid";
                return false;
            }

            var capsule = new ServerCapsule(anchor.capsuleRadius, anchor.capsuleHeight);
            if (collision.TryValidateSpawn(anchor.pose, capsule, anchor.maximumGroundSnap, _maximumSlopeDegrees, out resolved, out detail))
                return true;

            detail = $"{detail}; authored=({anchor.pose.x:0.###},{anchor.pose.y:0.###},{anchor.pose.z:0.###}), maxGroundSnap={anchor.maximumGroundSnap:0.###}m";
            return false;
        }

        private static void AddRejected(List<string> rejected, ServerSpawnAnchor anchor, string reason)
        {
            if (rejected.Count >= 4)
                return;
            string label = string.IsNullOrWhiteSpace(anchor?.label) ? "<unnamed>" : anchor.label.Trim();
            rejected.Add($"'{label}' id={anchor?.stableId ?? 0}: {reason}");
        }

        private static string DisplayInstance(string value) =>
            string.IsNullOrWhiteSpace(value) ? "<default>" : value.Trim();

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static float NormalizeYaw(float value)
        {
            float result = value % 360f;
            return result < 0f ? result + 360f : result;
        }
    }
}
