using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PCBoost.Shared;

namespace PCBoost.Server;

// Armazenamento SQLite. Cada operação abre sua conexão (pool do Microsoft.Data.Sqlite) e usa SQL parametrizado.
// "groups" = servidores na interface; cada servidor tem canais (texto, voz, quadro).
public sealed class Db
{
    private readonly string connection;
    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var db = Open();
        Exec(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL, hash BLOB NOT NULL, salt BLOB NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tokens(token TEXT PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS groups(id INTEGER PRIMARY KEY, name TEXT NOT NULL, code TEXT NOT NULL UNIQUE, owner_id INTEGER NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS members(group_id INTEGER NOT NULL, user_id INTEGER NOT NULL, role TEXT NOT NULL, joined TEXT NOT NULL, PRIMARY KEY(group_id, user_id));
            CREATE TABLE IF NOT EXISTS channels(id INTEGER PRIMARY KEY, group_id INTEGER NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, position INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY, group_id INTEGER NOT NULL, user_id INTEGER NOT NULL, text TEXT NOT NULL, at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ideas(id INTEGER PRIMARY KEY, group_id INTEGER NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL, status TEXT NOT NULL, kind TEXT NOT NULL, priority TEXT NOT NULL,
                author_id INTEGER NOT NULL, assignee_id INTEGER NULL, created TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS votes(idea_id INTEGER NOT NULL, user_id INTEGER NOT NULL, PRIMARY KEY(idea_id, user_id));
            """);
        Migrate(db);
    }
    // Bancos da versão anterior (um chat e um quadro por servidor) ganham canais padrão; o conteúdo antigo vai para #geral e #ideias.
    private static void Migrate(SqliteConnection db)
    {
        bool Has(string table, string column) => Query(db, r => r.GetString(1), $"PRAGMA table_info({table})").Contains(column);
        if (!Has("messages", "channel_id")) Exec(db, "ALTER TABLE messages ADD COLUMN channel_id INTEGER");
        if (!Has("ideas", "channel_id")) Exec(db, "ALTER TABLE ideas ADD COLUMN channel_id INTEGER");
        Exec(db, "CREATE INDEX IF NOT EXISTS messages_channel ON messages(channel_id, id)");
        Exec(db, "CREATE INDEX IF NOT EXISTS channels_group ON channels(group_id, position)");
        foreach (long group in Query(db, r => r.GetInt64(0), "SELECT id FROM groups g WHERE NOT EXISTS(SELECT 1 FROM channels c WHERE c.group_id=g.id)"))
            DefaultChannels(db, group);
        Exec(db, "UPDATE messages SET channel_id=(SELECT c.id FROM channels c WHERE c.group_id=messages.group_id AND c.kind='texto' ORDER BY c.position LIMIT 1) WHERE channel_id IS NULL");
        Exec(db, "UPDATE ideas SET channel_id=(SELECT c.id FROM channels c WHERE c.group_id=ideas.group_id AND c.kind='quadro' ORDER BY c.position LIMIT 1) WHERE channel_id IS NULL");
    }
    private static void DefaultChannels(SqliteConnection db, long group)
    {
        int position = 0;
        foreach (var (name, kind) in new[] { ("geral", ChannelKind.Text), ("ideias", ChannelKind.Board), ("Geral", ChannelKind.Voice) })
            Exec(db, "INSERT INTO channels(group_id,name,kind,position) VALUES($g,$n,$k,$p)", ("$g", group), ("$n", name), ("$k", kind), ("$p", position++));
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); using var fk = db.CreateCommand(); fk.CommandText = "PRAGMA foreign_keys=ON"; fk.ExecuteNonQuery(); return db; }
    private static SqliteCommand Cmd(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static int Exec(SqliteConnection db, string sql, params (string, object?)[] args) { using var cmd = Cmd(db, sql, args); return cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql, params (string, object?)[] args) { using var cmd = Cmd(db, sql, args); return cmd.ExecuteScalar(); }
    private static List<T> Query<T>(SqliteConnection db, Func<SqliteDataReader, T> map, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(db, sql, args); using var reader = cmd.ExecuteReader(); var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }
    private static string Now() => DateTime.UtcNow.ToString("O");
    private static DateTime Date(SqliteDataReader r, int i) => DateTime.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);

    // ---------- Contas ----------
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA512, 32);
    public UserDto Register(string email, string name, string password)
    {
        using var db = Open();
        if (Scalar(db, "SELECT 1 FROM users WHERE email=$e", ("$e", email)) is not null) throw new AppError("Este e-mail já tem conta.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        Exec(db, "INSERT INTO users(email,name,hash,salt,created) VALUES($e,$n,$h,$s,$c)", ("$e", email), ("$n", name), ("$h", Hash(password, salt)), ("$s", salt), ("$c", Now()));
        return new((long)Scalar(db, "SELECT last_insert_rowid()")!, email, name);
    }
    public UserDto? Verify(string email, string password)
    {
        using var db = Open();
        var row = Query(db, r => (Id: r.GetInt64(0), Email: r.GetString(1), Name: r.GetString(2), Hash: (byte[])r[3], Salt: (byte[])r[4]),
            "SELECT id,email,name,hash,salt FROM users WHERE email=$e", ("$e", email)).FirstOrDefault();
        if (row.Hash is null) { Hash(password, new byte[16]); return null; } // tempo semelhante para e-mail inexistente
        return CryptographicOperations.FixedTimeEquals(row.Hash, Hash(password, row.Salt)) ? new(row.Id, row.Email, row.Name) : null;
    }
    public string IssueToken(long userId)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using var db = Open(); Exec(db, "INSERT INTO tokens(token,user_id,created) VALUES($t,$u,$c)", ("$t", token), ("$u", userId), ("$c", Now()));
        return token;
    }
    public void RevokeToken(string token) { using var db = Open(); Exec(db, "DELETE FROM tokens WHERE token=$t", ("$t", token)); }
    public UserDto? UserByToken(string token)
    {
        using var db = Open();
        return Query(db, r => new UserDto(r.GetInt64(0), r.GetString(1), r.GetString(2)),
            "SELECT u.id,u.email,u.name FROM tokens t JOIN users u ON u.id=t.user_id WHERE t.token=$t AND t.created > $min", ("$t", token), ("$min", DateTime.UtcNow.AddDays(-30).ToString("O"))).FirstOrDefault();
    }

    // ---------- Servidores ----------
    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }
    private const string GroupSelect = "SELECT g.id,g.name,g.code,g.owner_id,(SELECT COUNT(*) FROM members x WHERE x.group_id=g.id) FROM groups g";
    private static GroupDto MapGroup(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4));
    public GroupDto CreateGroup(long userId, string name)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        string code; do code = NewCode(); while (Scalar(db, "SELECT 1 FROM groups WHERE code=$c", ("$c", code)) is not null);
        Exec(db, "INSERT INTO groups(name,code,owner_id,created) VALUES($n,$c,$o,$t)", ("$n", name), ("$c", code), ("$o", userId), ("$t", Now()));
        long id = (long)Scalar(db, "SELECT last_insert_rowid()")!;
        Exec(db, "INSERT INTO members(group_id,user_id,role,joined) VALUES($g,$u,'dono',$t)", ("$g", id), ("$u", userId), ("$t", Now()));
        DefaultChannels(db, id);
        tx.Commit();
        return Query(db, MapGroup, GroupSelect + " WHERE g.id=$g", ("$g", id)).Single();
    }
    public GroupDto JoinGroup(long userId, string code)
    {
        using var db = Open();
        var group = Query(db, MapGroup, GroupSelect + " WHERE g.code=$c", ("$c", code.Trim().ToUpperInvariant())).FirstOrDefault() ?? throw new AppError("Código de convite não encontrado.");
        Exec(db, "INSERT OR IGNORE INTO members(group_id,user_id,role,joined) VALUES($g,$u,'membro',$t)", ("$g", group.Id), ("$u", userId), ("$t", Now()));
        return Query(db, MapGroup, GroupSelect + " WHERE g.id=$g", ("$g", group.Id)).Single();
    }
    public void LeaveGroup(long userId, long groupId)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Exec(db, "DELETE FROM members WHERE group_id=$g AND user_id=$u", ("$g", groupId), ("$u", userId));
        if (Scalar(db, "SELECT COUNT(*) FROM members WHERE group_id=$g", ("$g", groupId)) is long n && n == 0)
        {
            Exec(db, "DELETE FROM votes WHERE idea_id IN (SELECT id FROM ideas WHERE group_id=$g)", ("$g", groupId));
            Exec(db, "DELETE FROM ideas WHERE group_id=$g", ("$g", groupId)); Exec(db, "DELETE FROM messages WHERE group_id=$g", ("$g", groupId));
            Exec(db, "DELETE FROM channels WHERE group_id=$g", ("$g", groupId)); Exec(db, "DELETE FROM groups WHERE id=$g", ("$g", groupId));
        }
        else if (Scalar(db, "SELECT owner_id FROM groups WHERE id=$g", ("$g", groupId)) is long owner && owner == userId)
        {
            long next = (long)Scalar(db, "SELECT user_id FROM members WHERE group_id=$g ORDER BY joined LIMIT 1", ("$g", groupId))!;
            Exec(db, "UPDATE groups SET owner_id=$u WHERE id=$g", ("$u", next), ("$g", groupId));
            Exec(db, "UPDATE members SET role='dono' WHERE group_id=$g AND user_id=$u", ("$u", next), ("$g", groupId));
        }
        tx.Commit();
    }
    public List<GroupDto> Groups(long userId)
    {
        using var db = Open();
        return Query(db, MapGroup, GroupSelect + " JOIN members m ON m.group_id=g.id AND m.user_id=$u ORDER BY m.joined", ("$u", userId));
    }
    public bool IsMember(long userId, long groupId)
    {
        using var db = Open(); return Scalar(db, "SELECT 1 FROM members WHERE group_id=$g AND user_id=$u", ("$g", groupId), ("$u", userId)) is not null;
    }
    public bool IsOwner(long userId, long groupId)
    {
        using var db = Open(); return Scalar(db, "SELECT owner_id FROM groups WHERE id=$g", ("$g", groupId)) is long owner && owner == userId;
    }
    public List<MemberDto> Members(long groupId, Func<long, bool> online)
    {
        using var db = Open();
        return Query(db, r => new MemberDto(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), online(r.GetInt64(0))),
            "SELECT u.id,u.name,u.email,m.role FROM members m JOIN users u ON u.id=m.user_id WHERE m.group_id=$g ORDER BY m.role='dono' DESC, u.name COLLATE NOCASE", ("$g", groupId));
    }

    // ---------- Canais ----------
    private static ChannelDto MapChannel(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4));
    public List<ChannelDto> Channels(long groupId)
    {
        using var db = Open(); return Query(db, MapChannel, "SELECT id,group_id,name,kind,position FROM channels WHERE group_id=$g ORDER BY position, id", ("$g", groupId));
    }
    public ChannelDto? Channel(long channelId)
    {
        using var db = Open(); return Query(db, MapChannel, "SELECT id,group_id,name,kind,position FROM channels WHERE id=$c", ("$c", channelId)).FirstOrDefault();
    }
    public ChannelDto CreateChannel(long groupId, string name, string kind)
    {
        using var db = Open();
        long position = Scalar(db, "SELECT COALESCE(MAX(position),-1)+1 FROM channels WHERE group_id=$g", ("$g", groupId)) as long? ?? 0;
        Exec(db, "INSERT INTO channels(group_id,name,kind,position) VALUES($g,$n,$k,$p)", ("$g", groupId), ("$n", name), ("$k", kind), ("$p", position));
        return Query(db, MapChannel, "SELECT id,group_id,name,kind,position FROM channels WHERE id=last_insert_rowid()").Single();
    }
    public void DeleteChannel(ChannelDto channel)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (channel.Kind == ChannelKind.Text && (long)Scalar(db, "SELECT COUNT(*) FROM channels WHERE group_id=$g AND kind='texto'", ("$g", channel.GroupId))! <= 1)
            throw new AppError("O servidor precisa de pelo menos um canal de texto.");
        Exec(db, "DELETE FROM votes WHERE idea_id IN (SELECT id FROM ideas WHERE channel_id=$c)", ("$c", channel.Id));
        Exec(db, "DELETE FROM ideas WHERE channel_id=$c", ("$c", channel.Id)); Exec(db, "DELETE FROM messages WHERE channel_id=$c", ("$c", channel.Id));
        Exec(db, "DELETE FROM channels WHERE id=$c", ("$c", channel.Id));
        tx.Commit();
    }

    // ---------- Mensagens ----------
    public MessageDto AddMessage(ChannelDto channel, UserDto user, string text)
    {
        using var db = Open(); var at = DateTime.UtcNow;
        Exec(db, "INSERT INTO messages(group_id,channel_id,user_id,text,at) VALUES($g,$c,$u,$x,$t)", ("$g", channel.GroupId), ("$c", channel.Id), ("$u", user.Id), ("$x", text), ("$t", at.ToString("O")));
        return new((long)Scalar(db, "SELECT last_insert_rowid()")!, channel.GroupId, channel.Id, user.Id, user.Name, text, at);
    }
    public List<MessageDto> Messages(long channelId, long? before, int take)
    {
        using var db = Open();
        var list = Query(db, r => new MessageDto(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5), Date(r, 6)),
            "SELECT m.id,m.group_id,m.channel_id,m.user_id,u.name,m.text,m.at FROM messages m JOIN users u ON u.id=m.user_id WHERE m.channel_id=$c AND m.id < $b ORDER BY m.id DESC LIMIT $n",
            ("$c", channelId), ("$b", before ?? long.MaxValue), ("$n", take));
        list.Reverse(); return list;
    }

    // ---------- Quadro de ideias e tarefas (por canal de quadro) ----------
    private const string IdeaSelect = """
        SELECT i.id,i.group_id,i.channel_id,i.title,i.body,i.status,i.kind,i.priority,i.author_id,a.name,i.assignee_id,s.name,
               (SELECT COUNT(*) FROM votes v WHERE v.idea_id=i.id),
               EXISTS(SELECT 1 FROM votes v WHERE v.idea_id=i.id AND v.user_id=$me), i.created, i.updated
        FROM ideas i JOIN users a ON a.id=i.author_id LEFT JOIN users s ON s.id=i.assignee_id
        """;
    private static IdeaDto MapIdea(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetInt64(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt32(12), r.GetInt64(13) == 1, Date(r, 14), Date(r, 15));
    public List<IdeaDto> Ideas(long channelId, long me)
    {
        using var db = Open(); return Query(db, MapIdea, IdeaSelect + " WHERE i.channel_id=$c ORDER BY i.updated DESC", ("$c", channelId), ("$me", me));
    }
    public IdeaDto? Idea(long id, long me)
    {
        using var db = Open(); return Query(db, MapIdea, IdeaSelect + " WHERE i.id=$i", ("$i", id), ("$me", me)).FirstOrDefault();
    }
    public long SaveIdea(IdeaInput input, ChannelDto channel, long me)
    {
        using var db = Open(); string now = Now();
        if (input.AssigneeId is long assignee && Scalar(db, "SELECT 1 FROM members WHERE group_id=$g AND user_id=$u", ("$g", channel.GroupId), ("$u", assignee)) is null)
            throw new AppError("O responsável precisa ser membro do servidor.");
        if (input.Id == 0)
        {
            Exec(db, "INSERT INTO ideas(group_id,channel_id,title,body,status,kind,priority,author_id,assignee_id,created,updated) VALUES($g,$ch,$t,$b,$s,$k,$p,$a,$r,$c,$c)",
                ("$g", channel.GroupId), ("$ch", channel.Id), ("$t", input.Title), ("$b", input.Body), ("$s", input.Status), ("$k", input.Kind), ("$p", input.Priority), ("$a", me), ("$r", input.AssigneeId), ("$c", now));
            return (long)Scalar(db, "SELECT last_insert_rowid()")!;
        }
        int changed = Exec(db, "UPDATE ideas SET title=$t,body=$b,status=$s,kind=$k,priority=$p,assignee_id=$r,updated=$c WHERE id=$i AND channel_id=$ch",
            ("$t", input.Title), ("$b", input.Body), ("$s", input.Status), ("$k", input.Kind), ("$p", input.Priority), ("$r", input.AssigneeId), ("$c", now), ("$i", input.Id), ("$ch", channel.Id));
        if (changed == 0) throw new AppError("Item não encontrado neste quadro.");
        return input.Id;
    }
    public long? IdeaChannel(long ideaId) { using var db = Open(); return Scalar(db, "SELECT channel_id FROM ideas WHERE id=$i", ("$i", ideaId)) as long?; }
    public void DeleteIdea(long ideaId)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Exec(db, "DELETE FROM votes WHERE idea_id=$i", ("$i", ideaId)); Exec(db, "DELETE FROM ideas WHERE id=$i", ("$i", ideaId)); tx.Commit();
    }
    public void ToggleVote(long ideaId, long me)
    {
        using var db = Open();
        if (Exec(db, "DELETE FROM votes WHERE idea_id=$i AND user_id=$u", ("$i", ideaId), ("$u", me)) == 0)
            Exec(db, "INSERT INTO votes(idea_id,user_id) VALUES($i,$u)", ("$i", ideaId), ("$u", me));
    }
}

public sealed class AppError(string message) : Exception(message);
