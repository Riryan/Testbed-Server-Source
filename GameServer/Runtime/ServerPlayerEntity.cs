using Game.Server.Application.Movement;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Resources;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.World;
using Player.Shared;

namespace Game.GameServer.Runtime;

[Flags]
internal enum ServerPlayerSnapshotReason : byte
{
    None = 0,
    Position = 1 << 0,
    Rotation = 1 << 1,
    PresentationState = 1 << 2,
    DeathState = 1 << 3,
    Forced = 1 << 4,
    Heartbeat = 1 << 5,
}

/// <summary>
/// Pure-.NET authoritative movement shell for one connected Player runtime.
/// The transport adapter serializes this state using the existing PlayerEntity wire contract.
/// Alive/dead state is derived from the canonical Health resource, never duplicated here.
/// </summary>
internal sealed class ServerPlayerEntity : IPlayerEntityPresentationSource
{
    private const float InputPrecision = 1f / 127f;
    private const float PositionPrecision = 0.01f;
    private const long CommandTimeoutMilliseconds = 650;
    private const int MaxAcceptedCommandsPerSecond = 60;
    private const double HeartbeatSeconds = 1.0;
    private const byte SprintIntentFlag = 0x02; // PlayerEntityFlags.Sprinting
    private const byte JumpIntentFlag = 0x08; // PlayerEntityFlags.Jumping
    private const byte CombatReadyInputFlag = 0x02; // PlayerCombatInputFlags.AimHeld

    private readonly ServerMapCatalog _maps;
    private readonly ServerCharacterMotor _motor;
    private CharacterMotorState _motorState;

    private bool _hasCommand;
    private uint _lastAcceptedSequence;
    private sbyte _inputX;
    private sbyte _inputZ;
    private byte _commandYaw;
    private byte _commandFlags;
    private byte _combatInput;
    private bool _combatReady;
    // Jump is edge-triggered on the server. A held client flag must not turn into
    // automatic bunny-hopping whenever the motor becomes grounded again.
    private bool _jumpHeld;
    private bool _jumpRequested;
    private long _lastCommandTimestampMs;
    private long _rateWindowStartMs;
    private int _rateWindowAccepted;
    private float _lastSnapshotX;
    private float _lastSnapshotY;
    private float _lastSnapshotZ;
    private byte _lastSnapshotWireYaw;
    private byte _lastSnapshotFlags;
    private byte _lastSnapshotMoveState = byte.MaxValue;
    private bool _lastSnapshotDead;
    private bool _forceSnapshot;
    private double _nextHeartbeatAt;

    public uint ObjectId { get; }
    public ushort Generation { get; }
    public long ConnectionId { get; }
    public PlayerRuntime Runtime { get; }
    public float X { get; private set; }
    public float Y { get; private set; }
    public float Z { get; private set; }
    public float YawDegrees { get; private set; }
    public uint AppearanceSequence { get; private set; } = 1;
    public float SnapshotSpeed { get; private set; }
    public float SnapshotVerticalSpeed => _motorState.VerticalVelocity;
    public byte SnapshotFlags { get; private set; } = 0x01; // Grounded
    public byte SnapshotMoveState { get; private set; }
    public bool IsCombatReady => !IsDead && _combatReady;

    public void MarkAppearanceChanged()
    {
        AppearanceSequence++;
        if (AppearanceSequence == 0)
            AppearanceSequence = 1;
    }

    public void ApplyMovementSettings(MovementRulesDefinition movement)
    {
        if (movement == null)
            return;

        // GameplayContentValidation has already validated the active revision. Clamp again
        // at this final authority boundary so malformed runtime injection cannot produce
        // non-finite or inverted movement tuning.
        float moveSpeed = SanitizePositive(movement.moveSpeed, _motor.Settings.WalkSpeed, 30f);
        float sprintSpeed = SanitizePositive(movement.sprintSpeed, _motor.Settings.SprintSpeed, 50f);
        sprintSpeed = Math.Max(moveSpeed, sprintSpeed);

        _motor.Settings.WalkSpeed = moveSpeed;
        _motor.Settings.SprintSpeed = sprintSpeed;
        _motor.Settings.Gravity = SanitizeNonNegative(movement.gravity, _motor.Settings.Gravity, 100f);
        _motor.Settings.JumpSpeed = SanitizeNonNegative(movement.jumpSpeed, _motor.Settings.JumpSpeed, 50f);
        _forceSnapshot = true;
    }

