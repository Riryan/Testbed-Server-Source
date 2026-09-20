using System;
using Game.Server.Application.Population;
using Game.Shared.Actors;
using Player.Shared;

namespace Game.GameServer.Runtime;

/// <summary>
/// Lightweight server-side presentation view for one canonical Population actor.
/// It deliberately does not create a PlayerRuntime. The existing PlayerEntity wire is
/// reused only so clients render humanoid Population through the exact PlayerEntityClient
/// character/presentation path already used by Players and synthetic bots.
/// </summary>
internal sealed class ServerPopulationPresentationEntity : IPlayerEntityPresentationSource
{
    public PopulationActorRuntime Population { get; }
    public uint ObjectId { get; }
    public ushort Generation => Population.Actor.Handle.generation;
    public long ConnectionId => -1L; // canonical unowned/server-owned network object
    public float X => Population.Actor.Position.X;
    public float Y => Population.Actor.Position.Y;
    public float Z => Population.Actor.Position.Z;
    public float YawDegrees => Population.Actor.YawDegrees;
    public float SnapshotVerticalSpeed => Population.Actor.VelocityY;
    public bool IsDead => !Population.Actor.Alive || Population.Actor.MovementMode == ActorMovementMode.Dead;

    public float SnapshotSpeed
    {
        get
        {
            float x = Population.Actor.VelocityX;
            float z = Population.Actor.VelocityZ;
            float motion = MathF.Sqrt((x * x) + (z * z));
            if (motion <= 0.0001f)
                return 0f;
            // Population currently stores per-step displacement in Actor.Velocity. The
            // PlayerEntity presentation contract expects world speed, so feed its authored
            // movement speed while motion is active rather than coupling animation cadence
            // to Population simulation LOD/cadence.
            return Population.AiState == Game.Shared.Population.PopulationAiState.Fleeing ||
                   Population.AiState == Game.Shared.Population.PopulationAiState.Fighting
                ? Math.Max(0f, Population.RunSpeed)
                : Math.Max(0f, Population.WalkSpeed);
        }
    }

    public byte SnapshotFlags
    {
        get
        {
            if (IsDead)
                return (byte)PlayerEntityFlags.Grounded;

            PlayerEntityFlags flags = PlayerEntityFlags.None;
            ActorMovementMode mode = Population.Actor.MovementMode;
            if (mode != ActorMovementMode.Falling && mode != ActorMovementMode.Jumping)
                flags |= PlayerEntityFlags.Grounded;

            float speed = SnapshotSpeed;
            if (speed > Math.Max(0.1f, Population.WalkSpeed * 1.15f))
                flags |= PlayerEntityFlags.Running;
            return (byte)flags;
        }
    }

    public byte SnapshotMoveState
    {
        get
        {
            if (IsDead)
                return (byte)PlayerEntityMoveState.Dead;

            return Population.Actor.MovementMode switch
            {
                ActorMovementMode.Falling or ActorMovementMode.Jumping => (byte)PlayerEntityMoveState.Airborne,
                ActorMovementMode.Mounted => (byte)PlayerEntityMoveState.Mounted,
                _ => SnapshotSpeed > 0.05f
                    ? (byte)PlayerEntityMoveState.Moving
                    : (byte)PlayerEntityMoveState.Idle,
            };
        }
    }

    public ServerPopulationPresentationEntity(uint objectId, PopulationActorRuntime population)
    {
        if (objectId == 0u) throw new ArgumentOutOfRangeException(nameof(objectId));
        Population = population ?? throw new ArgumentNullException(nameof(population));
        if (Population.Actor == null || !Population.Actor.Handle.IsValid)
            throw new ArgumentException("Population actor is invalid.", nameof(population));
        ObjectId = objectId;
    }
}
