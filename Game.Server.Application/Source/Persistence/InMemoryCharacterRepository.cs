using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public sealed class InMemoryCharacterRepository : ICharacterRepository
    {
        private readonly object _gate = new object();
        private readonly Dictionary<CharacterId, CharacterPersistenceRecord> _records = new Dictionary<CharacterId, CharacterPersistenceRecord>();
        private readonly Dictionary<CharacterId, CharacterPersistenceRecord> _deletedRecords = new Dictionary<CharacterId, CharacterPersistenceRecord>();
        private long _nextCharacterId = 1;

        public InMemoryCharacterRepository(IEnumerable<CharacterPersistenceRecord> seed = null)
        {
            if (seed == null)
                return;

            foreach (CharacterPersistenceRecord record in seed)
            {
                if (record == null)
                    continue;
                _records[record.CharacterId] = record.Copy();
                if (record.CharacterId.Value >= _nextCharacterId)
                    _nextCharacterId = record.CharacterId.Value + 1;
            }
        }

        public Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(AccountId accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<CharacterSummary>();

            lock (_gate)
            {
                foreach (CharacterPersistenceRecord record in _records.Values)
                {
                    if (record.AccountId == accountId)
                        result.Add(new CharacterSummary(record.CharacterId, record.Name, record.Location.MapId));
                }
            }

            result.Sort((a, b) => a.CharacterId.Value.CompareTo(b.CharacterId.Value));
            return Task.FromResult((IReadOnlyList<CharacterSummary>)result);
        }

        public Task<CharacterPersistenceRecord> LoadForAccountAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (_records.TryGetValue(characterId, out CharacterPersistenceRecord record) && record.AccountId == accountId)
                    return Task.FromResult(record.Copy());
            }

            return Task.FromResult<CharacterPersistenceRecord>(null);
        }

        public Task<CharacterCreatePersistenceResult> TryCreateAsync(
            AccountId accountId,
            string name,
            Game.Server.Domain.Characters.CharacterLocationState initialLocation,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            int maxCharactersForAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accountId.IsValid || string.IsNullOrWhiteSpace(name) || maxCharactersForAccount <= 0)
                return Task.FromResult(CharacterCreatePersistenceResult.Failed());

            lock (_gate)
            {
                int ownedCount = 0;
                foreach (CharacterPersistenceRecord existing in _records.Values)
                {
                    if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(CharacterCreatePersistenceResult.DuplicateName());
                    if (existing.AccountId == accountId)
                        ownedCount++;
                }

                if (ownedCount >= maxCharactersForAccount)
                    return Task.FromResult(CharacterCreatePersistenceResult.LimitReached());

                var id = new CharacterId(_nextCharacterId++);
                _records[id] = new CharacterPersistenceRecord(
                    accountId,
                    id,
                    name,
                    initialLocation,
                    0,
                    CharacterPersistenceRecord.CurrentSchemaVersion,
                    PlayerDirtyFlags.None,
                    null,
                    0,
                    null,
                    initialAppearance,
                    initialPresentation);
                return Task.FromResult(CharacterCreatePersistenceResult.Created(id));
            }
        }

        public Task<CharacterDeletePersistenceResult> TryArchiveDeleteAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accountId.IsValid || !characterId.IsValid)
                return Task.FromResult(CharacterDeletePersistenceResult.Failed(Game.Shared.Protocol.CharacterDeleteFailure.InvalidCharacter));

            lock (_gate)
            {
                if (!_records.TryGetValue(characterId, out CharacterPersistenceRecord record) ||
                    record.AccountId != accountId)
                {
                    return Task.FromResult(CharacterDeletePersistenceResult.Failed(
                        Game.Shared.Protocol.CharacterDeleteFailure.CharacterNotFoundOrNotOwned));
                }

                _records.Remove(characterId);
                _deletedRecords[characterId] = record.Copy();
                return Task.FromResult(CharacterDeletePersistenceResult.Deleted(characterId, record.Name));
            }
        }

        public Task SaveAsync(CharacterPersistenceRecord record, CancellationToken cancellationToken)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                // Never let an older async snapshot overwrite a newer committed
                // revision. Real database implementations should enforce the same
                // property transactionally/with optimistic concurrency.
                if (_records.TryGetValue(record.CharacterId, out CharacterPersistenceRecord current) &&
                    current.Revision > record.Revision)
                {
                    return Task.CompletedTask;
                }

                _records[record.CharacterId] = record.Copy();
                if (record.CharacterId.Value >= _nextCharacterId)
                    _nextCharacterId = record.CharacterId.Value + 1;
            }

            return Task.CompletedTask;
        }
    }
}
