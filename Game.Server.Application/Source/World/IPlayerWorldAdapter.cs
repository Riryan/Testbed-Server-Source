using Game.Server.Application.Connections;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Boundary between portable authoritative game/session code and the current
    /// world host. Today the implementation may be Unity headless; the future
    /// implementation may be a pure .NET world server.
    /// </summary>
    public interface IPlayerWorldAdapter
    {
        /// <summary>
        /// Insert/spawn the runtime into the authoritative world. Called only from
        /// the authoritative world-tick boundary. Returning false must leave no
        /// externally visible player entity behind.
        /// </summary>
        bool TryEnter(PlayerRuntime runtime, ConnectionKey connection, out PlayerWorldHandle handle);

        /// <summary>
        /// Capture the latest authoritative world location before removal.
        /// Returns false when no newer location can be obtained; callers retain
        /// the runtime's last known authoritative location in that case.
        /// </summary>
        bool TryReadLocation(PlayerWorldHandle handle, out CharacterLocationState location);

        /// <summary>
        /// Remove/despawn the authoritative world entity. Implementations should be
        /// idempotent when practical so disconnect cleanup can be safely retried.
        /// </summary>
        bool TryLeave(PlayerWorldHandle handle);
    }
}
