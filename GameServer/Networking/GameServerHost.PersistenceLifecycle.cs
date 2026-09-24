using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Game.GameServer.Runtime;
using Game.GameServer.Backend;
using Game.GameServer.Replication;
using Game.Server.Application.Abilities;
using Game.Server.Application.Connections;
using Game.Server.Application.Population;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using Game.Shared.World;
using Game.Shared.WorldItems;
using Game.UnityIntegration;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    // Host-side persistence lifecycle integration: checkpoint scheduling, lease maintenance,
    // disconnect finalization, and graceful shutdown coordination. Durable writes remain
    // owned by the existing persistence services/repositories.
    private void ScheduleAuthenticationSweep()
    {
        if (!_running || !_scheduler.IsRunning)
            return;

        _authenticationSweepTask = _scheduler.Schedule(AuthenticationSweepIntervalSeconds, () =>
        {
            SweepAuthenticationDeadlines();
            ScheduleAuthenticationSweep();
        });
    }

    private void ScheduleCharacterCheckpoint()
    {
        if (!_running || !_scheduler.IsRunning)
            return;

        _characterCheckpointTask = _scheduler.Schedule(_options.CharacterCheckpointSweepIntervalSeconds, () =>
        {
            if (!_checkpointInFlight && _runtime.DirtyPlayers.DirtyCount > 0)
            {
                _checkpointInFlight = true;
                _checkpointDrainTask = SaveCheckpointDrainAsync();
                _checkpointDrainTask.Forget();
            }
            ScheduleCharacterCheckpoint();
        });
    }

    /// <summary>
    /// Character authority is persistence-critical and must not depend on the budgeted
    /// gameplay delayed-task scheduler. A single wall-clock loop renews all held leases
    /// in batches. This adds no client/gameplay wire traffic and prevents active-session
    /// equipment/inventory authority from expiring because a scheduler callback was delayed.
    /// </summary>
    private void StartCharacterLeaseRenewalLoop(CancellationToken shutdownToken)
    {
        if (_characterLeaseRenewalLoopTask != null)
            return;

        _characterLeaseRenewalCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        CancellationToken token = _characterLeaseRenewalCancellation.Token;
        _characterLeaseRenewalLoopTask = Task.Run(() => CharacterLeaseRenewalLoopAsync(token), token);
    }

    private void StopCharacterLeaseRenewalLoop()
    {
        CancellationTokenSource cancellation = _characterLeaseRenewalCancellation;
        if (cancellation == null)
            return;

        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task CharacterLeaseRenewalLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(CharacterLeaseRenewalIntervalSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    break;

                if (_runtime.Leases.HeldCount <= 0)
                    continue;

                _leaseRenewalDrainTask = RenewCharacterLeasesAsync(cancellationToken);
                await _leaseRenewalDrainTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
        catch (Exception ex)
        {
            // A lease-maintenance loop may never silently die. Surface the fault and
            // fail every locally-held session closed before persistence authority expires.
            Console.Error.WriteLine($"Character lease maintenance loop stopped unexpectedly: {ex.Message}");
            QueueMainThreadCompletion(FailClosedAllHeldLeaseSessions, MainThreadCompletionPriority.Critical);
        }
    }

    private async Task RenewCharacterLeasesAsync(CancellationToken cancellationToken)
    {
        BackendCharacterLeaseService.RenewalResult result = null;
        Exception failure = null;
        try
        {
            result = await _runtime.Leases
                .RenewHeldAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            if (failure != null)
            {
                Console.Error.WriteLine($"Character lease renewal failed: {failure.Message}");
                FailClosedAllHeldLeaseSessions();
                return;
            }

            if (result == null)
                return;

            if (!result.BackendAvailable)
                Console.Error.WriteLine("Character lease renewal could not reach the Backend; near-expiry sessions will fail closed.");

            PlayerSessionId[] atRisk = result.AtRiskSessions;
            for (int i = 0; i < atRisk.Length; ++i)
                FailClosedLeaseSession(atRisk[i]);
        }, MainThreadCompletionPriority.Critical);
    }

    private void FailClosedAllHeldLeaseSessions()
    {
        foreach (ClientSession session in _sessions.Values)
        {
            if (session == null || !session.Connected)
                continue;

            if (!TryGetAuthoritativeSession(session, out PlayerSession authoritative) ||
                !authoritative.HasSelectedCharacter)
            {
                continue;
            }

            // Any selected-character session depends on this maintenance loop for
            // persistence fencing. If the loop itself dies unexpectedly, do not leave a
            // playable character alive long enough to cross an unknown lease boundary.
            FailClosedLeaseSession(session.SessionHandle.SessionId);
        }
    }

    private void FailClosedLeaseSession(PlayerSessionId sessionId)
    {
        if (!sessionId.IsValid)
            return;

        foreach (ClientSession session in _sessions.Values)
        {
            if (session == null ||
                session.SessionHandle.SessionId != sessionId ||
                !session.Connected)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"Character lease authority lost; disconnecting peer={session.Peer.Id}, session={sessionId}.");
            // Flip authority immediately. LiteNetLib disconnect completion performs the
            // existing generation-safe finalization/lease release path.
            session.Connected = false;
            session.Peer.Disconnect();
            return;
        }
    }

    private async Task SaveCheckpointDrainAsync()
    {
        int saved = 0;
        int batches = 0;
        Exception failure = null;
        try
        {
            while (batches < _options.CharacterCheckpointMaxBatchesPerCycle &&
                   _runtime.DirtyPlayers.DirtyCount > 0)
            {
                int batchSaved = await _runtime.Saves
                    .SaveDueBatchAsync(
                        _runtime.DirtyPlayers,
                        _options.CharacterCheckpointBatchSize,
                        DateTime.UtcNow,
                        TimeSpan.FromSeconds(_options.CharacterCheckpointPreferredAgeSeconds),
                        TimeSpan.FromSeconds(_options.CharacterCheckpointMaximumAgeSeconds),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                batches++;
                saved += batchSaved;
                if (batchSaved <= 0)
                    break;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        QueueMainThreadCompletion(() =>
        {
            _checkpointInFlight = false;
            if (failure != null)
                Console.Error.WriteLine($"Character checkpoint failed after {batches} batch(es), {saved} save(s): {failure.Message}");
        }, MainThreadCompletionPriority.Critical);
    }

    private void BeginSessionDisconnect(ClientSession session)
    {
        if (session == null)
            return;

        ServerPlayerEntity entity = session.Entity;
        if (entity != null)
        {
            // InteractionSessionService already owns the canonical disconnect cleanup path.
            // Run it while the ready-session/entity indexes are still intact so any peer
            // cancellation/occupancy release observes the same authoritative participant.
            _runtime.InteractionSessions.NotifyDisconnect(entity.Runtime.CharacterId.Value);
            CloseSocialEconomyForCharacter(entity.Runtime.CharacterId.Value);
            DeactivatePlayerGameplayRuntime(session, entity.Runtime);
        }
        UnregisterReadySessionIndexes(session);
        // Tear down interest edges while the target entity still exists so removal deltas
        // carry the authoritative object id. The disconnecting client itself is not notified.
        if (entity != null)
        {
            RemovePopulationObserver(session);
            ApplyInterestChanges(_worldInterest.Unregister(session));
            _worldItemInterest.Unregister(session);
        }

        RemoveSnapshotBatch(session);
        session.Entity = null;
        session.Ready = false;

        _runtime.SessionService.BeginDisconnect(session.SessionHandle);
        if (entity != null)
        {
            CharacterLocationState finalLocation = entity.CaptureLocation();
            _runtime.WorldLifecycle.CompleteExternalLeave(session.SessionHandle, finalLocation);
        }
        else
        {
            _runtime.WorldLifecycle.CompleteExternalLeave(session.SessionHandle);
        }

        TrackSessionFinalizer(FinalizeDisconnectedSessionAsync(session.SessionHandle));
    }

    private void TrackSessionFinalizer(Task task)
    {
        if (task == null)
            return;

        lock (_finalizerGate)
            _sessionFinalizers.Add(task);

        _ = task.ContinueWith(
            completed =>
            {
                lock (_finalizerGate)
                    _sessionFinalizers.Remove(completed);
                if (completed.IsFaulted)
                    Console.Error.WriteLine(completed.Exception);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private int SessionFinalizerCount
    {
        get
        {
            lock (_finalizerGate)
                return _sessionFinalizers.Count;
        }
    }

    private async Task FinalizeDisconnectedSessionAsync(PlayerSessionHandle handle)
    {
        // Resolve this exact session generation while still on the authoritative thread.
        // The retained runtime remains tracked and its character lease remains held until
        // SessionService.Close is explicitly queued after a successful (or unnecessary)
        // final save.
        if (!_runtime.SessionService.TryGetSession(handle, out PlayerSession session))
            return;

        PlayerRuntime runtime = session.Runtime;
        if (runtime == null)
        {
            QueueMainThreadCompletion(
                () => _runtime.SessionService.Close(handle),
                MainThreadCompletionPriority.Critical);
            return;
        }

        const int initialRetryDelayMilliseconds = 500;
        const int maximumRetryDelayMilliseconds = 15_000;
        const int maximumRetryJitterMilliseconds = 500;

        int retryDelayMilliseconds = initialRetryDelayMilliseconds;
        int failedAttempts = 0;

        while (true)
        {
            try
            {
                await _runtime.Saves.SaveAsync(runtime, CancellationToken.None).ConfigureAwait(false);

                // Close is the canonical ownership transition: it untracks the runtime,
                // releases the local character lease, and removes the session. Never reach
                // it after a failed final save.
                QueueMainThreadCompletion(
                    () => _runtime.SessionService.Close(handle),
                    MainThreadCompletionPriority.Critical);
                return;
            }
            catch (Exception ex)
            {
                failedAttempts++;

                // Stagger retained disconnect retries so a Backend/database outage followed
                // by recovery does not make many disconnected sessions retry in lockstep.
                int jitterMilliseconds = Random.Shared.Next(0, maximumRetryJitterMilliseconds + 1);
                int waitMilliseconds = retryDelayMilliseconds + jitterMilliseconds;
                Console.Error.WriteLine(
                    $"Final character save failed for {handle} (attempt {failedAttempts}); " +
                    $"retaining runtime/lease and retrying in {waitMilliseconds} ms: {ex.Message}");

                await Task.Delay(waitMilliseconds).ConfigureAwait(false);
                retryDelayMilliseconds = Math.Min(
                    retryDelayMilliseconds * 2,
                    maximumRetryDelayMilliseconds);
            }
        }
    }

    private void GracefulShutdown()
    {
        var sessions = _sessions.Values.ToArray();
        foreach (ClientSession session in sessions)
        {
            if (session.Connected)
            {
                session.Connected = false;
                BeginSessionDisconnect(session);
            }
        }

        // Phase 1: let any in-flight checkpoint and every generation-safe session finalizer
        // finish while continuing to execute their authoritative main-thread completions.
        DateTime finalizerDeadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < finalizerDeadline)
        {
            DrainMainThreadCompletions();

            bool checkpointDone = _checkpointDrainTask == null || _checkpointDrainTask.IsCompleted;
            bool leaseRenewalDone = _leaseRenewalDrainTask == null || _leaseRenewalDrainTask.IsCompleted;
            if (checkpointDone && leaseRenewalDone && SessionFinalizerCount == 0 && _mainThreadCompletions.Count == 0)
                break;

            Thread.Sleep(10);
        }

        DrainMainThreadCompletions();

        bool leaseRenewalStillRunning = _leaseRenewalDrainTask != null && !_leaseRenewalDrainTask.IsCompleted;
        if (leaseRenewalStillRunning)
            Console.Error.WriteLine("Shutdown timed out waiting for in-flight character lease renewal.");

        bool checkpointStillRunning = _checkpointDrainTask != null && !_checkpointDrainTask.IsCompleted;
        bool finalizersStillRunning = SessionFinalizerCount > 0;
        if (checkpointStillRunning || finalizersStillRunning)
        {
            // Never start a second persistence drain while another authoritative save path
            // is still active. Revision fencing is retained as a final safety net, but clean
            // shutdown should avoid knowingly creating overlapping writers.
            Console.Error.WriteLine(
                $"Shutdown persistence work is still in flight (checkpoint={checkpointStillRunning}, " +
                $"finalizers={SessionFinalizerCount}); skipping overlapping synchronous dirty drain.");
        }
        else
        {
            try
            {
                int guard = 0;
                while (_runtime.DirtyPlayers.DirtyCount > 0 && guard++ < 64)
                {
                    int saved = _runtime.Saves
                        .SaveDirtyBatchAsync(
                            _runtime.DirtyPlayers,
                            _options.CharacterCheckpointBatchSize,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    if (saved <= 0)
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Shutdown character checkpoint failed: {ex.Message}");
            }
        }

        // Session finalizers enqueue SessionService.Close, and Close is the canonical point
        // that removes the local lease before issuing its best-effort Backend release.
        for (int i = 0; i < 8 && _mainThreadCompletions.Count > 0; ++i)
            DrainMainThreadCompletions();

        if (SessionFinalizerCount > 0)
            Console.Error.WriteLine($"Shutdown timed out with {SessionFinalizerCount} character finalizer(s) still running.");

        try
        {
            bool releasesDrained = _runtime.Leases
                .DrainPendingReleasesAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!releasesDrained)
                Console.Error.WriteLine("Shutdown timed out draining pending character lease releases; Backend TTL remains the crash-safety fallback.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Shutdown lease-release drain failed: {ex.Message}");
        }
    }
}
