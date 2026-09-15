using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Characters;
using Game.Shared.Backend;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Progression;
using Game.Shared.Resources;
using Game.Shared.World;

namespace Game.UnityIntegration.Backend
{
    /// <summary>
    /// Unity game-server repository adapter. The standalone backend owns SQLite;
    /// this adapter only exchanges explicit persistence contracts over the internal API.
    /// </summary>
    public sealed class BackendCharacterRepository : ICharacterRepository, ICharacterBatchRepository
    {
        private readonly BackendInternalClient _backend;
        private readonly IPlayerSystemsRepository _playerSystems;
        private readonly ICharacterPersistenceLeaseProofProvider _leaseProofProvider;

        public BackendCharacterRepository(
            BackendInternalClient backend,
            IPlayerSystemsRepository playerSystems = null,
            ICharacterPersistenceLeaseProofProvider leaseProofProvider = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _playerSystems = playerSystems;
            _leaseProofProvider = leaseProofProvider;
        }

        public async Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(
            AccountId accountId,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid)
                return new CharacterSummary[0];

            BackendCharacterListResponse response = await _backend.ListCharactersAsync(
                new BackendCharacterListRequest { accountId = accountId.Value },
                cancellationToken).ConfigureAwait(false);

            if (!response.success)
                throw new InvalidOperationException("Backend character list failed.");

            BackendCharacterSummaryDto[] source = response.characters ?? new BackendCharacterSummaryDto[0];
            var result = new CharacterSummary[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                BackendCharacterSummaryDto item = source[i];
                result[i] = new CharacterSummary(
                    new CharacterId(item.characterId),
                    item.name,
                    item.mapId);
            }
            return result;
        }

