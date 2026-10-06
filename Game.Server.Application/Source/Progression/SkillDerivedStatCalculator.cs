using System;
using System.Collections.Generic;
using Game.Server.Application.Content;
using Game.Server.Domain.Stats;
using Game.Shared.Content;
using Game.Shared.Progression;

namespace Game.Server.Application.Progression
{
    /// <summary>
    /// Converts the existing canonical progression tracks into hidden character attributes,
    /// then derives the existing resource maximum stats from those attributes.
    ///
    /// The client never sends attribute totals and no new wire contract is introduced.
    /// </summary>
    public static class SkillDerivedStatCalculator
    {
        public const string StrengthStatId = "Attribute.Strength";
        public const string DexterityStatId = "Attribute.Dexterity";
        public const string IntelligenceStatId = "Attribute.Intelligence";
        public const string CharismaStatId = "Attribute.Charisma";

        public const string HealthMaximumStatId = "Health.Max";
        public const string ManaMaximumStatId = "Mana.Max";
        public const string StaminaMaximumStatId = "Stamina.Max";

        public const float BaseAttribute = 20f;
        public const float MaximumAttribute = 100f;
        public const float BaseResourceMaximum = 50f;
        public const float MaximumResourceMaximum = 150f;

        private readonly struct SkillAttributeRule
        {
            public string DefinitionId { get; }
            public float StrengthAtMaximum { get; }
            public float DexterityAtMaximum { get; }
            public float IntelligenceAtMaximum { get; }
            public float CharismaAtMaximum { get; }

            public SkillAttributeRule(
                string definitionId,
                float strengthAtMaximum = 0f,
                float dexterityAtMaximum = 0f,
                float intelligenceAtMaximum = 0f,
                float charismaAtMaximum = 0f)
            {
                DefinitionId = definitionId ?? string.Empty;
                StrengthAtMaximum = strengthAtMaximum;
                DexterityAtMaximum = dexterityAtMaximum;
                IntelligenceAtMaximum = intelligenceAtMaximum;
                CharismaAtMaximum = charismaAtMaximum;
            }
        }

        // Initial tuning values follow the agreed major / secondary / minor weighting.
        // They intentionally remain modest because every hidden attribute starts at 20 and
        // hard-caps at 100 while visible HP / Mana / Stamina hard-cap at 150.
        private static readonly Dictionary<string, SkillAttributeRule> Rules =
            new Dictionary<string, SkillAttributeRule>(StringComparer.Ordinal)
            {
                ["mastery.small_arms"] = new SkillAttributeRule(
                    "mastery.small_arms",
                    dexterityAtMaximum: 12f,
                    intelligenceAtMaximum: 3f),

                ["mastery.rifles"] = new SkillAttributeRule(
                    "mastery.rifles",
                    dexterityAtMaximum: 6f,
                    intelligenceAtMaximum: 6f),

                ["mastery.shotguns"] = new SkillAttributeRule(
                    "mastery.shotguns",
                    strengthAtMaximum: 6f,
                    dexterityAtMaximum: 6f),

                ["mastery.thrown_weapons"] = new SkillAttributeRule(
                    "mastery.thrown_weapons",
                    strengthAtMaximum: 5f,
                    dexterityAtMaximum: 12f),

                ["mastery.melee_weapons"] = new SkillAttributeRule(
                    "mastery.melee_weapons",
                    strengthAtMaximum: 12f,
                    dexterityAtMaximum: 5f),

                ["mastery.unarmed"] = new SkillAttributeRule(
                    "mastery.unarmed",
                    strengthAtMaximum: 12f,
                    dexterityAtMaximum: 3f),

                ["general.tech"] = new SkillAttributeRule(
                    "general.tech",
                    dexterityAtMaximum: 3f,
                    intelligenceAtMaximum: 12f),

                ["general.first_aid"] = new SkillAttributeRule(
                    "general.first_aid",
                    intelligenceAtMaximum: 12f,
                    charismaAtMaximum: 3f),

                ["general.lockpicking"] = new SkillAttributeRule(
                    "general.lockpicking",
                    dexterityAtMaximum: 12f,
                    intelligenceAtMaximum: 5f),

                ["general.stealth"] = new SkillAttributeRule(
                    "general.stealth",
                    dexterityAtMaximum: 12f,
                    intelligenceAtMaximum: 3f),

                ["profession.salvaging"] = new SkillAttributeRule(
                    "profession.salvaging",
                    strengthAtMaximum: 6f,
                    intelligenceAtMaximum: 6f),

                ["mastery.recovery_fieldcraft"] = new SkillAttributeRule(
                    "mastery.recovery_fieldcraft",
                    strengthAtMaximum: 6f,
                    dexterityAtMaximum: 6f,
                    intelligenceAtMaximum: 3f),

                ["hunter.investigation"] = new SkillAttributeRule(
                    "hunter.investigation",
                    intelligenceAtMaximum: 12f,
                    charismaAtMaximum: 6f),

                ["vampire.feeding"] = new SkillAttributeRule(
                    "vampire.feeding",
                    dexterityAtMaximum: 6f,
                    charismaAtMaximum: 12f),
            };

