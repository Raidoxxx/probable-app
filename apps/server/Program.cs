using System.Net.Mail;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PCBoost.Server;
using PCBoost.Shared;

// ContentRoot fixo na pasta do executável: o site (wwwroot/) é achado mesmo quando o processo
// é iniciado de outra pasta (como o start.py da Discloud faz).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Hospedagem: a maioria dos provedores (Render, Railway, Fly, Azure) informa a porta em PORT e põe HTTPS na frente.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://0.0.0.0:" + (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port ? port : "5080"));
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear(); // o proxy do provedor fica na frente; o limite de tentativas usa o IP real
});
string dataPath = builder.Configuration["data"] ?? Environment.GetEnvironmentVariable("PCBOOST_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data", "pcboost-team.db");
builder.Services.AddSingleton(new Db(dataPath));
// Instalador do app: arquivo local em downloads/ ou, se não houver, um link externo (ex.: GitHub Releases).
const string SetupName = "Ximbuismo-Setup.exe";
string downloadsDir = builder.Configuration["downloads"] ?? Environment.GetEnvironmentVariable("PCBOOST_DOWNLOADS") ?? Path.Combine(AppContext.BaseDirectory, "downloads");
string? downloadUrl = builder.Configuration["downloadUrl"] ?? Environment.GetEnvironmentVariable("PCBOOST_DOWNLOAD_URL");
FileInfo? LocalSetup() { var file = new FileInfo(Path.Combine(downloadsDir, SetupName)); return file.Exists ? file : null; }
string appVersion = typeof(Db).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
builder.Services.AddSingleton<Presence>();
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 64 * 1024; o.EnableDetailedErrors = false; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseWebSockets();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Revalida a interface a cada visita para não misturar HTML novo com scripts antigos.
        var extension = Path.GetExtension(ctx.File.Name).ToLowerInvariant();
        ctx.Context.Response.Headers.CacheControl = extension is ".html" or ".js" or ".css"
            ? "no-cache" : "public, max-age=86400";
    }
});

// O hub só aceita conexões com token válido (enviado pelo cliente SignalR como access_token).
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/hub"))
    {
        string? token = http.Request.Query["access_token"];
        if (string.IsNullOrEmpty(token)) token = http.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        var user = string.IsNullOrEmpty(token) ? null : http.RequestServices.GetRequiredService<Db>().UserByToken(token);
        if (user is null) { http.Response.StatusCode = 401; return; }
        http.Items[TeamHub.UserKey] = user;
    }
    await next();
});

static string? Problem(AuthRequest r, bool register)
{
    if (string.IsNullOrWhiteSpace(r.Email) || r.Email.Length > 200 || !MailAddress.TryCreate(r.Email.Trim(), out _)) return "Informe um e-mail válido.";
    if (string.IsNullOrEmpty(r.Password) || r.Password.Length > 200) return "Informe a senha.";
    if (register && r.Password.Length < 8) return "A senha precisa ter pelo menos 8 caracteres.";
    if (register && (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 40)) return "Informe um nome de até 40 caracteres.";
    return null;
}

app.MapGet("/api/health", () =>
{
    var local = LocalSetup();
    var download = new { available = local is not null || !string.IsNullOrEmpty(downloadUrl), size = local?.Length, version = appVersion };
    return Results.Ok(new { ok = true, app = "Ximbuismo.Server", version = 1, download });
});
app.MapMethods("/download", new[] { "GET", "HEAD" }, () =>
{
    if (LocalSetup() is FileInfo local)
        return Results.File(local.FullName, "application/octet-stream", SetupName, local.LastWriteTimeUtc, enableRangeProcessing: true);
    if (!string.IsNullOrEmpty(downloadUrl)) return Results.Redirect(downloadUrl);
    return Results.Text("O instalador ainda não foi publicado. Tente de novo mais tarde.", "text/plain; charset=utf-8", statusCode: 404);
});
app.MapPost("/api/register", (AuthRequest r, Db db) =>
{
    if (Problem(r, true) is string error) return Results.BadRequest(new ErrorResponse(error));
    try
    {
        var user = db.Register(r.Email.Trim(), r.Name!.Trim(), r.Password);
        return Results.Ok(new AuthResponse(db.IssueToken(user.Id), user));
    }
    catch (AppError e) { return Results.Conflict(new ErrorResponse(e.Message)); }
}).RequireRateLimiting("auth");
app.MapPost("/api/login", (AuthRequest r, Db db) =>
{
    if (Problem(r, false) is string error) return Results.BadRequest(new ErrorResponse(error));
    var user = db.Verify(r.Email.Trim(), r.Password);
    return user is null ? Results.Json(new ErrorResponse("E-mail ou senha incorretos."), statusCode: 401) : Results.Ok(new AuthResponse(db.IssueToken(user.Id), user));
}).RequireRateLimiting("auth");
app.MapGet("/api/me", (HttpRequest req, Db db) =>
{
    string token = req.Headers.Authorization.ToString().Replace("Bearer ", "");
    return db.UserByToken(token) is UserDto user ? Results.Ok(user) : Results.Unauthorized();
});
app.MapPost("/api/logout", (HttpRequest req, Db db) => { db.RevokeToken(req.Headers.Authorization.ToString().Replace("Bearer ", "")); return Results.Ok(); });
app.MapHub<TeamHub>("/hub");

app.Logger.LogInformation("PCBoost.Server · dados em {Path}", dataPath);
app.Run();
