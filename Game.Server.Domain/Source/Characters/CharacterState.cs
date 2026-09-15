using System;

namespace Game.Server.Domain.Characters
{
    public sealed class CharacterState
    {
        public string Name { get; }

        public CharacterState(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Character name is required.", nameof(name));
            Name = name;
        }
    }
}
