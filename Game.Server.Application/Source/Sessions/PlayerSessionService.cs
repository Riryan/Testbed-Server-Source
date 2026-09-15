using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Characters;
using Game.Server.Application.Connections;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;

namespace Game.Server.Application.Sessions
{
    public sealed class PlayerSessionService
    {
        private readonly PlayerSessionRegistry _sessions;
        private readonly CharacterService _characters;
        private readonly ICharacterLeaseService _leases;
        private readonly DirtyPlayerTracker _dirtyPlayers;

        public PlayerSessionService(
            PlayerSessionRegistry sessions,
            CharacterService characters,
            ICharacterLeaseService leases,
            DirtyPlayerTracker dirtyPlayers)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _leases = leases ?? throw new ArgumentNullException(nameof(leases));
            _dirtyPlayers = dirtyPlayers ?? throw new ArgumentNullException(nameof(dirtyPlayers));
        }

        public PlayerSession Open(ConnectionKey connection)
        {
            var session = new PlayerSession(connection, PlayerSessionId.New());
            return _sessions.TryAdd(session) ? session : null;
        }

        public bool BeginAuthentication(ConnectionKey connection)
        {
            return _sessions.TryGet(connection, out PlayerSession session) &&
                   BeginAuthentication(session.Handle);
        }

        public bool BeginAuthentication(PlayerSessionHandle handle)
        {
            return TryGetSession(handle, out PlayerSession session) &&
                   session.TryBeginAuthentication();
        }

        public bool CancelAuthentication(PlayerSessionHandle handle)
        {
            return TryGetSession(handle, out PlayerSession session) &&
                   session.TryCancelAuthentication();
        }

        public bool CompleteAuthentication(ConnectionKey connection, AccountId accountId)
        {
            return _sessions.TryGet(connection, out PlayerSession session) &&
                   CompleteAuthentication(session.Handle, accountId);
        }

        public bool CompleteAuthentication(PlayerSessionHandle handle, AccountId accountId)
        {
            if (!TryGetSession(handle, out PlayerSession session))
                return false;
            if (_sessions.ContainsAccount(accountId))
                return false;
            if (!session.TryCompleteAuthentication(accountId))
                return false;

            if (_sessions.TryBindAccount(session))
                return true;

            // Binding failure is fail-closed. This should only happen on a race.
            session.TryBeginDisconnect();
            return false;
        }

        public Task<CharacterListResult> GetCharacterListAsync(ConnectionKey connection, CancellationToken cancellationToken)
        {
            if (!_sessions.TryGet(connection, out PlayerSession session))
                return Task.FromResult(CharacterListResult.Failed("session not found"));

            return GetCharacterListAsync(session.Handle, cancellationToken);
        }

        /// <summary>
        /// Loads the character list for one exact session generation. Async integration
        /// callers should retain PlayerSessionHandle so a reused transport connection id
        /// cannot redirect a late response into a replacement session.
        /// </summary>
        public async Task<CharacterListResult> GetCharacterListAsync(
            PlayerSessionHandle handle,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(handle, out PlayerSession session))
                return CharacterListResult.Failed("session not found");
            if (session.State != PlayerSessionState.CharacterLobby)
                return CharacterListResult.Failed("session is not in character lobby");

            var list = await _characters.GetCharacterListAsync(session.AccountId, cancellationToken).ConfigureAwait(false);

            // The repository call may complete after disconnect/reconnect. Never publish
            // old-generation lobby data as a successful result for a dead session.
            if (!TryGetSession(handle, out PlayerSession current) ||
                !ReferenceEquals(current, session) ||
                current.State != PlayerSessionState.CharacterLobby)
            {
                return CharacterListResult.Failed("session changed during character list load");
            }

