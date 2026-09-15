using System;

namespace Game.Server.Domain.Players
{
    [Flags]
    public enum PlayerDirtyFlags : ushort
    {
        None = 0,
        Character = 1 << 0,
        Location = 1 << 1,
        Stats = 1 << 2,
        Inventory = 1 << 3,
        Equipment = 1 << 4,
        Skills = 1 << 5,
        Quests = 1 << 6,
        Social = 1 << 7,
        Resources = 1 << 8,
        Appearance = 1 << 9,
        Presentation = 1 << 10,
        Magazine = 1 << 11,
        Progression = 1 << 12,
    }
}
