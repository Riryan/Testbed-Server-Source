using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Backend;
using Game.Shared.Identity;

namespace Game.UnityIntegration.Backend
{
    /// <summary>
    /// Internal game-server client for the standalone backend. The endpoint is
    /// intended to be loopback-only in the single-machine deployment and protected
    /// by a random game-server key. No password material is handled here.
    /// </summary>
    public sealed partial class BackendInternalClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClient _eventHttp;
        private readonly string _gameServerKey;
        private int _availabilityState = -1;

        public string BaseUrl { get; }
        public bool? IsAvailable
        {
            get
            {
                int state = Volatile.Read(ref _availabilityState);
                return state < 0 ? (bool?)null : state == 1;
            }
        }

        public event Action<bool, string> AvailabilityChanged;

        public BackendInternalClient(
            string baseUrl,
            string gameServerKey,
            TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Backend internal URL is required.", nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(gameServerKey))
                throw new ArgumentException("Backend game-server key is required.", nameof(gameServerKey));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            BaseUrl = baseUrl.TrimEnd('/');
            _gameServerKey = gameServerKey.Trim();
            _http = new HttpClient { Timeout = timeout };
            _eventHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task<AccountId> RedeemAdmissionAsync(
            string token,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
                return default(AccountId);

            BackendAdmissionRedeemResponse response = await PostAsync<BackendAdmissionRedeemRequest, BackendAdmissionRedeemResponse>(
                "/v1/internal/admissions/redeem",
                new BackendAdmissionRedeemRequest { token = token },
                cancellationToken).ConfigureAwait(false);

            return response != null && response.success && response.accountId > 0
                ? new AccountId(response.accountId)
                : default(AccountId);
        }

        public Task<BackendCharacterListResponse> ListCharactersAsync(
            BackendCharacterListRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterListRequest, BackendCharacterListResponse>(
                "/v1/internal/characters/list", request, cancellationToken);

        public Task<BackendCharacterLoadResponse> LoadCharacterAsync(
            BackendCharacterLoadRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterLoadRequest, BackendCharacterLoadResponse>(
                "/v1/internal/characters/load", request, cancellationToken);

        public Task<BackendCharacterCreateResponse> CreateCharacterAsync(
            BackendCharacterCreateRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterCreateRequest, BackendCharacterCreateResponse>(
                "/v1/internal/characters/create", request, cancellationToken);

        public Task<BackendCharacterDeleteResponse> DeleteCharacterAsync(
            BackendCharacterDeleteRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterDeleteRequest, BackendCharacterDeleteResponse>(
                "/v1/internal/characters/delete", request, cancellationToken);


        public Task<BackendCharacterLeaseAcquireResponse> AcquireCharacterLeaseAsync(
            BackendCharacterLeaseAcquireRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterLeaseAcquireRequest, BackendCharacterLeaseAcquireResponse>(
                "/v1/internal/character-leases/acquire", request, cancellationToken);

        public Task<BackendCharacterLeaseRenewBatchResponse> RenewCharacterLeasesAsync(
            BackendCharacterLeaseRenewBatchRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterLeaseRenewBatchRequest, BackendCharacterLeaseRenewBatchResponse>(
                "/v1/internal/character-leases/renew", request, cancellationToken);

        public Task<BackendCharacterLeaseReleaseResponse> ReleaseCharacterLeaseAsync(
            BackendCharacterLeaseReleaseRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterLeaseReleaseRequest, BackendCharacterLeaseReleaseResponse>(
                "/v1/internal/character-leases/release", request, cancellationToken);

        public Task<BackendGameServerRegisterResponse> RegisterGameServerAsync(
            BackendGameServerRegisterRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendGameServerRegisterRequest, BackendGameServerRegisterResponse>(
                "/v1/internal/game-servers/register", request, cancellationToken);

        public Task<BackendGameServerHeartbeatResponse> HeartbeatGameServerAsync(
            BackendGameServerHeartbeatRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendGameServerHeartbeatRequest, BackendGameServerHeartbeatResponse>(
                "/v1/internal/game-servers/heartbeat", request, cancellationToken);

        public Task<BackendGameServerUnregisterResponse> UnregisterGameServerAsync(
            BackendGameServerUnregisterRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendGameServerUnregisterRequest, BackendGameServerUnregisterResponse>(
                "/v1/internal/game-servers/unregister", request, cancellationToken);

        public Task<BackendGameServerResolveMapResponse> ResolveGameServerForMapAsync(
            BackendGameServerResolveMapRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendGameServerResolveMapRequest, BackendGameServerResolveMapResponse>(
                "/v1/internal/game-servers/resolve-map", request, cancellationToken);

        public Task<BackendGameplayContentResponse> GetGameplayContentAsync(CancellationToken cancellationToken) =>
            GetAsync<BackendGameplayContentResponse>("/v1/internal/content/current", cancellationToken);

        /// <summary>
        /// Maintains one server-sent-event stream for backend-originated content revision
        /// notifications. No gameplay content is transferred until a higher revision is
        /// announced. If the stream is interrupted, reconnecting receives the backend's
        /// current revision immediately, which reconciles events missed while disconnected.
        /// </summary>
        public async Task RunContentRevisionEventStreamAsync(
            Action<long> onRevision,
            CancellationToken cancellationToken)
        {
            if (onRevision == null)
                throw new ArgumentNullException(nameof(onRevision));

            using (var message = new HttpRequestMessage(
                HttpMethod.Get,
                BaseUrl + "/v1/internal/events/content-revisions"))
            {
                message.Headers.TryAddWithoutValidation(
                    BackendServiceContracts.GameServerKeyHeader,
                    _gameServerKey);
                message.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                using (HttpResponseMessage response = await _eventHttp
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"Backend event stream failed with HTTP {(int)response.StatusCode}.");
                    }

                    // A live event stream proves the BackendServer is reachable, but losing
                    // only this stream does not prove persistence is unavailable. Request
                    // failures below are the authoritative degraded-service signal.
                    ReportAvailability(true, "Backend services connected.");

                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: false))
                    using (cancellationToken.Register(response.Dispose))
                    {
                        string eventName = string.Empty;
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null)
                                return;

                            if (line.Length == 0)
                            {
                                eventName = string.Empty;
                                continue;
                            }

                            if (line.StartsWith("event:", StringComparison.Ordinal))
                            {
                                eventName = line.Substring(6).Trim();
                                continue;
                            }

                            if (string.Equals(eventName, "content-revision", StringComparison.Ordinal) &&
                                line.StartsWith("data:", StringComparison.Ordinal) &&
                                long.TryParse(line.Substring(5).Trim(), out long revision) &&
                                revision > 0)
                            {
                                onRevision(revision);
                            }
                        }
                    }
                }
            }
        }

        public Task<BackendPlayerSystemsLoadResponse> LoadPlayerSystemsAsync(
            BackendPlayerSystemsLoadRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendPlayerSystemsLoadRequest, BackendPlayerSystemsLoadResponse>(
                "/v1/internal/player-systems/load", request, cancellationToken);

        public Task<BackendPlayerSystemsCommitResponse> CommitPlayerSystemsAsync(
            BackendPlayerSystemsCommitRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendPlayerSystemsCommitRequest, BackendPlayerSystemsCommitResponse>(
                "/v1/internal/player-systems/commit", request, cancellationToken);

        public Task<BackendPlayerItemConsumeResponse> ConsumePlayerItemAsync(
            BackendPlayerItemConsumeRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendPlayerItemConsumeRequest, BackendPlayerItemConsumeResponse>(
                "/v1/internal/player-systems/consume", request, cancellationToken);
        public Task<BackendPlayerAmmoReloadResponse> ConsumeAmmoForReloadAsync(
            BackendPlayerAmmoReloadRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendPlayerAmmoReloadRequest, BackendPlayerAmmoReloadResponse>(
                "/v1/internal/player-systems/reload-ammo", request, cancellationToken);

        public Task<BackendPlayerItemDropResponse> DropPlayerItemAsync(
            BackendPlayerItemDropRequest request,
            CancellationToken cancellationToken = default) =>
            PostAsync<BackendPlayerItemDropRequest, BackendPlayerItemDropResponse>(
                "/v1/internal/player-systems/drop", request, cancellationToken);

        public Task<BackendPlayerItemPickupResponse> PickupPlayerItemAsync(
            BackendPlayerItemPickupRequest request,
            CancellationToken cancellationToken = default) =>
            PostAsync<BackendPlayerItemPickupRequest, BackendPlayerItemPickupResponse>(
                "/v1/internal/player-systems/pickup", request, cancellationToken);

        public Task<BackendPlayerItemGrantResponse> GrantPlayerItemAsync(
            BackendPlayerItemGrantRequest request,
            CancellationToken cancellationToken = default) =>
            PostAsync<BackendPlayerItemGrantRequest, BackendPlayerItemGrantResponse>(
                "/v1/internal/player-systems/grant", request, cancellationToken);


        public Task<BackendPlayerItemBundleGrantResponse> GrantPlayerItemBundleAsync(
            BackendPlayerItemBundleGrantRequest request,
            CancellationToken cancellationToken = default) =>
            PostAsync<BackendPlayerItemBundleGrantRequest, BackendPlayerItemBundleGrantResponse>(
                "/v1/internal/player-systems/grant-bundle", request, cancellationToken);

        public Task<BackendPlayerCraftResponse> CraftPlayerItemAsync(
            BackendPlayerCraftRequest request,
            CancellationToken cancellationToken = default) =>
            PostAsync<BackendPlayerCraftRequest, BackendPlayerCraftResponse>(
                "/v1/internal/player-systems/craft", request, cancellationToken);

        public Task<BackendWorldItemsLoadResponse> LoadWorldItemsAsync(CancellationToken cancellationToken = default) =>
            GetAsync<BackendWorldItemsLoadResponse>("/v1/internal/world-items/load", cancellationToken);

        public Task<BackendCharacterSaveBatchResponse> SaveCharactersAsync(
            BackendCharacterSaveBatchRequest request,
            CancellationToken cancellationToken) =>
            PostAsync<BackendCharacterSaveBatchRequest, BackendCharacterSaveBatchResponse>(
                "/v1/internal/characters/save-batch", request, cancellationToken);

        private async Task<TResponse> PostAsync<TRequest, TResponse>(
            string path,
            TRequest request,
            CancellationToken cancellationToken)
            where TResponse : class
        {
            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path))
                {
                    message.Content = new StringContent(Serialize(request), Encoding.UTF8, "application/json");
                    message.Headers.TryAddWithoutValidation(
                        BackendServiceContracts.GameServerKeyHeader,
                        _gameServerKey);

                    using (HttpResponseMessage response = await _http
                        .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException(
                                $"Backend request {path} failed with HTTP {(int)response.StatusCode}.");
                        }

                        TResponse parsed = Deserialize<TResponse>(body);
                        if (parsed == null)
                            throw new InvalidOperationException($"Backend request {path} returned an empty response.");

                        ReportAvailability(true, "Backend services connected.");
                        return parsed;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ReportAvailability(false, "Backend persistence services are temporarily unavailable.");
                throw;
            }
        }

        private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken cancellationToken)
            where TResponse : class
        {
            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path))
                {
                    message.Headers.TryAddWithoutValidation(
                        BackendServiceContracts.GameServerKeyHeader,
                        _gameServerKey);

                    using (HttpResponseMessage response = await _http
                        .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException($"Backend request {path} failed with HTTP {(int)response.StatusCode}.");

                        TResponse parsed = Deserialize<TResponse>(body);
                        if (parsed == null)
                            throw new InvalidOperationException($"Backend request {path} returned an empty response.");

                        ReportAvailability(true, "Backend services connected.");
                        return parsed;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ReportAvailability(false, "Backend persistence services are temporarily unavailable.");
                throw;
            }
        }

        private void ReportAvailability(bool available, string message)
        {
            int next = available ? 1 : 0;
            int previous = Interlocked.Exchange(ref _availabilityState, next);
            if (previous == next)
                return;

            AvailabilityChanged?.Invoke(available, message ?? string.Empty);
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return serializer.ReadObject(stream) as T;
        }

        public void Dispose()
        {
            _eventHttp.Dispose();
            _http.Dispose();
        }
    }
}
