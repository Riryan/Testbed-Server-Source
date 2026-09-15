using System;

namespace Game.Shared.Actors
{
    [Flags]
    public enum GameplayActorAccessMask : byte
    {
        None = 0,
        Player = 1 << 0,
        Monster = 1 << 1,
        Pet = 1 << 2,
        Npc = 1 << 3,
        Population = 1 << 4,
        All = Player | Monster | Pet | Npc | Population,
    }
}
