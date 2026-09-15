using System;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Interactions;
using LiteNetLib.Utils;

namespace Player.Networking
{
    /// <summary>
    /// Client intent request IDs for the already-converted authoritative combat,
    /// ability, and interaction services. ObjectId + generation are transport-level
    /// target identity only; the server resolves them back to authoritative runtimes.
    /// </summary>
    public static class PlayerGameplayActionRequestTypes
    {
        public const ushort BasicAttack = 500;
        public const ushort BeginAbility = 501;
        public const ushort CancelAbility = 502;
        public const ushort Interaction = 503;
        public const ushort Respawn = 504;
        public const ushort InteractionMenu = 505;
        public const ushort ContextInteraction = 506;
        public const ushort WorldLootOpen = 507;
        public const ushort WorldLootTake = 508;
        public const ushort Reload = 509;
        public const ushort CombatOwnerState = 510;
        public const ushort WorldLootTakeAll = 511;
    }

    public static class PlayerGameplayActionMessageTypes
    {
        // Top-level LiteNetLib messages. Requests occupy a separate ID namespace.
        public const ushort CombatDamage = 45;
        public const ushort AbilityCastState = 46;
        public const ushort WorldInteractableState = 49;
        public const ushort CombatPresentationBatch = 51;
        public const ushort CombatOwnerState = 52;
        public const ushort CombatFireCycleBatch = 53;
        public const ushort CombatFireOwnerCycle = 54;
        public const ushort CombatActionIntent = 63;
    }

    /// <summary>
    /// Compact one-way combat action used by the canonical targetless combat path.
    /// Melee/unarmed actions carry only packed sequence + input control (two bytes for small
    /// sequences). Precision-ranged actions append the existing 16-bit packed yaw/pitch aim.
    /// </summary>
    public struct PlayerCombatActionIntentMessage : INetSerializable
    {
        private const byte InputKindMask = 0x03;
        private const byte HasPrecisionAimFlag = 0x80;

        public uint sequence;
        public byte inputKind;
        public bool hasPrecisionAim;
        public ushort packedAim;

        public BasicAttackInputKind InputKind => (BasicAttackInputKind)(inputKind & InputKindMask);
        public bool HasPrecisionAim => hasPrecisionAim;

        public void Serialize(NetDataWriter writer)
        {
            writer.PutPackedUInt(sequence);
            byte control = (byte)(inputKind & InputKindMask);
            if (hasPrecisionAim)
                control |= HasPrecisionAimFlag;
            writer.Put(control);
            if (hasPrecisionAim)
                writer.Put(packedAim);
        }

        public void Deserialize(NetDataReader reader)
        {
            sequence = reader.GetPackedUInt();
            byte control = reader.GetByte();
            inputKind = (byte)(control & InputKindMask);
            hasPrecisionAim = (control & HasPrecisionAimFlag) != 0;
            packedAim = hasPrecisionAim ? reader.GetUShort() : (ushort)0;
        }
    }

    /// <summary>
    /// 16-bit targetless combat aim: ten circular yaw bits and six clamped pitch bits.
    /// </summary>
    public static class PlayerCombatAimEncoding
    {
        public const int YawBits = 10;
        public const int PitchBits = 6;
        public const int YawSteps = 1 << YawBits;
        public const int PitchLevels = 1 << PitchBits;
        public const int PitchSteps = PitchLevels - 1;

        private const ushort YawMask = YawSteps - 1;

        public static ushort Encode(float yawDegrees, float pitchDegrees)
        {
            float normalizedYaw = Repeat(yawDegrees, 360f) / 360f;
            int yaw = ((int)Math.Round(normalizedYaw * YawSteps, MidpointRounding.AwayFromZero)) & YawMask;

            float minPitch = Player.Shared.PlayerCombatInputEncoding.MinimumAimPitchDegrees;
            float maxPitch = Player.Shared.PlayerCombatInputEncoding.MaximumAimPitchDegrees;
            float clampedPitch = Math.Max(minPitch, Math.Min(maxPitch, pitchDegrees));
            float normalizedPitch = (clampedPitch - minPitch) / (maxPitch - minPitch);
            int pitch = Math.Max(0, Math.Min(PitchSteps,
                (int)Math.Round(normalizedPitch * PitchSteps, MidpointRounding.AwayFromZero)));

            return (ushort)((pitch << YawBits) | yaw);
        }

        public static float DecodeYawDegrees(ushort packed) =>
            (packed & YawMask) * (360f / YawSteps);

        public static float DecodePitchDegrees(ushort packed)
        {
            int pitch = (packed >> YawBits) & PitchSteps;
            float minPitch = Player.Shared.PlayerCombatInputEncoding.MinimumAimPitchDegrees;
            float maxPitch = Player.Shared.PlayerCombatInputEncoding.MaximumAimPitchDegrees;
            return minPitch + (maxPitch - minPitch) * (pitch / (float)PitchSteps);
        }

