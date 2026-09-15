using System;
using Game.Server.Application.Connections;
using Game.Server.Domain.Players;
using Game.Shared.Identity;

namespace Game.Server.Application.World
{
    public sealed class PlayerWorldBinding
    {
        public ConnectionKey Connection { get; }
        public PlayerSessionId SessionId { get; }
        public CharacterId CharacterId { get; }
        public PlayerRuntime Runtime { get; }
        public PlayerWorldHandle Handle { get; }

        public PlayerWorldBinding(
            ConnectionKey connection,
            PlayerRuntime runtime,
            PlayerWorldHandle handle)
        {
            if (runtime == null)
                throw new ArgumentNullException(nameof(runtime));
            if (!handle.IsValid)
                throw new ArgumentException("World handle is invalid.", nameof(handle));

            Connection = connection;
            SessionId = runtime.SessionId;
            CharacterId = runtime.CharacterId;
            Runtime = runtime;
            Handle = handle;
        }
    }
}
