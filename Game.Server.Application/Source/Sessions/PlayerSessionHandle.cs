using System;
using Game.Server.Application.Connections;
using Game.Shared.Identity;

namespace Game.Server.Application.Sessions
{
    /// <summary>
    /// Identifies one exact session generation on a transport connection.
    /// Connection IDs may be reused after reconnect, so authoritative work that
    /// can outlive a callback must retain this handle rather than ConnectionKey alone.
    /// </summary>
    public readonly struct PlayerSessionHandle : IEquatable<PlayerSessionHandle>
    {
        public ConnectionKey Connection { get; }
        public PlayerSessionId SessionId { get; }
        public bool IsValid => Connection.IsValid && SessionId.IsValid;

        public PlayerSessionHandle(ConnectionKey connection, PlayerSessionId sessionId)
        {
            if (!connection.IsValid)
                throw new ArgumentException("Connection is invalid.", nameof(connection));
            if (!sessionId.IsValid)
                throw new ArgumentException("SessionId is invalid.", nameof(sessionId));

            Connection = connection;
            SessionId = sessionId;
        }

        public bool Equals(PlayerSessionHandle other) =>
            Connection == other.Connection && SessionId == other.SessionId;

        public override bool Equals(object obj) =>
            obj is PlayerSessionHandle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Connection.GetHashCode() * 397) ^ SessionId.GetHashCode();
            }
        }

        public override string ToString() => $"{Connection}/{SessionId}";

        public static bool operator ==(PlayerSessionHandle left, PlayerSessionHandle right) => left.Equals(right);
        public static bool operator !=(PlayerSessionHandle left, PlayerSessionHandle right) => !left.Equals(right);
    }
}
