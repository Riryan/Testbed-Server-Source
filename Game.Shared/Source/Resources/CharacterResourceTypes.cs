using System;

namespace Game.Shared.Resources
{
    // Stable wire/save IDs. Existing values must never be renumbered after shipping.
    // Health/Mana occupy 1-2 in the new portable server model; the remaining ranges
    // preserve the IDs already established by the uMMORPG reference implementation.
    public enum CharacterResourceId : ushort
    {
        None = 0,
        Health = 1,
        Mana = 2,

        // General/core resources: 10-99
        Stamina = 10,
        Shield = 11,
        Energy = 12,
        Focus = 13,
        Resolve = 14,
        Spirit = 15,
        ActionPoints = 16,
        Guard = 17,
        Poise = 18,
        Momentum = 19,
        Adrenaline = 20,
        Breath = 21,
        Concentration = 22,
        Willpower = 23,
        Composure = 24,
        Fatigue = 25,

        // Vampire resources: 100-199
        BloodStorage = 100,
        BloodLust = 101,
        VampireHunger = 102,
        VampireThirst = 103,
        Beast = 104,
        Frenzy = 105,
        Humanity = 106,
        Vitae = 107,
        BloodPotency = 108,
        Darkness = 109,
        Torpor = 110,
        MasqueradeExposure = 111,
        SunExposure = 112,
        FireTerror = 113,

        // Undead resources: 200-299
        NecroticEnergy = 200,
        Decay = 201,
        CorpseIntegrity = 202,
        SoulEnergy = 203,
        Rot = 204,
        GraveHunger = 205,
        DeathlyCold = 206,
        Possession = 207,
        UndeadSanity = 208,
        ReanimationCharge = 209,

        // Hunter resources: 300-399
        HunterFocus = 300,
        Faith = 301,
        Conviction = 302,
        Zeal = 303,
        Grit = 304,
        HunterResolve = 305,
        Discipline = 306,
        Taint = 307,
        SilverCharge = 308,
        HolyFervor = 309,

        // Psychological/social/world resources: 400-499
        Sanity = 400,
        Stress = 401,
        Fear = 402,
        Courage = 403,
        Morale = 404,
        Corruption = 405,
        Purity = 406,
        Suspicion = 407,
        Heat = 408,
        Exposure = 409,
        Reputation = 410,
        Infamy = 411,
        Loyalty = 412,
        Trust = 413,
        Despair = 414,
        Hope = 415,

        // Combat meters: 500-599
        Rage = 500,
        Fury = 501,
        Combo = 502,
        Overdrive = 503,
        BattleTrance = 504,
        ExecutionCharge = 505,
        Retaliation = 506,

        // Survival/condition meters: 600-699
        Hunger = 600,
        Thirst = 601,
        Warmth = 602,
        BodyTemperature = 603,
        Toxicity = 604,
        Infection = 605,
        Exhaustion = 606,
        Oxygen = 607,
        Wetness = 608,

        // Magic/supernatural meters: 700-799
        ArcaneCharge = 700,
        SoulShards = 701,
        DivineFavor = 702,
        ShadowEssence = 703,
        RitualPower = 704,
        Curse = 705,
        Blessing = 706,
        CorruptionCharge = 707,
        PsychicStrain = 708,

        // Optional adult-only meters: 800-899. IDs are reserved only; no resource in
        // this range is enabled by the default content shipped with this patch.
        Lust = 800,
        Arousal = 801,
        Desire = 802,
        Pleasure = 803,
        Intimacy = 804,
        Satisfaction = 805,
        Dominance = 806,
        Submission = 807,
        Stimulation = 808,
        Climax = 809,
        Temptation = 810,
        Restraint = 811,
    }

    [Flags]
    public enum CharacterResourceTags : uint
    {
        None = 0,
        Core = 1 << 0,
        Combat = 1 << 1,
        Vampire = 1 << 2,
        Undead = 1 << 3,
        Hunter = 1 << 4,
        Psychological = 1 << 5,
        Social = 1 << 6,
        Survival = 1 << 7,
        Magic = 1 << 8,
        AdultOnly = 1 << 9,
        Hidden = 1 << 10,
    }

    public enum CharacterResourceUpdateMode : byte
    {
        None = 0,
        Regenerate = 1,
        Decay = 2,
    }

    public enum CharacterResourceUpdateCondition : byte
    {
        Always = 0,
        InCombatOnly = 1,
        OutOfCombatOnly = 2,
    }

    public enum CharacterResourcePersistenceMode : byte
    {
        None = 0,
        Session = 1,
        Character = 2,
    }

    public enum CharacterResourceReplicationMode : byte
    {
        ServerOnly = 0,
        OwnerOnly = 1,
        Observers = 2,
    }

    public enum CharacterResourceResetMode : byte
    {
        KeepCurrent = 0,
        SetToMinimum = 1,
        SetToStartingValue = 2,
        SetToMaximum = 3,
    }

    public enum CharacterResourceChangeReason : byte
    {
        Unknown = 0,
        Initialization = 1,
        AbilityCost = 2,
        Feeding = 3,
        DamageTaken = 4,
        DamageDealt = 5,
        PassiveRecovery = 6,
        PassiveDecay = 7,
        Item = 8,
        Death = 9,
        Respawn = 10,
        Login = 11,
        Logout = 12,
        MapTransfer = 13,
        Quest = 14,
        Dialogue = 15,
        Crime = 16,
        HumanityEvent = 17,
        AdultInteraction = 18,
        Administrative = 19,
        Healing = 20,
        Environmental = 21,
        UnarmedAttack = 22,
    }
}
