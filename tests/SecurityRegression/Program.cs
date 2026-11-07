using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using PCBoost;
using PCBoost.Server;
using PCBoost.Shared;
using PCBoost.Team;

if (args.Contains("--child")) { await Task.Delay(60000); return; }
string sdk = args[0], serverDll = Path.GetFullPath(args[1]);
string root = Path.Combine(Path.GetTempPath(), "ximbuismo-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool valid, string label) { if (!valid) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
async Task Until(Func<bool> test, string label)
{
    for (int i = 0; i < 200 && !test(); i++) await Task.Delay(50);
    Check(test(), label);
}
async Task Rejected(Func<Task> action, string label)
{
    Console.WriteLine("CHECK: " + label);
    try { await action().WaitAsync(TimeSpan.FromSeconds(15)); }
    catch (TimeoutException) { throw; }
    catch { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
string url = $"http://127.0.0.1:{port}";
var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
start.Environment["PCBOOST_DATA"] = Path.Combine(root, "team.db");
start.Environment["PCBOOST_BACKUP_DIR"] = Path.Combine(root, "scheduled-backups");
start.Environment["PCBOOST_KNOWN_PROXIES"] = "";
start.Environment["Logging__LogLevel__Default"] = "Warning";
using var server = Process.Start(start)!;
Console.WriteLine($"Test server PID {server.Id}, test runner PID {Environment.ProcessId}, URL {url}");
Task<string> stdout = server.StandardOutput.ReadToEndAsync(), stderr = server.StandardError.ReadToEndAsync();
var clients = new List<HubConnection>();
try
{
    using var http = new HttpClient { BaseAddress = new Uri(url) };
    bool healthy = false;
    for (int i = 0; i < 100; i++)
    {
        try { healthy = (await http.GetAsync("/api/health")).IsSuccessStatusCode; } catch (HttpRequestException) { }
        if (healthy || server.HasExited) break;
        await Task.Delay(100);
    }
    Check(healthy, "server startup");
    async Task<AuthResponse> Register(string id) => (await (await http.PostAsJsonAsync("/api/register", new AuthRequest(id + "@test.example", "secret-pass-123", id))).Content.ReadFromJsonAsync<AuthResponse>())!;
    var alice = await Register("Alice"); var bob = await Register("Bob");
    async Task<HubConnection> Connect(string token)
    {
        var hub = new HubConnectionBuilder().WithUrl(url + "/hub", o => o.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();
        clients.Add(hub); await hub.StartAsync(); return hub;
    }
    var a = await Connect(alice.Token); var b = await Connect(bob.Token); var b2 = await Connect(bob.Token);
    var group = await a.InvokeAsync<GroupDto>("CreateGroup", "private");
    await b.InvokeAsync<GroupDto>("JoinGroup", group.Code);
    var channels = await a.InvokeAsync<List<ChannelDto>>("Channels", group.Id);
    long text = channels.Single(c => c.Kind == ChannelKind.Text).Id, voice = channels.Single(c => c.Kind == ChannelKind.Voice).Id;
    var messages = new ConcurrentQueue<MessageDto>(); var sound = new ConcurrentQueue<byte[]>();
    b2.On<MessageDto>("Message", messages.Enqueue); b2.On<long, byte[]>("Audio", (_, f) => sound.Enqueue(f));
    await a.InvokeAsync("Send", text, "before"); await Until(() => messages.Count == 1, "joining synchronizes all account connections");
    var members = await a.InvokeAsync<JsonElement>("Members", group.Id);
    Check(members.EnumerateArray().All(m => !m.TryGetProperty("email", out _)), "members JSON contains no email");
    await a.InvokeAsync<List<CallParticipant>>("JoinCall", voice, false);
    await b.InvokeAsync<List<CallParticipant>>("JoinCall", voice, false);
    await b2.InvokeAsync<List<CallParticipant>>("JoinCall", voice, false);
    byte[] frame = new byte[1280];
    await a.InvokeAsync("Audio", frame); await Until(() => sound.Count == 1, "valid PCM audio delivered");
    await a.InvokeAsync("SetMuted", true); await a.InvokeAsync("Audio", frame); await Task.Delay(200);
    Check(sound.Count == 1, "muted direct hub audio is blocked");
    await a.InvokeAsync("SetMuted", false);
    await a.InvokeAsync("Audio", new byte[3]); await a.InvokeAsync("Audio", new byte[8194]); await Task.Delay(100);
    Check(sound.Count == 1, "odd and oversized PCM packets blocked");
    var audioClock = Stopwatch.StartNew();
    for (int i = 0; i < 80; i++) await a.InvokeAsync("Audio", frame);
    await Task.Delay(200);
    Check(sound.Count <= 1 + 31 * ((int)Math.Ceiling(audioClock.Elapsed.TotalSeconds) + 1), "audio bandwidth and packet rate bounded");
    int leftNotifications = 0;
    b.On<long>("GroupLeft", _ => Interlocked.Increment(ref leftNotifications)); b2.On<long>("GroupLeft", _ => Interlocked.Increment(ref leftNotifications));
    await b.InvokeAsync("LeaveGroup", group.Id);
    await Until(() => leftNotifications == 2, "all sessions notified to clear local group and voice state");
    int messageCount = messages.Count, audioCount = sound.Count;
    await a.InvokeAsync("Send", text, "after"); await a.InvokeAsync("Audio", frame); await Task.Delay(200);
    Check(messages.Count == messageCount && sound.Count == audioCount, "all sessions stop receiving messages and audio after group leave");
    int staleAudio = 0; a.On<long, byte[]>("Audio", (_, _) => Interlocked.Increment(ref staleAudio));
    await b2.InvokeAsync("Audio", frame); await Task.Delay(100);
    Check(staleAudio == 0, "stale call cannot transmit after group leave");
    await Rejected(() => b2.InvokeAsync<List<MessageDto>>("History", text, (long?)null), "other connection cannot read history after leave");
    await b.InvokeAsync<GroupDto>("JoinGroup", group.Code);
    await b2.InvokeAsync<List<CallParticipant>>("JoinCall", voice, false);
    await a.InvokeAsync("DeleteChannel", voice);
    await b2.InvokeAsync("Audio", frame);
    Check((await a.InvokeAsync<List<VoiceState>>("CallStates", group.Id)).Count == 0, "deleted channel removes all callers");
    using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/logout"))
    {
        logout.Headers.Authorization = new("Bearer", bob.Token);
        Check((await http.SendAsync(logout)).IsSuccessStatusCode, "logout accepted");
    }
    await Until(() => b.State == HubConnectionState.Disconnected && b2.State == HubConnectionState.Disconnected, "logout actively disconnects every socket sharing the token");
    await Rejected(async () => { var revoked = await Connect(bob.Token); }, "revoked token cannot reconnect");
    var db = new Db(Path.Combine(root, "team.db"));
    string legacyToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    using (var legacy = new SqliteConnection("Data Source=" + db.PathOnDisk))
    {
        legacy.Open(); using var insert = legacy.CreateCommand();
        insert.CommandText = "INSERT INTO tokens(token,user_id,created) VALUES($t,$u,$c)";
        insert.Parameters.AddWithValue("$t", legacyToken); insert.Parameters.AddWithValue("$u", alice.User.Id); insert.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O")); insert.ExecuteNonQuery();
    }
    _ = new Db(db.PathOnDisk);
    Check(db.UserByToken(legacyToken)?.Id == alice.User.Id, "legacy plaintext token migrated without invalidating session");
    using (var connection = new SqliteConnection("Data Source=" + db.PathOnDisk))
    {
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT token FROM tokens";
        using var reader = command.ExecuteReader(); bool hashed = true;
        while (reader.Read()) hashed &= reader.GetString(0).StartsWith("sha256:") && reader.GetString(0) != alice.Token;
        Check(hashed, "database stores session hashes only");
    }
    string backup = DatabaseBackups.Create(db, Path.Combine(root, "manual-backups"));
    DatabaseBackups.Verify(backup);
    var restored = new Db(backup);
    Check(restored.UserByToken(alice.Token)?.Name == "Alice" && restored.Groups(alice.User.Id).Count == 1 && restored.Messages(text, null, 80).Any(m => m.Text == "after"), "WAL backup restores accounts, sessions, membership and recent messages in separate database");
    var attempts = new AccountAttempts();
    for (int i = 0; i < 10; i++) Check(attempts.Allow("shared@example.test"), "account quota allows request " + (i + 1));
    Check(!attempts.Allow(" SHARED@example.test "), "account limit normalizes case and whitespace"); attempts.Dispose();
    var traffic = new Presence(); bool aborted = false;
    traffic.Track("flood", "session", () => aborted = true); traffic.SetCall("flood", 1, 1, new CallParticipant(1, "flood", false));
    int acceptedPackets = 0;
    for (int i = 0; i < 200; i++) if (traffic.AllowAudio("flood", 1280)) acceptedPackets++;
    Check(acceptedPackets == 31 && aborted, "byte quota enforced and persistent audio flooding aborts the abusive connection");
    HttpStatusCode last = HttpStatusCode.OK;
    for (int i = 0; i < 12; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = JsonContent.Create(new AuthRequest($"unknown{i}@example.test", "wrong-password")) };
        request.Headers.Add("X-Forwarded-For", "203.0.113." + (i + 1)); last = (await http.SendAsync(request)).StatusCode;
    }
    Check(last == HttpStatusCode.TooManyRequests, "forged forwarding headers cannot bypass IP login quota");
    foreach (string address in new[] { "http://example.org", "http://192.168.1.2", "http://localhost.evil.example" })
        await Rejected(() => Task.Run(() => TeamClient.NormalizeServer(address)), "remote HTTP rejected: " + address);
    Check(TeamClient.NormalizeServer("example.org") == "https://example.org" && TeamClient.NormalizeServer("http://[::1]:5080").StartsWith("http:"), "HTTPS default and IPv6 loopback development supported");
    var b3 = await Connect(db.IssueToken(bob.User.Id));
    var newVoice = await a.InvokeAsync<ChannelDto>("CreateChannel", group.Id, "new voice", ChannelKind.Voice);
    await a.InvokeAsync<List<CallParticipant>>("JoinCall", newVoice.Id, false);
    await b3.InvokeAsync<List<CallParticipant>>("JoinCall", newVoice.Id, false);
    int offline = 0; a.On<long, long, bool>("Presence", (_, user, online) => { if (user == bob.User.Id && !online) Interlocked.Increment(ref offline); });
    await b3.DisposeAsync();
    await Until(() => offline > 0, "disconnect removes online presence");
    Check((await a.InvokeAsync<List<VoiceState>>("CallStates", group.Id)).Single().Participants.Count == 1, "disconnect removes voice participant");
    using (var expire = new SqliteConnection("Data Source=" + db.PathOnDisk))
    {
        expire.Open(); using var command = expire.CreateCommand();
        command.CommandText = "UPDATE tokens SET created=$c WHERE token=$t";
        command.Parameters.AddWithValue("$c", DateTime.UtcNow.AddDays(-31).ToString("O")); command.Parameters.AddWithValue("$t", Db.SessionHash(alice.Token)); command.ExecuteNonQuery();
    }
    await Until(() => a.State == HubConnectionState.Disconnected, "expired idle session is actively disconnected");

    var sent = new ConcurrentQueue<byte>(); var firstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var queue = new AudioSendQueue(async f => { sent.Enqueue(f[0]); firstSend.TrySetResult(); await release.Task; }))
    {
        queue.Enqueue([0]); await firstSend.Task;
        for (byte i = 1; i <= 20; i++) queue.Enqueue([i]);
        release.SetResult(); await Until(() => sent.Count == 6, "bounded queue has one sender plus five queued frames");
        Check(sent.SequenceEqual(new byte[] { 0, 16, 17, 18, 19, 20 }), "congestion discards oldest audio frames");
    }
    using (var queue = new AudioSendQueue(_ => Task.FromException(new IOException("network failed"))))
    {
        Exception? failure = null; queue.Failed += e => failure = e; queue.Enqueue([1]);
        await Until(() => failure is not null, "audio send exceptions observed"); await queue.Completion;
        Check(!queue.Enqueue([2]), "failed sender rejects further audio");
    }
    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var afterMute = new ConcurrentQueue<byte>();
    using (var queue = new AudioSendQueue(async f => { afterMute.Enqueue(f[0]); started.TrySetResult(); await blocked.Task; }))
    {
        queue.Enqueue([0]); await started.Task; queue.Enqueue([1]); queue.Muted = true; queue.Muted = false; queue.Enqueue([2]); blocked.SetResult();
        await Until(() => afterMute.Count == 2, "mute discards buffered audio even after unmuting");
        Check(afterMute.SequenceEqual(new byte[] { 0, 2 }), "only fresh audio is sent after unmute");
    }
    using var rsa = RSA.Create(2048);
    byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest("1.0.1", "https://example.org/setup.exe", 100, new string('A', 64)));
    byte[] signature = rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    string key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    Check(UpdateTrust.VerifyManifest(manifest, signature, key).Version == "1.0.1", "signed update manifest verified");
    manifest[10] ^= 1;
    await Rejected(() => Task.Run(() => UpdateTrust.VerifyManifest(manifest, signature, key)), "tampered update manifest rejected");
    await Rejected(() => Task.Run(() => UpdateTrust.VerifyInstaller(serverDll)), "unsigned installer never accepted");
    await Rejected(() => Task.Run(() => UpdateTrust.VerifyAuthenticode(serverDll, new string('A', 64))), "native Authenticode rejects unsigned binary even with configured publisher");

    // Certificado próprio (autoassinado, raiz desconhecida do Windows): só o certificado fixado passa, e só com o arquivo íntegro.
    string signedCopy = Path.Combine(root, "self-signed.dll"); File.Copy(serverDll, signedCopy);
    var signer = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    // Chamado de dentro do PowerShell 7 (runner do GitHub), o 5.1 herdaria o PSModulePath do 7 e perderia o drive Cert:.
    signer.Environment.Remove("PSModulePath");
    foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference='Stop'; Import-Module Microsoft.PowerShell.Security, PKI; $c =New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Ximbuismo Regression' -CertStoreLocation Cert:\\CurrentUser\\My -KeyAlgorithm RSA -KeyLength 2048; " +
        $"try {{ $s = Set-AuthenticodeSignature -LiteralPath '{signedCopy}' -Certificate $c -HashAlgorithm SHA256; if (-not $s.SignerCertificate) {{ throw 'nao assinou' }} }} finally {{ Remove-Item ('Cert:\\CurrentUser\\My\\' + $c.Thumbprint) -Force }}" })
        signer.ArgumentList.Add(arg);
    using (var signing = Process.Start(signer)!) { string error = await signing.StandardError.ReadToEndAsync(); await signing.WaitForExitAsync(); Check(signing.ExitCode == 0, "self-signed test binary created " + error.Trim()); }
