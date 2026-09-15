using System.Globalization;

namespace Game.Shared.Combat
{
    /// <summary>
    /// Internal authoritative derived-stat keys. They are calculated when equipment/status
    /// membership changes so combat resolution does not scan item/status tags per hit.
    /// </summary>
    public static class CombatDerivedStatIds
    {
        public static string DamageResponseMultiplier(ushort damageTypeId) =>
            "Combat.ResponseMultiplier." + damageTypeId.ToString(CultureInfo.InvariantCulture);

        public static string DamageResponseFlat(ushort damageTypeId) =>
            "Combat.ResponseFlat." + damageTypeId.ToString(CultureInfo.InvariantCulture);
    }
}
