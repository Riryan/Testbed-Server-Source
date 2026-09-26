using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Shared.Backend;
using Game.Shared.Content;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Game.BackendServer;

internal static class AtomicItemExchangeEndpoint
{
    // Kept as one registration helper so Program.cs only needs one canonical route call.
    public static RouteHandlerBuilder MapAtomicItemExchange(
        this WebApplication app,
        Func<HttpContext, bool> isInternal,
        Func<GameplayContentSnapshot> getContent,
        Func<DatabaseWorkPriority, Func<BackendDatabase, BackendPlayerItemExchangeResponse>, CancellationToken, Task<IResult>> executeDatabaseAsync)
    {
        return app.MapPost("/v1/internal/player-systems/exchange", async Task<IResult> (
            HttpContext context,
            BackendPlayerItemExchangeRequest request) =>
        {
            if (!isInternal(context))
                return Results.NotFound();

            GameplayContentSnapshot content = getContent();
            return await executeDatabaseAsync(
                DatabaseWorkPriority.Critical,
                db => db.ExchangePlayerItems(request, content),
                context.RequestAborted);
        });
    }
}
