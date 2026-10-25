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
/// Pure-.NET authoritative position validator for one connected Player runtime.
/// The client resolves ordinary movement; this class validates accepted positions and
/// the transport adapter serializes them through the existing PlayerEntity wire contract.
/// Alive/dead state is derived from the canonical Health resource, never duplicated here.
/// </summary>
internal sealed class ServerPlayerEntity : IPlayerEntityPresentationSource
{
    private const float PositionPrecision = 0.01f;
    private const long CommandTimeoutMilliseconds = 650;
    private const int MaxAcceptedCommandsPerSecond = 60;
    private const double HeartbeatSeconds = 1.0;
    private const byte SprintIntentFlag = 0x02; // PlayerEntityFlags.Sprinting
    private const byte JumpIntentFlag = 0x08; // PlayerEntityFlags.Jumping
    private const byte CombatReadyInputFlag = 0x02; // PlayerCombatInputFlags.AimHeld

    private readonly ServerMapCatalog _maps;
    private float _walkSpeed;
    private float _sprintSpeed;
    private float _gravity;
    private float _jumpSpeed;
    private bool _grounded = true;
    private bool _correctionPending;
    private long _lastAcceptedPositionAtMs;
    private long _jumpValidationUntilMs;

    private bool _hasCommand;
    private uint _lastAcceptedSequence;
    private sbyte _inputX;
    private sbyte _inputZ;
    private byte _commandYaw;
    private byte _commandFlags;
    private byte _combatInput;
    private bool _combatReady;
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

    internal double LastTickStatusReadMs { get; private set; }
    internal double LastTickInputMs { get; private set; }
    internal double LastTickMotorMs { get; private set; }
    internal double LastTickCollisionWorldLookupMs { get; private set; }
    internal double LastTickMotorPreparationMs { get; private set; }
    internal double LastTickHorizontalCollisionMs { get; private set; }
    internal double LastTickPostCollisionMs { get; private set; }
    internal double LastTickMotorOtherMs { get; private set; }
    internal double LastTickSnapshotDecisionMs { get; private set; }
    internal double LastTickLocationUpdateMs { get; private set; }
    internal double LastTickUnattributedMs { get; private set; }

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
    public float SnapshotVerticalSpeed { get; private set; }
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
        float moveSpeed = SanitizePositive(movement.moveSpeed, _walkSpeed, 30f);
        float sprintSpeed = SanitizePositive(movement.sprintSpeed, _sprintSpeed, 50f);
        sprintSpeed = Math.Max(moveSpeed, sprintSpeed);

        _walkSpeed = moveSpeed;
        _sprintSpeed = sprintSpeed;
        _gravity = SanitizeNonNegative(movement.gravity, _gravity, 100f);
        _jumpSpeed = SanitizeNonNegative(movement.jumpSpeed, _jumpSpeed, 50f);
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
        _walkSpeed = SanitizePositive(movementRules.moveSpeed, PlayerMovementDefaults.WalkSpeed, 30f);
        _sprintSpeed = Math.Max(_walkSpeed,
            SanitizePositive(movementRules.sprintSpeed, PlayerMovementDefaults.SprintSpeed, 50f));
        _gravity = SanitizeNonNegative(movementRules.gravity, PlayerMovementDefaults.Gravity, 100f);
        _jumpSpeed = SanitizeNonNegative(movementRules.jumpSpeed, PlayerMovementDefaults.JumpSpeed, 50f);