            var array = new Game.Shared.Characters.CharacterSummary[list.Count];
            for (int i = 0; i < list.Count; ++i)
                array[i] = list[i];
            return CharacterListResult.Succeeded(array);
        }

        public Task<CharacterCreateResult> CreateCharacterAsync(
            PlayerSessionHandle handle,
            string name,
            CharacterLocationState initialLocation,
            CancellationToken cancellationToken) =>
            CreateCharacterAsync(
                handle,
                name,
                initialLocation,
                CharacterAppearanceRecipe.CreateDefault(),
                CharacterPresentationPreferences.CreateDefault(),
                cancellationToken);

        public async Task<CharacterCreateResult> CreateCharacterAsync(
            PlayerSessionHandle handle,
            string name,
            CharacterLocationState initialLocation,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(handle, out PlayerSession session))
                return CharacterCreateResult.Failed(CharacterCreateFailure.SessionNotFound);
            if (session.State != PlayerSessionState.CharacterLobby)
                return CharacterCreateResult.Failed(CharacterCreateFailure.InvalidSessionState);

            CharacterCreateResult created = await _characters
                .CreateCharacterAsync(session.AccountId, name, initialLocation, initialAppearance, initialPresentation, cancellationToken)
                .ConfigureAwait(false);

            // Creation may commit in persistence even if the transport disconnects while
            // the database operation is in flight. Never publish it to a replacement
            // session generation; it will simply appear on the next legitimate list.
            if (!TryGetSession(handle, out PlayerSession current) ||
                !ReferenceEquals(current, session) ||
                current.State != PlayerSessionState.CharacterLobby)
            {
                return CharacterCreateResult.Failed(CharacterCreateFailure.SessionChangedDuringCreate);
            }

            return created;
        }

        public async Task<CharacterDeleteResult> DeleteCharacterAsync(
            PlayerSessionHandle handle,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(handle, out PlayerSession session))
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.SessionNotFound);
            if (session.State != PlayerSessionState.CharacterLobby)
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.InvalidSessionState);
            if (!characterId.IsValid)
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.InvalidCharacter);
            if (_sessions.ContainsCharacter(characterId))
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.CharacterActive);

            CharacterDeleteResult deleted = await _characters
                .DeleteCharacterAsync(session.AccountId, characterId, cancellationToken)
                .ConfigureAwait(false);

            // Deletion may commit even if this transport generation disappears while the
            // backend transaction is in flight. Never publish a late success to a reused
            // connection; the local cache can recover explicitly if the response was lost.
            if (!TryGetSession(handle, out PlayerSession current) ||
                !ReferenceEquals(current, session) ||
                current.State != PlayerSessionState.CharacterLobby)
            {
                return CharacterDeleteResult.Failed(CharacterDeleteFailure.SessionChangedDuringDelete);
            }

            return deleted;
        }

        public Task<CharacterSelectResult> SelectCharacterAsync(
            ConnectionKey connection,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            if (!_sessions.TryGet(connection, out PlayerSession session))
                return Task.FromResult(CharacterSelectResult.Failed(CharacterSelectFailure.SessionNotFound));

            return SelectCharacterAsync(session.Handle, characterId, cancellationToken);
        }

        /// <summary>
        /// Selects/loads a character for one exact session generation. This is the
        /// integration-safe overload for transport requests that can outlive a callback.
        /// </summary>
        public async Task<CharacterSelectResult> SelectCharacterAsync(
            PlayerSessionHandle handle,
            CharacterId characterId,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(handle, out PlayerSession session))
                return CharacterSelectResult.Failed(CharacterSelectFailure.SessionNotFound);
            if (session.State != PlayerSessionState.CharacterLobby)
                return CharacterSelectResult.Failed(CharacterSelectFailure.InvalidSessionState);
            if (_sessions.ContainsCharacter(characterId))
                return CharacterSelectResult.Failed(CharacterSelectFailure.CharacterAlreadyActive);

            // Reserve this exact session's character-load state before awaiting Backend
            // lease acquisition. Without this reservation, two concurrent SelectCharacter
            // requests from one session can both acquire the same distributed lease and
            // the losing request can release authority out from under the winner.
            if (!session.TryBeginCharacterLoad(characterId))
                return CharacterSelectResult.Failed(CharacterSelectFailure.InvalidSessionState);

            bool leaseAcquired;
            try
            {
                leaseAcquired = await _leases
                    .TryAcquireAsync(session.AccountId, characterId, session.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.TryCancelCharacterLoad();
                throw;
            }
            catch
            {
                session.TryCancelCharacterLoad();
                return CharacterSelectResult.Failed(CharacterSelectFailure.CharacterLoadFailed);
            }

            if (!leaseAcquired)
            {
                session.TryCancelCharacterLoad();
                return CharacterSelectResult.Failed(CharacterSelectFailure.CharacterAlreadyActive);
            }

            // The transport/session may have disconnected or otherwise transitioned while
            // the asynchronous lease request was in flight. If so, relinquish the lease
            // immediately and never begin persistence load for a dead session generation.
            if (session.State != PlayerSessionState.LoadingCharacter ||
                session.SelectedCharacterId != characterId)
            {
                _leases.Release(characterId, session.SessionId);
                return CharacterSelectResult.Failed(CharacterSelectFailure.SessionChangedDuringLoad);
            }

            CharacterLoadOutcome load;
            try
            {
                load = await _characters
                    .LoadOwnedCharacterAsync(session.AccountId, characterId, session.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.TryCancelCharacterLoad();
                _leases.Release(characterId, session.SessionId);
                throw;
            }
            catch
            {
                session.TryCancelCharacterLoad();
                _leases.Release(characterId, session.SessionId);
                return CharacterSelectResult.Failed(CharacterSelectFailure.CharacterLoadFailed);
            }

            if (!load.Success)
            {
                session.TryCancelCharacterLoad();
                _leases.Release(characterId, session.SessionId);
                return CharacterSelectResult.Failed(load.Failure);
            }

            // The connection may have disconnected while persistence was loading.
            // Attachment is only legal if this exact session is still LoadingCharacter.
            if (!session.TryAttachLoadedRuntime(load.Runtime))
            {
                _leases.Release(characterId, session.SessionId);
                return CharacterSelectResult.Failed(CharacterSelectFailure.SessionChangedDuringLoad);
            }

            if (!_sessions.TryBindCharacter(session))
            {
                session.TryBeginDisconnect();
                _leases.Release(characterId, session.SessionId);
                return CharacterSelectResult.Failed(CharacterSelectFailure.CharacterAlreadyActive);
            }

            _dirtyPlayers.Track(load.Runtime);
            return CharacterSelectResult.Succeeded(characterId);
        }

        public bool TryGetSession(ConnectionKey connection, out PlayerSession session)
        {
            return _sessions.TryGet(connection, out session);
        }

        /// <summary>
        /// Resolves an exact session generation. This is the required lookup for
        /// queued/asynchronous integration work because transport connection IDs
        /// can be reused after reconnect.
        /// </summary>
        public bool TryGetSession(PlayerSessionHandle handle, out PlayerSession session)
        {
            if (!handle.IsValid)
            {
                session = null;
                return false;
            }
            return _sessions.TryGet(handle, out session);
        }

        /// <summary>
        /// Returns a successfully loaded but not-yet-entered character to CharacterLobby.
        /// Used when integration/world-routing admission determines this GameServer does not
        /// own the loaded character's current map partition.
        ///
        /// This is intentionally legal only from AwaitingWorldEntry. InWorld rollback must
        /// use the normal disconnect/transfer lifecycle instead.
        /// </summary>
        public bool ReturnAwaitingWorldEntryToLobby(PlayerSessionHandle handle)
        {
            if (!TryGetSession(handle, out PlayerSession session) ||
                session.State != PlayerSessionState.AwaitingWorldEntry ||
                session.Runtime == null ||
                !session.HasSelectedCharacter)
            {
                return false;
            }

            CharacterId characterId = session.SelectedCharacterId;
            PlayerRuntime runtime = session.Runtime;

            if (!session.TryReturnAwaitingWorldEntryToLobby())
                return false;

            _sessions.UnbindCharacter(characterId, session);
            _dirtyPlayers.Untrack(runtime);
            _leases.Release(characterId, session.SessionId);
            return true;
        }

        // Convenience overload for immediate callers. Integration code that retains
        // work beyond the current callback should capture PlayerSession.Handle and use
        // the generation-safe overload below.
        public bool CommitWorldEntry(ConnectionKey connection)
        {
            return _sessions.TryGet(connection, out PlayerSession session) && CommitWorldEntry(session.Handle);
        }

        // Integration calls this only after the authoritative tick has successfully
        // inserted/spawned the player into the world.
        public bool CommitWorldEntry(PlayerSessionHandle handle)
        {
            return TryGetSession(handle, out PlayerSession session) && session.TryEnterWorld();
        }

        public bool BeginDisconnect(ConnectionKey connection)
        {
            return _sessions.TryGet(connection, out PlayerSession session) && BeginDisconnect(session.Handle);
        }

        public bool BeginDisconnect(PlayerSessionHandle handle)
        {
            return TryGetSession(handle, out PlayerSession session) && session.TryBeginDisconnect();
        }

        // Convenience overload for immediate callers. Generation-safe integration
        // paths should retain and pass PlayerSessionHandle.
        public bool Close(ConnectionKey connection)
        {
            return _sessions.TryGet(connection, out PlayerSession session) && Close(session.Handle);
        }

        // Call after world removal and required final save have completed.
        public bool Close(PlayerSessionHandle handle)
        {
            if (!TryGetSession(handle, out PlayerSession session))
                return false;
            if (!session.TryClose())
                return false;

            if (session.Runtime != null)
                _dirtyPlayers.Untrack(session.Runtime);
            if (session.HasSelectedCharacter)
                _leases.Release(session.SelectedCharacterId, session.SessionId);

            _sessions.Remove(session);
            return true;
        }
    }
}
