using System;
using System.Collections.Generic;
using Game.GameServer.Runtime;
using Game.Server.Application.Actors;
using Game.Server.Application.Population;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Actors;
using Game.Shared.Abilities;
using Game.Shared.World;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    // Precision-ranged target-body tolerance around the authoritative aim ray.
    private const float DirectCombatContactRadius = 0.85f;
    private const float DirectCombatOriginHeight = 1.10f;
    private const float DirectCombatTargetHeight = 1.00f;

    private readonly List<ClientSession> _combatContactPlayerScratch = new List<ClientSession>(64);
    private readonly List<AuthoritativeActorRuntime> _combatContactActorScratch = new List<AuthoritativeActorRuntime>(64);

    /// <summary>
    /// Resolves one direct basic-combat contact from authoritative source pose + AOI state.
    /// Melee/unarmed never consume client aim: they use authoritative player facing plus a
    /// horizontal swing arc and body-edge reach. Firearms use the existing precision aim ray.
    /// </summary>
    private PlayerRuntime ResolveDirectCombatContact(
        ClientSession sourceSession,
        PlayerRuntime source,
        float range,
        BasicAttackMode mode)
    {
        if (mode == BasicAttackMode.Unarmed || mode == BasicAttackMode.MeleeWeapon)
            return ResolveMeleeSwingCombatContact(sourceSession, source, range, mode);

        float pitch = 0f;
        if (sourceSession?.Entity != null && sourceSession.Entity.TryGetFreshCombatInput(
                Environment.TickCount64,
                out _,
                out float currentPitch))
        {
            pitch = currentPitch;
        }

        return ResolveDirectCombatContact(
            sourceSession, source, range, mode,
            sourceSession?.Entity?.YawDegrees ?? 0f, pitch);
    }

    private PlayerRuntime ResolveDirectCombatContact(
        ClientSession sourceSession,
        PlayerRuntime source,
        float range,
        BasicAttackMode mode,
        float aimYawDegrees,
        float aimPitchDegrees)
    {
        if (mode == BasicAttackMode.Unarmed || mode == BasicAttackMode.MeleeWeapon)
            return ResolveMeleeSwingCombatContact(sourceSession, source, range, mode);

        if (mode != BasicAttackMode.Firearm || sourceSession?.Entity == null || source == null ||
            !float.IsFinite(range) || range <= 0f)
        {
            return null;
        }

        CharacterLocationState sourceLocation = sourceSession.Entity.CaptureLocation();
        BuildAimDirection(aimYawDegrees, aimPitchDegrees, out float fx, out float fy, out float fz);
        float ox = sourceSession.Entity.X;
        float oy = sourceSession.Entity.Y + DirectCombatOriginHeight;
        float oz = sourceSession.Entity.Z;

        PlayerRuntime best = null;
        float bestAlong = float.PositiveInfinity;

        if (_worldInterest != null)
        {
            _worldInterest.CollectVisibleTargets(sourceSession, _combatContactPlayerScratch);
            for (int i = 0; i < _combatContactPlayerScratch.Count; ++i)
            {
                ClientSession candidateSession = _combatContactPlayerScratch[i];
                if (candidateSession?.Entity == null || !IsCurrent(candidateSession) || !candidateSession.Ready ||
                    !TryGetGameplayRuntime(candidateSession, out PlayerRuntime candidate) || candidate == null)
                {
                    continue;
                }

                if (!TryScoreDirectContact(
                        ox, oy, oz,
                        fx, fy, fz,
                        candidateSession.Entity.X,
                        candidateSession.Entity.Y + DirectCombatTargetHeight,
                        candidateSession.Entity.Z,
                        range,
                        out float along) ||
                    along >= bestAlong ||
                    !HasAuthoritativeCombatLineOfSight(sourceSession, candidate))
                {
                    continue;
                }

                best = candidate;
                bestAlong = along;
            }
        }

        foreach (PlayerRuntime target in _runtime.PopulationCombat.CombatTestRuntimes)
        {
            if (target == null || ReferenceEquals(target, source))
                continue;
            CharacterLocationState location = target.Location;
            if (!string.Equals(sourceLocation.MapId, location.MapId, StringComparison.Ordinal) ||
                !string.Equals(sourceLocation.InstanceId, location.InstanceId, StringComparison.Ordinal))
            {
                continue;
            }

            WorldPosition position = location.Position;
            if (!TryScoreDirectContact(
                    ox, oy, oz,
                    fx, fy, fz,
                    position.X,
                    position.Y + DirectCombatTargetHeight,
                    position.Z,
                    range,
                    out float along) ||
                along >= bestAlong ||
                !HasAuthoritativeCombatLineOfSight(sourceSession, location))
            {
                continue;
            }

            best = target;
            bestAlong = along;
        }

        PopulationActorRuntime bestPopulation = null;
        float populationQueryRadius = (float)Math.Sqrt(
            ((range + DirectCombatContactRadius) * (range + DirectCombatContactRadius)) +
            (DirectCombatContactRadius * DirectCombatContactRadius));
        _runtime.Actors.QueryRadius(
            sourceLocation.MapId,
            sourceLocation.InstanceId,
            sourceLocation.Position,
            populationQueryRadius,
            _combatContactActorScratch);
        for (int i = 0; i < _combatContactActorScratch.Count; ++i)
        {
            AuthoritativeActorRuntime actor = _combatContactActorScratch[i];
            if (actor == null || actor.Handle.kind != AuthoritativeActorKind.Population ||
                !_runtime.PopulationCombat.TryGetPopulationActor(actor.Handle.actorId, actor.Handle.generation, out PopulationActorRuntime population))
            {
                continue;
            }

            WorldPosition position = population.Actor.Position;
            var location = new CharacterLocationState(
                population.Actor.MapId,
                population.Actor.InstanceId,
                position,
                population.Actor.YawDegrees);
            if (!TryScoreDirectContact(
                    ox, oy, oz,
                    fx, fy, fz,
                    position.X,
                    position.Y + DirectCombatTargetHeight,
                    position.Z,
                    range,
                    out float along) ||
                along >= bestAlong ||
                !HasAuthoritativeCombatLineOfSight(sourceSession, location))
            {
                continue;
            }

            best = null;
            bestPopulation = population;
            bestAlong = along;
        }

        if (bestPopulation != null &&
            _runtime.PopulationCombat.TryActivatePopulation(bestPopulation, out PlayerRuntime populationRuntime))
        {
            return populationRuntime;
        }

        return best;
    }

    private PlayerRuntime ResolveMeleeSwingCombatContact(
        ClientSession sourceSession,
        PlayerRuntime source,
        float range,
        BasicAttackMode mode)
    {
        if (sourceSession?.Entity == null || source == null || !float.IsFinite(range) || range <= 0f ||
            (mode != BasicAttackMode.Unarmed && mode != BasicAttackMode.MeleeWeapon))
        {
            return null;
        }

        CharacterLocationState sourceLocation = sourceSession.Entity.CaptureLocation();
        float sourceX = sourceSession.Entity.X;
        float sourceY = sourceSession.Entity.Y;
        float sourceZ = sourceSession.Entity.Z;
        float facingYaw = sourceSession.Entity.YawDegrees;

        PlayerRuntime best = null;
        float bestEdgeDistance = float.PositiveInfinity;

        if (_worldInterest != null)
        {
            _worldInterest.CollectVisibleTargets(sourceSession, _combatContactPlayerScratch);
            for (int i = 0; i < _combatContactPlayerScratch.Count; ++i)
            {
                ClientSession candidateSession = _combatContactPlayerScratch[i];
                if (candidateSession?.Entity == null || !IsCurrent(candidateSession) || !candidateSession.Ready ||
                    !TryGetGameplayRuntime(candidateSession, out PlayerRuntime candidate) || candidate == null)
                {
                    continue;
                }

                if (!TryScoreMeleeSwingContact(
                        sourceX, sourceY, sourceZ, facingYaw,
                        candidateSession.Entity.X, candidateSession.Entity.Y, candidateSession.Entity.Z,
                        range, mode, out float edgeDistance) ||
                    edgeDistance >= bestEdgeDistance ||
                    !HasAuthoritativeCombatLineOfSight(sourceSession, candidate))
                {
                    continue;
                }

                best = candidate;
                bestEdgeDistance = edgeDistance;
            }
        }

        foreach (PlayerRuntime target in _runtime.PopulationCombat.CombatTestRuntimes)
        {
            if (target == null || ReferenceEquals(target, source))
                continue;

            CharacterLocationState location = target.Location;
            if (!string.Equals(sourceLocation.MapId, location.MapId, StringComparison.Ordinal) ||
                !string.Equals(sourceLocation.InstanceId, location.InstanceId, StringComparison.Ordinal))
            {
                continue;
            }

            WorldPosition position = location.Position;
            if (!TryScoreMeleeSwingContact(
                    sourceX, sourceY, sourceZ, facingYaw,
                    position.X, position.Y, position.Z,
                    range, mode, out float edgeDistance) ||
                edgeDistance >= bestEdgeDistance ||
                !HasAuthoritativeCombatLineOfSight(sourceSession, location))
            {
                continue;
            }

            best = target;
            bestEdgeDistance = edgeDistance;
        }

        PopulationActorRuntime bestPopulation = null;
        float populationQueryRadius = range + CombatRangePolicy.MeleeTargetBodyRadius;
        _runtime.Actors.QueryRadius(
            sourceLocation.MapId,
            sourceLocation.InstanceId,
            sourceLocation.Position,
            populationQueryRadius,
            _combatContactActorScratch);
        for (int i = 0; i < _combatContactActorScratch.Count; ++i)
        {
            AuthoritativeActorRuntime actor = _combatContactActorScratch[i];
            if (actor == null || actor.Handle.kind != AuthoritativeActorKind.Population ||
                !_runtime.PopulationCombat.TryGetPopulationActor(actor.Handle.actorId, actor.Handle.generation, out PopulationActorRuntime population))
            {
                continue;
            }

            WorldPosition position = population.Actor.Position;
            var location = new CharacterLocationState(
                population.Actor.MapId,
                population.Actor.InstanceId,
                position,
                population.Actor.YawDegrees);
            if (!TryScoreMeleeSwingContact(
                    sourceX, sourceY, sourceZ, facingYaw,
                    position.X, position.Y, position.Z,
                    range, mode, out float edgeDistance) ||
                edgeDistance >= bestEdgeDistance ||
                !HasAuthoritativeCombatLineOfSight(sourceSession, location))
            {
                continue;
            }

            best = null;
            bestPopulation = population;
            bestEdgeDistance = edgeDistance;
        }

        if (bestPopulation != null &&
            _runtime.PopulationCombat.TryActivatePopulation(bestPopulation, out PlayerRuntime populationRuntime))
        {
            return populationRuntime;
        }

        return best;
    }

    private static bool TryScoreMeleeSwingContact(
        float sourceX,
        float sourceY,
        float sourceZ,
        float facingYawDegrees,
        float targetX,
        float targetY,
        float targetZ,
        float range,
        BasicAttackMode mode,
        out float edgeDistance)
    {
        edgeDistance = float.PositiveInfinity;
        if (mode != BasicAttackMode.Unarmed && mode != BasicAttackMode.MeleeWeapon)
            return false;

        float dy = targetY - sourceY;
        if (Math.Abs(dy) > CombatRangePolicy.MeleeVerticalTolerance)
            return false;

        float dx = targetX - sourceX;
        float dz = targetZ - sourceZ;
        float centerDistanceSq = (dx * dx) + (dz * dz);
        float centerDistance = (float)Math.Sqrt(centerDistanceSq);
        float bodyRadius = CombatRangePolicy.MeleeTargetBodyRadius;
        edgeDistance = Math.Max(0f, centerDistance - bodyRadius);
        if (edgeDistance > range)
            return false;

        // If the target body overlaps the attacker center, it is necessarily inside the swing.
        if (centerDistance <= 0.001f)
            return true;

        float arcDegrees = CombatRangePolicy.SwingArcDegreesFor(mode);
        if (arcDegrees <= 0f)
            return false;

        // Reach uses body-edge distance, but direction remains a predictable fixed swing arc.
        // This keeps a sword/fist forgiving without allowing targets nearly beside/behind the actor.
        float allowedHalfArc = arcDegrees * 0.5f;

        double yaw = facingYawDegrees * (Math.PI / 180d);
        float facingX = (float)Math.Sin(yaw);
        float facingZ = (float)Math.Cos(yaw);
        float dot = ((dx * facingX) + (dz * facingZ)) / centerDistance;
        float minimumDot = (float)Math.Cos(allowedHalfArc * (Math.PI / 180d));
        return dot >= minimumDot;
    }

    private static bool TryScoreDirectContact(
        float ox, float oy, float oz,
        float fx, float fy, float fz,
        float tx, float ty, float tz,
        float range,
        out float along)
    {
        float dx = tx - ox;
        float dy = ty - oy;
        float dz = tz - oz;
        along = (dx * fx) + (dy * fy) + (dz * fz);
        if (along < 0f || along > range + DirectCombatContactRadius)
            return false;

        float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
        float perpendicularSquared = Math.Max(0f, distanceSquared - (along * along));
        float radiusSquared = DirectCombatContactRadius * DirectCombatContactRadius;
        return perpendicularSquared <= radiusSquared;
    }

    private static void BuildAimDirection(
        float yawDegrees,
        float pitchDegrees,
        out float x,
        out float y,
        out float z)
    {
        double yaw = yawDegrees * (Math.PI / 180d);
        double pitch = pitchDegrees * (Math.PI / 180d);
        double cosPitch = Math.Cos(pitch);
        x = (float)(Math.Sin(yaw) * cosPitch);
        y = (float)Math.Sin(pitch);
        z = (float)(Math.Cos(yaw) * cosPitch);
    }
}
