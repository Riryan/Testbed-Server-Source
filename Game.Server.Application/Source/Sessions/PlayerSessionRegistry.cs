using System.Collections.Generic;
using Game.Server.Application.Connections;
using Game.Shared.Identity;

namespace Game.Server.Application.Sessions
{
    public sealed class PlayerSessionRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ConnectionKey, PlayerSession> _byConnection = new Dictionary<ConnectionKey, PlayerSession>();
        private readonly Dictionary<AccountId, PlayerSession> _byAccount = new Dictionary<AccountId, PlayerSession>();
        private readonly Dictionary<CharacterId, PlayerSession> _byCharacter = new Dictionary<CharacterId, PlayerSession>();

        public bool TryAdd(PlayerSession session)
        {
            if (session == null)
                return false;

            lock (_gate)
            {
                if (_byConnection.ContainsKey(session.Connection))
                    return false;
                _byConnection.Add(session.Connection, session);
                return true;
            }
        }

        public bool TryBindAccount(PlayerSession session)
        {
            if (session == null || !session.HasAccount)
                return false;

            lock (_gate)
            {
                if (!_byConnection.TryGetValue(session.Connection, out PlayerSession current) || !ReferenceEquals(current, session))
                    return false;
                if (_byAccount.TryGetValue(session.AccountId, out PlayerSession existing) && !ReferenceEquals(existing, session))
                    return false;
                _byAccount[session.AccountId] = session;
                return true;
            }
        }

        public bool TryBindCharacter(PlayerSession session)
        {
            if (session == null || !session.HasSelectedCharacter)
                return false;

            lock (_gate)
            {
                if (!_byConnection.TryGetValue(session.Connection, out PlayerSession current) || !ReferenceEquals(current, session))
                    return false;
                if (_byCharacter.TryGetValue(session.SelectedCharacterId, out PlayerSession existing) && !ReferenceEquals(existing, session))
                    return false;
                _byCharacter[session.SelectedCharacterId] = session;
                return true;
            }
        }

        public bool TryGet(ConnectionKey connection, out PlayerSession session)
        {
            lock (_gate)
                return _byConnection.TryGetValue(connection, out session);
        }

        /// <summary>
        /// Resolves an exact session generation atomically under the registry lock.
        /// This prevents a remove/reopen race on a reused ConnectionKey between
        /// lookup and PlayerSessionId validation.
        /// </summary>
        public bool TryGet(PlayerSessionHandle handle, out PlayerSession session)
        {
            lock (_gate)
            {
                if (!_byConnection.TryGetValue(handle.Connection, out session) || session.SessionId != handle.SessionId)
                {
                    session = null;
                    return false;
                }
                return true;
            }
        }

        public bool ContainsAccount(AccountId accountId)
        {
            lock (_gate)
                return _byAccount.ContainsKey(accountId);
        }

        public bool ContainsCharacter(CharacterId characterId)
        {
            lock (_gate)
                return _byCharacter.ContainsKey(characterId);
        }

        public void UnbindCharacter(PlayerSession session)
        {
            if (session == null || !session.HasSelectedCharacter)
                return;

            lock (_gate)
            {
                if (_byCharacter.TryGetValue(session.SelectedCharacterId, out PlayerSession current) && ReferenceEquals(current, session))
                    _byCharacter.Remove(session.SelectedCharacterId);
            }
        }

        public void UnbindCharacter(CharacterId characterId, PlayerSession session)
        {
            if (!characterId.IsValid || session == null)
                return;

            lock (_gate)
            {
                if (_byCharacter.TryGetValue(characterId, out PlayerSession current) &&
                    ReferenceEquals(current, session))
                {
                    _byCharacter.Remove(characterId);
                }
            }
        }

        public void Remove(PlayerSession session)
        {
            if (session == null)
                return;

            lock (_gate)
            {
                if (_byConnection.TryGetValue(session.Connection, out PlayerSession current) && ReferenceEquals(current, session))
                    _byConnection.Remove(session.Connection);
                if (session.HasAccount && _byAccount.TryGetValue(session.AccountId, out current) && ReferenceEquals(current, session))
                    _byAccount.Remove(session.AccountId);
                if (session.HasSelectedCharacter && _byCharacter.TryGetValue(session.SelectedCharacterId, out current) && ReferenceEquals(current, session))
                    _byCharacter.Remove(session.SelectedCharacterId);
            }
        }

        public PlayerSession[] SnapshotSessions()
        {
            lock (_gate)
            {
                var result = new PlayerSession[_byConnection.Count];
                int i = 0;
                foreach (PlayerSession session in _byConnection.Values)
                    result[i++] = session;
                return result;
            }
        }
    }
}
