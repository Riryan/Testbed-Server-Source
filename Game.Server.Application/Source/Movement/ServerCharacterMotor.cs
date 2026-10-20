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
    /// Server-authoritative kinematic motor. Grounded horizontal movement uses the
    /// SAME GroundedMove.cs as Unity client prediction. Jump/fall/landing retains
    /// the existing motor paths. Animator/root motion has no authority here.
    /// </summary>
    public sealed class ServerCharacterMotor
    {
        private readonly CharacterMotorSettings _settings;
        private readonly ServerCapsule _capsule;

        private ServerCollisionWorld _rejectedWorld;
        private WorldPosition _rejectedAt;
        private float _rejectedDeltaX;
        private float _rejectedDeltaZ;
        private bool _hasRejectedGroundedMove;

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

        // Population and other explicit six-argument callers keep the validated
        // existing movement resolver. PlayerEntity uses the four-argument overload
        // and ONLY that path uses canonical shared horizontal movement.
        public void Tick(CharacterMotorState state, CharacterMovementIntent intent,
            float fixedDelta, ServerCollisionWorld collisionWorld,
            bool allowWallSlide, out bool blocked)
        {
            TickInternal(state, intent, fixedDelta, collisionWorld,
                allowWallSlide, out blocked, false);
        }

        private void TickInternal(CharacterMotorState state, CharacterMovementIntent intent,
            float fixedDelta, ServerCollisionWorld collisionWorld,
            bool allowWallSlide, out bool blocked, bool sharedGrounded)
        {
            long tickStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            long stageStartedAt = tickStartedAt;
            LastTickPreparationMs = 0d;
            LastTickHorizontalCollisionMs = 0d;
            LastTickPostCollisionMs = 0d;
            LastTickOtherMs = 0d;
            blocked = false;
            if (state == null || fixedDelta <= 0f ||
                float.IsNaN(fixedDelta) || float.IsInfinity(fixedDelta))
            {
                LastTickOtherMs = ElapsedMillisecondsSince(tickStartedAt);
                return;
            }

            if (state.Mode == ActorMovementMode.Dead ||
                state.Mode == ActorMovementMode.InteractionLocked ||
                state.Mode == ActorMovementMode.Vaulting ||
                state.Mode == ActorMovementMode.Ladder ||
                state.Mode == ActorMovementMode.Mounted)
            {
                LastTickOtherMs = ElapsedMillisecondsSince(tickStartedAt);
                return;
            }

            float x = intent.X;
            float z = intent.Z;
            float magnitudeSq = x * x + z * z;
            if (magnitudeSq > 1f)
            {
                float inv = 1f / MathF.Sqrt(magnitudeSq);
                x *= inv;
                z *= inv;
            }
            float speed = intent.Sprint ? _settings.SprintSpeed : _settings.WalkSpeed;
            float dx = x * speed * fixedDelta;
            float dz = z * speed * fixedDelta;
            LastTickPreparationMs = ElapsedMillisecondsSince(stageStartedAt);

            if (collisionWorld == null)
            {
                state.Position = new WorldPosition(state.Position.X + dx,
                    state.Position.Y, state.Position.Z + dz);
                state.LastSafePosition = state.Position;
                state.Grounded = true;
                state.Mode = ActorMovementMode.Grounded;
                state.VerticalVelocity = 0f;
                LastTickPostCollisionMs =
                    ElapsedMillisecondsSince(tickStartedAt) - LastTickPreparationMs;
                return;
            }

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            WorldPosition previousPosition = state.Position;
            WorldPosition moved = state.Position;
            bool groundedHorizontal = state.Grounded && !intent.Jump;

            // Static wall press: cached only for an identical world, position and
            // displacement. New direction, jump, world or pose means revalidate.
            if (groundedHorizontal && _hasRejectedGroundedMove &&
                ReferenceEquals(collisionWorld, _rejectedWorld) &&
                state.Position == _rejectedAt &&
                dx == _rejectedDeltaX && dz == _rejectedDeltaZ)
            {
                blocked = true;
                LastTickHorizontalCollisionMs = ElapsedMillisecondsSince(stageStartedAt);
                LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStartedAt) -
                    LastTickPreparationMs - LastTickHorizontalCollisionMs);
                return;
            }
            _hasRejectedGroundedMove = false;

            if (groundedHorizontal && sharedGrounded)
            {
                // Player-only: exact source is compiled in client and server.
                moved = GroundedMove.Resolve(collisionWorld, state.Position,
                    dx, dz, _capsule, _settings.StepHeight,
                    _settings.GroundSnapDistance, _settings.MaximumSlopeDegrees,
                    out blocked);
            }
            else if (groundedHorizontal && !sharedGrounded &&
                collisionWorld.TryResolveCertifiedGroundedNoSlide(
                    state.Position, dx, dz, _capsule,
                    _settings.StepHeight, _settings.GroundSnapDistance,
                    _settings.MaximumSlopeDegrees, out moved, out blocked))
            {
                // Existing Population certified movement result.
            }
            else if (groundedHorizontal && !sharedGrounded &&
                     collisionWorld.HasCertifiedGroundedMovement)
            {
                // Existing Population fail-closed policy on certified maps.
                moved = state.Position;
                blocked = true;
            }
            else
            {
                // Existing jump, airborne and Population fallback policy.
                moved = collisionWorld.ResolveHorizontalMove(
                    state.Position, dx, dz, _capsule,
                    _settings.StepHeight, _settings.GroundSnapDistance,
                    _settings.MaximumSlopeDegrees, state.Grounded,
                    allowWallSlide: false, out blocked);
            }
            LastTickHorizontalCollisionMs = ElapsedMillisecondsSince(stageStartedAt);

            if (groundedHorizontal && blocked && moved == previousPosition)
            {
                if (Math.Abs(dx) > 0.000001f || Math.Abs(dz) > 0.000001f)
                {
                    _hasRejectedGroundedMove = true;
                    _rejectedWorld = collisionWorld;
                    _rejectedAt = state.Position;
                    _rejectedDeltaX = dx;
                    _rejectedDeltaZ = dz;
                }
                LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStartedAt) -
                    LastTickPreparationMs - LastTickHorizontalCollisionMs);
                return;
            }

            stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool canJump = state.Grounded && state.Mode == ActorMovementMode.Grounded;
            bool groundedPoseValidated = false;
            if (intent.Jump && canJump)
            {
                state.Grounded = false;
                state.Mode = ActorMovementMode.Jumping;
                state.VerticalVelocity = _settings.JumpSpeed;
            }

            if (state.Grounded && !intent.Jump &&
                collisionWorld.TryFindGround(moved, _capsule.Radius,
                    _settings.StepHeight, _settings.GroundSnapDistance,
                    _settings.MaximumSlopeDegrees, out ServerGroundHit ground))
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
                    if ((Math.Abs(dx) > 0.000001f || Math.Abs(dz) > 0.000001f) &&
                        moved != previousPosition)
                        state.LastSafePosition = state.Position;
                }
                else if (collisionWorld.IsCapsuleClear(state.Position, _capsule))
                {
                    state.LastSafePosition = state.Position;
                }
            }
            LastTickPostCollisionMs = ElapsedMillisecondsSince(stageStartedAt);
            LastTickOtherMs = Math.Max(0d, ElapsedMillisecondsSince(tickStartedAt) -
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