        private static float Repeat(float value, float length)
        {
            if (length <= 0f)
                return 0f;
            float repeated = value - (float)Math.Floor(value / length) * length;
            return repeated >= length ? 0f : repeated;
        }
    }

    public struct PlayerTargetReferenceWire : INetSerializable
    {
        public uint objectId;
        public ushort generation;

        public bool IsValid => objectId != 0 && generation != 0;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(objectId);
            writer.Put(generation);
        }

        public void Deserialize(NetDataReader reader)
        {
            objectId = reader.GetUInt();
            generation = reader.GetUShort();
        }
    }

    public struct CombatTargetReferenceWire : INetSerializable
    {
        public byte targetKind;
        public uint objectId;
        public ushort generation;
        public long primaryId;

        public InteractionTargetKind Kind => (InteractionTargetKind)targetKind;
        public bool IsPlayer => Kind == InteractionTargetKind.PlayerEntity && objectId != 0 && generation != 0;
        public bool IsPopulation => Kind == InteractionTargetKind.PopulationEntity && primaryId > 0 && generation != 0;
        public bool IsCombatTestTarget => Kind == InteractionTargetKind.CombatTestTarget && primaryId > 0;
        public bool IsValid => IsPlayer || IsPopulation || IsCombatTestTarget;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(targetKind);
            writer.Put(objectId);
            writer.Put(generation);
            writer.Put(primaryId);
        }

        public void Deserialize(NetDataReader reader)
        {
            targetKind = reader.GetByte();
            objectId = reader.GetUInt();
            generation = reader.GetUShort();
            primaryId = reader.GetLong();
        }

        public static CombatTargetReferenceWire Player(uint objectId, ushort generation) =>
            new CombatTargetReferenceWire
            {
                targetKind = (byte)InteractionTargetKind.PlayerEntity,
                objectId = objectId,
                generation = generation,
            };

        public static CombatTargetReferenceWire Population(long actorId, ushort generation) =>
            new CombatTargetReferenceWire
            {
                targetKind = (byte)InteractionTargetKind.PopulationEntity,
                generation = generation,
                primaryId = actorId,
            };

        public static CombatTargetReferenceWire CombatTest(long stableId) =>
            new CombatTargetReferenceWire
            {
                targetKind = (byte)InteractionTargetKind.CombatTestTarget,
                primaryId = stableId,
            };
    }

    public struct PlayerBasicAttackRequestMessage : INetSerializable
    {
        public CombatTargetReferenceWire target;
        public byte inputKind;

        public BasicAttackInputKind InputKind => (BasicAttackInputKind)inputKind;

        public void Serialize(NetDataWriter writer)
        {
            target.Serialize(writer);
            writer.Put(inputKind);
        }

        public void Deserialize(NetDataReader reader)
        {
            target.Deserialize(reader);
            inputKind = reader.GetByte();
        }
    }

    public struct CombatDamageWire : INetSerializable
    {
        // Participant-only result. Health replication is a separate authoritative state
        // delta, so before/after/raw values are intentionally not duplicated here.
        public uint eventSequence;
        public PlayerTargetReferenceWire source;
        public PlayerTargetReferenceWire target;
        public ushort damageTypeId;
        public int amount;
        public byte resultCode;
        public byte cause;
        public ushort flags;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(eventSequence);
            source.Serialize(writer);
            target.Serialize(writer);
            writer.Put(damageTypeId);
            writer.Put(amount);
            writer.Put(resultCode);
            writer.Put(cause);
            writer.Put(flags);
        }

        public void Deserialize(NetDataReader reader)
        {
            eventSequence = reader.GetUInt();
            source.Deserialize(reader);
            target.Deserialize(reader);
            damageTypeId = reader.GetUShort();
            amount = reader.GetInt();
            resultCode = reader.GetByte();
            cause = reader.GetByte();
            flags = reader.GetUShort();
        }

        public CombatDamageResultCode ResultCode => (CombatDamageResultCode)resultCode;
        public CombatDamageCause Cause => (CombatDamageCause)cause;
        public CombatDamagePresentationFlags Flags => (CombatDamagePresentationFlags)flags;
    }

    public struct PlayerBasicAttackResponseMessage : INetSerializable
    {
        // Request acknowledgement only. The detailed result travels once through the
        // participant combat-result event instead of being duplicated in this response.
        public bool success;
        public byte resultCode;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(resultCode);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            resultCode = reader.GetByte();
        }

        public BasicAttackResultCode ResultCode => (BasicAttackResultCode)resultCode;

        public static PlayerBasicAttackResponseMessage Failed(BasicAttackResultCode code, string ignored = null) =>
            new PlayerBasicAttackResponseMessage
            {
                success = false,
                resultCode = (byte)code,
            };
    }

    public struct PlayerBeginAbilityRequestMessage : INetSerializable
    {
        public CombatTargetReferenceWire target;
        public ushort abilityWireId;
        public byte rank;

        public void Serialize(NetDataWriter writer)
        {
            target.Serialize(writer);
            writer.Put(abilityWireId);
            writer.Put(rank);
        }

        public void Deserialize(NetDataReader reader)
        {
            target.Deserialize(reader);
            abilityWireId = reader.GetUShort();
            rank = reader.GetByte();
        }
    }

    public struct PlayerReloadRequestMessage : INetSerializable
    {
        public ushort preferredAmmoDataId;
        public string preferredAmmoDefinitionId;

        public void Serialize(NetDataWriter writer) => writer.Put(preferredAmmoDataId);

        public void Deserialize(NetDataReader reader)
        {
            preferredAmmoDataId = reader.GetUShort();
            preferredAmmoDefinitionId = string.Empty;
            if (preferredAmmoDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetItem(
                    preferredAmmoDataId,
                    out GameplayItemReferenceWire item))
                preferredAmmoDefinitionId = item.definitionId ?? string.Empty;
        }
    }

    /// <summary>Compact authoritative reload result. Static weapon/loadout fields stay in
    /// the cached combat-owner baseline; reload only mutates magazine state.
    /// </summary>
    public struct PlayerReloadResponseMessage : INetSerializable
    {
        public byte resultCode;
        public long revision;
        public ushort loadedAmmoDataId;
        public string loadedAmmoDefinitionId;
        public int loadedRounds;

        public bool success => resultCode == 0;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(resultCode);
            if (success)
            {
                writer.Put(revision);
                writer.Put(loadedAmmoDataId);
                writer.Put(loadedRounds);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            resultCode = reader.GetByte();
            if (success)
            {
                revision = reader.GetLong();
                loadedAmmoDataId = reader.GetUShort();
                loadedRounds = reader.GetInt();
                loadedAmmoDefinitionId = string.Empty;
                if (loadedAmmoDataId != 0 &&
                    PlayerGameplaySettingsRuntime.TryGetItem(
                        loadedAmmoDataId,
                        out GameplayItemReferenceWire ammo))
                    loadedAmmoDefinitionId = ammo.definitionId ?? string.Empty;
            }
            else
            {
                revision = 0;
                loadedAmmoDataId = 0;
                loadedAmmoDefinitionId = string.Empty;
                loadedRounds = 0;
            }
        }

        public static PlayerReloadResponseMessage Failed(byte code) => new PlayerReloadResponseMessage
        {
            resultCode = code,
            loadedAmmoDefinitionId = string.Empty,
        };
    }

    public struct PlayerCombatOwnerStateRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct PlayerCombatOwnerStateMessage : INetSerializable
    {
        public bool success;
        public byte resultCode;
        public long revision;
        public byte mode;
        public ushort weaponDataId;
        public string weaponDefinitionId;
        public ushort loadedAmmoDataId;
        public string loadedAmmoDefinitionId;
        public int loadedRounds;
        public int magazineCapacity;
        public int unarmedLightStaminaCost;
        public int unarmedHeavyStaminaCost;
        public float primaryInterval;
        public float heavyInterval;
        public byte comboStep;
        public byte firearmFireMode;
        public byte firearmRoundsPerTrigger;
        public float firearmRoundsPerSecond;
        public ushort firearmPresentationId;

        // Compatibility semantic used by the compact combat client. This is the same
        // serialized ushort as firearmPresentationId; no wire bytes/order are changed.
        public ushort attackPresentationId
        {
            get => firearmPresentationId;
            set => firearmPresentationId = value;
        }

        public BasicAttackMode Mode => (BasicAttackMode)mode;
        public FirearmFireMode FireMode => (FirearmFireMode)firearmFireMode;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(resultCode);
            writer.Put(revision);
            writer.Put(mode);
            writer.Put(weaponDataId);
            writer.Put(loadedAmmoDataId);
            writer.Put(loadedRounds);
            writer.Put(magazineCapacity);
            writer.Put(unarmedLightStaminaCost);
            writer.Put(unarmedHeavyStaminaCost);
            writer.Put(primaryInterval);
            writer.Put(heavyInterval);
            writer.Put(comboStep);
            writer.Put(firearmFireMode);
            writer.Put(firearmRoundsPerTrigger);
            writer.Put(firearmRoundsPerSecond);
            writer.Put(firearmPresentationId);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            resultCode = reader.GetByte();
            revision = reader.GetLong();
            mode = reader.GetByte();
            weaponDataId = reader.GetUShort();
            loadedAmmoDataId = reader.GetUShort();
            loadedRounds = reader.GetInt();

            weaponDefinitionId = string.Empty;
            if (weaponDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetItem(
                    weaponDataId,
                    out GameplayItemReferenceWire weapon))
                weaponDefinitionId = weapon.definitionId ?? string.Empty;

            loadedAmmoDefinitionId = string.Empty;
            if (loadedAmmoDataId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetItem(
                    loadedAmmoDataId,
                    out GameplayItemReferenceWire ammo))
                loadedAmmoDefinitionId = ammo.definitionId ?? string.Empty;
            magazineCapacity = reader.GetInt();
            unarmedLightStaminaCost = reader.GetInt();
            unarmedHeavyStaminaCost = reader.GetInt();
            primaryInterval = reader.GetFloat();
            heavyInterval = reader.GetFloat();
            comboStep = reader.GetByte();
            firearmFireMode = reader.GetByte();
            firearmRoundsPerTrigger = reader.GetByte();
            firearmRoundsPerSecond = reader.GetFloat();
            firearmPresentationId = reader.GetUShort();
        }

        public static PlayerCombatOwnerStateMessage Failed(byte code = 1) => new PlayerCombatOwnerStateMessage
        {
            success = false,
            resultCode = code,
        };
    }

    [Flags]
    public enum CombatFireCycleFlags : byte
    {
        None = 0,
        Aiming = 1 << 5,
        Burst = 1 << 6,
        Automatic = 1 << 7,
    }

    /// <summary>Compact AOI observer record. With a small packed object id this is 8 bytes.</summary>
    public struct CombatFireCycleWire : INetSerializable
    {
        public uint sourceObjectId;
        public ushort sourceGeneration;
        public ushort semanticId;
        public byte sequence;
        // low five bits = rounds (0..31), high three bits = CombatFireCycleFlags
        public byte roundsAndFlags;
        // Authoritative cycle span in 20 ms quanta. Clients reconstruct muzzle/recoil pulses locally.
        public byte cycleSpan20Ms;

        public int RoundsFired => roundsAndFlags & 0x1F;
        public bool Aiming => (roundsAndFlags & (byte)CombatFireCycleFlags.Aiming) != 0;
        public bool Burst => (roundsAndFlags & (byte)CombatFireCycleFlags.Burst) != 0;
        public bool Automatic => (roundsAndFlags & (byte)CombatFireCycleFlags.Automatic) != 0;
        public float CycleSpanSeconds => Math.Max(0.02f, cycleSpan20Ms * 0.02f);
        public PlayerTargetReferenceWire Source => new PlayerTargetReferenceWire { objectId = sourceObjectId, generation = sourceGeneration };

        public void Serialize(NetDataWriter writer)
        {
            writer.PutPackedUInt(sourceObjectId);
            writer.Put(sourceGeneration);
            writer.Put(semanticId);
            writer.Put(sequence);
            writer.Put(roundsAndFlags);
            writer.Put(cycleSpan20Ms);
        }

        public void Deserialize(NetDataReader reader)
        {
            sourceObjectId = reader.GetPackedUInt();
            sourceGeneration = reader.GetUShort();
            semanticId = reader.GetUShort();
            sequence = reader.GetByte();
            roundsAndFlags = reader.GetByte();
            cycleSpan20Ms = reader.GetByte();
        }

        public static CombatFireCycleWire Create(
            uint sourceObjectId, ushort sourceGeneration, ushort semanticId, byte sequence,
            int rounds, bool aiming, FirearmFireMode fireMode, float cycleSpanSeconds)
        {
            int clampedRounds = Math.Max(0, Math.Min(31, rounds));
            byte packed = (byte)clampedRounds;
            if (aiming) packed |= (byte)CombatFireCycleFlags.Aiming;
            if (fireMode == FirearmFireMode.Burst) packed |= (byte)CombatFireCycleFlags.Burst;
            if (fireMode == FirearmFireMode.FullAutomatic) packed |= (byte)CombatFireCycleFlags.Automatic;
            int spanUnits = Math.Max(1, Math.Min(255, (int)Math.Round(Math.Max(0.02f, cycleSpanSeconds) / 0.02f)));
            return new CombatFireCycleWire
            {
                sourceObjectId = sourceObjectId, sourceGeneration = sourceGeneration, semanticId = semanticId, sequence = sequence,
                roundsAndFlags = packed, cycleSpan20Ms = (byte)spanUnits,
            };
        }
    }

    public struct PlayerCombatFireCycleBatchMessage : INetSerializable
    {
        public const int MaximumCycles = 64;
        public CombatFireCycleWire[] cycles;

        public void Serialize(NetDataWriter writer)
        {
            CombatFireCycleWire[] source = cycles ?? Array.Empty<CombatFireCycleWire>();
            if (source.Length > MaximumCycles) throw new InvalidOperationException("fire-cycle batch exceeds protocol capacity");
            writer.Put((byte)source.Length);
            for (int i = 0; i < source.Length; ++i) source[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            int count = reader.GetByte();
            if (count > MaximumCycles) throw new InvalidOperationException("fire-cycle batch is too large");
            cycles = new CombatFireCycleWire[count];
            for (int i = 0; i < count; ++i)
            {
                CombatFireCycleWire cycle = default;
                cycle.Deserialize(reader);
                cycles[i] = cycle;
            }
        }
    }

    [Flags]
    public enum CombatFireOwnerCycleFlags : byte
    {
        None = 0,
        Empty = 1 << 0,
        Stopped = 1 << 1,
        TriggerAction = 1 << 2,
    }

    /// <summary>Owner-only compact authoritative ammo/result correction for one weapon action/cycle.</summary>
    public struct PlayerCombatFireOwnerCycleMessage : INetSerializable
    {
        public ushort sequence;
        public ushort semanticId;
        public byte roundsFired;
        public byte hitCount;
        public ushort loadedRounds;
        public byte flags;

        public bool Empty => (flags & (byte)CombatFireOwnerCycleFlags.Empty) != 0;
        public bool Stopped => (flags & (byte)CombatFireOwnerCycleFlags.Stopped) != 0;
        public bool TriggerAction => (flags & (byte)CombatFireOwnerCycleFlags.TriggerAction) != 0;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(sequence);
            writer.Put(semanticId);
            writer.Put(roundsFired);
            writer.Put(hitCount);
            writer.Put(loadedRounds);
            writer.Put(flags);
        }

        public void Deserialize(NetDataReader reader)
        {
            sequence = reader.GetUShort();
            semanticId = reader.GetUShort();
            roundsFired = reader.GetByte();
            hitCount = reader.GetByte();
            loadedRounds = reader.GetUShort();
            flags = reader.GetByte();
        }
    }

    public struct PlayerCancelAbilityRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    /// <summary>Compact request acknowledgement for begin/cancel ability intent.
    /// Full authoritative cast state is delivered by AbilityCastState messages.
    /// </summary>
    public struct PlayerAbilityRequestAckMessage : INetSerializable
    {
        public bool success;
        public byte failure;
        public ushort abilityWireId;

        public AbilityCastFailure Failure => (AbilityCastFailure)failure;

        public void Serialize(NetDataWriter writer)
        {
            // Failure=None is a successful ACK. The ability id is already known from the
            // request (or the authoritative cast-state push), so it is not repeated here.
            writer.Put(failure);
        }

        public void Deserialize(NetDataReader reader)
        {
            failure = reader.GetByte();
            success = failure == (byte)AbilityCastFailure.None;
            abilityWireId = 0;
        }

        public static PlayerAbilityRequestAckMessage Failed(AbilityCastFailure failure, ushort abilityWireId = 0) =>
            new PlayerAbilityRequestAckMessage
            {
                success = false,
                failure = (byte)failure,
                abilityWireId = abilityWireId,
            };
    }

    public struct PlayerAbilityCastStateMessage : INetSerializable
    {
        public bool success;
        public byte failure;
        public byte phase;
        public uint castSequence;
        public ushort abilityWireId;
        public PlayerTargetReferenceWire source;
        public PlayerTargetReferenceWire target;
        // Milliseconds remaining at send time, sufficient for client presentation.
        public uint remainingMilliseconds;
        public ushort totalAffected;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(failure);
            writer.Put(phase);
            writer.Put(castSequence);
            writer.Put(abilityWireId);
            source.Serialize(writer);
            target.Serialize(writer);
            writer.Put(remainingMilliseconds);
            writer.Put(totalAffected);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            failure = reader.GetByte();
            phase = reader.GetByte();
            castSequence = reader.GetUInt();
            abilityWireId = reader.GetUShort();
            source.Deserialize(reader);
            target.Deserialize(reader);
            remainingMilliseconds = reader.GetUInt();
            totalAffected = reader.GetUShort();
        }

        public AbilityCastFailure Failure => (AbilityCastFailure)failure;
        public AbilityPresentationPhase Phase => (AbilityPresentationPhase)phase;

        public static PlayerAbilityCastStateMessage Failed(AbilityCastFailure failure, ushort abilityWireId = 0) =>
            new PlayerAbilityCastStateMessage
            {
                success = false,
                failure = (byte)failure,
                phase = (byte)AbilityPresentationPhase.CastCancelled,
                abilityWireId = abilityWireId,
            };
    }

    public enum CombatPresentationCueKind : byte
    {
        Action = 1,
        AbilityStart = 2,
        AbilityRelease = 3,
        AbilityCancel = 4,
        VisibleProjectile = 5,
        Impact = 6,
        StatusApplied = 7,
        StatusRemoved = 8,
        StreamStart = 9,
        StreamStop = 10,
    }

    [Flags]
    public enum CombatPresentationCueFlags : byte
    {
        None = 0,
        HasTarget = 1 << 0,
        Critical = 1 << 1,
        Blocked = 1 << 2,
        Weakness = 1 << 3,
        Resisted = 1 << 4,
        Killed = 1 << 5,
        Immune = 1 << 6,
    }

    public struct CombatPresentationCueWire : INetSerializable
    {
        public byte kind;
        public PlayerTargetReferenceWire source;
        public PlayerTargetReferenceWire target;
        public ushort semanticId;
        public ushort sequence;
        public byte flags;

        public bool HasTarget => (flags & (byte)CombatPresentationCueFlags.HasTarget) != 0;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(kind);
            source.Serialize(writer);
            writer.Put(semanticId);
            writer.Put(sequence);
            writer.Put(flags);
            if (HasTarget)
                target.Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            kind = reader.GetByte();
            source.Deserialize(reader);
            semanticId = reader.GetUShort();
            sequence = reader.GetUShort();
            flags = reader.GetByte();
            target = default;
            if (HasTarget)
                target.Deserialize(reader);
        }
    }

    public struct PlayerCombatPresentationBatchMessage : INetSerializable
    {
        public const int MaxCues = 64;
        public CombatPresentationCueWire[] cues;

        public void Serialize(NetDataWriter writer)
        {
            CombatPresentationCueWire[] source = cues ?? Array.Empty<CombatPresentationCueWire>();
            int count = Math.Min(source.Length, MaxCues);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                source[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            int count = reader.GetByte();
            if (count > MaxCues)
                throw new InvalidOperationException("combat presentation batch is too large");
            cues = new CombatPresentationCueWire[count];
            for (int i = 0; i < count; ++i)
            {
                CombatPresentationCueWire cue = default;
                cue.Deserialize(reader);
                cues[i] = cue;
            }
        }
    }

    public enum PlayerRespawnResultCode : byte
    {
        Success = 0,
        CharacterUnavailable = 1,
        NotDead = 2,
        SpawnUnavailable = 3,
        ResourceResetFailed = 4,
    }

    public struct PlayerRespawnRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct PlayerRespawnResponseMessage : INetSerializable
    {
        public bool success;
        public byte resultCode;
        public string detail;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(resultCode);
            writer.Put(detail ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            resultCode = reader.GetByte();
            detail = reader.GetString(256);
        }

        public PlayerRespawnResultCode ResultCode => (PlayerRespawnResultCode)resultCode;

        public static PlayerRespawnResponseMessage Failed(PlayerRespawnResultCode code, string detail) =>
            new PlayerRespawnResponseMessage
            {
                success = false,
                resultCode = (byte)code,
                detail = detail ?? string.Empty,
            };
    }

    public struct PlayerInteractionRequestMessage : INetSerializable
    {
        public PlayerTargetReferenceWire target;
        public ushort categoryId;
        public ushort actionId;
        public uint sequence;

        public void Serialize(NetDataWriter writer)
        {
            target.Serialize(writer);
            writer.Put(categoryId);
            writer.Put(actionId);
            writer.Put(sequence);
        }

        public void Deserialize(NetDataReader reader)
        {
            target.Deserialize(reader);
            categoryId = reader.GetUShort();
            actionId = reader.GetUShort();
            sequence = reader.GetUInt();
        }

        public InteractionCategoryId CategoryId => (InteractionCategoryId)categoryId;
        public InteractionActionId ActionId => (InteractionActionId)actionId;
    }

    public struct PlayerInteractionResponseMessage : INetSerializable
    {
        public bool success;
        public uint sequence;
        public ushort categoryId;
        public ushort actionId;
        public byte resultCode;
        public long targetCharacterId;
        public string detail;

        public void Serialize(NetDataWriter writer)
        {
            // sequence + actionId are request-known. Keep only the authoritative result,
            // resolved target identity, and optional user-facing detail.
            writer.Put(resultCode);
            writer.Put(targetCharacterId);
            writer.Put(detail ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            resultCode = reader.GetByte();
            success = resultCode == (byte)InteractionResultCode.Success;
            sequence = 0;
            categoryId = 0;
            actionId = 0;
            targetCharacterId = reader.GetLong();
            detail = reader.GetString(384);
        }

        public InteractionCategoryId CategoryId => (InteractionCategoryId)categoryId;
        public InteractionActionId ActionId => (InteractionActionId)actionId;
        public InteractionResultCode ResultCode => (InteractionResultCode)resultCode;

        public static PlayerInteractionResponseMessage Failed(
            uint sequence,
            InteractionCategoryId categoryId,
            InteractionActionId actionId,
            InteractionResultCode code,
            string detail) =>
            new PlayerInteractionResponseMessage
            {
                success = false,
                sequence = sequence,
                categoryId = (ushort)categoryId,
                actionId = (ushort)actionId,
                resultCode = (byte)code,
                detail = detail ?? string.Empty,
            };

    }

    public struct InteractionTargetReferenceWire : INetSerializable
    {
        public byte kind;
        public uint objectId;
        public ushort generation;
        public long primaryId;
        public long secondaryId;

        public InteractionTargetKind Kind => (InteractionTargetKind)kind;
        public bool IsValid => Kind != InteractionTargetKind.None &&
            ((Kind == InteractionTargetKind.PlayerEntity && objectId != 0 && generation != 0) || primaryId > 0);

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(kind);
            writer.Put(objectId);
            writer.Put(generation);
            writer.Put(primaryId);
            writer.Put(secondaryId);
        }

        public void Deserialize(NetDataReader reader)
        {
            kind = reader.GetByte();
            objectId = reader.GetUInt();
            generation = reader.GetUShort();
            primaryId = reader.GetLong();
            secondaryId = reader.GetLong();
        }

        public static InteractionTargetReferenceWire Player(uint objectId, ushort generation) =>
            new InteractionTargetReferenceWire
            {
                kind = (byte)InteractionTargetKind.PlayerEntity,
                objectId = objectId,
                generation = generation,
            };

        public static InteractionTargetReferenceWire WorldItem(long itemInstanceId) =>
            new InteractionTargetReferenceWire
            {
                kind = (byte)InteractionTargetKind.NetworkWorldObject,
                primaryId = itemInstanceId,
            };

        public static InteractionTargetReferenceWire SceneObject(long stableId) =>
            new InteractionTargetReferenceWire
            {
                kind = (byte)InteractionTargetKind.SceneObject,
                primaryId = stableId,
            };

        public static InteractionTargetReferenceWire CombatTest(long stableId) =>
            new InteractionTargetReferenceWire
            {
                kind = (byte)InteractionTargetKind.CombatTestTarget,
                primaryId = stableId,
            };
    }

    public struct InteractionActionEntryWire : INetSerializable
    {
        public ushort categoryId;
        public ushort actionId;
        public string label;
        public byte availability;
        public string disabledReason;
        public byte consentMode;
        public byte contentLevel;
        public byte feature;
        public short sortOrder;

        public InteractionCategoryId CategoryId => (InteractionCategoryId)categoryId;
        public InteractionActionId ActionId => (InteractionActionId)actionId;
        public InteractionAvailability Availability => (InteractionAvailability)availability;
        public InteractionConsentMode ConsentMode => (InteractionConsentMode)consentMode;
        public InteractionContentLevel ContentLevel => (InteractionContentLevel)contentLevel;
        public InteractionFeature Feature => (InteractionFeature)feature;
        public bool IsAvailable => Availability == InteractionAvailability.Available;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(categoryId);
            writer.Put(actionId);
            writer.Put(label ?? string.Empty);
            writer.Put(availability);
            writer.Put(disabledReason ?? string.Empty);
            writer.Put(consentMode);
            writer.Put(contentLevel);
            writer.Put(feature);
            writer.Put(sortOrder);
        }

        public void Deserialize(NetDataReader reader)
        {
            categoryId = reader.GetUShort();
            actionId = reader.GetUShort();
            label = reader.GetString(96);
            availability = reader.GetByte();
            disabledReason = reader.GetString(192);
            consentMode = reader.GetByte();
            contentLevel = reader.GetByte();
            feature = reader.GetByte();
            sortOrder = reader.GetShort();
        }

        public static InteractionActionEntryWire From(InteractionActionEntry entry) =>
            new InteractionActionEntryWire
            {
                categoryId = (ushort)entry.CategoryId,
                actionId = (ushort)entry.ActionId,
                label = entry.Label,
                availability = (byte)entry.Availability,
                disabledReason = entry.DisabledReason,
                consentMode = (byte)entry.ConsentMode,
                contentLevel = (byte)entry.ContentLevel,
                feature = (byte)entry.Feature,
                sortOrder = entry.SortOrder,
            };
    }

    public struct InteractionMenuRequestMessage : INetSerializable
    {
        public InteractionTargetReferenceWire target;
        public void Serialize(NetDataWriter writer) => target.Serialize(writer);
        public void Deserialize(NetDataReader reader) => target.Deserialize(reader);
    }

    public struct InteractionMenuResponseMessage : INetSerializable
    {
        public const int MaxActions = 32;
        public bool success;
        public InteractionTargetReferenceWire target;
        public string targetLabel;
        public string detail;
        public InteractionActionEntryWire[] actions;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            target.Serialize(writer);
            writer.Put(targetLabel ?? string.Empty);
            writer.Put(detail ?? string.Empty);
            InteractionActionEntryWire[] source = actions ?? Array.Empty<InteractionActionEntryWire>();
            if (source.Length > MaxActions) throw new InvalidOperationException("interaction menu exceeds protocol capacity");
            writer.Put((byte)source.Length);
            for (int i = 0; i < source.Length; ++i) source[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            target.Deserialize(reader);
            targetLabel = reader.GetString(128);
            detail = reader.GetString(256);
            int count = reader.GetByte();
            if (count > MaxActions) throw new InvalidOperationException("interaction menu is too large");
            actions = new InteractionActionEntryWire[count];
            for (int i = 0; i < count; ++i)
            {
                InteractionActionEntryWire entry = default;
                entry.Deserialize(reader);
                actions[i] = entry;
            }
        }

        public static InteractionMenuResponseMessage Failed(InteractionTargetReferenceWire target, string detail) =>
            new InteractionMenuResponseMessage
            {
                success = false,
                target = target,
                targetLabel = string.Empty,
                detail = detail ?? string.Empty,
                actions = Array.Empty<InteractionActionEntryWire>(),
            };
    }

    public struct ContextInteractionRequestMessage : INetSerializable
    {
        public InteractionTargetReferenceWire target;
        public ushort categoryId;
        public ushort actionId;
        public uint sequence;

        public InteractionCategoryId CategoryId => (InteractionCategoryId)categoryId;
        public InteractionActionId ActionId => (InteractionActionId)actionId;

        public void Serialize(NetDataWriter writer)
        {
            target.Serialize(writer);
            writer.Put(categoryId);
            writer.Put(actionId);
            writer.Put(sequence);
        }

        public void Deserialize(NetDataReader reader)
        {
            target.Deserialize(reader);
            categoryId = reader.GetUShort();
            actionId = reader.GetUShort();
            sequence = reader.GetUInt();
        }
    }

    public struct ContextInteractionResponseMessage : INetSerializable
    {
        public bool success;
        public uint sequence;
        public ushort categoryId;
        public ushort actionId;
        public byte resultCode;
        public InteractionTargetReferenceWire target;
        public string detail;

        public InteractionCategoryId CategoryId => (InteractionCategoryId)categoryId;
        public InteractionActionId ActionId => (InteractionActionId)actionId;
        public InteractionResultCode ResultCode => (InteractionResultCode)resultCode;

        public void Serialize(NetDataWriter writer)
        {
            // The request already carries sequence/action/target. Return only authority result.
            writer.Put(resultCode);
            writer.Put(detail ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            resultCode = reader.GetByte();
            success = resultCode == (byte)InteractionResultCode.Success;
            sequence = 0;
            categoryId = 0;
            actionId = 0;
            target = default;
            detail = reader.GetString(384);
        }

        public static ContextInteractionResponseMessage Failed(
            uint sequence,
            InteractionTargetReferenceWire target,
            InteractionCategoryId categoryId,
            InteractionActionId actionId,
            InteractionResultCode resultCode,
            string detail) =>
            new ContextInteractionResponseMessage
            {
                success = false,
                sequence = sequence,
                target = target,
                categoryId = (ushort)categoryId,
                actionId = (ushort)actionId,
                resultCode = (byte)resultCode,
                detail = detail ?? string.Empty,
            };

    }

    public struct WorldInteractableStateMessage : INetSerializable
    {
        public string mapId;
        public string instanceId;
        public long stableId;
        public long revision;
        public byte kind;
        public bool enabled;
        public bool open;
        public bool depleted;
        public long dynamicBlockerId;
        public bool blockerEnabled;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(mapId ?? string.Empty);
            writer.Put(instanceId ?? string.Empty);
            writer.Put(stableId);
            writer.Put(revision);
            writer.Put(kind);
            writer.Put(enabled);
            writer.Put(open);
            writer.Put(depleted);
            writer.Put(dynamicBlockerId);
            writer.Put(blockerEnabled);
        }

        public void Deserialize(NetDataReader reader)
        {
            mapId = reader.GetString(128);
            instanceId = reader.GetString(128);
            stableId = reader.GetLong();
            revision = reader.GetLong();
            kind = reader.GetByte();
            enabled = reader.GetBool();
            open = reader.GetBool();
            depleted = reader.GetBool();
            dynamicBlockerId = reader.GetLong();
            blockerEnabled = reader.GetBool();
        }
    }

    public struct PlayerCombatDamageEventMessage : INetSerializable
    {
        public CombatDamageWire damage;
        public void Serialize(NetDataWriter writer) => damage.Serialize(writer);
        public void Deserialize(NetDataReader reader) => damage.Deserialize(reader);
    }
}
