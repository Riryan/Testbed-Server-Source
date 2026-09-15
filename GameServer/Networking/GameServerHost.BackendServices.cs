using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using Game.Shared.Backend;
using Game.Shared.Content;
using LiteNetLib;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private CancellationTokenSource _backendEventsCancellation;
    private Task _backendEventsTask;
    private int _backendAvailabilityState = -1;
    private string _backendAvailabilityMessage = string.Empty;
    private long _pendingGameplayContentRevision;
    private int _contentRefreshQueued;
    private bool _contentRefreshInFlight;
    private bool _backendAvailabilitySubscribed;

    private void StartBackendEventStream(CancellationToken hostCancellationToken)
    {
        if (_backendEventsTask != null)
            return;

        if (!_backendAvailabilitySubscribed)
        {
            _runtime.Backend.AvailabilityChanged += OnBackendAvailabilityChanged;
            _backendAvailabilitySubscribed = true;

            bool? current = _runtime.Backend.IsAvailable;
            if (current.HasValue)
            {
                SetBackendAvailability(
                    current.Value,
                    current.Value
                        ? "Backend services connected."
                        : "Backend persistence services are temporarily unavailable.");
            }
        }

        _backendEventsCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        _backendEventsTask = RunBackendEventsAsync(_backendEventsCancellation.Token);
    }

    private void StopBackendEventStream()
    {
        CancellationTokenSource cancellation = _backendEventsCancellation;
        Task task = _backendEventsTask;
        _backendEventsCancellation = null;
        _backendEventsTask = null;

        if (_backendAvailabilitySubscribed)
        {
            _runtime.Backend.AvailabilityChanged -= OnBackendAvailabilityChanged;
            _backendAvailabilitySubscribed = false;
        }

        if (cancellation == null)
            return;

        cancellation.Cancel();
        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Backend event stream shutdown: {ex.Message}");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunBackendEventsAsync(CancellationToken cancellationToken)
    {
        int reconnectDelayMilliseconds = 1000;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _runtime.Backend.RunContentRevisionEventStreamAsync(
                    revision =>
                    {
                        QueueGameplayContentRevision(revision);
                    },
                    cancellationToken).ConfigureAwait(false);

                // Losing only the content event stream does not prove that persistence
                // requests are unavailable. The canonical BackendInternalClient reports
                // actual internal API request success/failure separately.
                Console.Error.WriteLine("Backend content event stream ended. Reconnecting...");
                reconnectDelayMilliseconds = 1000;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Backend content event stream disconnected: {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await Task.Delay(reconnectDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            reconnectDelayMilliseconds = Math.Min(reconnectDelayMilliseconds * 2, 10000);
        }
    }

    private void OnBackendAvailabilityChanged(bool available, string message) =>
        QueueBackendAvailability(available, message);

    private bool IsBackendPersistenceMutationAvailable => _backendAvailabilityState != 0;

    private const string BackendPersistenceUnavailableMessage =
        "backend persistence is temporarily unavailable";

    private void QueueBackendAvailability(bool available, string message)
    {
        _mainThreadCompletions.Enqueue(() => SetBackendAvailability(available, message));
    }

    private void SetBackendAvailability(bool available, string message)
    {
        int next = available ? 1 : 0;
        string prepared = message ?? string.Empty;
        if (_backendAvailabilityState == next &&
            string.Equals(_backendAvailabilityMessage, prepared, StringComparison.Ordinal))
        {
            return;
        }

        _backendAvailabilityState = next;
        _backendAvailabilityMessage = prepared;

        var status = new PlayerServiceStatusMessage
        {
            service = (byte)PlayerServiceKind.Backend,
            available = available,
            message = prepared,
        };

        foreach (ClientSession session in _sessions.Values)
        {
            if (!IsCurrent(session))
                continue;
            SendClientMessage(session, PlayerServiceStatusMessageTypes.Status, status, DeliveryMethod.ReliableOrdered);
        }
    }

    private void SendCurrentBackendStatus(ClientSession session)
    {
        if (_backendAvailabilityState < 0 || !IsCurrent(session))
            return;

        SendClientMessage(
            session,
            PlayerServiceStatusMessageTypes.Status,
            new PlayerServiceStatusMessage
            {
                service = (byte)PlayerServiceKind.Backend,
                available = _backendAvailabilityState == 1,
                message = _backendAvailabilityMessage ?? string.Empty,
            },
            DeliveryMethod.ReliableOrdered);
    }

    private void QueueGameplayContentRevision(long revision)
    {
        if (revision <= _runtime.Content.Revision)
            return;

        long observed = Interlocked.Read(ref _pendingGameplayContentRevision);
        while (revision > observed)
        {
            long original = Interlocked.CompareExchange(
                ref _pendingGameplayContentRevision,
                revision,
                observed);
            if (original == observed)
                break;
            observed = original;
        }

        // Every backend event/reconnect is allowed to trigger one reconciliation attempt.
        // This is event-driven revision recovery, not content polling.
        if (Interlocked.Exchange(ref _contentRefreshQueued, 1) == 0)
        {
            _mainThreadCompletions.Enqueue(() =>
            {
                Interlocked.Exchange(ref _contentRefreshQueued, 0);
                BeginPendingGameplayContentRefresh();
            });
        }
    }

    private void BeginPendingGameplayContentRefresh()
    {
        if (_contentRefreshInFlight)
            return;

        long announcedRevision = Interlocked.Read(ref _pendingGameplayContentRevision);
        if (announcedRevision <= _runtime.Content.Revision)
            return;

        _contentRefreshInFlight = true;
        RefreshGameplayContentAsync(announcedRevision).Forget();
    }

    private async Task RefreshGameplayContentAsync(long announcedRevision)
    {
        BackendGameplayContentResponse response = null;
        Exception failure = null;
        try
        {
            response = await _runtime.Backend.GetGameplayContentAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        _mainThreadCompletions.Enqueue(() =>
        {
            _contentRefreshInFlight = false;

            if (failure != null)
            {
                Console.Error.WriteLine(
                    $"Gameplay content refresh for announced revision {announcedRevision} failed: {failure.Message}");
                return;
            }

            GameplayContentSnapshot candidate = response?.content;
            if (response == null || !response.success || candidate == null)
            {
                Console.Error.WriteLine(
                    $"Gameplay content refresh for announced revision {announcedRevision} returned no usable content.");
                ClearPendingGameplayContentRevisionUpTo(announcedRevision);
                return;
            }

            if (candidate.revision <= _runtime.Content.Revision)
            {
                ClearPendingGameplayContentRevisionUpTo(candidate.revision);
                return;
            }

            GameplayContentSnapshot current = _runtime.Content.Snapshot;
            if (!GameplayContentValidation.TryValidateCompatibleUpdate(current, candidate, out string error))
            {
                Console.Error.WriteLine(
                    $"Rejected live gameplay content revision {candidate.revision}: {error}");
                ClearPendingGameplayContentRevisionUpTo(candidate.revision);
                return;
            }

            if (!_runtime.Content.Replace(candidate))
            {
                Console.Error.WriteLine($"Gameplay content revision {candidate.revision} was not activated.");
                ClearPendingGameplayContentRevisionUpTo(candidate.revision);
                return;
            }

            MovementRulesDefinition movement = _runtime.Content.GetMovementRules();
            foreach (ClientSession session in _sessions.Values)
            {
                if (!TryGetGameplayRuntime(session, out PlayerRuntime runtime))
                    continue;

                session.Entity?.ApplyMovementSettings(movement);
                MarkPlayerSimulationDirty(session);
                _runtime.PlayerItems.RecalculateStatsForContentChange(runtime);
                _runtime.Resources.RecalculateMaximums(runtime);
                _runtime.StatusEffects.ReconcileDefinitions(runtime);
                _resourceScheduler.NotifyDefinitionsChanged(runtime);
                _combatStateScheduler.NotifyDefinitionsChanged(runtime);
                _statusEffectScheduler.NotifyDefinitionsChanged(runtime);
            }

            // Advance every ready client's gameplay-settings revision, even when the
            // content edit changed only server-side/catalog data. The delta can have an
            // empty mask; this keeps later client-safe tuning deltas contiguous.
            BroadcastGameplaySettingsDelta(current, candidate);

            ClearPendingGameplayContentRevisionUpTo(candidate.revision);
            Console.WriteLine($"Activated gameplay content revision {candidate.revision} from BackendServer event stream.");

            if (Interlocked.Read(ref _pendingGameplayContentRevision) > _runtime.Content.Revision)
                QueueGameplayContentRevision(Interlocked.Read(ref _pendingGameplayContentRevision));
        });
    }

    private void ClearPendingGameplayContentRevisionUpTo(long appliedOrRejectedRevision)
    {
        long observed = Interlocked.Read(ref _pendingGameplayContentRevision);
        while (observed > 0 && observed <= appliedOrRejectedRevision)
        {
            long original = Interlocked.CompareExchange(
                ref _pendingGameplayContentRevision,
                0,
                observed);
            if (original == observed)
                return;
            observed = original;
        }
    }
}
