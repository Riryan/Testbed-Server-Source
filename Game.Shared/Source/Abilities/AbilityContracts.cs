using Game.Shared.Combat;
using Game.Shared.Protocol;

namespace Game.Shared.Abilities
{
    public enum BasicAttackResultCode : byte
    {
        Applied = 0,
        RejectedInvalidSource = 1,
        RejectedInvalidTarget = 2,
        RejectedDead = 3,
        RejectedAlreadyCasting = 4,
        RejectedRecovery = 5,
        RejectedOutOfRange = 6,
        RejectedNoDamage = 7,
        RejectedDifferentWorld = 8,
        RejectedControlled = 9,
        RejectedInsufficientResource = 10,
        RejectedNoAmmo = 11,
        RejectedInvalidInput = 12,
        RejectedRateLimited = 13,
    }

    public readonly struct BasicAttackResult
    {
        public BasicAttackResultCode Code { get; }
        public long SourceCharacterId { get; }
        public long TargetCharacterId { get; }
        public long ActionRevision { get; }
        public double RecoveryEnd { get; }
        public CombatDamageResult Damage { get; }
        public BasicAttackInputKind InputKind { get; }
        public BasicAttackMode Mode { get; }
        public byte ComboStep { get; }
        public bool Success => Code == BasicAttackResultCode.Applied;

        public BasicAttackResult(
            BasicAttackResultCode code,
            long sourceCharacterId,
            long targetCharacterId,
            long actionRevision,
            double recoveryEnd,
            CombatDamageResult damage,
            BasicAttackInputKind inputKind = BasicAttackInputKind.Primary,
            BasicAttackMode mode = BasicAttackMode.Unarmed,
            byte comboStep = 0)
        {
            Code = code;
            SourceCharacterId = sourceCharacterId;
            TargetCharacterId = targetCharacterId;
            ActionRevision = actionRevision;
            RecoveryEnd = recoveryEnd;
            Damage = damage;
            InputKind = inputKind;
            Mode = mode;
            ComboStep = comboStep;
        }
    }

    public readonly struct AbilityCastResult
    {
        public AbilityCastFailure Failure { get; }
        public AbilityPresentationPhase Phase { get; }
        public long CastId { get; }
        public string AbilityDefinitionId { get; }
        public long SourceCharacterId { get; }
        public long TargetCharacterId { get; }
        public long ActionRevision { get; }
        public double CompletesAt { get; }
        public int TotalAffected { get; }
        public bool Success => Failure == AbilityCastFailure.None;

        public AbilityCastResult(
            AbilityCastFailure failure,
            AbilityPresentationPhase phase,
            long castId,
            string abilityDefinitionId,
            long sourceCharacterId,
            long targetCharacterId,
            long actionRevision,
            double completesAt,
            int totalAffected)
        {
            Failure = failure;
            Phase = phase;
            CastId = castId;
            AbilityDefinitionId = abilityDefinitionId ?? string.Empty;
            SourceCharacterId = sourceCharacterId;
            TargetCharacterId = targetCharacterId;
            ActionRevision = actionRevision;
            CompletesAt = completesAt;
            TotalAffected = totalAffected;
        }
    }
}
