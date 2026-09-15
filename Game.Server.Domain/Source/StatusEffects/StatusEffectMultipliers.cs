namespace Game.Server.Domain.StatusEffects
{
    public readonly struct StatusEffectMultipliers
    {
        public float DamageDealt { get; }
        public float DamageTaken { get; }
        public float HealingReceived { get; }

        public StatusEffectMultipliers(float damageDealt, float damageTaken, float healingReceived)
        {
            DamageDealt = damageDealt;
            DamageTaken = damageTaken;
            HealingReceived = healingReceived;
        }

        public static StatusEffectMultipliers Identity => new StatusEffectMultipliers(1f, 1f, 1f);
    }
}
