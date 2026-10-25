using System;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.World;

namespace Game.Server.Application.Movement
{
    public readonly struct CharacterMovementIntent
    {
        public float X { get; }
        public float Z { get; }
        public bool Sprint { get; }
        public bool Jump { get; }

        public CharacterMovementIntent(float x, float z, bool sprint = false, bool jump = false)
        {
            X = ClampUnit(x);
            Z = ClampUnit(z);
            Sprint = sprint;
            Jump = jump;
        }

        private static float ClampUnit(float value) =>
            float.IsNaN(value) ? 0f : Math.Clamp(value, -1f, 1f);
    }

    public sealed class CharacterMotorSettings
    {
        public float Radius = 0.35f;
        public float Height = 1.8f;
        public float WalkSpeed = 3.825f;
        public float SprintSpeed = 5.95f;
        public float Gravity = 24f;
        public float MaximumFallSpeed = 45f;
        public float JumpSpeed = 7f;
        public float StepHeight = 0.4f;
        public float GroundSnapDistance = 0.5f;
        public float MaximumSlopeDegrees = 50f;
    }

    public sealed class CharacterMotorState
    {
        public WorldPosition Position;
        public WorldPosition LastSafePosition;
        public float VerticalVelocity;
        public bool Grounded = true;
        public long GroundSurfaceId;
        public ActorMovementMode Mode = ActorMovementMode.Grounded;

        public CharacterMotorState(WorldPosition position)
        {
            Position = position;
            LastSafePosition = position;
        }
    }

    /// <summary>
    /// Player input is integrated authoritatively against the same baked-world
    /// GroundedMove contract already used by the Unity client's local predictor.
    /// No separate NavMesh certificate acceptance logic runs for grounded Players.
    /// Population and airborne movement retain their existing branches.
    /// </summary>
    public sealed class ServerCharacterMotor
    {
        private readonly CharacterMotorSettings _settings;
        private readonly ServerCapsule _capsule;
        // Kept as a read-only compatibility diagnostic. Grounded Players now use
        // GroundedMove.Resolve rather than the legacy PlayerNavMesh V6 fallback.
        public PlayerNavFallbackReason LastTickNavFallbackReason { get; private set; }

        public double LastTickPreparationMs { get; private set; }
        public double LastTickHorizontalCollisionMs { get; private set; }
        public double LastTickPostCollisionMs { get; private set; }
        public double LastTickOtherMs { get; private set; }

        public ServerCharacterMotor(CharacterMotorSettings settings = null)
        {
            _settings = settings ?? new CharacterMotorSettings();
            Sanitize(_settings);
            _capsule = new ServerCapsule(_settings.Radius, _settings.Height);
        }

        public CharacterMotorSettings Settings => _settings;

        public void Tick(CharacterMotorState state, CharacterMovementIntent intent,
            float fixedDelta, ServerCollisionWorld collisionWorld)
        {
            TickInternal(state, intent, fixedDelta, collisionWorld, false, out _, true);
        }

        public void Tick(CharacterMotorState state, CharacterMovementIntent intent,
            float fixedDelta, ServerCollisionWorld collisionWorld,
            bool allowWallSlide, out bool blocked)
        {
            TickInternal(state, intent, fixedDelta, collisionWorld,
                allowWallSlide, out blocked, false);
        }

