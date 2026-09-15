using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Domain.Characters;
using Game.Shared.Characters;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public interface ICharacterRepository
    {
        Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(AccountId accountId, CancellationToken cancellationToken);
        Task<CharacterPersistenceRecord> LoadForAccountAsync(AccountId accountId, CharacterId characterId, CancellationToken cancellationToken);
        Task<CharacterCreatePersistenceResult> TryCreateAsync(
            AccountId accountId,
            string name,
            CharacterLocationState initialLocation,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            int maxCharactersForAccount,
            CancellationToken cancellationToken);
        Task<CharacterDeletePersistenceResult> TryArchiveDeleteAsync(
            AccountId accountId,
            CharacterId characterId,
            CancellationToken cancellationToken);
        Task SaveAsync(CharacterPersistenceRecord record, CancellationToken cancellationToken);
    }
}