#pragma warning disable SYSLIB0057
    string selfPin; using (var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(signedCopy))) selfPin = cert.GetCertHashString(HashAlgorithmName.SHA256);
#pragma warning restore SYSLIB0057
    UpdateTrust.VerifyAuthenticode(signedCopy, selfPin); Check(true, "self-signed installer accepted with its pinned certificate");
    await Rejected(() => Task.Run(() => UpdateTrust.VerifyAuthenticode(signedCopy, new string('A', 64))), "self-signed installer rejected with another pinned certificate");
    string tamperedCopy = Path.Combine(root, "self-signed-tampered.dll"); byte[] tampered = File.ReadAllBytes(signedCopy); tampered[tampered.Length / 3] ^= 0xFF; File.WriteAllBytes(tamperedCopy, tampered);
    await Rejected(() => Task.Run(() => UpdateTrust.VerifyAuthenticode(tamperedCopy, selfPin)), "tampered self-signed installer rejected even with pinned certificate");

    string boostRoot = Path.Combine(root, "boost"); Directory.CreateDirectory(boostRoot);
    var childStart = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true };
    childStart.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); childStart.ArgumentList.Add("--child");
    using var child = Process.Start(childStart)!;
    try
    {
        child.PriorityClass = ProcessPriorityClass.Normal;
        var boost = new AutoBoost(boostRoot);
        var remember = typeof(AutoBoost).GetMethod("Remember", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string path = child.MainModule!.FileName!;
        var isMain = typeof(AutoBoost).GetMethod("IsMainGame", BindingFlags.NonPublic | BindingFlags.Static)!;
        string candidate = Path.Combine(root, "games", "game.exe");
        var gameEntry = new GameEntry("test", "test", "MANUAL", Path.GetDirectoryName(candidate)!, candidate, null, null);
        Check((bool)isMain.Invoke(null, [child, candidate, gameEntry])!, "explicit main game executable is eligible");
        Check(!(bool)isMain.Invoke(null, [child, Path.Combine(gameEntry.InstallDir, "helper.exe"), gameEntry])!, "helper executable in game folder is excluded");
        Check(!(bool)isMain.Invoke(null, [child, candidate, gameEntry with { Exe = candidate + ".other.exe" }])!, "other executables in game folder are not raised");
        Check(!(bool)isMain.Invoke(null, [child, path, gameEntry with { Exe = path }])!, "sensitive development process remains exempt even when selected");
        var inside = typeof(AutoBoost).GetMethod("IsInGameDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(!(bool)inside.Invoke(null, [@"C:\Games\FooOther\game.exe", gameEntry with { InstallDir = @"C:\Games\Foo" }])!, "game folder matching respects directory boundaries");
        remember.Invoke(boost, [child, path]);
        Check(File.ReadAllText(Path.Combine(boostRoot, "boost-restore.json")).Contains("Processes"), "process identity and original priority persisted before mutation");
        child.PriorityClass = ProcessPriorityClass.BelowNormal;
        remember.Invoke(boost, [child, path]); child.PriorityClass = ProcessPriorityClass.High;
        boost.Deactivate(); child.Refresh();
        Check(child.PriorityClass == ProcessPriorityClass.Normal, "lowered then raised process restores its first original priority");
        remember.Invoke(boost, [child, path]); child.PriorityClass = ProcessPriorityClass.BelowNormal;
        var recovered = new AutoBoost(boostRoot); child.Refresh();
        Check(child.PriorityClass == ProcessPriorityClass.Normal && recovered.State == BoostState.Idle, "crash journal restores child priority on next startup");
        File.WriteAllText(Path.Combine(boostRoot, "boost-restore.json"), JsonSerializer.Serialize(new { Processes = new[] { new { Pid = child.Id, Started = child.StartTime.ToUniversalTime().AddSeconds(-5), Path = path, Before = ProcessPriorityClass.High } } }));
        _ = new AutoBoost(boostRoot); child.Refresh();
        Check(child.PriorityClass == ProcessPriorityClass.Normal, "reused PID identity is never modified during recovery");
        object gate = typeof(AutoBoost).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(recovered)!;
        Task activating;
        lock (gate) { activating = recovered.Activate(); Check(recovered.State == BoostState.Activating, "activation state published before asynchronous work"); recovered.Deactivate(); }
        await Rejected(() => activating, "closing during activation cancels pending work");
        Check(recovered.State == BoostState.Idle && !File.Exists(Path.Combine(boostRoot, "boost-restore.json")), "cancelled activation leaves no changes or journal");
    }
    finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    Console.WriteLine("ALL SECURITY REGRESSIONS PASSED");
}
catch
{
    if (!server.HasExited) server.Kill();
    await server.WaitForExitAsync(); Console.Error.WriteLine(await stdout); Console.Error.WriteLine(await stderr); throw;
}
finally
{
    foreach (var client in clients) await client.DisposeAsync();
    if (!server.HasExited) { server.Kill(); await server.WaitForExitAsync(); }
    SqliteConnection.ClearAllPools();
    // Mantém os arquivos de diagnóstico no diretório temporário, sem dados de usuários reais.
    Console.WriteLine("Test artifacts: " + root);
}
