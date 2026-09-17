#!/usr/bin/env python3
"""Install the six existing Social/Economy Gateway endpoints into GatewayServer/Program.cs.

Run from the standalone server repository root. The installer is idempotent, preserves
existing line endings, refuses an ambiguous source anchor, and adds no runtime shim.
"""
from pathlib import Path

path = Path("GatewayServer/Program.cs")
if not path.is_file():
    raise SystemExit(f"Missing {path}. Run this from the standalone server repository root.")

raw = path.read_bytes()
try:
    text = raw.decode("utf-8")
except UnicodeDecodeError as exc:
    raise SystemExit(f"{path} is not UTF-8: {exc}")

if '/v1/internal/friends/load' in text:
    print("Gateway Social/Economy routes are already present; no change made.")
    raise SystemExit(0)

newline = "\r\n" if "\r\n" in text else "\n"
anchor = f'app.MapPost("/v1/internal/guilds/load", async Task<IResult> ({newline}'
if text.count(anchor) != 1:
    raise SystemExit("Expected exactly one guild-load route anchor; Program.cs was not modified.")

routes_lf = r'''app.MapPost("/v1/internal/friends/load", async Task<IResult> (
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

'''
routes = routes_lf.replace("\n", newline)
path.write_bytes((text.replace(anchor, routes + anchor)).encode("utf-8"))
print(f"Installed Social/Economy Gateway routes into {path}.")