        private void TickInternal(CharacterMotorState state, CharacterMovementIntent intent,
            float fixedDelta, ServerCollisionWorld collisionWorld,
            bool allowWallSlide, out bool blocked, bool player)
        {
            long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long stageStart = tickStart;
            LastTickNavFallbackReason = PlayerNavFallbackReason.None;
            LastTickPreparationMs = 0d;
            LastTickHorizontalCollisionMs = 0d;
            LastTickPostCollisionMs = 0d;
            LastTickOtherMs = 0d;
            blocked = false;

            if (state == null || !float.IsFinite(fixedDelta) || fixedDelta <= 0f)
            {
                LastTickOtherMs = ElapsedMillisecondsSince(tickStart);
                return;
            }

            if (state.Mode == ActorMovementMode.Dead ||
                state.Mode == ActorMovementMode.InteractionLocked ||
                state.Mode == ActorMovementMode.Vaulting ||
                state.Mode == ActorMovementMode.Ladder ||
                state.Mode == ActorMovementMode.Mounted)
            {
                LastTickOtherMs = ElapsedMillisecondsSince(tickStart);
                return;
            }

            float x = intent.X;
            float z = intent.Z;
            float magnitudeSquared = x * x + z * z;
            if (magnitudeSquared > 1f)
            {
                float inv = 1f / MathF.Sqrt(magnitudeSquared);
                x *= inv;
                z *= inv;
            }

            float speed = intent.Sprint ? _settings.SprintSpeed : _settings.WalkSpeed;
            float dx = x * speed * fixedDelta;
            float dz = z * speed * fixedDelta;
            LastTickPreparationMs = ElapsedMillisecondsSince(stageStart);

            if (collisionWorld == null)
            {
                if (player)
                {
                    blocked = true;
                    LastTickOtherMs = ElapsedMillisecondsSince(tickStart) -
                        LastTickPreparationMs;
                    return;
                }

                state.Position = new WorldPosition(state.Position.X + dx,
                    state.Position.Y, state.Position.Z + dz);
                state.LastSafePosition = state.Position;
                state.Grounded = true;
                state.Mode = ActorMovementMode.Grounded;
                state.VerticalVelocity = 0f;
                LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                    LastTickPreparationMs);
                return;
            }

            bool grounded = state.Grounded && state.Mode == ActorMovementMode.Grounded;
            bool hasHorizontalInput = Math.Abs(dx) + Math.Abs(dz) > 0.000001f;

            // Both the local client and the authoritative server now use the SAME
            // GroundedMove.Resolve implementation, the same authored support
            // triangles and static collision, and the same 0.35m/1.80m capsule.
            //
            // Reuse the world-level index prepared once in ServerMapCatalog.
            // No separate V6 NavMesh surface certificate, neighbour walk, or
            // blocked-step cache is consulted. Mutable blocker state remains
            // observable through the canonical ServerCollisionWorld.
            if (player && grounded && !intent.Jump)
            {
                if (!hasHorizontalInput)
                {
                    LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                        LastTickPreparationMs);
                    return;
                }

                stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
                WorldPosition next = GroundedMove.Resolve(
                    collisionWorld, state.Position, dx, dz, _capsule,
                    _settings.StepHeight, _settings.GroundSnapDistance,
                    _settings.MaximumSlopeDegrees, out blocked);
                LastTickHorizontalCollisionMs = ElapsedMillisecondsSince(stageStart);
                if (blocked)
                {
                    LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                        LastTickPreparationMs - LastTickHorizontalCollisionMs);
                    return;
                }

