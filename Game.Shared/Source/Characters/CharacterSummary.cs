using System;
using Game.Shared.Identity;

namespace Game.Shared.Characters
{
    public sealed class CharacterSummary
    {
        public CharacterId CharacterId { get; }
        public string Name { get; }
        public string MapId { get; }

        public CharacterSummary(CharacterId characterId, string name, string mapId)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Character name is required.", nameof(name));
            CharacterId = characterId;
            Name = name;
            MapId = mapId ?? string.Empty;
        }
    }
}
