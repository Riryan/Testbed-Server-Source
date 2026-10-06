using System;
using Game.Server.Application.Abilities;
using Game.Server.Application.Content;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Identity;
using Game.Shared.Protocol;

namespace Game.Server.Application.Progression
{
    /// <summary>
    /// Piggybacks combat skill progression onto BasicAttackService's existing authoritative
    /// Resolved event. No new request, response, polling loop or combat subsystem is introduced.
    /// </summary>
    public sealed class CombatSkillProgressionService : IDisposable
    {
        private readonly GameplayContentCatalog _content;
        private readonly ProgressionService _progression;
        private readonly BasicAttackService _basicAttacks;
        private readonly PlayerSessionRegistry _sessions;
        private bool _disposed;

        public CombatSkillProgressionService(
            GameplayContentCatalog content,
            ProgressionService progression,
            BasicAttackService basicAttacks,
            PlayerSessionRegistry sessions)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _basicAttacks = basicAttacks ?? throw new ArgumentNullException(nameof(basicAttacks));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

            _basicAttacks.Resolved += HandleResolved;
        }

        private void HandleResolved(BasicAttackResult result)
        {
            if (_disposed || !result.Success || result.SourceCharacterId <= 0)
                return;

            CombatDamageResultCode damageCode = result.Damage.resultCode;
            if (damageCode != CombatDamageResultCode.Applied &&
                damageCode != CombatDamageResultCode.Killed)
                return;

            var characterId = new CharacterId(result.SourceCharacterId);
            if (!_sessions.TryGet(characterId, out PlayerSession session))
                return;

            PlayerRuntime source = session?.Runtime;
            if (source == null || source.CharacterId != characterId)
                return;

            ushort trackDataId = ResolveTrackDataId(source, result.Mode);
            if (trackDataId == 0)
                return;

            SkillUseGainPolicy.TryGain(
                _content,
                _progression,
                source,
                trackDataId);
        }

        private ushort ResolveTrackDataId(PlayerRuntime source, BasicAttackMode mode)
        {
            string trackDefinitionId;

            switch (mode)
            {
                case BasicAttackMode.Unarmed:
                    trackDefinitionId = "mastery.unarmed";
                    break;

                case BasicAttackMode.MeleeWeapon:
                    trackDefinitionId = "mastery.melee_weapons";
                    break;

                case BasicAttackMode.Firearm:
                    if (!TryResolveFirearmTrackDefinitionId(source, out trackDefinitionId))
                        return 0;
                    break;

                default:
                    return 0;
            }

            return _content.TryGetProgressTrack(trackDefinitionId, out ProgressTrackDefinition track)
                ? track.dataId
                : (ushort)0;
        }

        private bool TryResolveFirearmTrackDefinitionId(
            PlayerRuntime source,
            out string trackDefinitionId)
        {
            trackDefinitionId = string.Empty;

            var itemSystems = source?.CapturePlayerItemSystems();
            var mainHand = itemSystems?.Equipment?.Get("MainHand");
            if (mainHand == null ||
                string.IsNullOrWhiteSpace(mainHand.DefinitionId) ||
                !_content.TryGetItem(mainHand.DefinitionId, out ItemDefinition weapon))
                return false;

            string[] tags = weapon.tags ?? Array.Empty<string>();
            int matches = 0;

            for (int i = 0; i < tags.Length; ++i)
            {
                string candidate = null;
                string tag = tags[i] ?? string.Empty;

                if (string.Equals(tag, "skill.small_arms", StringComparison.OrdinalIgnoreCase))
                    candidate = "mastery.small_arms";
                else if (string.Equals(tag, "skill.rifles", StringComparison.OrdinalIgnoreCase))
                    candidate = "mastery.rifles";
                else if (string.Equals(tag, "skill.shotguns", StringComparison.OrdinalIgnoreCase))
                    candidate = "mastery.shotguns";

                if (candidate == null)
                    continue;

                matches++;
                trackDefinitionId = candidate;
            }

            return matches == 1;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _basicAttacks.Resolved -= HandleResolved;
        }
    }
}
