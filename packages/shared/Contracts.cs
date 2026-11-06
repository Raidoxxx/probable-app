namespace PCBoost.Shared;

// Contratos trocados entre o app (WPF) e o servidor (ASP.NET Core + SignalR).
// "Group" no código = "servidor" na interface (como no Discord); cada um tem canais de texto, voz e quadro.
public sealed record AuthRequest(string Email, string Password, string? Name = null);
public sealed record AuthResponse(string Token, UserDto User);
public sealed record ErrorResponse(string Error);

public sealed record UserDto(long Id, string Email, string Name);
public sealed record GroupDto(long Id, string Name, string Code, long OwnerId, int Members);
public sealed record MemberDto(long UserId, string Name, string Email, string Role, bool Online);

public static class ChannelKind
{
    public const string Text = "texto", Voice = "voz", Board = "quadro";
    public static readonly string[] All = [Text, Voice, Board];
}
public sealed record ChannelDto(long Id, long GroupId, string Name, string Kind, int Position);
public sealed record MessageDto(long Id, long GroupId, long ChannelId, long UserId, string Author, string Text, DateTime At);

public static class IdeaStatus
{
    public const string Backlog = "ideia", Doing = "andamento", Done = "concluido";
    public static readonly string[] All = [Backlog, Doing, Done];
    public static string Label(string status) => status switch { Doing => "EM ANDAMENTO", Done => "CONCLUÍDO", _ => "IDEIAS" };
}
public static class IdeaKind
{
    public const string Idea = "ideia", Task = "tarefa", Bug = "problema";
    public static readonly string[] All = [Idea, Task, Bug];
}
public static class IdeaPriority
{
    public const string Low = "baixa", Normal = "normal", High = "alta";
    public static readonly string[] All = [Low, Normal, High];
}
public sealed record IdeaDto(long Id, long GroupId, long ChannelId, string Title, string Body, string Status, string Kind, string Priority,
    long AuthorId, string Author, long? AssigneeId, string? Assignee, int Votes, bool Voted, DateTime Created, DateTime Updated);
public sealed record IdeaInput(long Id, long ChannelId, string Title, string Body, string Status, string Kind, string Priority, long? AssigneeId);

public sealed record CallParticipant(long UserId, string Name, bool Muted);
public sealed record VoiceState(long ChannelId, List<CallParticipant> Participants);