    public bool IsDead =>
        Runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) &&
        health.Enabled &&
        health.Current <= health.Minimum;

    public ServerPlayerEntity(
        uint objectId,
        ushort generation,
        long connectionId,
        PlayerRuntime runtime,
        ServerMapCatalog maps = null,
        MovementRulesDefinition movement = null)
    {
        if (objectId == 0) throw new ArgumentOutOfRangeException(nameof(objectId));
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        ObjectId = objectId;
        Generation = generation;
        ConnectionId = connectionId;
        _maps = maps;
        MovementRulesDefinition movementRules = movement ?? new MovementRulesDefinition();
        _motor = new ServerCharacterMotor(new CharacterMotorSettings
        {
            WalkSpeed = movementRules.moveSpeed,
            SprintSpeed = movementRules.sprintSpeed,
            Gravity = movementRules.gravity,
            JumpSpeed = movementRules.jumpSpeed,
            Radius = 0.35f,
            Height = 1.8f,
            StepHeight = 0.4f,
            GroundSnapDistance = 0.5f,
            MaximumSlopeDegrees = 50f,
        });

        CharacterLocationState location = runtime.Location;
        X = location.Position.X;
        Y = location.Position.Y;
        Z = location.Position.Z;
        YawDegrees = NormalizeYaw(location.YawDegrees);
        _motorState = new CharacterMotorState(location.Position);
        _lastSnapshotX = X;
        _lastSnapshotY = Y;
        _lastSnapshotZ = Z;
        _lastSnapshotWireYaw = QuantizeYaw(YawDegrees);
        _lastSnapshotDead = IsDead;
        _lastCommandTimestampMs = Environment.TickCount64;
        _rateWindowStartMs = _lastCommandTimestampMs;
        _nextHeartbeatAt = 0d;
    }

    public bool TryAcceptMovement(
        uint sequence,
        sbyte inputX,
        sbyte inputZ,
        byte yaw,
        byte flags,
        byte combatFlags,
        long nowMilliseconds,
        out bool simulationChanged)
    {
        simulationChanged = false;
        if (IsDead)
        {
            ClearMovementIntent();
            return false;
        }
        if (!IsNewer(sequence, _lastAcceptedSequence))
            return false;

        if (nowMilliseconds - _rateWindowStartMs >= 1000)
        {
            _rateWindowStartMs = nowMilliseconds;
            _rateWindowAccepted = 0;
        }
        if (_rateWindowAccepted >= MaxAcceptedCommandsPerSecond)
            return false;

        _rateWindowAccepted++;
        _lastAcceptedSequence = sequence;

        // The wire type can technically represent -128, while the canonical client
        // quantizer emits -127..127. Clamp to the canonical input domain and discard all
        // client-authored presentation/state flags except the movement intent bits the
        // authoritative motor consumes (Sprint + Jump). Grounded/running/death remain derived.
        sbyte nextInputX = (sbyte)Math.Clamp((int)inputX, -127, 127);
        sbyte nextInputZ = (sbyte)Math.Clamp((int)inputZ, -127, 127);
        byte intentFlags = (byte)(flags & (SprintIntentFlag | JumpIntentFlag));
        bool jumpHeld = (intentFlags & JumpIntentFlag) != 0;
        bool combatReady = (combatFlags & CombatReadyInputFlag) != 0;
        bool wasFresh = _hasCommand &&
                        nowMilliseconds - _lastCommandTimestampMs <= CommandTimeoutMilliseconds;

        simulationChanged =
            !wasFresh ||
            nextInputX != _inputX ||
            nextInputZ != _inputZ ||
            yaw != _commandYaw ||
            intentFlags != _commandFlags ||
            combatReady != _combatReady ||
            (jumpHeld && !_jumpHeld);

        _inputX = nextInputX;
        _inputZ = nextInputZ;
        _commandYaw = yaw;
        if (jumpHeld && !_jumpHeld)
            _jumpRequested = true;
        _jumpHeld = jumpHeld;
        _commandFlags = intentFlags;

        // Combat hold/aim state shares the existing one-byte movement field and does not
        // by itself wake movement simulation. Low bits are hold flags; upper bits are
        // quantized aim pitch. Re-encode after decode to keep malformed clients inside the
        // canonical input domain.
        PlayerCombatInputFlags combatState = PlayerCombatInputEncoding.DecodeFlags(combatFlags);
        float aimPitch = PlayerCombatInputEncoding.DecodeAimPitchDegrees(combatFlags);
        _combatInput = PlayerCombatInputEncoding.Encode(combatState, aimPitch);
        _combatReady = combatReady;
        _lastCommandTimestampMs = nowMilliseconds;
        _hasCommand = true;
        return true;
    }

    public bool TryGetFreshCombatInput(long nowMilliseconds, out byte combatFlags)
    {
        bool fresh = !IsDead && _hasCommand && nowMilliseconds - _lastCommandTimestampMs <= CommandTimeoutMilliseconds;
        combatFlags = fresh ? (byte)PlayerCombatInputEncoding.DecodeFlags(_combatInput) : (byte)0;
        return fresh;
    }

    public bool TryGetFreshCombatInput(long nowMilliseconds, out byte combatFlags, out float aimPitchDegrees)
    {
        bool fresh = !IsDead && _hasCommand && nowMilliseconds - _lastCommandTimestampMs <= CommandTimeoutMilliseconds;
        if (!fresh)
        {
            combatFlags = 0;
            aimPitchDegrees = 0f;
            return false;
        }

        combatFlags = (byte)PlayerCombatInputEncoding.DecodeFlags(_combatInput);
        aimPitchDegrees = PlayerCombatInputEncoding.DecodeAimPitchDegrees(_combatInput);
        return true;
    }

    /// <summary>
    /// True while this entity needs fixed-cadence movement simulation without another
    /// external wake event. Grounded stationary players return false. Fresh movement,
    /// jump/airborne motion, and one final stale-input stop transition remain continuous.
    /// </summary>
    public bool RequiresContinuousSimulation(long nowMilliseconds)
    {
        if (!_motorState.Grounded || _jumpRequested)
            return true;

        if (!_hasCommand)
            return false;

        long age = Math.Max(0L, nowMilliseconds - _lastCommandTimestampMs);
        if (age <= CommandTimeoutMilliseconds)
            return _inputX != 0 || _inputZ != 0;

        // If the last replicated state was moving, run one timeout tick so authority
        // publishes the transition back to idle before this entity goes dormant.
        return SnapshotSpeed > 0.01f || _lastSnapshotMoveState == 1;
    }

    /// <summary>
    /// Executes one fixed authoritative simulation step. Returns true when a snapshot
    /// should be emitted because authoritative presentation state changed or heartbeat is due.
    /// </summary>
    public bool Tick(float fixedDelta, double nowSeconds, long nowMilliseconds, out float moveSpeed, out byte flags, out byte moveState, out ServerPlayerSnapshotReason reason)
    {
        bool dead = IsDead;
        if (dead)
            ClearMovementIntent();

        CharacterStatusEffectsState statusState = Runtime.CaptureStatusEffects();
        bool stunned = statusState.HasControl(ControlEffectType.Stun);
        bool rooted = stunned || statusState.HasControl(ControlEffectType.Root);
        bool timedOut = dead || !_hasCommand || nowMilliseconds - _lastCommandTimestampMs > CommandTimeoutMilliseconds;
        float x = timedOut || rooted ? 0f : _inputX * InputPrecision;
        float z = timedOut || rooted ? 0f : _inputZ * InputPrecision;
        float magnitudeSq = x * x + z * z;
        if (magnitudeSq > 1f)
        {
            float inv = 1f / MathF.Sqrt(magnitudeSq);
            x *= inv;
            z *= inv;
            magnitudeSq = 1f;
        }

        bool sprint = !timedOut && !rooted && (_commandFlags & SprintIntentFlag) != 0;
        bool jump = !timedOut && !rooted && _jumpRequested;
        // Consume the edge exactly once regardless of whether the motor can execute it
        // (for example, an airborne jump request). A new jump requires release + press.
        _jumpRequested = false;

        float speed = sprint
            ? _motor.Settings.SprintSpeed
            : _motor.Settings.WalkSpeed;
        float magnitude = magnitudeSq <= 0f ? 0f : MathF.Sqrt(magnitudeSq);
        moveSpeed = dead ? 0f : magnitude * speed;

        if (!timedOut && !stunned)
            YawDegrees = DequantizeYaw(_commandYaw);

        if (dead)
        {
            _motorState.Mode = Game.Shared.Actors.ActorMovementMode.Dead;
            _motorState.Grounded = true;
            _motorState.VerticalVelocity = 0f;
        }
        else
        {
            if (_motorState.Mode == Game.Shared.Actors.ActorMovementMode.Dead)
                _motorState.Mode = Game.Shared.Actors.ActorMovementMode.Grounded;

            ServerCollisionWorld collision = null;
            _maps?.TryGetCollisionWorld(Runtime.Location.MapId, Runtime.Location.InstanceId, out collision);
            _motor.Tick(
                _motorState,
                new CharacterMovementIntent(x, z, sprint, jump),
                fixedDelta,
                collision);
            X = _motorState.Position.X;
            Y = _motorState.Position.Y;
            Z = _motorState.Position.Z;
        }

        flags = 0;
        if (_motorState.Grounded || dead)
            flags |= 0x01; // Grounded
        if (!dead && sprint && magnitudeSq > 0.0001f)
            flags |= 0x02 | 0x80; // Sprinting | Running
        if (!dead && !_motorState.Grounded)
            flags |= 0x08; // Jumping/Airborne presentation bit
        if (!dead && _combatReady)
            flags |= 0x20; // CombatReady presentation bit; zero additional snapshot bytes
        moveState = dead ? (byte)6 : !_motorState.Grounded ? (byte)2 : moveSpeed > 0.01f ? (byte)1 : (byte)0;
        SnapshotSpeed = moveSpeed;
        SnapshotFlags = flags;
        SnapshotMoveState = moveState;

        bool moved =
            DistanceSquared(X, Y, Z, _lastSnapshotX, _lastSnapshotY, _lastSnapshotZ) >=
            PositionPrecision * PositionPrecision;
        // Dirty rotation must match the actual wire representation. If quantization still
        // produces the same byte, the client cannot observe a rotation change and there is
        // no reason to wake replication for it.
        byte wireYaw = QuantizeYaw(YawDegrees);
        bool rotated = wireYaw != _lastSnapshotWireYaw;
        bool flagsChanged = flags != _lastSnapshotFlags;
        bool movementStateChanged = moveState != _lastSnapshotMoveState;
        bool deathStateChanged = dead != _lastSnapshotDead;
        bool heartbeat = nowSeconds >= _nextHeartbeatAt;

        reason = ServerPlayerSnapshotReason.None;
        if (_forceSnapshot)
            reason |= ServerPlayerSnapshotReason.Forced;
        if (moved)
            reason |= ServerPlayerSnapshotReason.Position;
        if (rotated)
            reason |= ServerPlayerSnapshotReason.Rotation;
        if (flagsChanged || movementStateChanged)
            reason |= ServerPlayerSnapshotReason.PresentationState;
        if (deathStateChanged)
            reason |= ServerPlayerSnapshotReason.DeathState;
        if (heartbeat)
            reason |= ServerPlayerSnapshotReason.Heartbeat;

        if (reason == ServerPlayerSnapshotReason.None)
            return false;

        _lastSnapshotX = X;
        _lastSnapshotY = Y;
        _lastSnapshotZ = Z;
        _lastSnapshotWireYaw = wireYaw;
        _lastSnapshotFlags = flags;
        _lastSnapshotMoveState = moveState;
        _lastSnapshotDead = dead;
        _nextHeartbeatAt = nowSeconds + HeartbeatSeconds;
        _forceSnapshot = false;

        CharacterLocationState prior = Runtime.Location;
        Runtime.UpdateLocation(new CharacterLocationState(
            prior.MapId,
            prior.InstanceId,
            new WorldPosition(X, Y, Z),
            YawDegrees));
        return true;
    }

    /// <summary>Server-authoritative teleport used by lifecycle/world services.</summary>
    public void Warp(CharacterLocationState location)
    {
        X = location.Position.X;
        Y = location.Position.Y;
        Z = location.Position.Z;
        YawDegrees = NormalizeYaw(location.YawDegrees);
        _motorState = new CharacterMotorState(location.Position);
        ClearMovementIntent();
        _lastSnapshotX = X;
        _lastSnapshotY = Y;
        _lastSnapshotZ = Z;
        _lastSnapshotWireYaw = QuantizeYaw(YawDegrees);
        _lastSnapshotFlags = 0;
        _lastSnapshotMoveState = byte.MaxValue;
        _lastSnapshotDead = IsDead;
        SnapshotSpeed = 0f;
        SnapshotFlags = 0x01; // Grounded
        SnapshotMoveState = _lastSnapshotDead ? (byte)6 : (byte)0;
        _forceSnapshot = true;
        _nextHeartbeatAt = 0d;
        Runtime.UpdateLocation(location);
    }

    public CharacterLocationState CaptureLocation()
    {
        CharacterLocationState prior = Runtime.Location;
        return new CharacterLocationState(
            prior.MapId,
            prior.InstanceId,
            new WorldPosition(X, Y, Z),
            YawDegrees);
    }

    public static int QuantizePosition(float value)
    {
        double scaled = value / PositionPrecision;
        if (scaled >= int.MaxValue) return int.MaxValue;
        if (scaled <= int.MinValue) return int.MinValue;
        return (int)Math.Round(scaled, MidpointRounding.ToEven);
    }

    public const int YawBits = 8;
    public const int YawSteps = 1 << YawBits;
    public const float YawStepDegrees = 360f / YawSteps;

    public static byte QuantizeYaw(float yaw)
    {
        float normalized = NormalizeYaw(yaw) / 360f;
        int value = ((int)Math.Round(normalized * YawSteps, MidpointRounding.ToEven)) & (YawSteps - 1);
        return (byte)value;
    }

    public static float DequantizeYaw(byte yaw) => yaw * YawStepDegrees;

    public static ushort QuantizeUnsignedSpeed(float speed)
    {
        int value = (int)Math.Round(Math.Max(0f, speed) / 0.01f, MidpointRounding.ToEven);
        return (ushort)Math.Clamp(value, 0, ushort.MaxValue);
    }

    public static short QuantizeSignedSpeed(float speed)
    {
        int value = (int)Math.Round(speed / 0.01f, MidpointRounding.ToEven);
        return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    }

    private void ClearMovementIntent()
    {
        _hasCommand = false;
        _inputX = 0;
        _inputZ = 0;
        _combatInput = 0;
        _commandFlags = 0;
        _jumpHeld = false;
        _jumpRequested = false;
    }

    private static bool IsNewer(uint candidate, uint reference) =>
        candidate != reference && unchecked((int)(candidate - reference)) > 0;

    private static float SanitizePositive(float value, float fallback, float maximum)
    {
        float safeFallback = float.IsFinite(fallback) && fallback > 0f
            ? fallback
            : Math.Min(1f, Math.Max(0.001f, maximum));

        if (!float.IsFinite(value) || value <= 0f)
            return Math.Min(safeFallback, maximum);

        return Math.Min(value, maximum);
    }

    private static float SanitizeNonNegative(float value, float fallback, float maximum)
    {
        float safeFallback = float.IsFinite(fallback) && fallback >= 0f
            ? fallback
            : 0f;

        if (!float.IsFinite(value) || value < 0f)
            return Math.Min(safeFallback, maximum);

        return Math.Min(value, maximum);
    }

    private static float NormalizeYaw(float value)
    {
        float result = value % 360f;
        if (result < 0f) result += 360f;
        return result;
    }

    private static float DeltaAngle(float current, float target)
    {
        float delta = NormalizeYaw(target - current);
        if (delta > 180f) delta -= 360f;
        return delta;
    }

    private static float DistanceSquared(float ax, float ay, float az, float bx, float by, float bz)
    {
        float x = ax - bx;
        float y = ay - by;
        float z = az - bz;
        return x * x + y * y + z * z;
    }
}
