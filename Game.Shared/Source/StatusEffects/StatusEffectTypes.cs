using System;

namespace Game.Shared.StatusEffects
{
    public enum StatusEffectClassification : byte
    {
        Buff = 0,
        Debuff = 1,
    }

    public enum StatusEffectStackingPolicy : byte
    {
        RefreshDuration = 0,
        AddStacksAndRefresh = 1,
        Replace = 2,
        IgnoreWhileActive = 3,
    }

    public enum StatusEffectChangeKind : byte
    {
        Applied = 0,
        Refreshed = 1,
        StacksChanged = 2,
        Replaced = 3,
        Removed = 4,
        Expired = 5,
        ClearedOnDeath = 6,
        Reconciled = 7,
    }

    public enum StatusEffectChangeReason : byte
    {
        Unknown = 0,
        Ability = 1,
        Item = 2,
        Combat = 3,
        Environment = 4,
        Administrative = 5,
        Expired = 6,
        Death = 7,
        ContentRevision = 8,
    }
}
