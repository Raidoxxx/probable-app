using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using PCBoost.Shared;

namespace PCBoost.Team;

public sealed class SavedLogin
{
    public string Server { get; set; } = TeamClient.DefaultServer;
    public string Email { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
}

// Conexão com o servidor de equipe: login via HTTP e tudo o mais pelo hub SignalR.
public sealed class TeamClient : IAsyncDisposable
{
    // O servidor oficial vem gravado no build (TeamServerUrl). PCBOOST_SERVER sobrescreve para testes.
    public static string DefaultServer { get; } = Environment.GetEnvironmentVariable("PCBOOST_SERVER") is { Length: > 0 } env ? env
        : typeof(TeamClient).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "TeamServer")?.Value ?? "http://localhost:5080";
    public static bool HasOfficialServer => !DefaultServer.Contains("localhost", StringComparison.OrdinalIgnoreCase) && !DefaultServer.Contains("127.0.0.1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string savePath;
    private HubConnection? hub;
    private string token = "";
    public string Server { get; private set; } = DefaultServer;
    public UserDto? User { get; private set; }
    public bool Connected => hub?.State == HubConnectionState.Connected;
    public HubConnectionState State => hub?.State ?? HubConnectionState.Disconnected;

    public event Action<MessageDto>? Message;
    public event Action<long, long, bool>? Presence;
    public event Action<long>? MembersChanged;
    public event Action<long>? ChannelsChanged;
    public event Action<long, long, long>? IdeaChanged;   // servidor, canal, item
    public event Action<long, long, long>? IdeaDeleted;
    public event Action<long, long, List<CallParticipant>>? CallChanged; // servidor, canal de voz, participantes
    public event Action<long, byte[]>? Audio;
    public event Action<string>? ConnectionChanged;

    public TeamClient(string root) => savePath = Path.Combine(root, "team-login.json");

    public SavedLogin LoadSaved()
    {
        try
        {
            var saved = File.Exists(savePath) ? JsonSerializer.Deserialize<SavedLogin>(File.ReadAllText(savePath)) ?? new() : new();
            if (HasOfficialServer) saved.Server = DefaultServer; // com servidor oficial, ninguém precisa digitar endereço
            return saved;
        }
        catch (Exception) { return new(); }
    }
    private void Save(bool keepToken)
    {
        var saved = new SavedLogin { Server = Server, Email = User?.Email ?? LoadSaved().Email };
        if (keepToken && token.Length > 0)
            saved.ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        try { File.WriteAllText(savePath, JsonSerializer.Serialize(saved)); } catch (IOException) { }
    }

    public static string NormalizeServer(string server)
    {
        server = server.Trim().TrimEnd('/');
        if (server.Length == 0) server = DefaultServer;
        if (!server.Contains("://")) server = "http://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Endereço do servidor inválido.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private static HttpClient Http(string server) => new() { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(10) };

    public async Task SignIn(string server, string email, string password, string? name, bool register, bool remember)
    {
        Server = NormalizeServer(server);
        using var http = Http(Server);
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync(register ? "/api/register" : "/api/login", new AuthRequest(email.Trim(), password, name?.Trim()), Json); }
        catch (HttpRequestException) { throw new InvalidOperationException($"Não foi possível alcançar o servidor {Server}. Inicie o PCBoost.Server ou confira o endereço."); }
        catch (TaskCanceledException) { throw new InvalidOperationException($"O servidor {Server} não respondeu a tempo."); }
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "Muitas tentativas. Aguarde um minuto." : "Falha na autenticação.";
            try { message = (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json))?.Error ?? message; } catch (Exception) { }
            throw new InvalidOperationException(message);
        }
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        token = auth.Token; User = auth.User;
        await Connect();
        Save(remember);
    }
    public async Task<bool> Resume()
    {
        var saved = LoadSaved();
        if (saved.ProtectedToken.Length == 0) return false;
        try
        {
            Server = NormalizeServer(saved.Server);
            token = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved.ProtectedToken), null, DataProtectionScope.CurrentUser));
            using var http = Http(Server);
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var response = await http.GetAsync("/api/me");
            if (!response.IsSuccessStatusCode) { token = ""; Save(false); return false; }
            User = await response.Content.ReadFromJsonAsync<UserDto>(Json);
            await Connect(); return true;
        }
        catch (Exception) { token = ""; User = null; return false; }
    }
    public async Task SignOut()
    {
        try { using var http = Http(Server); http.DefaultRequestHeaders.Authorization = new("Bearer", token); await http.PostAsync("/api/logout", null); } catch (Exception) { }
        if (hub is not null) { await hub.DisposeAsync(); hub = null; }
        token = ""; User = null; Save(false);
    }

    private async Task Connect()
    {
        if (hub is not null) await hub.DisposeAsync();
        hub = new HubConnectionBuilder()
            .WithUrl(Server + "/hub", o => o.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();
        hub.On<MessageDto>("Message", m => Message?.Invoke(m));
        hub.On<long, long, bool>("Presence", (g, u, on) => Presence?.Invoke(g, u, on));
        hub.On<long>("MembersChanged", g => MembersChanged?.Invoke(g));
        hub.On<long>("ChannelsChanged", g => ChannelsChanged?.Invoke(g));
        hub.On<long, long, long>("IdeaChanged", (g, c, i) => IdeaChanged?.Invoke(g, c, i));
        hub.On<long, long, long>("IdeaDeleted", (g, c, i) => IdeaDeleted?.Invoke(g, c, i));
        hub.On<long, long, List<CallParticipant>>("CallChanged", (g, c, p) => CallChanged?.Invoke(g, c, p));
        hub.On<long, byte[]>("Audio", (u, f) => Audio?.Invoke(u, f));
        hub.Reconnecting += _ => { ConnectionChanged?.Invoke("RECONECTANDO"); return Task.CompletedTask; };
        hub.Reconnected += _ => { ConnectionChanged?.Invoke("CONECTADO"); return Task.CompletedTask; };
        hub.Closed += _ => { ConnectionChanged?.Invoke("DESCONECTADO"); return Task.CompletedTask; };
        try { await hub.StartAsync(); }
        catch (HttpRequestException) { throw new InvalidOperationException("O servidor recusou a conexão em tempo real. Entre novamente."); }
        ConnectionChanged?.Invoke("CONECTADO");
    }

    private HubConnection Hub => hub is { State: HubConnectionState.Connected } h ? h : throw new InvalidOperationException("Sem conexão com o servidor.");
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (HubException e) { throw new InvalidOperationException(e.Message.Contains("HubException: ") ? e.Message[(e.Message.LastIndexOf("HubException: ") + 14)..] : e.Message); }
    }
    private static async Task Call(Func<Task> call) => await Call(async () => { await call(); return 0; });

    public Task<List<GroupDto>> Groups() => Call(() => Hub.InvokeAsync<List<GroupDto>>("MyGroups"));
    public Task<GroupDto> CreateGroup(string name) => Call(() => Hub.InvokeAsync<GroupDto>("CreateGroup", name));
    public Task<GroupDto> JoinGroup(string code) => Call(() => Hub.InvokeAsync<GroupDto>("JoinGroup", code));
    public Task LeaveGroup(long id) => Call(() => Hub.InvokeAsync("LeaveGroup", id));
    public Task<List<MemberDto>> Members(long id) => Call(() => Hub.InvokeAsync<List<MemberDto>>("Members", id));
    public Task<List<ChannelDto>> Channels(long group) => Call(() => Hub.InvokeAsync<List<ChannelDto>>("Channels", group));
    public Task<ChannelDto> CreateChannel(long group, string name, string kind) => Call(() => Hub.InvokeAsync<ChannelDto>("CreateChannel", group, name, kind));
    public Task DeleteChannel(long channel) => Call(() => Hub.InvokeAsync("DeleteChannel", channel));
    public Task<List<MessageDto>> History(long channel, long? before = null) => Call(() => Hub.InvokeAsync<List<MessageDto>>("History", channel, before));
    public Task Send(long channel, string text) => Call(() => Hub.InvokeAsync("Send", channel, text));
    public Task<List<IdeaDto>> Ideas(long channel) => Call(() => Hub.InvokeAsync<List<IdeaDto>>("Ideas", channel));
    public Task<IdeaDto?> Idea(long id) => Call(() => Hub.InvokeAsync<IdeaDto?>("Idea", id));
    public Task<IdeaDto> SaveIdea(IdeaInput input) => Call(() => Hub.InvokeAsync<IdeaDto>("SaveIdea", input));
    public Task DeleteIdea(long id) => Call(() => Hub.InvokeAsync("DeleteIdea", id));
    public Task ToggleVote(long id) => Call(() => Hub.InvokeAsync("ToggleVote", id));
    public Task<List<CallParticipant>> JoinCall(long channel, bool muted = false) => Call(() => Hub.InvokeAsync<List<CallParticipant>>("JoinCall", channel, muted));
    public Task LeaveCall() => hub is { State: HubConnectionState.Connected } h ? Call(() => h.InvokeAsync("LeaveCall")) : Task.CompletedTask;
    public Task SetMuted(bool muted) => Call(() => Hub.InvokeAsync("SetMuted", muted));
    public Task<List<VoiceState>> CallStates(long group) => Call(() => Hub.InvokeAsync<List<VoiceState>>("CallStates", group));
    public Task SendAudio(byte[] frame) => hub is { State: HubConnectionState.Connected } h ? h.SendAsync("Audio", frame) : Task.CompletedTask;

    public async ValueTask DisposeAsync() { if (hub is not null) await hub.DisposeAsync(); }
}
