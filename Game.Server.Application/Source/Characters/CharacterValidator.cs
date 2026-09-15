using Game.Server.Application.Persistence;

namespace Game.Server.Application.Characters
{
    public sealed class CharacterValidator
    {
        public bool IsValid(CharacterPersistenceRecord record)
        {
            if (record == null)
                return false;
            if (!record.AccountId.IsValid || !record.CharacterId.IsValid)
                return false;
            if (record.SchemaVersion <= 0 || record.SchemaVersion > CharacterPersistenceRecord.CurrentSchemaVersion)
                return false;
            if (string.IsNullOrWhiteSpace(record.Name))
                return false;
            if (string.IsNullOrWhiteSpace(record.Location.MapId))
                return false;
            if (record.Appearance == null || !record.Appearance.IsValid(out _))
                return false;
            if (record.PresentationPreferences == null || !record.PresentationPreferences.IsValid(out _))
                return false;
            return true;
        }
    }
}
