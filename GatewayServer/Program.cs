using System.Net;
using System.Threading.RateLimiting;
using Game.BackendServer;
using Game.Shared.Backend;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Microsoft.AspNetCore.RateLimiting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Keep the Gateway console focused on warnings/errors. Normal ASP.NET request and
// hosting lifecycle information is intentionally suppressed; explicit startup
// readiness lines below remain visible.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var options = new BackendOptions();
builder.Configuration.GetSection("GatewayServer").Bind(options);
options.Validate();

if (builder.Environment.IsEnvironment("LoadTest"))
    Console.WriteLine("WARNING: LOAD-TEST authentication limits are active. Do not use this environment for public production deployment.");

if (!string.IsNullOrWhiteSpace(options.DevelopmentAdminAccount))
    Console.WriteLine($"WARNING: DEVELOPMENT ADMIN BOOTSTRAP is active for '{options.DevelopmentAdminAccount}'. Clear GatewayServer:DevelopmentAdminAccount before production.");

string contentRoot = builder.Environment.ContentRootPath;
string databasePath = PathUtility.Resolve(contentRoot, options.DatabasePath);
string gameplayContentPath = PathUtility.Resolve(contentRoot, options.ContentDefinitionsPath);
string secretsPath = PathUtility.Resolve(contentRoot, options.SecretsPath);
string gameServerKeyPath = PathUtility.Resolve(contentRoot, options.GameServerKeyPath);
string certificatePath = PathUtility.Resolve(contentRoot, options.CertificatePath);
string certificateFingerprintPath = PathUtility.Resolve(contentRoot, options.CertificateFingerprintPath);

BackendSecrets secrets = BackendSecrets.LoadOrCreate(secretsPath, gameServerKeyPath);
using var accountTelemetry = new AccountAccessTelemetry(secrets.AccountTelemetryHmacKey);
using var certificate = CertificateBootstrap.LoadOrCreate(
    certificatePath,
    secrets.CertificatePassword,
    options.CertificateHosts);

string certificateFingerprint = CertificateBootstrap.Sha256Fingerprint(certificate);
Directory.CreateDirectory(Path.GetDirectoryName(certificateFingerprintPath) ?? ".");
File.WriteAllText(certificateFingerprintPath, certificateFingerprint + Environment.NewLine);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 256 * 1024;

    // Public account/login surface. This is the only listener that ever accepts passwords.
    kestrel.ListenAnyIP(options.HttpsPort, listen => listen.UseHttps(certificate));

    // Private game-server surface. Single-machine deployment is loopback only.
    kestrel.Listen(IPAddress.Loopback, options.InternalPort);
});