        CharacterLocationState location = runtime.Location;
        X = location.Position.X;
        Y = location.Position.Y;
        Z = location.Position.Z;
        YawDegrees = NormalizeYaw(location.YawDegrees);
        _lastAcceptedPositionAtMs = Environment.TickCount64;
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
        int positionX,
        int positionY,
        int positionZ,
        long nowMilliseconds,
        out bool simulationChanged)
    {
        simulationChanged = false;
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

        // The server accepts the client's resolved location, then independently validates
        // its envelope and baked-map legality. Input remains only for public presentation
        // intent and sprint/jump plausibility; it never drives a server Player motor.
        sbyte nextInputX = (sbyte)Math.Clamp((int)inputX, -127, 127);
        sbyte nextInputZ = (sbyte)Math.Clamp((int)inputZ, -127, 127);
        byte intentFlags = (byte)(flags & (SprintIntentFlag | JumpIntentFlag));
        bool jumpHeld = (intentFlags & JumpIntentFlag) != 0;
        bool combatReady = (combatFlags & CombatReadyInputFlag) != 0;
        bool wasFresh = _hasCommand && nowMilliseconds - _lastCommandTimestampMs <= CommandTimeoutMilliseconds;
        bool inputChanged = !wasFresh || nextInputX != _inputX || nextInputZ != _inputZ ||
                            yaw != _commandYaw || intentFlags != _commandFlags || combatReady != _combatReady;

        WorldPosition reported = new WorldPosition(
            positionX * PositionPrecision,
            positionY * PositionPrecision,
            positionZ * PositionPrecision);
        WorldPosition previous = new WorldPosition(X, Y, Z);
        float dx = reported.X - X;
        float dz = reported.Z - Z;
        float dy = reported.Y - Y;
        float horizontalDistance = MathF.Sqrt(dx * dx + dz * dz);
        float elapsed = Math.Clamp((nowMilliseconds - _lastAcceptedPositionAtMs) / 1000f, 0.02f, 1.25f);
        float selectedSpeed = (intentFlags & SprintIntentFlag) != 0 ? _sprintSpeed : _walkSpeed;
        float distanceMultiplier = (intentFlags & SprintIntentFlag) != 0
            ? PlayerMovementDefaults.SprintDistanceMultiplier
            : PlayerMovementDefaults.WalkDistanceMultiplier;
        float horizontalLimit = selectedSpeed * distanceMultiplier * elapsed + 0.65f;
        float verticalLimit = Math.Max(_jumpSpeed, 2f) * elapsed +
                              0.5f * _gravity * elapsed * elapsed + 0.65f;
        bool positionChanged = horizontalDistance > 0.015f || Math.Abs(dy) > 0.015f;
        bool movementLocked = false;
        if (positionChanged)
        {
            CharacterStatusEffectsState statusState = Runtime.CaptureStatusEffects();
            movementLocked = statusState.HasControl(ControlEffectType.Stun) ||
                              statusState.HasControl(ControlEffectType.Root);
        }
        bool plausible = !IsDead && !movementLocked &&
                         horizontalDistance <= horizontalLimit && Math.Abs(dy) <= verticalLimit;
        bool jumpInitiated = jumpHeld && dy > 0.015f && _grounded;
        bool jumpValidationActive = jumpHeld || jumpInitiated ||
                                    nowMilliseconds <= _jumpValidationUntilMs;
        bool grounded = _grounded;
        if (plausible && positionChanged)
        {
            ServerCollisionWorld collision = null;
            _maps?.TryGetCollisionWorld(Runtime.Location.MapId, Runtime.Location.InstanceId, out collision);
            bool falling = reported.Y < previous.Y - 0.03f;
            plausible = collision != null && collision.ValidateReportedPlayerMove(
                previous, reported, jumpValidationActive, falling, 50f, out grounded);
        }

        simulationChanged = inputChanged || positionChanged || _correctionPending;
        _inputX = nextInputX;
        _inputZ = nextInputZ;
        _commandYaw = yaw;
        _commandFlags = intentFlags;

        // Combat hold/aim state shares the existing one-byte movement field and does not
        // by itself wake movement simulation. Low bits are hold flags; upper bits are
        // quantized aim pitch. Re-encode after decode to keep malformed clients inside the
        // canonical input domain.
        PlayerCombatInputFlags combatState = PlayerCombatInputEncoding.DecodeFlags(combatFlags);
        float aimPitch = PlayerCombatInputEncoding.DecodeAimPitchDegrees(combatFlags);
        _combatInput = IsDead ? (byte)0 : PlayerCombatInputEncoding.Encode(combatState, aimPitch);
        _combatReady = !IsDead && combatReady;
        _lastCommandTimestampMs = nowMilliseconds;
        _hasCommand = true;

        if (positionChanged && plausible)
        {
            X = reported.X;
            Y = reported.Y;
            Z = reported.Z;
            YawDegrees = DequantizeYaw(yaw);
            _grounded = grounded;
            CharacterLocationState priorLocation = Runtime.Location;
            Runtime.UpdateLocation(new CharacterLocationState(
                priorLocation.MapId,
                priorLocation.InstanceId,
                reported,
                YawDegrees));
            SnapshotVerticalSpeed = elapsed > 0.0001f ? dy / elapsed : 0f;
            SnapshotSpeed = Math.Min(selectedSpeed, horizontalDistance / (elapsed * distanceMultiplier));
            SnapshotFlags = (byte)((grounded ? 0x01 : 0) |
                ((intentFlags & SprintIntentFlag) != 0 ? 0x02 | 0x80 : 0) |
                (!grounded ? 0x08 : 0) |
                (_combatReady ? 0x20 : 0));
            SnapshotMoveState = grounded ? (byte)(SnapshotSpeed > 0.01f ? 1 : 0) : (byte)2;
            _lastAcceptedPositionAtMs = nowMilliseconds;
            if (jumpInitiated)
                _jumpValidationUntilMs = nowMilliseconds + 1200;
            if (grounded && dy <= 0.03f)
                _jumpValidationUntilMs = 0;
        }
        else if (positionChanged)
        {
            _correctionPending = true;
            SnapshotFlags |= 0x10; // Teleport: existing owner and observer snap path.
            simulationChanged = true;
        }
        else if (inputChanged)
        {
            YawDegrees = DequantizeYaw(yaw);
            if (nextInputX == 0 && nextInputZ == 0)
            {
                SnapshotSpeed = 0f;
                SnapshotFlags &= unchecked((byte)~0x82); // Running and sprinting presentation.
                SnapshotMoveState = _grounded ? (byte)0 : (byte)2;
            }
            if (_combatReady)
                SnapshotFlags |= 0x20;
            else
                SnapshotFlags &= unchecked((byte)~0x20);
        }

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
    /// Ordinary Player movement is reported by the client and never schedules a server motor.
    /// </summary>
    public bool RequiresContinuousSimulation(long nowMilliseconds)
    {
        // Client-resolved movement arrives through MovementCommand. Input leases never
        // schedule continuous server Player motor work.
        return false;
    }

    /// <summary>
    /// Evaluates event-driven Player presentation and snapshot state. It does not integrate
    /// or resolve ordinary Player movement.
    /// </summary>
    public bool Tick(float fixedDelta, double nowSeconds, long nowMilliseconds, out float moveSpeed, out byte flags, out byte moveState, out ServerPlayerSnapshotReason reason)
    {
        long tickStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        long stageStartedAt = tickStartedAt;
        LastTickCollisionWorldLookupMs = 0d;
        LastTickMotorPreparationMs = 0d;
        LastTickHorizontalCollisionMs = 0d;
        LastTickPostCollisionMs = 0d;
        LastTickMotorOtherMs = 0d;
        LastTickStatusReadMs = 0d;
        LastTickInputMs = 0d;
        LastTickMotorMs = 0d;
        LastTickLocationUpdateMs = 0d;

        bool dead = IsDead;
        CharacterStatusEffectsState statusState = Runtime.CaptureStatusEffects();
        bool stunned = statusState.HasControl(ControlEffectType.Stun);
        bool rooted = statusState.HasControl(ControlEffectType.Root);
        LastTickStatusReadMs = ElapsedMillisecondsSince(stageStartedAt);
        stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        if (dead)
        {
            ClearMovementIntent();
            _grounded = true;
            SnapshotVerticalSpeed = 0f;
            SnapshotSpeed = 0f;
        }
        else if (stunned || rooted)
        {
            SnapshotSpeed = 0f;
            SnapshotFlags &= 0x7D; // Clear running/sprinting while control is locked.
        }

        moveSpeed = dead ? 0f : SnapshotSpeed;
        flags = _correctionPending
            ? SnapshotFlags
            : (byte)(SnapshotFlags & 0xEF); // Teleport is a one-snapshot correction pulse.
        if (dead)
            flags = 0x01;
        moveState = dead ? (byte)6 : !_grounded ? (byte)2 : moveSpeed > 0.01f ? (byte)1 : (byte)0;
        SnapshotFlags = flags;
        SnapshotMoveState = moveState;
        LastTickInputMs = ElapsedMillisecondsSince(stageStartedAt);

        stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        bool moved = DistanceSquared(X, Y, Z, _lastSnapshotX, _lastSnapshotY, _lastSnapshotZ) >=
                     PositionPrecision * PositionPrecision;
        byte wireYaw = QuantizeYaw(YawDegrees);
        bool rotated = wireYaw != _lastSnapshotWireYaw;
        bool flagsChanged = flags != _lastSnapshotFlags;
        bool movementStateChanged = moveState != _lastSnapshotMoveState;
        bool deathStateChanged = dead != _lastSnapshotDead;
        bool heartbeat = nowSeconds >= _nextHeartbeatAt;

        reason = ServerPlayerSnapshotReason.None;
        if (_forceSnapshot || _correctionPending)
            reason |= ServerPlayerSnapshotReason.Forced;
        if (moved || _correctionPending)
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
        {
            LastTickSnapshotDecisionMs = ElapsedMillisecondsSince(stageStartedAt);
            LastTickUnattributedMs = Math.Max(0d, ElapsedMillisecondsSince(tickStartedAt) -
                LastTickStatusReadMs - LastTickInputMs - LastTickSnapshotDecisionMs);
            return false;
        }

        _lastSnapshotX = X;
        _lastSnapshotY = Y;
        _lastSnapshotZ = Z;
        _lastSnapshotWireYaw = wireYaw;
        _lastSnapshotFlags = flags;
        _lastSnapshotMoveState = moveState;
        _lastSnapshotDead = dead;
        _nextHeartbeatAt = nowSeconds + HeartbeatSeconds;
        _forceSnapshot = false;
        LastTickSnapshotDecisionMs = ElapsedMillisecondsSince(stageStartedAt);

        stageStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        CharacterLocationState prior = Runtime.Location;
        Runtime.UpdateLocation(new CharacterLocationState(
            prior.MapId,
            prior.InstanceId,
            new WorldPosition(X, Y, Z),
            YawDegrees));
        LastTickLocationUpdateMs = ElapsedMillisecondsSince(stageStartedAt);
        _correctionPending = false;
        LastTickUnattributedMs = Math.Max(0d, ElapsedMillisecondsSince(tickStartedAt) -
            LastTickStatusReadMs - LastTickInputMs - LastTickSnapshotDecisionMs - LastTickLocationUpdateMs);
        return true;
    }

    private static double ElapsedMillisecondsSince(long startedAt) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) *
        (1000d / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Server-authoritative teleport used by lifecycle/world services.</summary>
    public void Warp(CharacterLocationState location)
    {
        X = location.Position.X;
        Y = location.Position.Y;
        Z = location.Position.Z;
        YawDegrees = NormalizeYaw(location.YawDegrees);
        _grounded = true;
        SnapshotVerticalSpeed = 0f;
        _lastAcceptedPositionAtMs = Environment.TickCount64;
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
        _jumpValidationUntilMs = 0;
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

