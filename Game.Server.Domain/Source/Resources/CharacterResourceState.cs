using System;
using Game.Shared.Resources;

namespace Game.Server.Domain.Resources
{
    public readonly struct CharacterResourceState
    {
        public CharacterResourceId Id { get; }
        public int Current { get; }
        public int Minimum { get; }
        public int Maximum { get; }
        public bool Enabled { get; }
        public CharacterResourcePersistenceMode Persistence { get; }

        public CharacterResourceState(
            CharacterResourceId id,
            int current,
            int minimum,
            int maximum,
            bool enabled = true,
            CharacterResourcePersistenceMode persistence = CharacterResourcePersistenceMode.Character)
        {
            if (id == CharacterResourceId.None)
                throw new ArgumentOutOfRangeException(nameof(id));
            if (maximum < minimum)
                throw new ArgumentOutOfRangeException(nameof(maximum));
            if (current < minimum || current > maximum)
                throw new ArgumentOutOfRangeException(nameof(current));

            Id = id;
            Current = current;
            Minimum = minimum;
            Maximum = maximum;
            Enabled = enabled;
            Persistence = persistence;
        }

        public CharacterResourceState WithCurrent(int current) =>
            new CharacterResourceState(Id, current, Minimum, Maximum, Enabled, Persistence);

        public CharacterResourceState WithRange(int current, int minimum, int maximum) =>
            new CharacterResourceState(Id, current, minimum, maximum, Enabled, Persistence);
    }
}
