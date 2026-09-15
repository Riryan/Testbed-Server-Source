using System;
using Game.Shared.Abilities;
using Game.Shared.World;

namespace Game.Server.Domain.Actions
{
    public readonly struct ActiveAbilityCastState
    {
        public long CastId { get; }
        public string AbilityDefinitionId { get; }
        public int Rank { get; }
        public long TargetCharacterId { get; }
        public WorldPosition RequestedPoint { get; }
        public double StartedAt { get; }
        public double CompletesAt { get; }

        public bool IsActive => CastId > 0 && !string.IsNullOrWhiteSpace(AbilityDefinitionId);

        public ActiveAbilityCastState(
            long castId,
            string abilityDefinitionId,
            int rank,
            long targetCharacterId,
            WorldPosition requestedPoint,
            double startedAt,
            double completesAt)
        {
            CastId = castId;
            AbilityDefinitionId = abilityDefinitionId ?? string.Empty;
            Rank = Math.Max(1, rank);
            TargetCharacterId = targetCharacterId;
            RequestedPoint = requestedPoint;
            StartedAt = startedAt;
            CompletesAt = completesAt;
        }
    }

    public readonly struct AbilityCooldownState
    {
        public string AbilityDefinitionId { get; }
        public double CooldownEnd { get; }

        public AbilityCooldownState(string abilityDefinitionId, double cooldownEnd)
        {
            AbilityDefinitionId = abilityDefinitionId ?? string.Empty;
            CooldownEnd = cooldownEnd;
        }
    }

    /// <summary>
    /// Immutable action/cooldown state shared by basic attack and active abilities.
    /// New state is committed atomically through PlayerRuntime; no tick scans are required.
    /// </summary>
    public sealed class CharacterActionState
    {
        private readonly AbilityCooldownState[] _cooldowns;

        public static CharacterActionState Empty { get; } =
            new CharacterActionState(0, 0d, default(ActiveAbilityCastState), Array.Empty<AbilityCooldownState>());

        public long Revision { get; }
        public double BasicAttackRecoveryEnd { get; }
        public ActiveAbilityCastState ActiveCast { get; }

        public CharacterActionState(
            long revision,
            double basicAttackRecoveryEnd,
            ActiveAbilityCastState activeCast,
            AbilityCooldownState[] cooldowns)
        {
            Revision = Math.Max(0, revision);
            BasicAttackRecoveryEnd = Math.Max(0d, basicAttackRecoveryEnd);
            ActiveCast = activeCast;
            _cooldowns = cooldowns == null || cooldowns.Length == 0
                ? Array.Empty<AbilityCooldownState>()
                : (AbilityCooldownState[])cooldowns.Clone();
        }

        public double GetCooldownEnd(string abilityDefinitionId)
        {
            if (string.IsNullOrWhiteSpace(abilityDefinitionId)) return 0d;
            for (int i = 0; i < _cooldowns.Length; ++i)
                if (string.Equals(_cooldowns[i].AbilityDefinitionId, abilityDefinitionId, StringComparison.Ordinal))
                    return _cooldowns[i].CooldownEnd;
            return 0d;
        }

        public AbilityCooldownState[] SnapshotCooldowns() =>
            _cooldowns.Length == 0 ? Array.Empty<AbilityCooldownState>() : (AbilityCooldownState[])_cooldowns.Clone();

        public CharacterActionState WithBasicAttackRecovery(double recoveryEnd)
        {
            return new CharacterActionState(
                checked(Revision + 1),
                Math.Max(0d, recoveryEnd),
                ActiveCast,
                _cooldowns);
        }

        public CharacterActionState WithActiveCast(ActiveAbilityCastState activeCast)
        {
            return new CharacterActionState(
                checked(Revision + 1),
                BasicAttackRecoveryEnd,
                activeCast,
                _cooldowns);
        }

        public CharacterActionState ClearActiveCast()
        {
            if (!ActiveCast.IsActive) return this;
            return new CharacterActionState(
                checked(Revision + 1),
                BasicAttackRecoveryEnd,
                default(ActiveAbilityCastState),
                _cooldowns);
        }

        public CharacterActionState ResolveAbility(
            string abilityDefinitionId,
            double cooldownEnd,
            bool useBasicAttackRecovery)
        {
            if (string.IsNullOrWhiteSpace(abilityDefinitionId))
                throw new ArgumentException("Ability definition id is required.", nameof(abilityDefinitionId));

            AbilityCooldownState[] next = _cooldowns;
            double basicRecovery = BasicAttackRecoveryEnd;
            if (useBasicAttackRecovery)
            {
                basicRecovery = Math.Max(0d, cooldownEnd);
            }
            else if (cooldownEnd > 0d)
            {
                int index = -1;
                for (int i = 0; i < _cooldowns.Length; ++i)
                {
                    if (string.Equals(_cooldowns[i].AbilityDefinitionId, abilityDefinitionId, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0)
                {
                    next = (AbilityCooldownState[])_cooldowns.Clone();
                    next[index] = new AbilityCooldownState(abilityDefinitionId, cooldownEnd);
                }
                else
                {
                    next = new AbilityCooldownState[_cooldowns.Length + 1];
                    Array.Copy(_cooldowns, next, _cooldowns.Length);
                    next[next.Length - 1] = new AbilityCooldownState(abilityDefinitionId, cooldownEnd);
                }
            }

            return new CharacterActionState(
                checked(Revision + 1),
                basicRecovery,
                default(ActiveAbilityCastState),
                next);
        }

        public CharacterActionState WithAbilityCooldown(string abilityDefinitionId, double cooldownEnd)
        {
            if (string.IsNullOrWhiteSpace(abilityDefinitionId))
                throw new ArgumentException("Ability definition id is required.", nameof(abilityDefinitionId));

            int index = -1;
            for (int i = 0; i < _cooldowns.Length; ++i)
            {
                if (string.Equals(_cooldowns[i].AbilityDefinitionId, abilityDefinitionId, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            AbilityCooldownState[] next;
            if (index >= 0)
            {
                next = (AbilityCooldownState[])_cooldowns.Clone();
                next[index] = new AbilityCooldownState(abilityDefinitionId, Math.Max(0d, cooldownEnd));
            }
            else
            {
                next = new AbilityCooldownState[_cooldowns.Length + 1];
                Array.Copy(_cooldowns, next, _cooldowns.Length);
                next[next.Length - 1] = new AbilityCooldownState(abilityDefinitionId, Math.Max(0d, cooldownEnd));
            }

            return new CharacterActionState(
                checked(Revision + 1),
                BasicAttackRecoveryEnd,
                ActiveCast,
                next);
        }
    }

    public readonly struct CharacterActionChange
    {
        public long CharacterId { get; }
        public long Revision { get; }
        public CharacterActionChangeReason Reason { get; }
        public string AbilityDefinitionId { get; }
        public long CastId { get; }
        public double Deadline { get; }

        public CharacterActionChange(
            long characterId,
            long revision,
            CharacterActionChangeReason reason,
            string abilityDefinitionId,
            long castId,
            double deadline)
        {
            CharacterId = characterId;
            Revision = revision;
            Reason = reason;
            AbilityDefinitionId = abilityDefinitionId ?? string.Empty;
            CastId = castId;
            Deadline = deadline;
        }
    }
}