        public async Task<CharacterPersistenceRecord> LoadForAccountAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid || !characterId.IsValid)
                return null;

            BackendCharacterLoadResponse response = await _backend.LoadCharacterAsync(
                new BackendCharacterLoadRequest
                {
                    accountId = accountId.Value,
                    characterId = characterId.Value,
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.success)
                throw new InvalidOperationException("Backend character load failed.");
            if (!response.found)
                return null;

            PlayerSystemsPersistenceRecord systems = _playerSystems == null
                ? null
                : await _playerSystems.LoadAsync(accountId, characterId, cancellationToken).ConfigureAwait(false);
            return FromDto(response.character, systems);
        }

        public async Task<CharacterCreatePersistenceResult> TryCreateAsync(
            AccountId accountId,
            string name,
            CharacterLocationState initialLocation,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            int maxCharactersForAccount,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid || string.IsNullOrWhiteSpace(name))
                return CharacterCreatePersistenceResult.Failed();

            BackendCharacterCreateResponse response = await _backend.CreateCharacterAsync(
                new BackendCharacterCreateRequest
                {
                    accountId = accountId.Value,
                    name = name,
                    initialLocation = ToDto(initialLocation),
                    initialAppearance = initialAppearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault(),
                    initialPresentation = initialPresentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault(),
                },
                cancellationToken).ConfigureAwait(false);

            if (response.success && response.characterId > 0)
                return CharacterCreatePersistenceResult.Created(new CharacterId(response.characterId));

            CharacterCreateFailure failure = (CharacterCreateFailure)response.failure;
            if (failure == CharacterCreateFailure.NameAlreadyExists)
                return CharacterCreatePersistenceResult.DuplicateName();
            if (failure == CharacterCreateFailure.CharacterLimitReached)
                return CharacterCreatePersistenceResult.LimitReached();
            return CharacterCreatePersistenceResult.Failed();
        }

        public async Task<CharacterDeletePersistenceResult> TryArchiveDeleteAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid || !characterId.IsValid)
                return CharacterDeletePersistenceResult.Failed(CharacterDeleteFailure.InvalidCharacter);

            BackendCharacterDeleteResponse response = await _backend.DeleteCharacterAsync(
                new BackendCharacterDeleteRequest
                {
                    accountId = accountId.Value,
                    characterId = characterId.Value,
                },
                cancellationToken).ConfigureAwait(false);

            if (response != null && response.success && response.characterId > 0)
            {
                return CharacterDeletePersistenceResult.Deleted(
                    new CharacterId(response.characterId),
                    response.name);
            }

            CharacterDeleteFailure failure = response == null
                ? CharacterDeleteFailure.PersistenceFailed
                : (CharacterDeleteFailure)response.failure;
            if (failure == CharacterDeleteFailure.None)
                failure = CharacterDeleteFailure.PersistenceFailed;
            return CharacterDeletePersistenceResult.Failed(failure);
        }

        public async Task SaveAsync(
            CharacterPersistenceRecord record,
            CancellationToken cancellationToken)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            IReadOnlyList<CharacterSavePersistenceResult> result = await SaveBatchAsync(
                new[] { record },
                cancellationToken).ConfigureAwait(false);

            if (result.Count != 1 || !result[0].Persisted)
                throw new InvalidOperationException("Backend rejected the character checkpoint.");
        }

        public async Task<IReadOnlyList<CharacterSavePersistenceResult>> SaveBatchAsync(
            IReadOnlyList<CharacterPersistenceRecord> records,
            CancellationToken cancellationToken)
        {
            if (records == null)
                throw new ArgumentNullException(nameof(records));
            if (records.Count == 0)
                return new CharacterSavePersistenceResult[0];
            if (records.Count > BackendServiceContracts.MaxCharacterBatchSize)
                throw new ArgumentOutOfRangeException(nameof(records));

            var payload = new BackendCharacterRecordDto[records.Count];
            for (int i = 0; i < records.Count; ++i)
            {
                if (records[i] == null)
                    throw new ArgumentException("Character checkpoint batch contains null.", nameof(records));

                payload[i] = ToDto(records[i]);

                // Standalone authoritative servers configure a distributed lease proof
                // provider. Once configured, persistence fails closed if local authority
                // was lost; do not downgrade to an unfenced write.
                if (_leaseProofProvider != null)
                {
                    if (!_leaseProofProvider.TryGetPersistenceLeaseOwnerToken(
                            records[i].CharacterId,
                            out string leaseOwnerToken) ||
                        string.IsNullOrWhiteSpace(leaseOwnerToken))
                    {
                        throw new InvalidOperationException(
                            "Character checkpoint rejected locally because the authoritative lease is not held.");
                    }

                    payload[i].leaseOwnerToken = leaseOwnerToken;
                }
            }

            BackendCharacterSaveBatchResponse response = await _backend.SaveCharactersAsync(
                new BackendCharacterSaveBatchRequest { characters = payload },
                cancellationToken).ConfigureAwait(false);

            if (!response.success)
                throw new InvalidOperationException("Backend character checkpoint batch failed.");

            BackendCharacterSaveAckDto[] source = response.acknowledgements ?? new BackendCharacterSaveAckDto[0];
            var result = new CharacterSavePersistenceResult[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                BackendCharacterSaveAckDto ack = source[i];
                result[i] = new CharacterSavePersistenceResult(
                    new CharacterId(ack.characterId),
                    ack.requestedRevision,
                    ack.accepted,
                    ack.storedRevision);
            }
            return result;
        }

        private static BackendCharacterRecordDto ToDto(CharacterPersistenceRecord record)
        {
            PersistedCharacterResource[] source = record.Resources ?? Array.Empty<PersistedCharacterResource>();
            var resources = new BackendCharacterResourceDto[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                resources[i] = new BackendCharacterResourceDto
                {
                    id = source[i].ResourceId,
                    current = source[i].Current,
                    maximum = source[i].Maximum,
                };
            }

            BackendMagazineStateDto[] magazines = null;
            if (record.HasMagazineState)
            {
                PersistedMagazineState[] sourceMagazines = record.Magazines ?? Array.Empty<PersistedMagazineState>();
                magazines = new BackendMagazineStateDto[sourceMagazines.Length];
                for (int i = 0; i < sourceMagazines.Length; ++i)
                {
                    magazines[i] = new BackendMagazineStateDto
                    {
                        itemInstanceId = sourceMagazines[i].ItemInstanceId.Value,
                        loadedAmmoDefinitionId = sourceMagazines[i].LoadedAmmoDefinitionId,
                        loadedRounds = sourceMagazines[i].LoadedRounds,
                        magazineRevision = sourceMagazines[i].MagazineRevision,
                    };
                }
            }

            return new BackendCharacterRecordDto
            {
                accountId = record.AccountId.Value,
                characterId = record.CharacterId.Value,
                name = record.Name,
                location = ToDto(record.Location),
                revision = record.Revision,
                schemaVersion = record.SchemaVersion,
                resourceRevision = record.ResourceRevision,
                resources = resources,
                appearance = record.Appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault(),
                presentation = record.PresentationPreferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault(),
                magazines = magazines,
                progression = record.HasProgressionState ? record.Progression?.Clone() : null,
            };
        }

        private static BackendCharacterLocationDto ToDto(CharacterLocationState location) =>
            new BackendCharacterLocationDto
            {
                mapId = location.MapId,
                instanceId = location.InstanceId,
                positionX = location.Position.X,
                positionY = location.Position.Y,
                positionZ = location.Position.Z,
                yawDegrees = location.YawDegrees,
            };

        private static CharacterPersistenceRecord FromDto(BackendCharacterRecordDto dto, PlayerSystemsPersistenceRecord playerSystems)
        {
            if (dto == null || dto.location == null)
                return null;

            var location = new CharacterLocationState(
                dto.location.mapId,
                dto.location.instanceId ?? string.Empty,
                new WorldPosition(
                    dto.location.positionX,
                    dto.location.positionY,
                    dto.location.positionZ),
                dto.location.yawDegrees);

            BackendCharacterResourceDto[] resourceDtos = dto.resources ?? Array.Empty<BackendCharacterResourceDto>();
            var resources = new PersistedCharacterResource[resourceDtos.Length];
            for (int i = 0; i < resourceDtos.Length; ++i)
            {
                resources[i] = new PersistedCharacterResource(
                    resourceDtos[i].id,
                    resourceDtos[i].current,
                    resourceDtos[i].maximum);
            }

            return new CharacterPersistenceRecord(
                new AccountId(dto.accountId),
                new CharacterId(dto.characterId),
                dto.name,
                location,
                dto.revision,
                dto.schemaVersion,
                Game.Server.Domain.Players.PlayerDirtyFlags.None,
                playerSystems,
                dto.resourceRevision,
                resources,
                dto.appearance,
                dto.presentation,
                null,
                false,
                dto.progression ?? CharacterProgressionState.CreateDefault(),
                false);
        }
    }
}
