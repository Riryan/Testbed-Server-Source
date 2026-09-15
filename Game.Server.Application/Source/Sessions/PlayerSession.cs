using Game.Server.Application.Connections;
using Game.Server.Domain.Players;
using Game.Shared.Identity;
using Game.Shared.Sessions;

namespace Game.Server.Application.Sessions
{
    /// <summary>
    /// Thread-safe authoritative session state machine. Persistence may complete
    /// on a worker thread while disconnect/world lifecycle work runs on the server
    /// thread, so transitions must be atomic rather than relying on Unity's main thread.
    /// </summary>
    public sealed class PlayerSession
    {
        private readonly object _gate = new object();
        private PlayerSessionState _state;
        private AccountId _accountId;
        private CharacterId _selectedCharacterId;
        private PlayerRuntime _runtime;

        public ConnectionKey Connection { get; }
        public PlayerSessionId SessionId { get; }
        public PlayerSessionHandle Handle => new PlayerSessionHandle(Connection, SessionId);

        public PlayerSessionState State
        {
            get { lock (_gate) return _state; }
        }

        public AccountId AccountId
        {
            get { lock (_gate) return _accountId; }
        }

        public CharacterId SelectedCharacterId
        {
            get { lock (_gate) return _selectedCharacterId; }
        }

        public PlayerRuntime Runtime
        {
            get { lock (_gate) return _runtime; }
        }

        public bool HasAccount
        {
            get { lock (_gate) return _accountId.IsValid; }
        }

        public bool HasSelectedCharacter
        {
            get { lock (_gate) return _selectedCharacterId.IsValid; }
        }

        public bool HasRuntime
        {
            get { lock (_gate) return _runtime != null; }
        }

        public PlayerSession(ConnectionKey connection, PlayerSessionId sessionId)
        {
            Connection = connection;
            SessionId = sessionId;
            _state = PlayerSessionState.Connected;
        }

        public bool TryBeginAuthentication()
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.Connected)
                    return false;
                _state = PlayerSessionState.Authenticating;
                return true;
            }
        }

        public bool TryCancelAuthentication()
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.Authenticating)
                    return false;
                _state = PlayerSessionState.Connected;
                return true;
            }
        }

        public bool TryCompleteAuthentication(AccountId accountId)
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.Authenticating || !accountId.IsValid)
                    return false;
                _accountId = accountId;
                _state = PlayerSessionState.CharacterLobby;
                return true;
            }
        }

        public bool TryBeginCharacterLoad(CharacterId characterId)
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.CharacterLobby || !characterId.IsValid)
                    return false;
                _selectedCharacterId = characterId;
                _runtime = null;
                _state = PlayerSessionState.LoadingCharacter;
                return true;
            }
        }

        public bool TryCancelCharacterLoad()
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.LoadingCharacter)
                    return false;
                _selectedCharacterId = default(CharacterId);
                _runtime = null;
                _state = PlayerSessionState.CharacterLobby;
                return true;
            }
        }

        public bool TryAttachLoadedRuntime(PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.LoadingCharacter || runtime == null)
                    return false;
                if (runtime.SessionId != SessionId || runtime.AccountId != _accountId || runtime.CharacterId != _selectedCharacterId)
                    return false;

                _runtime = runtime;
                _state = PlayerSessionState.AwaitingWorldEntry;
                return true;
            }
        }

        public bool TryGetAwaitingWorldEntryRuntime(out PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (_state == PlayerSessionState.AwaitingWorldEntry && _runtime != null)
                {
                    runtime = _runtime;
                    return true;
                }

                runtime = null;
                return false;
            }
        }

        public bool TryGetDisconnectingRuntime(out PlayerRuntime runtime)
        {
            lock (_gate)
            {
                if (_state == PlayerSessionState.Disconnecting)
                {
                    runtime = _runtime;
                    return true;
                }

                runtime = null;
                return false;
            }
        }

        public bool TryReturnAwaitingWorldEntryToLobby()
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.AwaitingWorldEntry || _runtime == null)
                    return false;

                _selectedCharacterId = default(CharacterId);
                _runtime = null;
                _state = PlayerSessionState.CharacterLobby;
                return true;
            }
        }

        public bool TryEnterWorld()
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.AwaitingWorldEntry || _runtime == null)
                    return false;
                _state = PlayerSessionState.InWorld;
                return true;
            }
        }

        public bool TryBeginDisconnect()
        {
            lock (_gate)
            {
                if (_state == PlayerSessionState.Disconnecting || _state == PlayerSessionState.Closed)
                    return false;
                _state = PlayerSessionState.Disconnecting;
                return true;
            }
        }

        public bool TryClose()
        {
            lock (_gate)
            {
                if (_state != PlayerSessionState.Disconnecting)
                    return false;
                _state = PlayerSessionState.Closed;
                return true;
            }
        }
    }
}
