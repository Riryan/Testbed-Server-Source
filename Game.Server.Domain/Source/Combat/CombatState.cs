using System;
using Game.Shared.Identity;

namespace Game.Server.Domain.Combat
{
    public readonly struct CombatState
    {
        public long Revision { get; }
        public double LastCombatTime { get; }
        public bool Invincible { get; }

        public CombatState(long revision, double lastCombatTime, bool invincible)
        {
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (double.IsNaN(lastCombatTime) || double.IsInfinity(lastCombatTime) || lastCombatTime < 0d)
                throw new ArgumentOutOfRangeException(nameof(lastCombatTime));
            Revision = revision;
            LastCombatTime = lastCombatTime;
            Invincible = invincible;
        }
    }

    public readonly struct CombatActivityChange
    {
        public CharacterId CharacterId { get; }
        public long Revision { get; }
        public double LastCombatTime { get; }

        public CombatActivityChange(CharacterId characterId, long revision, double lastCombatTime)
        {
            CharacterId = characterId;
            Revision = revision;
            LastCombatTime = lastCombatTime;
        }
    }
}
