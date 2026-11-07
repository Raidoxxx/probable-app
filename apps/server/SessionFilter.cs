using Microsoft.AspNetCore.SignalR;

namespace PCBoost.Server;

public sealed class SessionFilter(Db db, Presence presence) : IHubFilter
{
    public const string SessionKey = "pcboost.session";
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await presence.Gate.WaitAsync(context.Context.ConnectionAborted);
        try
        {
            if (presence.Session(context.Context.ConnectionId) is not string session || db.UserBySession(session) is null)
            { context.Context.Abort(); throw new HubException("Sessão inválida."); }
            return await next(context);
        }
        finally { presence.Gate.Release(); }
    }
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await presence.Gate.WaitAsync(context.Context.ConnectionAborted);
        try { await next(context); } finally { presence.Gate.Release(); }
    }
    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        await presence.Gate.WaitAsync();
        try { await next(context, exception); } finally { presence.Gate.Release(); }
    }
}

public sealed class SessionExpiry(Db db, Presence presence) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken)) presence.Expire(db);
    }
}
