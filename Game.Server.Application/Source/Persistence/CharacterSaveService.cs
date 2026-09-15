using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Domain.Players;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public sealed class CharacterSaveService
    {
        // Character shell/location, non-value-bearing runtime resources, presentation,
        // progression-style soft state, and firearm shot-decrement magazine state use
        // the coalesced checkpoint stream. Inventory/economy remain separate immediate
        // transactional streams and are never cleared by a checkpoint acknowledgement.
        public const PlayerDirtyFlags SupportedDirtyFlags =
            PlayerDirtyFlags.Character | PlayerDirtyFlags.Location | PlayerDirtyFlags.Resources |
            PlayerDirtyFlags.Appearance | PlayerDirtyFlags.Presentation | PlayerDirtyFlags.Magazine |
            PlayerDirtyFlags.Progression;

        private readonly ICharacterRepository _repository;

        public CharacterSaveService(ICharacterRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Lifecycle/forced save. This intentionally ignores soft checkpoint age.
        /// </summary>
        public async Task<bool> SaveAsync(PlayerRuntime runtime, CancellationToken cancellationToken)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));

            CharacterPersistenceRecord snapshot = CharacterPersistenceRecord.Capture(runtime);
            PlayerDirtyFlags persistedFlags = snapshot.CapturedDirtyFlags & SupportedDirtyFlags;
            if (persistedFlags == PlayerDirtyFlags.None)
                return false;

            await _repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return runtime.AcknowledgePersisted(snapshot.Revision, persistedFlags);
        }

        /// <summary>
        /// Force-drains dirty state regardless of age. Used by graceful shutdown and other
        /// explicit lifecycle boundaries.
        /// </summary>
        public Task<int> SaveDirtyBatchAsync(
            DirtyPlayerTracker tracker,
            int maxCount,
            CancellationToken cancellationToken)
        {
            if (tracker == null)
                throw new ArgumentNullException(nameof(tracker));
            PlayerRuntime[] runtimes = tracker.GetBatch(maxCount, SupportedDirtyFlags);
            return SaveSelectedBatchAsync(tracker, runtimes, cancellationToken);
        }

        /// <summary>
        /// Normal soft-state checkpoint path. Only runtimes whose staggered dirty-age
        /// deadline is due are selected; a sweep that finds nothing due performs no
        /// Backend/database work.
        /// </summary>
        public Task<int> SaveDueBatchAsync(
            DirtyPlayerTracker tracker,
            int maxCount,
            DateTime utcNow,
            TimeSpan preferredAge,
            TimeSpan hardAge,
            CancellationToken cancellationToken)
        {
            if (tracker == null)
                throw new ArgumentNullException(nameof(tracker));
            PlayerRuntime[] runtimes = tracker.GetDueBatch(
                maxCount,
                SupportedDirtyFlags,
                utcNow,
                preferredAge,
                hardAge);
            return SaveSelectedBatchAsync(tracker, runtimes, cancellationToken);
        }

        private async Task<int> SaveSelectedBatchAsync(
            DirtyPlayerTracker tracker,
            PlayerRuntime[] runtimes,
            CancellationToken cancellationToken)
        {
            if (runtimes == null || runtimes.Length == 0)
                return 0;

            var records = new CharacterPersistenceRecord[runtimes.Length];
            for (int i = 0; i < runtimes.Length; ++i)
                records[i] = CharacterPersistenceRecord.Capture(runtimes[i]);

            if (_repository is ICharacterBatchRepository batchRepository)
            {
                IReadOnlyList<CharacterSavePersistenceResult> results = await batchRepository
                    .SaveBatchAsync(records, cancellationToken)
                    .ConfigureAwait(false);

                var byCharacter = new Dictionary<CharacterId, CharacterSavePersistenceResult>();
                if (results != null)
                {
                    for (int i = 0; i < results.Count; ++i)
                        byCharacter[results[i].CharacterId] = results[i];
                }

                int persisted = 0;
                bool rejected = false;
                for (int i = 0; i < records.Length; ++i)
                {
                    CharacterPersistenceRecord record = records[i];
                    PlayerRuntime runtime = runtimes[i];
                    PlayerDirtyFlags flags = record.CapturedDirtyFlags & SupportedDirtyFlags;
                    bool durableWriteSucceeded =
                        flags != PlayerDirtyFlags.None &&
                        byCharacter.TryGetValue(record.CharacterId, out CharacterSavePersistenceResult save) &&
                        save.Persisted &&
                        save.RequestedRevision == record.Revision;

                    if (durableWriteSucceeded)
                    {
                        // If a newer runtime mutation arrived while this immutable capture
                        // was in flight the acknowledgement intentionally fails, but the
                        // durable write still happened. Resetting tracker age prevents an
                        // immediate retry storm while preserving the newer dirty state.
                        runtime.AcknowledgePersisted(record.Revision, flags);
                        persisted++;
                    }
                    else
                    {
                        rejected = true;
                    }

                    tracker.RefreshAfterSave(runtime, durableWriteSucceeded);
                }

                if (rejected)
                {
                    throw new InvalidOperationException(
                        "One or more character checkpoints were rejected or not acknowledged by persistence.");
                }

                return persisted;
            }

            int saved = 0;
            for (int i = 0; i < runtimes.Length; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CharacterPersistenceRecord record = records[i];
                PlayerDirtyFlags flags = record.CapturedDirtyFlags & SupportedDirtyFlags;
                bool durableWriteSucceeded = false;
                try
                {
                    if (flags != PlayerDirtyFlags.None)
                    {
                        await _repository.SaveAsync(record, cancellationToken).ConfigureAwait(false);
                        durableWriteSucceeded = true;
                        AcknowledgeRuntime(runtimes[i], record, flags);
                        saved++;
                    }
                }
                finally
                {
                    tracker.RefreshAfterSave(runtimes[i], durableWriteSucceeded);
                }
            }

            return saved;
        }

        private static void AcknowledgeRuntime(
            PlayerRuntime runtime,
            CharacterPersistenceRecord record,
            PlayerDirtyFlags flags)
        {
            runtime?.AcknowledgePersisted(record.Revision, flags);
        }
    }
}
