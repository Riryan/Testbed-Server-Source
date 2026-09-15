using System.Collections.Generic;
using Game.Server.Application.Connections;
using Game.Server.Application.Sessions;
using Game.Shared.Identity;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Authoritative application-side mapping between a session/runtime and the
    /// opaque entity owned by the current world host.
    /// </summary>
    public sealed class PlayerWorldBindingRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ConnectionKey, PlayerWorldBinding> _byConnection = new Dictionary<ConnectionKey, PlayerWorldBinding>();
        private readonly Dictionary<PlayerSessionId, PlayerWorldBinding> _bySession = new Dictionary<PlayerSessionId, PlayerWorldBinding>();
        private readonly Dictionary<CharacterId, PlayerWorldBinding> _byCharacter = new Dictionary<CharacterId, PlayerWorldBinding>();
        private readonly Dictionary<PlayerWorldHandle, PlayerWorldBinding> _byHandle = new Dictionary<PlayerWorldHandle, PlayerWorldBinding>();

        public int Count
        {
            get
            {
                lock (_gate)
                    return _byConnection.Count;
            }
        }

        public bool TryAdd(PlayerWorldBinding binding)
        {
            if (binding == null)
                return false;

            lock (_gate)
            {
                if (_byConnection.ContainsKey(binding.Connection) ||
                    _bySession.ContainsKey(binding.SessionId) ||
                    _byCharacter.ContainsKey(binding.CharacterId) ||
                    _byHandle.ContainsKey(binding.Handle))
                    return false;

                _byConnection.Add(binding.Connection, binding);
                _bySession.Add(binding.SessionId, binding);
                _byCharacter.Add(binding.CharacterId, binding);
                _byHandle.Add(binding.Handle, binding);
                return true;
            }
        }

        public bool TryGet(ConnectionKey connection, out PlayerWorldBinding binding)
        {
            lock (_gate)
                return _byConnection.TryGetValue(connection, out binding);
        }

        /// <summary>
        /// Looks up a binding only when both the transport connection and session
        /// generation match. A stale reconnect generation may never retrieve the
        /// replacement session's world binding.
        /// </summary>
        public bool TryGet(PlayerSessionHandle session, out PlayerWorldBinding binding)
        {
            lock (_gate)
            {
                if (!_bySession.TryGetValue(session.SessionId, out binding) || binding.Connection != session.Connection)
                {
                    binding = null;
                    return false;
                }
                return true;
            }
        }

        public bool TryGet(CharacterId characterId, out PlayerWorldBinding binding)
        {
            lock (_gate)
                return _byCharacter.TryGetValue(characterId, out binding);
        }

        public bool TryGet(PlayerWorldHandle handle, out PlayerWorldBinding binding)
        {
            lock (_gate)
                return _byHandle.TryGetValue(handle, out binding);
        }

        public bool Remove(PlayerWorldBinding binding)
        {
            if (binding == null)
                return false;

            lock (_gate)
            {
                if (!_byConnection.TryGetValue(binding.Connection, out PlayerWorldBinding current) || !ReferenceEquals(current, binding))
                    return false;

                _byConnection.Remove(binding.Connection);
                if (_bySession.TryGetValue(binding.SessionId, out current) && ReferenceEquals(current, binding))
                    _bySession.Remove(binding.SessionId);
                if (_byCharacter.TryGetValue(binding.CharacterId, out current) && ReferenceEquals(current, binding))
                    _byCharacter.Remove(binding.CharacterId);
                if (_byHandle.TryGetValue(binding.Handle, out current) && ReferenceEquals(current, binding))
                    _byHandle.Remove(binding.Handle);
                return true;
            }
        }
    }
}
