using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Characters;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.Protocol;

namespace Game.Server.Application.Characters
{
    public sealed class CharacterService
    {
        public const int DefaultCharacterLimit = 4;

        private readonly ICharacterRepository _repository;
        private readonly CharacterValidator _validator;
        private readonly CharacterRuntimeFactory _runtimeFactory;

        public CharacterService(
            ICharacterRepository repository,
            CharacterValidator validator,
            CharacterRuntimeFactory runtimeFactory)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        }

        public Task<IReadOnlyList<CharacterSummary>> GetCharacterListAsync(AccountId accountId, CancellationToken cancellationToken) =>
            _repository.ListForAccountAsync(accountId, cancellationToken);

        public Task<CharacterCreateResult> CreateCharacterAsync(
            AccountId accountId,
            string requestedName,
            CharacterLocationState initialLocation,
            CancellationToken cancellationToken) =>
            CreateCharacterAsync(
                accountId,
                requestedName,
                initialLocation,
                CharacterAppearanceRecipe.CreateDefault(),
                CharacterPresentationPreferences.CreateDefault(),
                cancellationToken);

        public async Task<CharacterCreateResult> CreateCharacterAsync(
            AccountId accountId,
            string requestedName,
            CharacterLocationState initialLocation,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid)
                return CharacterCreateResult.Failed(CharacterCreateFailure.InvalidSessionState);

            string name = CharacterNamePolicy.Normalize(requestedName);
            if (!CharacterNamePolicy.IsAllowed(name))
                return CharacterCreateResult.Failed(CharacterCreateFailure.InvalidName);

            CharacterAppearanceRecipe appearance =
                initialAppearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            if (!appearance.IsValid(out _))
                return CharacterCreateResult.Failed(CharacterCreateFailure.InvalidAppearance);

            CharacterPresentationPreferences presentation =
                initialPresentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            if (!presentation.IsValid(out _))
                return CharacterCreateResult.Failed(CharacterCreateFailure.InvalidPresentation);

            CharacterCreatePersistenceResult created;
            try
            {
                created = await _repository
                    .TryCreateAsync(accountId, name, initialLocation, appearance, presentation, DefaultCharacterLimit, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return CharacterCreateResult.Failed(CharacterCreateFailure.PersistenceFailed);
            }

            if (!created.Success)
            {
                CharacterCreateFailure failure = created.NameAlreadyExists
                    ? CharacterCreateFailure.NameAlreadyExists
                    : created.CharacterLimitReached
                        ? CharacterCreateFailure.CharacterLimitReached
                        : CharacterCreateFailure.PersistenceFailed;
                return CharacterCreateResult.Failed(failure);
            }

            return CharacterCreateResult.Succeeded(created.CharacterId, name);
        }

        public async Task<CharacterDeleteResult> DeleteCharacterAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            if (!accountId.IsValid)
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.InvalidSessionState);
            if (!characterId.IsValid)
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.InvalidCharacter);

            CharacterDeletePersistenceResult deleted;
            try
            {
                deleted = await _repository
                    .TryArchiveDeleteAsync(accountId, characterId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.PersistenceFailed);
            }

            return deleted.Success
                ? CharacterDeleteResult.Succeeded(deleted.CharacterId, deleted.Name)
                : CharacterDeleteResult.Failed(deleted.Failure);
        }

        public async Task<CharacterLoadOutcome> LoadOwnedCharacterAsync(
            AccountId accountId,
            CharacterId characterId,
            PlayerSessionId sessionId,
            CancellationToken cancellationToken)
        {
            CharacterPersistenceRecord record = await _repository
                .LoadForAccountAsync(accountId, characterId, cancellationToken)
                .ConfigureAwait(false);

            if (record == null)
                return CharacterLoadOutcome.Failed(CharacterSelectFailure.CharacterNotFoundOrNotOwned);
            if (!_validator.IsValid(record))
                return CharacterLoadOutcome.Failed(CharacterSelectFailure.CharacterDataInvalid);

            try
            {
                return CharacterLoadOutcome.Succeeded(_runtimeFactory.Create(record, sessionId));
            }
            catch
            {
                return CharacterLoadOutcome.Failed(CharacterSelectFailure.CharacterLoadFailed);
            }
        }
    }
}
