using System;
using Game.Server.Application.Abilities;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Content;
using Game.Shared.Combat;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.World;

namespace Game.Server.Application.Lifecycle
{

    public interface ICharacterRespawnResolver
    {
        bool TryResolveRespawn(PlayerRuntime runtime, out CharacterLocationState location, out string detail);
    }

    public enum CharacterRespawnStatus : byte
    {
        Success = 0,
        CharacterUnavailable = 1,
        NotDead = 2,
        SpawnUnavailable = 3,
        ResourceResetFailed = 4,
    }

    public readonly struct CharacterRespawnResult
    {
        public CharacterRespawnStatus Status { get; }
        public string Detail { get; }
        public CharacterLocationState Location { get; }
        public bool Success => Status == CharacterRespawnStatus.Success;

        public CharacterRespawnResult(
            CharacterRespawnStatus status,
            string detail,
            CharacterLocationState location)
        {
            Status = status;
            Detail = detail ?? string.Empty;
            Location = location;
        }
    }

    /// <summary>
    /// Canonical character death/respawn lifecycle boundary. Death itself is detected by
    /// CombatService from Health reaching its minimum. This service owns the cross-system
    /// cleanup that must follow that event and the authoritative respawn transition.
    /// Alive/dead truth remains derived from the Health resource; no duplicate saved flag exists.
    /// </summary>
    public sealed class CharacterLifecycleService
    {
        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly AbilityService _abilities;
        private readonly ICharacterRespawnResolver _respawnResolver;

        public CharacterLifecycleService(
            GameplayContentCatalog content,
            CharacterResourceService resources,
            AbilityService abilities)
            : this(content, resources, abilities, null)
        {
        }

        public CharacterLifecycleService(
            GameplayContentCatalog content,
            CharacterResourceService resources,
            AbilityService abilities,
            ICharacterRespawnResolver respawnResolver)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _abilities = abilities ?? throw new ArgumentNullException(nameof(abilities));
            _respawnResolver = respawnResolver;
        }

        public bool IsDead(PlayerRuntime runtime)
        {
            return runtime != null &&
                   runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) &&
                   health.Enabled &&
                   health.Current <= health.Minimum;
        }

        /// <summary>
        /// Called exactly from the authoritative CombatService kill event. Cancels transient
        /// active work only; StatusEffect and Resource death policies are already applied by
        /// CombatService before it raises CharacterKilled.
        /// </summary>
        public void HandleKilled(PlayerRuntime runtime, CombatDamageResult result)
        {
            if (runtime == null)
                return;

            if (runtime.CaptureActionState().ActiveCast.IsActive)
                _abilities.CancelCast(runtime, AbilityCastFailure.Interrupted);
        }

        public CharacterRespawnResult TryRespawn(PlayerRuntime runtime)
        {
            if (runtime == null)
                return Failed(CharacterRespawnStatus.CharacterUnavailable, "character is unavailable");
            if (!IsDead(runtime))
                return Failed(CharacterRespawnStatus.NotDead, "character is not dead");

            CharacterLocationState resolvedLocation;
            string resolverDetail = string.Empty;
            if (_respawnResolver != null && _respawnResolver.TryResolveRespawn(runtime, out resolvedLocation, out resolverDetail))
            {
                // Baked server-map respawn anchors are authoritative when available.
            }
            else
            {
                CharacterSpawnDefinition spawn = _content.Snapshot?.initialCharacterSpawn;
                if (spawn == null || string.IsNullOrWhiteSpace(spawn.mapId) ||
                    !IsFinite(spawn.positionX) || !IsFinite(spawn.positionY) ||
                    !IsFinite(spawn.positionZ) || !IsFinite(spawn.yawDegrees))
                {
                    return Failed(CharacterRespawnStatus.SpawnUnavailable,
                        string.IsNullOrWhiteSpace(resolverDetail) ? "respawn location is not configured" : resolverDetail);
                }

                string spawnMapId = spawn.mapId.Trim();
                string spawnInstanceId = (spawn.instanceId ?? string.Empty).Trim();
                if (!string.Equals(runtime.Location.MapId, spawnMapId, StringComparison.Ordinal) ||
                    !string.Equals(runtime.Location.InstanceId, spawnInstanceId, StringComparison.Ordinal))
                {
                    return Failed(
                        CharacterRespawnStatus.SpawnUnavailable,
                        "cross-map/instance respawn is deferred; no valid same-map baked respawn anchor is available");
                }

                resolvedLocation = new CharacterLocationState(
                    spawnMapId,
                    spawnInstanceId,
                    new WorldPosition(spawn.positionX, spawn.positionY, spawn.positionZ),
                    NormalizeYaw(spawn.yawDegrees));
            }

            _resources.ApplyRespawnResets(runtime);
            if (IsDead(runtime))
            {
                return Failed(
                    CharacterRespawnStatus.ResourceResetFailed,
                    "respawn resource rules did not restore Health above its minimum");
            }

            runtime.UpdateLocation(resolvedLocation);

            return new CharacterRespawnResult(
                CharacterRespawnStatus.Success,
                string.Empty,
                resolvedLocation);
        }

        private static CharacterRespawnResult Failed(CharacterRespawnStatus status, string detail) =>
            new CharacterRespawnResult(status, detail, default(CharacterLocationState));

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static float NormalizeYaw(float value)
        {
            float result = value % 360f;
            if (result < 0f) result += 360f;
            return result;
        }
    }
}