                state.Position = next;
                state.LastSafePosition = next;
                state.VerticalVelocity = 0f;
                state.Grounded = true;
                state.Mode = ActorMovementMode.Grounded;
                LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                    LastTickPreparationMs - LastTickHorizontalCollisionMs);
                return;
            }

            // Player air travel is ballistic intent integration, not a second
            // capsule movement simulation. Consult baked surface on descent only.
            if (player)
            {
                stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
                WorldPosition before = state.Position;
                bool launching = grounded && intent.Jump;
                if (launching)
                {
                    state.Grounded = false;
                    state.Mode = ActorMovementMode.Jumping;
                    state.VerticalVelocity = _settings.JumpSpeed;
                }

                state.VerticalVelocity = Math.Max(-_settings.MaximumFallSpeed,
                    state.VerticalVelocity - _settings.Gravity * fixedDelta);
                if (state.VerticalVelocity <= 0f)
                    state.Mode = ActorMovementMode.Falling;

                WorldPosition next = new WorldPosition(
                    before.X + dx,
                    before.Y + state.VerticalVelocity * fixedDelta,
                    before.Z + dz);
                LastTickHorizontalCollisionMs = ElapsedMillisecondsSince(stageStart);

                stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (state.VerticalVelocity <= 0f &&
                    collisionWorld.TryFindPlayerBakedLandingSupport(
                        before.Y, next, _capsule,
                        _settings.MaximumSlopeDegrees,
                        out float landingY, out _))
                {
                    next = new WorldPosition(next.X, landingY, next.Z);
                    state.Grounded = true;
                    state.Mode = ActorMovementMode.Grounded;
                    state.VerticalVelocity = 0f;
                    state.LastSafePosition = next;
                    // The grounded solver reuses the shared baked-world index;
                    // landing requires no legacy PlayerNavMesh cache binding.
                }
                state.Position = next;
                LastTickPostCollisionMs = ElapsedMillisecondsSince(stageStart);
                LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                    LastTickPreparationMs - LastTickHorizontalCollisionMs -
                    LastTickPostCollisionMs);
                return;
            }

            // Existing Population motor, deliberately unchanged.
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            WorldPosition previousPosition = state.Position;
            WorldPosition moved;
            if (!player && state.Grounded && !intent.Jump &&
                collisionWorld.TryResolveCertifiedGroundedNoSlide(
                    state.Position, dx, dz, _capsule, _settings.StepHeight,
                    _settings.GroundSnapDistance, _settings.MaximumSlopeDegrees,
                    out moved, out blocked))
            {
                // Existing Population certified path.
            }
            else if (!player && state.Grounded && !intent.Jump &&
                     collisionWorld.HasCertifiedGroundedMovement)
            {
                moved = state.Position;
                blocked = true;
            }
            else
            {
                moved = collisionWorld.ResolveHorizontalMove(
                    state.Position, dx, dz, _capsule, _settings.StepHeight,
                    _settings.GroundSnapDistance, _settings.MaximumSlopeDegrees,
                    state.Grounded, allowWallSlide: false, out blocked);
            }
            LastTickHorizontalCollisionMs = ElapsedMillisecondsSince(stageStart);
            if (state.Grounded && !intent.Jump && blocked && moved == previousPosition)
            {
                LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                    LastTickPreparationMs - LastTickHorizontalCollisionMs);
                return;
            }

            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool canJump = state.Grounded && state.Mode == ActorMovementMode.Grounded;
            if (intent.Jump && canJump)
            {
                state.Grounded = false;
                state.Mode = ActorMovementMode.Jumping;
                state.VerticalVelocity = _settings.JumpSpeed;
            }

            ServerGroundHit ground = default;
            bool foundGround = state.Grounded && !intent.Jump &&
                collisionWorld.TryFindGround(moved, _capsule.Radius,
                    _settings.StepHeight, _settings.GroundSnapDistance,
                    _settings.MaximumSlopeDegrees, out ground);
            bool groundedPoseValidated = false;

            if (foundGround)
            {
                groundedPoseValidated = true;
                if (CanSettleGroundHeight(collisionWorld, moved, ground.Position.Y))
                    moved = new WorldPosition(moved.X, ground.Position.Y, moved.Z);
                state.GroundSurfaceId = ground.SurfaceId;
                state.VerticalVelocity = 0f;
                state.Grounded = true;
                state.Mode = ActorMovementMode.Grounded;
            }
            else
            {
                state.Grounded = false;
                if (state.Mode != ActorMovementMode.Jumping || state.VerticalVelocity <= 0f)
                    state.Mode = ActorMovementMode.Falling;
                state.VerticalVelocity = Math.Max(-_settings.MaximumFallSpeed,
                    state.VerticalVelocity - _settings.Gravity * fixedDelta);
                float targetY = moved.Y + state.VerticalVelocity * fixedDelta;
                WorldPosition vertical = new WorldPosition(moved.X, targetY, moved.Z);

                if (state.VerticalVelocity <= 0f &&
                    collisionWorld.TryFindGround(moved, _capsule.Radius,
                        0.05f, Math.Max(_settings.GroundSnapDistance,
                            moved.Y - targetY + 0.05f),
                        _settings.MaximumSlopeDegrees, out ServerGroundHit fallingGround) &&
                    fallingGround.Position.Y >= targetY - 0.01f)
                {
                    vertical = new WorldPosition(moved.X, fallingGround.Position.Y, moved.Z);
                    state.Grounded = true;
                    state.Mode = ActorMovementMode.Grounded;
                    state.VerticalVelocity = 0f;
                    state.GroundSurfaceId = fallingGround.SurfaceId;
                }
                else if (state.VerticalVelocity <= 0f &&
                    collisionWorld.TryFindSolidGround(moved, _capsule.Radius,
                        0.05f, Math.Max(_settings.GroundSnapDistance,
                            moved.Y - targetY + 0.05f), 89f,
                        out ServerGroundHit solidGround) &&
                    solidGround.Position.Y >= targetY - 0.01f)
                {
                    vertical = new WorldPosition(moved.X, solidGround.Position.Y, moved.Z);
                    state.Grounded = true;
                    state.Mode = ActorMovementMode.Grounded;
                    state.VerticalVelocity = 0f;
                    state.GroundSurfaceId = solidGround.SurfaceId;
                }
                else if (collisionWorld.IsCapsuleClear(vertical, _capsule))
                {
                    moved = vertical;
                }
                else
                {
                    state.VerticalVelocity = 0f;
                }

                if (state.Grounded)
                    moved = vertical;
            }

            state.Position = moved;
            if (state.Grounded)
            {
                if (groundedPoseValidated)
                {
                    if (hasHorizontalInput && moved != previousPosition)
                        state.LastSafePosition = state.Position;
                }
                else if (collisionWorld.IsCapsuleClear(state.Position, _capsule))
                {
                    state.LastSafePosition = state.Position;
                }
            }
            LastTickPostCollisionMs = ElapsedMillisecondsSince(stageStart);
            LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStart) -
                LastTickPreparationMs - LastTickHorizontalCollisionMs - LastTickPostCollisionMs);
        }

        private static double ElapsedMillisecondsSince(long startedAt) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) *
            (1000d / System.Diagnostics.Stopwatch.Frequency);

        private bool CanSettleGroundHeight(ServerCollisionWorld world,
            WorldPosition current, float supportY)
        {
            float deltaY = supportY - current.Y;
            if (Math.Abs(deltaY) < 0.001f)
                return true;
            float increment = Math.Max(0.05f, _capsule.Radius * 0.5f);
            int samples = Math.Clamp((int)Math.Ceiling(Math.Abs(deltaY) / increment), 1, 12);
            for (int i = 1; i <= samples; ++i)
            {
                float height = current.Y + deltaY * i / samples;
                if (!world.IsStandingCapsuleClear(
                        new WorldPosition(current.X, height, current.Z),
                        _capsule, _settings.MaximumSlopeDegrees))
                    return false;
            }
            return true;
        }

        private static void Sanitize(CharacterMotorSettings settings)
        {
            settings.Radius = ClampFinite(settings.Radius, 0.1f, 2f, 0.35f);
            settings.Height = ClampFinite(settings.Height, settings.Radius * 2f, 5f, 1.8f);
            settings.WalkSpeed = ClampFinite(settings.WalkSpeed, 0.1f, 30f, 3.825f);
            settings.SprintSpeed = ClampFinite(settings.SprintSpeed, settings.WalkSpeed, 50f, 5.95f);
            settings.Gravity = ClampFinite(settings.Gravity, 0f, 100f, 24f);
            settings.MaximumFallSpeed = ClampFinite(settings.MaximumFallSpeed, 1f, 200f, 45f);
            settings.JumpSpeed = ClampFinite(settings.JumpSpeed, 0f, 50f, 7f);
            settings.StepHeight = ClampFinite(settings.StepHeight, 0f, 2f, 0.4f);
            settings.GroundSnapDistance = ClampFinite(settings.GroundSnapDistance, 0.05f, 2f, 0.5f);
            settings.MaximumSlopeDegrees = ClampFinite(settings.MaximumSlopeDegrees, 0f, 89f, 50f);
        }

        private static float ClampFinite(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = fallback;
            return Math.Clamp(value, min, max);
        }
    }
}
