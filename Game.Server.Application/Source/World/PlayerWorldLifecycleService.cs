using System;
using System.Collections.Generic;
using Game.Server.Application.Connections;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Sessions;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Bounded, tick-consumed world lifecycle queue. Network callbacks and async
    /// persistence completions may request work, but authoritative world insertion
    /// and removal happen only when TryProcessNext is called by the server tick.
    /// Queued work is generation-bound so reused transport connection IDs cannot
    /// redirect stale work into a replacement session.
    /// </summary>
    public sealed class PlayerWorldLifecycleService
    {
        private readonly object _queueGate = new object();
        private readonly Queue<WorldCommand> _commands;
        private readonly HashSet<PlayerSessionHandle> _pendingEnter = new HashSet<PlayerSessionHandle>();
        private readonly HashSet<PlayerSessionHandle> _pendingLeave = new HashSet<PlayerSessionHandle>();
        private readonly int _maxPendingCommands;
        private readonly PlayerSessionService _sessions;
        private readonly PlayerWorldBindingRegistry _bindings;
        private readonly IPlayerWorldAdapter _adapter;

        private readonly struct WorldCommand
        {
            public PlayerSessionHandle Session { get; }
            public PlayerWorldLifecycleOperation Operation { get; }

            public WorldCommand(PlayerSessionHandle session, PlayerWorldLifecycleOperation operation)
            {
                Session = session;
                Operation = operation;
            }
        }

        public PlayerWorldLifecycleService(
            PlayerSessionService sessions,
            PlayerWorldBindingRegistry bindings,
            IPlayerWorldAdapter adapter,
            int maxPendingCommands = 4096)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            if (maxPendingCommands <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPendingCommands));

            _maxPendingCommands = maxPendingCommands;
            _commands = new Queue<WorldCommand>(Math.Min(maxPendingCommands, 256));
        }

        public int PendingCommandCount
        {
            get
            {
                lock (_queueGate)
                    return _commands.Count;
            }
        }

        public int ActiveBindingCount => _bindings.Count;

        public bool TryQueueEnter(ConnectionKey connection)
        {
            return _sessions.TryGetSession(connection, out PlayerSession session) && TryQueueEnter(session.Handle);
        }

        public bool TryQueueEnter(PlayerSessionHandle handle)
        {
            if (!_sessions.TryGetSession(handle, out PlayerSession session) ||
                session.State != PlayerSessionState.AwaitingWorldEntry ||
                session.Runtime == null ||
                _bindings.TryGet(handle, out _) ||
                HasDifferentGenerationBinding(handle.Connection, handle.SessionId))
                return false;

            lock (_queueGate)
            {
                if (_commands.Count >= _maxPendingCommands || _pendingEnter.Contains(handle))
                    return false;

                _pendingEnter.Add(handle);
                _commands.Enqueue(new WorldCommand(handle, PlayerWorldLifecycleOperation.Enter));
                return true;
            }
        }

        public bool TryQueueLeave(ConnectionKey connection)
        {
            return _sessions.TryGetSession(connection, out PlayerSession session) && TryQueueLeave(session.Handle);
        }

        public bool TryQueueLeave(PlayerSessionHandle handle)
        {
            if (!_sessions.TryGetSession(handle, out PlayerSession session) ||
                session.State != PlayerSessionState.Disconnecting)
                return false;

            lock (_queueGate)
            {
                if (_commands.Count >= _maxPendingCommands || _pendingLeave.Contains(handle))
                    return false;

                _pendingLeave.Add(handle);
                _commands.Enqueue(new WorldCommand(handle, PlayerWorldLifecycleOperation.Leave));
                return true;
            }
        }

        /// <summary>
        /// Processes exactly one queued command. Call this from the authoritative
        /// server tick/scheduler, repeatedly up to that tick's explicit lifecycle budget.
        /// </summary>
        public bool TryProcessNext(out PlayerWorldLifecycleResult result)
        {
            WorldCommand command;
            lock (_queueGate)
            {
                if (_commands.Count == 0)
                {
                    result = default(PlayerWorldLifecycleResult);
                    return false;
                }

                command = _commands.Dequeue();
                if (command.Operation == PlayerWorldLifecycleOperation.Enter)
                    _pendingEnter.Remove(command.Session);
                else
                    _pendingLeave.Remove(command.Session);
            }

            result = command.Operation == PlayerWorldLifecycleOperation.Enter
                ? ProcessEnter(command.Session)
                : ProcessLeave(command.Session);
            return true;
        }

        public int ProcessTick(int maxCommands)
        {
            if (maxCommands <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCommands));

            int processed = 0;
            while (processed < maxCommands && TryProcessNext(out _))
                processed++;
            return processed;
        }

        /// <summary>
        /// Adopts a world entity that was created by an external host through its own
        /// canonical lifecycle. This is intended for integrations such as the current
        /// LiteNetLib Ready/SpawnPlayer path, where the networking layer must remain the
        /// owner of network-object creation and connection bookkeeping.
        ///
        /// This method never calls IPlayerWorldAdapter.TryEnter/TryLeave. The caller owns
        /// the externally-created shell and MUST roll it back if this method returns
        /// EnterCommitFailedExternalRollbackRequired.
        /// </summary>
        public PlayerWorldLifecycleResult AdoptExternalEnter(
            PlayerSessionHandle handle,
            PlayerWorldHandle worldHandle)
        {
            if (!_sessions.TryGetSession(handle, out PlayerSession session))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterStaleSessionRejected, false);

            if (!worldHandle.IsValid || !session.TryGetAwaitingWorldEntryRuntime(out PlayerRuntime runtime))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterRejected, false);

            if (_bindings.TryGet(handle, out _) || HasDifferentGenerationBinding(handle.Connection, handle.SessionId))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterBindingFailed, false);

            var binding = new PlayerWorldBinding(handle.Connection, runtime, worldHandle);
            if (!_bindings.TryAdd(binding))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterBindingFailed, false);

            if (!_sessions.CommitWorldEntry(handle))
            {
                _bindings.Remove(binding);
                return Result(
                    handle,
                    PlayerWorldLifecycleOperation.Enter,
                    PlayerWorldLifecycleStatus.EnterCommitFailedExternalRollbackRequired,
                    false);
            }

            return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.Entered, true);
        }

        /// <summary>
        /// Completes application-side world removal after an external host has already
        /// captured/removed its canonical world shell. The optional location must be a
        /// final authoritative location captured before host-side destruction. No adapter
        /// call is made here, so network/player ownership remains entirely with the host.
        /// </summary>
        public PlayerWorldLifecycleResult CompleteExternalLeave(
            PlayerSessionHandle handle,
            CharacterLocationState? finalLocation = null)
        {
            if (!_sessions.TryGetSession(handle, out PlayerSession session))
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveStaleSessionRejected, false);

            if (session.State != PlayerSessionState.Disconnecting)
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveRejected, false);

            if (!_bindings.TryGet(handle, out PlayerWorldBinding binding))
            {
                if (HasDifferentGenerationBinding(handle.Connection, handle.SessionId))
                    return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveStaleSessionRejected, false);

                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeftWithoutBinding, true);
            }

            if (finalLocation.HasValue)
                binding.Runtime.UpdateLocation(finalLocation.Value);

            if (!_bindings.Remove(binding))
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveBindingFailed, false);

            return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.Left, true);
        }

        private PlayerWorldLifecycleResult ProcessEnter(PlayerSessionHandle handle)
        {
            if (!_sessions.TryGetSession(handle, out PlayerSession session))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterStaleSessionRejected, false);

            if (!session.TryGetAwaitingWorldEntryRuntime(out PlayerRuntime runtime))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterRejected, false);

            if (_bindings.TryGet(handle, out _) || HasDifferentGenerationBinding(handle.Connection, handle.SessionId))
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterBindingFailed, false);

            if (!_adapter.TryEnter(runtime, handle.Connection, out PlayerWorldHandle worldHandle) || !worldHandle.IsValid)
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterAdapterFailed, false);

            var binding = new PlayerWorldBinding(handle.Connection, runtime, worldHandle);
            if (!_bindings.TryAdd(binding))
            {
                _adapter.TryLeave(worldHandle);
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterBindingFailed, false);
            }

            // Fail closed: a world entity is not considered committed until this
            // exact session generation transitions to InWorld. If that transition
            // loses a race to disconnect/session replacement, roll the entity back.
            if (!_sessions.CommitWorldEntry(handle))
            {
                _bindings.Remove(binding);
                _adapter.TryLeave(worldHandle);
                return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.EnterCommitFailedRolledBack, false);
            }

            return Result(handle, PlayerWorldLifecycleOperation.Enter, PlayerWorldLifecycleStatus.Entered, true);
        }

        private PlayerWorldLifecycleResult ProcessLeave(PlayerSessionHandle handle)
        {
            if (!_sessions.TryGetSession(handle, out PlayerSession session))
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveStaleSessionRejected, false);

            if (session.State != PlayerSessionState.Disconnecting)
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveRejected, false);

            if (!_bindings.TryGet(handle, out PlayerWorldBinding binding))
            {
                // If another generation owns a binding on the same transport connection,
                // this command is stale and must not be treated as a successful no-op.
                if (HasDifferentGenerationBinding(handle.Connection, handle.SessionId))
                    return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveStaleSessionRejected, false);

                // A disconnect may win the race before an AwaitingWorldEntry command
                // ever spawned anything. That is a successful no-op world removal.
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeftWithoutBinding, true);
            }

            // Capture final authoritative location before removing the world shell.
            // Failure to read does not invent a position; the runtime keeps the last
            // authoritative location it already had.
            if (_adapter.TryReadLocation(binding.Handle, out CharacterLocationState location))
                binding.Runtime.UpdateLocation(location);

            if (!_adapter.TryLeave(binding.Handle))
                return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.LeaveAdapterFailed, false);

            _bindings.Remove(binding);
            return Result(handle, PlayerWorldLifecycleOperation.Leave, PlayerWorldLifecycleStatus.Left, true);
        }

        private bool HasDifferentGenerationBinding(ConnectionKey connection, Game.Shared.Identity.PlayerSessionId sessionId)
        {
            return _bindings.TryGet(connection, out PlayerWorldBinding binding) && binding.SessionId != sessionId;
        }

        private static PlayerWorldLifecycleResult Result(
            PlayerSessionHandle session,
            PlayerWorldLifecycleOperation operation,
            PlayerWorldLifecycleStatus status,
            bool success) =>
            new PlayerWorldLifecycleResult(session, operation, status, success);
    }
}
