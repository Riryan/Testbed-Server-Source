$ErrorActionPreference = 'Stop'
$path = Join-Path (Get-Location) 'GatewayServer/Program.cs'
if (-not (Test-Path $path)) { throw "Missing $path. Run this from the standalone server repository root." }
$text = [IO.File]::ReadAllText($path)
if ($text.Contains('/v1/internal/friends/load')) {
    Write-Host 'Gateway Social/Economy routes are already present; no change made.'
    exit 0
}
$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$anchor = 'app.MapPost("/v1/internal/guilds/load", async Task<IResult> (' + $newline
$count = ([regex]::Matches($text, [regex]::Escape($anchor))).Count
if ($count -ne 1) { throw 'Expected exactly one guild-load route anchor; Program.cs was not modified.' }
$routes = @'
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

'@
$routes = $routes.Replace("`r`n", "`n").Replace("`n", $newline)
$text = $text.Replace($anchor, $routes + $anchor)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
Write-Host "Installed Social/Economy Gateway routes into $path."
