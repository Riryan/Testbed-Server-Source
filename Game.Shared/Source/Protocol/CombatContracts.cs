using System;
using Game.Shared.Combat;

namespace Game.Shared.Protocol
{
    [Serializable]
    public readonly struct CombatDamageResult
    {
        public long eventId { get; }
        public CombatDamageResultCode resultCode { get; }
        public CombatDamageCause cause { get; }
        public long sourceCharacterId { get; }
        public long targetCharacterId { get; }
        public int rawAmount { get; }
        public int dealtAmount { get; }
        public int healthBefore { get; }
        public int healthAfter { get; }
        public CombatDamageType damageType { get; }
        public ushort damageTypeId { get; }
        public CombatDamagePresentationFlags presentationFlags { get; }
        public bool killed { get; }
        public bool stunned { get; }
        public bool weakness => (presentationFlags & CombatDamagePresentationFlags.Weakness) != 0;
        public bool resisted => (presentationFlags & CombatDamagePresentationFlags.Resisted) != 0;
        public bool immune => (presentationFlags & CombatDamagePresentationFlags.Immune) != 0;

        public CombatDamageResult(
            long eventId,
            CombatDamageResultCode resultCode,
            CombatDamageCause cause,
            long sourceCharacterId,
            long targetCharacterId,
            int rawAmount,
            int dealtAmount,
            int healthBefore,
            int healthAfter,
            CombatDamageType damageType,
            bool killed,
            bool stunned,
            ushort damageTypeId = 0,
            CombatDamagePresentationFlags presentationFlags = CombatDamagePresentationFlags.None)
        {
            this.eventId = eventId;
            this.resultCode = resultCode;
            this.cause = cause;
            this.sourceCharacterId = sourceCharacterId;
            this.targetCharacterId = targetCharacterId;
            this.rawAmount = rawAmount;
            this.dealtAmount = dealtAmount;
            this.healthBefore = healthBefore;
            this.healthAfter = healthAfter;
            this.damageType = damageType;
            this.damageTypeId = damageTypeId;
            CombatDamagePresentationFlags flags = presentationFlags;
            if (killed) flags |= CombatDamagePresentationFlags.Killed;
            if (damageType == CombatDamageType.Critical) flags |= CombatDamagePresentationFlags.Critical;
            if (damageType == CombatDamageType.Block) flags |= CombatDamagePresentationFlags.Blocked;
            if (stunned) flags |= CombatDamagePresentationFlags.Stunned;
            this.presentationFlags = flags;
            this.killed = killed;
            this.stunned = stunned;
        }
    }

    [Serializable]
    public readonly struct CombatHealingResult
    {
        public long eventId { get; }
        public long sourceCharacterId { get; }
        public long targetCharacterId { get; }
        public int requestedAmount { get; }
        public int appliedAmount { get; }
        public int healthBefore { get; }
        public int healthAfter { get; }

        public CombatHealingResult(
            long eventId,
            long sourceCharacterId,
            long targetCharacterId,
            int requestedAmount,
            int appliedAmount,
            int healthBefore,
            int healthAfter)
        {
            this.eventId = eventId;
            this.sourceCharacterId = sourceCharacterId;
            this.targetCharacterId = targetCharacterId;
            this.requestedAmount = requestedAmount;
            this.appliedAmount = appliedAmount;
            this.healthBefore = healthBefore;
            this.healthAfter = healthAfter;
        }
    }
}