        public static StatsState Apply(
            GameplayContentCatalog content,
            CharacterProgressionState progression,
            StatsState equipmentStats)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            equipmentStats ??= StatsState.DefaultCharacter();

            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            KeyValuePair<string, float>[] source = equipmentStats.Snapshot();
            for (int i = 0; i < source.Length; ++i)
                values[source[i].Key] = source[i].Value;

            float strength = equipmentStats.Get(StrengthStatId, BaseAttribute);
            float dexterity = equipmentStats.Get(DexterityStatId, BaseAttribute);
            float intelligence = equipmentStats.Get(IntelligenceStatId, BaseAttribute);
            float charisma = equipmentStats.Get(CharismaStatId, BaseAttribute);

            ProgressTrackState[] tracks = progression?.tracks ?? Array.Empty<ProgressTrackState>();
            for (int i = 0; i < tracks.Length; ++i)
            {
                ProgressTrackState track = tracks[i];
                if (track == null ||
                    !content.TryGetProgressTrack(track.dataId, out ProgressTrackDefinition definition) ||
                    definition == null ||
                    string.IsNullOrWhiteSpace(definition.definitionId) ||
                    !Rules.TryGetValue(definition.definitionId, out SkillAttributeRule rule))
                {
                    continue;
                }

                int maximum = Math.Max(1, definition.maximumValue);
                float normalized = Clamp01(track.value / (float)maximum);

                strength += rule.StrengthAtMaximum * normalized;
                dexterity += rule.DexterityAtMaximum * normalized;
                intelligence += rule.IntelligenceAtMaximum * normalized;
                charisma += rule.CharismaAtMaximum * normalized;
            }

            strength = Clamp(strength, BaseAttribute, MaximumAttribute);
            dexterity = Clamp(dexterity, BaseAttribute, MaximumAttribute);
            intelligence = Clamp(intelligence, BaseAttribute, MaximumAttribute);
            charisma = Clamp(charisma, BaseAttribute, MaximumAttribute);

            values[StrengthStatId] = strength;
            values[DexterityStatId] = dexterity;
            values[IntelligenceStatId] = intelligence;
            values[CharismaStatId] = charisma;

            // Preserve any existing equipment contribution to the resource stat, then add
            // the hidden-attribute contribution. Resource maximums remain hard-capped at 150.
            float healthBase = equipmentStats.Get(HealthMaximumStatId, BaseResourceMaximum);
            float manaBase = equipmentStats.Get(ManaMaximumStatId, BaseResourceMaximum);
            float staminaBase = equipmentStats.Get(StaminaMaximumStatId, BaseResourceMaximum);

            values[HealthMaximumStatId] = Clamp(
                healthBase + ((strength - BaseAttribute) * 1.25f),
                BaseResourceMaximum,
                MaximumResourceMaximum);

            values[ManaMaximumStatId] = Clamp(
                manaBase + ((intelligence - BaseAttribute) * 1.25f),
                BaseResourceMaximum,
                MaximumResourceMaximum);

            values[StaminaMaximumStatId] = Clamp(
                staminaBase +
                ((strength - BaseAttribute) * 0.375f) +
                ((dexterity - BaseAttribute) * 0.875f),
                BaseResourceMaximum,
                MaximumResourceMaximum);

            return new StatsState(values);
        }

        private static float Clamp01(float value) =>
            value < 0f ? 0f : value > 1f ? 1f : value;

        private static float Clamp(float value, float minimum, float maximum) =>
            value < minimum ? minimum : value > maximum ? maximum : value;
    }
}
