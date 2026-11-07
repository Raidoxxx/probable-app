using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using PCBoost.Shared;

namespace PCBoost.Server;

// Estado em memória: conexões ativas (presença) e quem está em cada canal de voz.
public sealed class Presence
{
    // Serializa mudanças de participação com entrada, saída e envio pelo hub.
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Session, Action Abort)> sessions = new();
    public void Track(string id, string session, Action abort) => sessions[id] = (session, abort);
    public string? Session(string id) => sessions.TryGetValue(id, out var s) ? s.Session : null;
    public string[] Connections(long userId) => connections.Where(c => c.Value.Id == userId).Select(c => c.Key).ToArray();
    public void Revoke(string session) { foreach (var s in sessions.Values.Where(s => s.Session == session)) s.Abort(); }
    public void Expire(Db db) { foreach (var s in sessions.Values) if (db.UserBySession(s.Session) is null) s.Abort(); }
    private readonly Dictionary<string, (long At, int Packets, int Bytes, int Rejected)> traffic = [];
    public bool AllowAudio(string id, int bytes)
    {
        lock (callGate)
        {
            if (!callers.TryGetValue(id, out var call) || call.Who.Muted) return false;
            long now = Environment.TickCount64;
            var t = traffic.GetValueOrDefault(id);
            if (now - t.At >= 1000) t = (now, 0, 0, 0);
            if (t.Packets >= 35 || t.Bytes + bytes > 40000)
            {
                traffic[id] = (t.At, t.Packets, t.Bytes, t.Rejected + 1);
                if (t.Rejected >= 99 && sessions.TryGetValue(id, out var session)) session.Abort();
                return false;
            }
            traffic[id] = (t.At, t.Packets + 1, t.Bytes + bytes, t.Rejected);
            return true;
        }
    }
    private readonly ConcurrentDictionary<string, UserDto> connections = new();
    private readonly ConcurrentDictionary<long, int> online = new();
    private readonly object callGate = new();
    private readonly Dictionary<string, (long GroupId, long ChannelId, CallParticipant Who)> callers = [];
    public bool IsOnline(long userId) => online.TryGetValue(userId, out int n) && n > 0;
    public bool Connect(string connectionId, UserDto user) { connections[connectionId] = user; return online.AddOrUpdate(user.Id, 1, (_, n) => n + 1) == 1; }
    public bool Disconnect(string connectionId, out UserDto? user)
    {
        sessions.TryRemove(connectionId, out _);
        lock (callGate) traffic.Remove(connectionId);
        if (!connections.TryRemove(connectionId, out user)) return false;
        return online.AddOrUpdate(user.Id, 0, (_, n) => Math.Max(0, n - 1)) == 0;
    }
    public UserDto? User(string connectionId) => connections.TryGetValue(connectionId, out var user) ? user : null;
    public (long GroupId, long ChannelId)? CallOf(string connectionId) { lock (callGate) return callers.TryGetValue(connectionId, out var c) ? (c.GroupId, c.ChannelId) : null; }
    public void SetCall(string connectionId, long groupId, long channelId, CallParticipant who) { lock (callGate) callers[connectionId] = (groupId, channelId, who); }
    public (long GroupId, long ChannelId)? LeaveCall(string connectionId) { lock (callGate) return callers.Remove(connectionId, out var c) ? (c.GroupId, c.ChannelId) : null; }
    public List<string> RemoveChannel(long channelId)
    {
        lock (callGate)
        {
            var gone = callers.Where(c => c.Value.ChannelId == channelId).Select(c => c.Key).ToList();
            foreach (var id in gone) callers.Remove(id);
            return gone;
        }
    }
    public bool SetMuted(string connectionId, bool muted)
    {
        lock (callGate)
        {
            if (!callers.TryGetValue(connectionId, out var c)) return false;
            callers[connectionId] = (c.GroupId, c.ChannelId, c.Who with { Muted = muted }); return true;
        }
    }
    public List<CallParticipant> Participants(long channelId)
    { lock (callGate) return callers.Values.Where(c => c.ChannelId == channelId).Select(c => c.Who).DistinctBy(w => w.UserId).ToList(); }
    public List<VoiceState> GroupCalls(long groupId)
    {
        lock (callGate)
            return callers.Values.Where(c => c.GroupId == groupId).GroupBy(c => c.ChannelId)
                .Select(g => new VoiceState(g.Key, g.Select(c => c.Who).DistinctBy(w => w.UserId).ToList())).ToList();
    }
}

