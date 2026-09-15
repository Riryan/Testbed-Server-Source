using Game.Server.Application.Connections;
using Game.Server.Application.Sessions;
using Game.Shared.Identity;

namespace Game.Server.Application.World
{
    public readonly struct PlayerWorldLifecycleResult
    {
        public PlayerSessionHandle Session { get; }
        public ConnectionKey Connection => Session.Connection;
        public PlayerSessionId SessionId => Session.SessionId;
        public PlayerWorldLifecycleOperation Operation { get; }
        public PlayerWorldLifecycleStatus Status { get; }
        public bool Success { get; }

        public PlayerWorldLifecycleResult(
            PlayerSessionHandle session,
            PlayerWorldLifecycleOperation operation,
            PlayerWorldLifecycleStatus status,
            bool success)
        {
            Session = session;
            Operation = operation;
            Status = status;
            Success = success;
        }
    }
}