builder.Services.AddRateLimiter(rate =>
{
    rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    rate.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.LoginAttemptsPerMinutePerIp,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    rate.AddPolicy("create", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.AccountCreatesPerTenMinutesPerIp,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    rate.AddPolicy("health", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.HealthChecksPerMinutePerIp,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    // Shared backend DTOs intentionally use public fields so Unity JsonUtility and
    // DataContractJsonSerializer can consume the same wire contracts.
    json.SerializerOptions.IncludeFields = true;
});

WebApplication app = builder.Build();
app.UseRateLimiter();

using var contentDefinitions = new ContentDefinitionStore(gameplayContentPath);
var backendEvents = new BackendEventHub();
contentDefinitions.RevisionChanged += backendEvents.PublishContentRevision;
var characterLeases = new CharacterLeaseStore();
var gameServers = new GameServerDirectory();
using var database = new BackendDatabase(databasePath, characterLeases);
using var databaseWork = new BackendDatabaseDispatcher(
    database,
    options.DatabaseCriticalQueueCapacity,
    options.DatabaseNormalQueueCapacity,
    options.DatabaseBackgroundQueueCapacity);
var admissions = new AdmissionTokenService(
    databaseWork,
    TimeSpan.FromSeconds(options.AdmissionLifetimeSeconds));
var accounts = new AccountAuthService(databaseWork, admissions, options, accountTelemetry);
await using var passwordWork = new PasswordWorkPool(
    accounts,
    options.PasswordWorkerCount,
    options.PasswordWorkerQueueCapacity);

bool IsPublic(HttpContext context) =>
    context.Request.IsHttps && context.Connection.LocalPort == options.HttpsPort;

bool IsInternal(HttpContext context) =>
    InternalAuthorization.IsAuthorized(context, options.InternalPort, secrets.GameServerKey);

async Task<IResult> ExecuteDatabaseAsync<T>(
    DatabaseWorkPriority priority,
    Func<BackendDatabase, T> operation,
    CancellationToken cancellationToken)
{
    if (!databaseWork.TryQueue(priority, operation, out Task<T> completion))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    try
    {
        T result = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Database operation failed: {ex.Message}");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}

app.MapGet("/health", (HttpContext context) =>
{
    bool publicHealth = IsPublic(context);
    bool internalHealth =
        context.Connection.LocalPort == options.InternalPort &&
        context.Connection.RemoteIpAddress != null &&
        IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
    return publicHealth || internalHealth
        ? Results.Ok(new
        {
            status = "ok",
            gameServerAvailable = gameServers.HasLiveServer(),
        })
        : Results.NotFound();
}).RequireRateLimiting("health");

// Internal-only scale diagnostics. This intentionally exposes queue pressure, not
// database contents. It is useful with 2 clients now and remains the same endpoint for
// future 100+ CCU load tests.
app.MapGet("/v1/internal/diagnostics/runtime", (HttpContext context) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    CharacterLeaseDiagnostics leaseStats = characterLeases.GetDiagnostics();
    GameServerDirectoryDiagnostics serverStats = gameServers.GetDiagnostics();
    return Results.Ok(new
    {
        database = new
        {
            queueDepth = databaseWork.QueueDepth,
            queueHighWater = databaseWork.HighWater,
            oldestQueueAgeMs = databaseWork.OldestQueueAgeMilliseconds,
            lastQueueWaitMs = databaseWork.LastQueueWaitMilliseconds,
            maxQueueWaitMs = databaseWork.MaxQueueWaitMilliseconds,
            lastExecutionMs = databaseWork.LastExecutionMilliseconds,
            maxExecutionMs = databaseWork.MaxExecutionMilliseconds,
            completed = databaseWork.Completed,
            rejected = databaseWork.Rejections,
            lanes = new
            {
                critical = new
                {
                    depth = databaseWork.CriticalQueueDepth,
                    capacity = databaseWork.CriticalCapacity,
                    rejected = databaseWork.CriticalRejections,
                },
                normal = new
                {
                    depth = databaseWork.NormalQueueDepth,
                    capacity = databaseWork.NormalCapacity,
                    rejected = databaseWork.NormalRejections,
                },
                background = new
                {
                    depth = databaseWork.BackgroundQueueDepth,
                    capacity = databaseWork.BackgroundCapacity,
                    rejected = databaseWork.BackgroundRejections,
                },
            },
        },
        passwordWorkers = new
        {
            workers = passwordWork.WorkerCount,
            activeWorkers = passwordWork.ActiveWorkers,
            queueDepth = passwordWork.QueueDepth,
            queueCapacity = passwordWork.QueueCapacity,
            queueHighWater = passwordWork.HighWater,
            completed = passwordWork.Completed,
            loginCompleted = passwordWork.LoginCompleted,
            createCompleted = passwordWork.CreateCompleted,
            rejected = passwordWork.Rejections,
        },
        characterLeases = new
        {
            active = leaseStats.Active,
            recoveryHandoff = leaseStats.RecoveryHandoff,
            stale = leaseStats.Stale,
            deleteReservations = leaseStats.DeleteReservations,
        },
        gameServers = new
        {
            liveServers = serverStats.LiveServers,
            connectedPlayers = serverStats.ConnectedPlayers,
            maxConnections = serverStats.MaxConnections,
            mapPartitions = serverStats.MapPartitions,
        },
        content = new
        {
            revision = contentDefinitions.GetCurrent().revision,
        },
    });
});

// -----------------------------------------------------------------------------
// Public HTTPS account/authentication API
// -----------------------------------------------------------------------------

app.MapPost("/v1/accounts/login", async Task<IResult> (HttpContext context, BackendAccountRequest request) =>
{
    if (!IsPublic(context))
        return Results.NotFound();

    string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    if (!passwordWork.TryQueueLogin(
            request?.account,
            request?.password,
            remoteIp,
            request?.deviceId,
            out Task<AuthOperationResult> completion))
    {
        return Results.Json(
            AuthFailed("authentication busy"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    AuthOperationResult result = await completion.ConfigureAwait(false);
    return result.Success
        ? Results.Ok(AuthSucceeded(result))
        : Results.Json(AuthFailed("login unavailable"), statusCode: StatusCodes.Status401Unauthorized);
}).RequireRateLimiting("login");

app.MapPost("/v1/accounts/create", async Task<IResult> (HttpContext context, BackendAccountRequest request) =>
{
    if (!IsPublic(context))
        return Results.NotFound();

    string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    if (!passwordWork.TryQueueCreate(
            request?.account,
            request?.password,
            remoteIp,
            request?.deviceId,
            out Task<AuthOperationResult> completion))
    {
        return Results.Json(
            AuthFailed("authentication busy"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    AuthOperationResult result = await completion.ConfigureAwait(false);
    return result.Success
        ? Results.Ok(AuthSucceeded(result))
        : Results.Json(AuthFailed("account creation unavailable"), statusCode: StatusCodes.Status400BadRequest);
}).RequireRateLimiting("create");

// -----------------------------------------------------------------------------
// Loopback-only game-server API
// -----------------------------------------------------------------------------

app.MapPost("/v1/internal/admissions/redeem", async Task<IResult> (
    HttpContext context,
    BackendAdmissionRedeemRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    if (!admissions.TryQueueRedeem(request?.token, out Task<BackendAdmissionRedeemResponse> completion))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    BackendAdmissionRedeemResponse result;
    try
    {
        result = await completion.WaitAsync(context.RequestAborted).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Admission redemption failed: {ex.Message}");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return result != null && result.success && result.accountId > 0 && result.policy != null
        ? Results.Ok(result)
        : Results.Json(
            result ?? new BackendAdmissionRedeemResponse
            {
                success = false,
                accountId = 0,
                policy = null,
                error = "admission unavailable",
            },
            statusCode: StatusCodes.Status401Unauthorized);
});

app.MapPost("/v1/internal/characters/list", async Task<IResult> (
    HttpContext context,
    BackendCharacterListRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.ListCharacters(request?.accountId ?? 0),
        context.RequestAborted);
});

app.MapPost("/v1/internal/characters/load", async Task<IResult> (
    HttpContext context,
    BackendCharacterLoadRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.LoadCharacter(
            request?.accountId ?? 0,
            request?.characterId ?? 0),
        context.RequestAborted);
});

app.MapPost("/v1/internal/characters/create", async Task<IResult> (
    HttpContext context,
    BackendCharacterCreateRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.TryCreateCharacter(
            request?.accountId ?? 0,
            request?.name,
            request?.initialLocation,
            request?.initialAppearance,
            request?.initialPresentation,
            options.CharacterLimit,
            content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/characters/delete", async Task<IResult> (
    HttpContext context,
    BackendCharacterDeleteRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    long characterId = request?.characterId ?? 0;
    if (request == null || request.accountId <= 0 || characterId <= 0)
    {
        return Results.Ok(new BackendCharacterDeleteResponse
        {
            success = false,
            failure = (byte)CharacterDeleteFailure.InvalidCharacter,
            characterId = 0,
            name = string.Empty,
            deletedUtcTicks = 0,
            purgeAfterUtcTicks = 0,
            error = "invalid character delete request",
        });
    }

    // Validate ownership before reserving the global lease slot. A modified client may
    // submit arbitrary character ids; it must not be able to momentarily block another
    // account's character simply by guessing its id. The archive transaction validates
    // ownership again after the reservation, so this precheck is security hygiene rather
    // than the authority boundary.
    if (!databaseWork.TryQueue(
            DatabaseWorkPriority.Critical,
            db => db.OwnsCharacter(request.accountId, characterId),
            out Task<bool> ownership))
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    bool ownsCharacter;
    try
    {
        ownsCharacter = await ownership.WaitAsync(context.RequestAborted).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Character delete ownership check failed: {ex.Message}");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (!ownsCharacter)
    {
        return Results.Ok(new BackendCharacterDeleteResponse
        {
            success = false,
            failure = (byte)CharacterDeleteFailure.CharacterNotFoundOrNotOwned,
            characterId = 0,
            name = string.Empty,
            deletedUtcTicks = 0,
            purgeAfterUtcTicks = 0,
            error = "character was not found or is not owned by this account",
        });
    }

    // Reserve the character against lease acquisition while the archive/delete database
    // transaction runs. This keeps deletion atomic with respect to cross-GameServer entry.
    if (!characterLeases.TryBeginDeletion(characterId))
    {
        return Results.Ok(new BackendCharacterDeleteResponse
        {
            success = false,
            failure = (byte)CharacterDeleteFailure.CharacterActive,
            characterId = 0,
            name = string.Empty,
            deletedUtcTicks = 0,
            purgeAfterUtcTicks = 0,
            error = "character is active or recovering from an active lease",
        });
    }

    try
    {
        return await ExecuteDatabaseAsync(
            DatabaseWorkPriority.Critical,
            db => db.TryArchiveDeleteCharacter(request.accountId, characterId),
            context.RequestAborted);
    }
    finally
    {
        characterLeases.EndDeletion(characterId);
    }
});

app.MapPost("/v1/internal/character-leases/acquire", async Task<IResult> (
    HttpContext context,
    BackendCharacterLeaseAcquireRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    if (request == null || request.accountId <= 0 || request.characterId <= 0)
        return Results.Ok(characterLeases.Acquire(request));

    if (!databaseWork.TryQueue(
            DatabaseWorkPriority.Critical,
            db => db.OwnsCharacter(request.accountId, request.characterId),
            out Task<bool> ownership))
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    bool ownsCharacter;
    try
    {
        ownsCharacter = await ownership.WaitAsync(context.RequestAborted).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Character lease ownership check failed: {ex.Message}");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (!ownsCharacter)
    {
        return Results.Ok(new BackendCharacterLeaseAcquireResponse
        {
            success = true,
            acquired = false,
            expiresUtcTicks = 0,
            error = "character unavailable",
        });
    }

    return Results.Ok(characterLeases.Acquire(request));
});

app.MapPost("/v1/internal/character-leases/renew", (
    HttpContext context,
    BackendCharacterLeaseRenewBatchRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(characterLeases.Renew(request));
});

app.MapPost("/v1/internal/character-leases/release", (
    HttpContext context,
    BackendCharacterLeaseReleaseRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(characterLeases.Release(request));
});

// -----------------------------------------------------------------------------
// Ephemeral GameServer world-directory authority. Directory state is intentionally
// independent of SQLite account/gameplay persistence and disappears on Gateway restart.
// Live GameServers recover by re-registering their lease.
// -----------------------------------------------------------------------------

app.MapPost("/v1/internal/game-servers/register", (
    HttpContext context,
    BackendGameServerRegisterRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(gameServers.Register(request));
});

app.MapPost("/v1/internal/game-servers/heartbeat", (
    HttpContext context,
    BackendGameServerHeartbeatRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(gameServers.Heartbeat(request));
});

app.MapPost("/v1/internal/game-servers/unregister", (
    HttpContext context,
    BackendGameServerUnregisterRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(gameServers.Unregister(request));
});

app.MapPost("/v1/internal/game-servers/resolve-map", (
    HttpContext context,
    BackendGameServerResolveMapRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(gameServers.Resolve(request));
});


app.MapGet("/v1/internal/content/current", (HttpContext context) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return Results.Ok(new BackendGameplayContentResponse
    {
        success = true,
        error = string.Empty,
        content = contentDefinitions.GetCurrent(),
    });
});

app.MapGet("/v1/internal/events/content-revisions", async (HttpContext context) =>
{
    if (!IsInternal(context))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    using BackendEventHub.ContentRevisionSubscription subscription =
        backendEvents.SubscribeContentRevisions();

    // Immediate revision announcement reconciles a GameServer that reconnects after
    // missing one or more events. It does not fetch/send the full content snapshot.
    await WriteContentRevisionEventAsync(
        context.Response,
        contentDefinitions.GetCurrent().revision,
        context.RequestAborted);

    try
    {
        await foreach (long revision in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            await WriteContentRevisionEventAsync(
                context.Response,
                revision,
                context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal when a GameServer disconnects or GatewayServer shuts down.
    }
});

app.MapPost("/v1/internal/player-systems/load", async Task<IResult> (
    HttpContext context,
    BackendPlayerSystemsLoadRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    // Reconciliation loads deliberately enter the same FIFO critical lane as item
    // mutations. If an HTTP response was lost after the mutation was accepted by
    // Gateway, this load becomes a queue barrier and observes the post-mutation state.
    DatabaseWorkPriority priority = request?.mutationBarrier == true
        ? DatabaseWorkPriority.Critical
        : DatabaseWorkPriority.Normal;
    return await ExecuteDatabaseAsync(
        priority,
        db => db.LoadPlayerSystems(
            request?.accountId ?? 0,
            request?.characterId ?? 0,
            content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/commit", async Task<IResult> (
    HttpContext context,
    BackendPlayerSystemsCommitRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.CommitPlayerSystems(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/consume", async Task<IResult> (
    HttpContext context,
    BackendPlayerItemConsumeRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.ConsumePlayerItem(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/reload-ammo", async Task<IResult> (
    HttpContext context,
    BackendPlayerAmmoReloadRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.ConsumeAmmoForReload(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/drop", async Task<IResult> (
    HttpContext context,
    BackendPlayerItemDropRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.DropPlayerItem(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/pickup", async Task<IResult> (
    HttpContext context,
    BackendPlayerItemPickupRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.PickupPlayerItem(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/grant", async Task<IResult> (
    HttpContext context,
    BackendPlayerItemGrantRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.GrantPlayerItem(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/grant-bundle", async Task<IResult> (
    HttpContext context,
    BackendPlayerItemBundleGrantRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.GrantPlayerItemBundle(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/player-systems/craft", async Task<IResult> (
    HttpContext context,
    BackendPlayerCraftRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.CraftPlayerItem(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/friends/load", async Task<IResult> (
    HttpContext context,
    BackendFriendLoadRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.LoadFriends(request?.characterId ?? 0),
        context.RequestAborted);
});

app.MapPost("/v1/internal/friends/add", async Task<IResult> (
    HttpContext context,
    BackendFriendMutationRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.AddFriend(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/friends/remove", async Task<IResult> (
    HttpContext context,
    BackendFriendMutationRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.RemoveFriend(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/trade/commit", async Task<IResult> (
    HttpContext context,
    BackendTradeCommitRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.CommitTrade(request, content),
        context.RequestAborted);
});

app.MapPost("/v1/internal/storage/load", async Task<IResult> (
    HttpContext context,
    BackendStorageLoadRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.LoadStorage(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/storage/transfer", async Task<IResult> (
    HttpContext context,
    BackendStorageTransferRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.TransferStorage(request),
        context.RequestAborted);
});


app.MapPost("/v1/internal/guilds/load", async Task<IResult> (
    HttpContext context,
    BackendGuildLoadRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.LoadGuild(request?.characterId ?? 0),
        context.RequestAborted);
});

app.MapPost("/v1/internal/guilds/create", async Task<IResult> (
    HttpContext context,
    BackendGuildCreateRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.CreateGuild(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/guilds/join", async Task<IResult> (
    HttpContext context,
    BackendGuildJoinRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.JoinGuild(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/guilds/leave", async Task<IResult> (
    HttpContext context,
    BackendGuildLeaveRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.LeaveGuild(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/guilds/kick", async Task<IResult> (
    HttpContext context,
    BackendGuildKickRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.KickGuildMember(request),
        context.RequestAborted);
});

app.MapPost("/v1/internal/guilds/disband", async Task<IResult> (
    HttpContext context,
    BackendGuildDisbandRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Critical,
        db => db.DisbandGuild(request),
        context.RequestAborted);
});

app.MapGet("/v1/internal/world-items/load", async Task<IResult> (HttpContext context) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Background,
        db => db.LoadWorldItems(),
        context.RequestAborted);
});

app.MapPost("/v1/internal/characters/save-batch", async Task<IResult> (
    HttpContext context,
    BackendCharacterSaveBatchRequest request) =>
{
    if (!IsInternal(context))
        return Results.NotFound();

    GameplayContentSnapshot content = contentDefinitions.GetCurrent();
    return await ExecuteDatabaseAsync(
        DatabaseWorkPriority.Normal,
        db => db.SaveCharacterBatch(request?.characters, content),
        context.RequestAborted);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("GatewayServer ready.");
    Console.WriteLine($"Public HTTPS auth: https://0.0.0.0:{options.HttpsPort}");
    Console.WriteLine($"Internal game-server API: http://127.0.0.1:{options.InternalPort}");
    Console.WriteLine($"Gameplay content revision: {contentDefinitions.GetCurrent().revision}");
});

await app.RunAsync();

static async Task WriteContentRevisionEventAsync(
    HttpResponse response,
    long revision,
    CancellationToken cancellationToken)
{
    await response.WriteAsync("event: content-revision\n", cancellationToken);
    await response.WriteAsync($"data: {revision}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static BackendAuthResponse AuthSucceeded(AuthOperationResult result) =>
    new()
    {
        success = true,
        error = string.Empty,
        admissionToken = result.Admission.Token,
        expiresUtcTicks = result.Admission.ExpiresUtcTicks,
        policy = result.Policy,
    };

static BackendAuthResponse AuthFailed(string error) =>
    new()
    {
        success = false,
        error = error,
        admissionToken = string.Empty,
        expiresUtcTicks = 0,
        policy = null,
    };