public sealed class TeamHub(Db db, Presence presence, ILogger<TeamHub> log) : Hub
{
    public const int MaxAudioFrame = 8192;
    public const string UserKey = "pcboost.user";
    private static string G(long groupId) => "g:" + groupId;   // todos os membros do servidor
    private static string V(long channelId) => "v:" + channelId; // quem está no canal de voz
    private UserDto Me => presence.Session(Context.ConnectionId) is string session && db.UserBySession(session) is UserDto user
        ? user : throw new HubException("Sessão inválida.");
    private void Require(long groupId) { if (!db.IsMember(Me.Id, groupId)) throw new HubException("Você não participa deste servidor."); }
    private ChannelDto RequireChannel(long channelId, string? kind = null)
    {
        var channel = db.Channel(channelId) ?? throw new HubException("Canal não encontrado.");
        Require(channel.GroupId);
        if (kind is not null && channel.Kind != kind) throw new HubException("Este canal não é do tipo certo para esta ação.");
        return channel;
    }
    private static string Clean(string? text, int max, string field)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new HubException(field + " não pode ficar vazio.");
        return text.Length > max ? throw new HubException($"{field}: máximo de {max} caracteres.") : text;
    }

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[UserKey] is not UserDto user) { Context.Abort(); return; }
        string session = (string)Context.GetHttpContext()!.Items[SessionFilter.SessionKey]!;
        if (db.UserBySession(session) is null) { Context.Abort(); return; }
        presence.Track(Context.ConnectionId, session, Context.Abort);
        bool first = presence.Connect(Context.ConnectionId, user);
        foreach (var group in db.Groups(user.Id))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, G(group.Id));
            if (first) await Clients.OthersInGroup(G(group.Id)).SendAsync("Presence", group.Id, user.Id, true);
        }
        await base.OnConnectedAsync();
    }
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (presence.LeaveCall(Context.ConnectionId) is (long g, long c)) await BroadcastCall(g, c);
        if (presence.Disconnect(Context.ConnectionId, out var user) && user is not null)
            foreach (var group in db.Groups(user.Id)) await Clients.Group(G(group.Id)).SendAsync("Presence", group.Id, user.Id, false);
        await base.OnDisconnectedAsync(exception);
    }

    // ---------- Servidores ----------
    public UserDto WhoAmI() => Me;
    public List<GroupDto> MyGroups() => db.Groups(Me.Id);
    public async Task<GroupDto> CreateGroup(string name)
    {
        var group = db.CreateGroup(Me.Id, Clean(name, 60, "Nome do servidor"));
        foreach (string id in presence.Connections(Me.Id)) await Groups.AddToGroupAsync(id, G(group.Id));
        log.LogInformation("Servidor {Id} criado por {User}", group.Id, Me.Id);
        return group;
    }
    public async Task<GroupDto> JoinGroup(string code)
    {
        GroupDto group;
        try { group = db.JoinGroup(Me.Id, Clean(code, 12, "Código")); } catch (AppError e) { throw new HubException(e.Message); }
        foreach (string id in presence.Connections(Me.Id)) await Groups.AddToGroupAsync(id, G(group.Id));
        await Clients.OthersInGroup(G(group.Id)).SendAsync("MembersChanged", group.Id);
        return group;
    }
    public async Task LeaveGroup(long groupId)
    {
        Require(groupId);
        long userId = Me.Id;
        var changed = new HashSet<long>();
        foreach (string id in presence.Connections(userId))
        {
            if (presence.CallOf(id) is (long g, long c) && g == groupId)
            {
                presence.LeaveCall(id);
                await Groups.RemoveFromGroupAsync(id, V(c));
                changed.Add(c);
            }
            await Groups.RemoveFromGroupAsync(id, G(groupId));
        }
        db.LeaveGroup(userId, groupId);
        await Clients.Clients(presence.Connections(userId)).SendAsync("GroupLeft", groupId);
        foreach (long c in changed) await BroadcastCall(groupId, c);
        await Clients.Group(G(groupId)).SendAsync("MembersChanged", groupId);
    }
    public List<MemberDto> Members(long groupId) { Require(groupId); return db.Members(groupId, presence.IsOnline); }

    // ---------- Canais ----------
    public List<ChannelDto> Channels(long groupId) { Require(groupId); return db.Channels(groupId); }
    public async Task<ChannelDto> CreateChannel(long groupId, string name, string kind)
    {
        Require(groupId);
        if (!db.IsOwner(Me.Id, groupId)) throw new HubException("Só o dono do servidor cria canais.");
        if (!ChannelKind.All.Contains(kind)) throw new HubException("Tipo de canal inválido.");
        string clean = Clean(name, 40, "Nome do canal");
        if (kind != ChannelKind.Voice) clean = string.Join('-', clean.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var channel = db.CreateChannel(groupId, clean, kind);
        await Clients.Group(G(groupId)).SendAsync("ChannelsChanged", groupId);
        return channel;
    }
    public async Task DeleteChannel(long channelId)
    {
        var channel = RequireChannel(channelId);
        if (!db.IsOwner(Me.Id, channel.GroupId)) throw new HubException("Só o dono do servidor apaga canais.");
        try { db.DeleteChannel(channel); } catch (AppError e) { throw new HubException(e.Message); }
        foreach (var connection in presence.RemoveChannel(channelId)) await Groups.RemoveFromGroupAsync(connection, V(channelId));
        await Clients.Group(G(channel.GroupId)).SendAsync("ChannelsChanged", channel.GroupId);
        await BroadcastCall(channel.GroupId, channelId);
    }

    // ---------- Mensagens ----------
    public List<MessageDto> History(long channelId, long? before) { RequireChannel(channelId, ChannelKind.Text); return db.Messages(channelId, before, 80); }
    public async Task Send(long channelId, string text)
    {
        var channel = RequireChannel(channelId, ChannelKind.Text);
        var message = db.AddMessage(channel, Me, Clean(text, 4000, "Mensagem"));
        await Clients.Group(G(channel.GroupId)).SendAsync("Message", message);
    }

    // ---------- Quadro ----------
    public List<IdeaDto> Ideas(long channelId) { RequireChannel(channelId, ChannelKind.Board); return db.Ideas(channelId, Me.Id); }
    public IdeaDto? Idea(long ideaId)
    {
        if (db.IdeaChannel(ideaId) is not long channel) return null;
        RequireChannel(channel); return db.Idea(ideaId, Me.Id);
    }
    public async Task<IdeaDto> SaveIdea(IdeaInput input)
    {
        var channel = RequireChannel(input.ChannelId, ChannelKind.Board);
        if (input.Id != 0 && db.IdeaChannel(input.Id) != channel.Id) throw new HubException("Item não encontrado neste quadro.");
        if (!IdeaStatus.All.Contains(input.Status) || !IdeaKind.All.Contains(input.Kind) || !IdeaPriority.All.Contains(input.Priority)) throw new HubException("Status, tipo ou prioridade inválidos.");
        var clean = input with { Title = Clean(input.Title, 140, "Título"), Body = (input.Body ?? "").Trim() is var b && b.Length <= 8000 ? b : throw new HubException("Descrição: máximo de 8000 caracteres.") };
        long id;
        try { id = db.SaveIdea(clean, channel, Me.Id); } catch (AppError e) { throw new HubException(e.Message); }
        await Clients.Group(G(channel.GroupId)).SendAsync("IdeaChanged", channel.GroupId, channel.Id, id);
        return db.Idea(id, Me.Id)!;
    }
    public async Task DeleteIdea(long ideaId)
    {
        if (db.IdeaChannel(ideaId) is not long channelId) return;
        var channel = RequireChannel(channelId); db.DeleteIdea(ideaId);
        await Clients.Group(G(channel.GroupId)).SendAsync("IdeaDeleted", channel.GroupId, channel.Id, ideaId);
    }
    public async Task ToggleVote(long ideaId)
    {
        if (db.IdeaChannel(ideaId) is not long channelId) throw new HubException("Item não encontrado.");
        var channel = RequireChannel(channelId); db.ToggleVote(ideaId, Me.Id);
        await Clients.Group(G(channel.GroupId)).SendAsync("IdeaChanged", channel.GroupId, channel.Id, ideaId);
    }

    // ---------- Voz: o servidor repassa quadros PCM 16 kHz mono para os demais do mesmo canal ----------
    public async Task<List<CallParticipant>> JoinCall(long channelId, bool muted = false)
    {
        var channel = RequireChannel(channelId, ChannelKind.Voice);
        if (presence.CallOf(Context.ConnectionId) is (_, long current) && current != channelId) await LeaveCall();
        presence.SetCall(Context.ConnectionId, channel.GroupId, channelId, new(Me.Id, Me.Name, muted));
        await Groups.AddToGroupAsync(Context.ConnectionId, V(channelId));
        await BroadcastCall(channel.GroupId, channelId);
        return presence.Participants(channelId);
    }
    public async Task LeaveCall()
    {
        if (presence.LeaveCall(Context.ConnectionId) is not (long groupId, long channelId)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, V(channelId));
        await BroadcastCall(groupId, channelId);
    }
    public async Task SetMuted(bool muted)
    {
        if (presence.SetMuted(Context.ConnectionId, muted) && presence.CallOf(Context.ConnectionId) is (long g, long c)) await BroadcastCall(g, c);
    }
    public List<VoiceState> CallStates(long groupId) { Require(groupId); return presence.GroupCalls(groupId); }
    public Task Audio(byte[] frame)
    {
        if (frame is null || frame.Length == 0 || frame.Length > MaxAudioFrame || frame.Length % 2 != 0 || presence.CallOf(Context.ConnectionId) is not (_, long channelId)) return Task.CompletedTask;
        RequireChannel(channelId, ChannelKind.Voice);
        if (!presence.AllowAudio(Context.ConnectionId, frame.Length)) return Task.CompletedTask;
        return Clients.OthersInGroup(V(channelId)).SendAsync("Audio", Me.Id, frame);
    }
    private Task BroadcastCall(long groupId, long channelId) => Clients.Group(G(groupId)).SendAsync("CallChanged", groupId, channelId, presence.Participants(channelId));
}
