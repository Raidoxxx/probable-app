using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PCBoost;

public sealed record Preflight(bool EnvironmentValid, bool MemoryEnough, MemorySample Memory, List<string> Notes);
public sealed record StageResult(string Stage, string Result, int? ExitCode, double? Seconds);
public sealed class SessionLog : IDisposable
{
    private readonly object gate = new(); private readonly StringBuilder tail = new();
    private readonly string path; private StreamWriter writer;
    private long version;
    public long Version => Interlocked.Read(ref version);
    public SessionLog(LocalStore store)
    {
        string directory = Path.Combine(store.Root, "logs"); Directory.CreateDirectory(directory); SafePaths.NoLinks(directory);
        foreach (var old in new DirectoryInfo(directory).GetFiles("session-*.log*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(38))
            if ((old.Attributes & FileAttributes.ReparsePoint) == 0) old.Delete();
        path = Path.Combine(directory, "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..8] + ".log");
        writer = new StreamWriter(path, false, Encoding.UTF8);
    }
    public void Add(string line)
    {
        lock (gate)
        {
            line = $"{DateTime.Now:HH:mm:ss} {line}";
            tail.AppendLine(line); if (tail.Length > 131072) tail.Remove(0, tail.Length - 98304);
            Interlocked.Increment(ref version);
            writer.WriteLine(line);
            if (writer.BaseStream.Position > 1048576) { writer.Dispose(); File.Move(path, path + ".previous", true); writer = new StreamWriter(path, false, Encoding.UTF8); }
        }
    }
    public string Text { get { lock (gate) return tail.ToString(); } }
    public void Dispose() { lock (gate) writer.Dispose(); }
}
public static class ProjectValidation
{
    public static ProjectConfig Detect(string folder, string java, string server)
    {
        folder = Path.GetFullPath(folder);
        bool gradle = File.Exists(Path.Combine(folder, "gradlew.bat")) || File.Exists(Path.Combine(folder, "build.gradle")) || File.Exists(Path.Combine(folder, "build.gradle.kts"));
        bool maven = File.Exists(Path.Combine(folder, "mvnw.cmd")) || File.Exists(Path.Combine(folder, "pom.xml"));
        if (!gradle && !maven) throw new InvalidDataException("Não encontrei Gradle ou Maven nessa pasta.");
        return new ProjectConfig { Name = new DirectoryInfo(folder).Name, Folder = folder, Java = java, ServerFolder = server,
            Build = new() { Executable = gradle ? (File.Exists(Path.Combine(folder, "gradlew.bat")) ? "gradlew.bat" : "gradle.bat") : (File.Exists(Path.Combine(folder, "mvnw.cmd")) ? "mvnw.cmd" : "mvn.cmd"), Arguments = gradle ? ["--no-daemon", "assemble"] : ["package", "-DskipTests"] },
            Tests = new() { Executable = gradle ? (File.Exists(Path.Combine(folder, "gradlew.bat")) ? "gradlew.bat" : "gradle.bat") : (File.Exists(Path.Combine(folder, "mvnw.cmd")) ? "mvnw.cmd" : "mvn.cmd"), Arguments = gradle ? ["--no-daemon", "test"] : ["test"] } };
    }
    public static string Validate(ProjectConfig project)
    {
        if (new[] { project.Folder, project.ServerFolder, project.Java, project.ServerJar }.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
            throw new InvalidDataException("Selecione caminhos absolutos para projeto, JDK, pasta e JAR do servidor.");
        string folder = Path.GetFullPath(project.Folder), server = Path.GetFullPath(project.ServerFolder);
        if (!Directory.Exists(folder) || !Directory.Exists(server)) throw new DirectoryNotFoundException("As pastas do projeto e do servidor precisam existir.");
        SafePaths.NoLinks(folder); SafePaths.NoLinks(server);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows), home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (server == Path.GetPathRoot(server) || string.Equals(server, folder, StringComparison.OrdinalIgnoreCase)
            || FileCleaner.IsInside(folder, server) || string.Equals(server, home, StringComparison.OrdinalIgnoreCase)
            || FileCleaner.IsInside(server, windows) || string.Equals(server, windows, StringComparison.OrdinalIgnoreCase)
            || new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }.Any(p => string.Equals(server, p, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Use uma pasta dedicada de servidor; não use raiz, pasta pessoal, sistema ou a pasta do projeto.");
        string java = SafePaths.Executable(project.Java, folder);
        if (!Path.GetFileName(java).Equals("java.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(Path.GetDirectoryName(java)!, "javac.exe"))) throw new InvalidDataException("Selecione java.exe de um JDK com javac.exe na mesma pasta.");
        if (!File.Exists(project.ServerJar) || !project.ServerJar.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Selecione o JAR real do servidor.");
        SafePaths.NoLinks(project.ServerJar);
        SafePaths.Command(project.Build, folder); SafePaths.Command(project.Tests, folder);
        if (project.XmsMb < 128 || project.XmxMb < project.XmsMb || project.XmxMb > 65536 || project.NativeMarginMb is < 128 or > 262144
            || project.BuildAvailableMb is < 1 or > 262144 || project.CommitMarginMb is < 1 or > 1048576 || project.ReadyTimeoutSeconds is < 2 or > 600) throw new InvalidDataException("Revise heap, margens e timeout.");
        if (string.IsNullOrWhiteSpace(project.PluginFileName) || Path.GetFileName(project.PluginFileName) != project.PluginFileName
            || !project.PluginFileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || project.PluginFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("O destino do plugin deve ser somente um nome .jar.");
        foreach (var pattern in new[] { project.ReadyPattern, project.PluginPattern, project.TestPassPattern }.Where(p => p.Length > 0)) _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        if (string.IsNullOrWhiteSpace(project.ReadyPattern)) throw new InvalidDataException("Configure um padrão real de prontidão do servidor.");
        ParsePorts(project.Ports);
        SafePaths.Command(new() { Executable = project.Java, Arguments = project.ServerArguments }, project.ServerFolder);
        return Directory.GetParent(Path.GetDirectoryName(java)!)!.FullName;
    }
    public static List<(string Protocol, int Port)> ParsePorts(string text)
    {
        var result = new List<(string, int)>();
        foreach (string item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':');
            if (parts.Length != 2 || !new[] { "tcp", "udp" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || !int.TryParse(parts[1], out int port) || port is < 1 or > 65535) throw new InvalidDataException("Portas: use tcp:25565,udp:19132.");
            result.Add((parts[0].ToLowerInvariant(), port));
        }
        return result;
    }
    public static Preflight Check(ProjectConfig project, bool serverStage = false, bool lockOwned = false, Func<MemorySample>? capture = null)
    {
        var notes = new List<string>(); bool valid = true; var sample = (capture ?? WindowsMemory.Capture)();
        try
        {
            Validate(project); notes.Add("JDK, projeto, comandos e ambiente dedicado localizados.");
            if (!lockOwned) using (new FileStream(Path.Combine(project.ServerFolder, ".pcboost-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
            foreach (var (protocol, port) in ParsePorts(project.Ports))
            {
                using var socket = new Socket(AddressFamily.InterNetwork, protocol == "tcp" ? SocketType.Stream : SocketType.Dgram, protocol == "tcp" ? ProtocolType.Tcp : ProtocolType.Udp) { ExclusiveAddressUse = true };
                socket.Bind(new IPEndPoint(IPAddress.Any, port)); notes.Add($"{protocol}:{port} disponível no instante da checagem.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SocketException or InvalidOperationException)
        { valid = false; notes.Add("Ambiente inválido: " + ex.Message); }
        int needMb = serverStage ? Math.Max(project.BuildAvailableMb, project.XmxMb + project.NativeMarginMb) : project.BuildAvailableMb;
        int commitMb = serverStage ? Math.Max(project.CommitMarginMb, project.XmxMb + project.NativeMarginMb) : project.CommitMarginMb;
        bool enough = sample.Available >= needMb * 1048576L && sample.CommitHeadroom is long headroom && headroom >= commitMb * 1048576L;
        notes.Add($"{sample.TimeLabel}: disponível {FileCleaner.FormatSize(sample.Available)} / mínimo {needMb} MB; margem de comprometimento {(sample.CommitHeadroom is long margin ? FileCleaner.FormatSize(margin) : "indisponível")} / mínimo {commitMb} MB.");
        return new(valid, enough, sample, notes);
    }
}
public static class PluginInstaller
{
    public static void Install(string artifact, ProjectConfig project)
    {
        SafePaths.NoLinks(artifact); SafePaths.NoLinks(project.ServerFolder);
        using var source = new FileStream(artifact, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
            if (!zip.Entries.Any(e => new[] { "plugin.yml", "paper-plugin.yml", "nukkit.yml", "powernukkitx.yml" }.Contains(e.FullName, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("O JAR não contém metadados conhecidos de plugin. Revise artefato e compatibilidade do servidor.");
        string plugins = Path.Combine(project.ServerFolder, "plugins"), backups = Path.Combine(project.ServerFolder, ".pcboost-backups");
        SafePaths.NoLinks(plugins); SafePaths.NoLinks(backups); Directory.CreateDirectory(plugins); Directory.CreateDirectory(backups);
        string target = Path.Combine(plugins, project.PluginFileName), temp = target + ".pcboost-" + Guid.NewGuid().ToString("N") + ".tmp";
        SafePaths.NoLinks(target);
        if (!FileCleaner.IsInside(target, plugins)) throw new IOException("Destino fora da pasta de plugins.");
        try
        {
            source.Position = 0;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { source.CopyTo(output); output.Flush(true); }
            SafePaths.NoLinks(plugins); SafePaths.NoLinks(backups); SafePaths.NoLinks(target);
            if (File.Exists(target)) File.Copy(target, Path.Combine(backups, project.PluginFileName + "." + Guid.NewGuid().ToString("N") + ".bak"), false);
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class MinecraftSession : IDisposable
{
    private readonly LocalStore store;
    private readonly List<OwnedProcess> owned = [];
    private readonly object gate = new();
    private FileStream? environmentLock;
    private bool cancel, overrideMemory;
    private bool serverMemoryCheck;
    private TaskCompletionSource<bool>? memoryAnswer;
    private TaskCompletionSource<string>? artifactAnswer;
    private ProjectConfig? project;
    public SessionLog Log { get; }
    public System.Collections.Concurrent.ConcurrentQueue<StageResult> Stages { get; } = new();
    public string State { get; private set; } = "Sem sessão";
    public bool Running { get; private set; }
    public bool StopAttempted { get; private set; }
    public bool NeedsMemoryDecision => memoryAnswer is not null;
    public IReadOnlyList<string> ArtifactChoices { get; private set; } = [];
    public event Action? Changed;
    public bool HasProcesses { get { lock (gate) return owned.Any(p => p.HasActiveProcesses); } }
    public IEnumerable<int> OwnedIds { get { lock (gate) return owned.Select(p => p.Process.Id).ToArray(); } }
    public void RecordMemory(MemorySample sample) => Log.Add("[Memória] " + MemoryEvidence(sample)
        + $" Pools: paginável {(sample.PagedPool is long p ? FileCleaner.FormatSize(p) : "indisponível")}; não paginável {(sample.NonpagedPool is long n ? FileCleaner.FormatSize(n) : "indisponível")}.");
    public MinecraftSession(LocalStore store) { this.store = store; Log = new(store); }
    private void Notify(string state) { State = state; Log.Add(state); Changed?.Invoke(); }
    public void AllowMemory(bool allow) { overrideMemory = allow; memoryAnswer?.TrySetResult(allow); }
    public void ChooseArtifact(string path) { if (ArtifactChoices.Contains(path, StringComparer.OrdinalIgnoreCase)) artifactAnswer?.TrySetResult(path); }
    private OwnedProcess Start(CommandSpec command, string cwd, string kind, Action<string>? observe = null)
    {
        if (cancel) throw new OperationCanceledException();
        var child = OwnedProcess.Start(command, cwd, kind, line => { Log.Add(line); observe?.Invoke(line); }, project is null ? null : Directory.GetParent(Path.GetDirectoryName(project.Java)!)!.FullName);
        lock (gate) owned.Add(child); Changed?.Invoke(); return child;
    }
    private async Task<int> Stage(string name, CommandSpec command, string cwd, Action<string>? observe = null)
    {
        Notify(name + " em andamento"); var clock = Stopwatch.StartNew();
        Stages.Enqueue(new(name, "Em andamento", null, null)); Changed?.Invoke();
        var process = Start(command, cwd, name, observe); int code = await process.WaitAsync();
        Stages.Enqueue(new(name, code == 0 ? (name == "Build" ? "Build concluído" : "Comando concluído") : "Falhou", code, clock.Elapsed.TotalSeconds)); Changed?.Invoke();
        if (cancel) throw new OperationCanceledException();
        return code;
    }
    private async Task CheckMemory(bool server)
    {
        serverMemoryCheck = server;
        var check = ProjectValidation.Check(project!, server, true);
        foreach (var note in check.Notes) Log.Add(note);
        if (!check.EnvironmentValid) throw new InvalidDataException(string.Join("\n", check.Notes));
        if (!check.MemoryEnough && !overrideMemory)
        {
            memoryAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Notify("Falta margem de memória. Revise candidatos, tente novamente ou prossiga conscientemente.");
            bool proceed = await memoryAnswer.Task; memoryAnswer = null;
            if (!proceed || cancel) throw new OperationCanceledException();
        }
        overrideMemory = false;
    }
    public Preflight RecheckMemory()
    {
        if (project is null) throw new InvalidOperationException("Não há projeto nesta sessão.");
        var check = ProjectValidation.Check(project, serverMemoryCheck, true);
        foreach (var note in check.Notes) Log.Add(note);
        if (check.EnvironmentValid && check.MemoryEnough) memoryAnswer?.TrySetResult(true);
        return check;
    }
    public async Task RunAsync(ProjectConfig config, bool allowLowMemory = false)
    {
        if (Running || HasProcesses) throw new InvalidOperationException("A sessão anterior ainda possui processos. Solicite a parada antes de iniciar outra.");
        project = System.Text.Json.JsonSerializer.Deserialize<ProjectConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
        ProjectValidation.Validate(project);
        environmentLock = new(Path.Combine(project.ServerFolder, ".pcboost-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Running = true; cancel = false; overrideMemory = allowLowMemory; StopAttempted = false; Stages.Clear();
        try
        {
            await CheckMemory(false);
            if (await Stage("Java", new() { Executable = project.Java, Arguments = ["-version"] }, project.Folder) != 0) throw new InvalidOperationException("O Java configurado não iniciou.");
            if (await Stage("Build", project.Build, project.Folder) != 0) throw new InvalidOperationException("O build retornou código de erro.");
            Notify("Build concluído");
            bool testPattern = false;
            DateTime testStarted = DateTime.UtcNow;
            if (await Stage("Testes", project.Tests, project.Folder, line => { if (Matches(line, project.TestPassPattern)) testPattern = true; }) != 0) throw new InvalidOperationException("O comando de testes falhou.");
            bool testsPassed = testPattern || TestsPassed(project.Folder, testStarted.AddSeconds(-2));
            Stages.Enqueue(new("Evidência dos testes", testsPassed ? "Testes passaram" : "Comando terminou; resultados inconclusivos", null, 0));
            Notify(testsPassed ? "Testes passaram" : "Testes inconclusivos: sem relatório/padrão confirmatório; comando terminou com código zero.");
            string artifact = project.Artifact;
            if (!string.IsNullOrWhiteSpace(artifact)) artifact = Path.GetFullPath(artifact, project.Folder);
            if (string.IsNullOrWhiteSpace(artifact))
            {
                var choices = new[] { Path.Combine(project.Folder, "build", "libs"), Path.Combine(project.Folder, "target") }
                    .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.jar", SearchOption.TopDirectoryOnly))
                    .Where(f => !f.EndsWith("-sources.jar") && !f.EndsWith("-javadoc.jar") && !Path.GetFileName(f).StartsWith("original-")).ToList();
                if (choices.Count == 0) throw new FileNotFoundException("Build concluído, mas nenhum JAR foi localizado; configure o artefato.");
                if (choices.Count > 1)
                {
                    ArtifactChoices = choices; artifactAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Notify("Mais de um JAR encontrado. Selecione o artefato para continuar."); artifact = await artifactAnswer.Task;
                    ArtifactChoices = []; artifactAnswer = null;
                }
                else artifact = choices[0];
            }
            if (cancel) throw new OperationCanceledException();
            await CheckMemory(true);
            var installClock = Stopwatch.StartNew(); PluginInstaller.Install(artifact, project); Stages.Enqueue(new("Instalação", "Plugin copiado; versão anterior preservada", 0, installClock.Elapsed.TotalSeconds));
            bool ready = false, loaded = false;
            var serverClock = Stopwatch.StartNew();
            var readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Start(new() { Executable = project.Java, Arguments = new List<string> { $"-Xms{project.XmsMb}m", $"-Xmx{project.XmxMb}m", "-jar", Path.GetFullPath(project.ServerJar) }.Concat(project.ServerArguments).ToList() }, project.ServerFolder, "Servidor", line =>
            {
                if (Matches(line, project.ReadyPattern) && !ready) { ready = true; Stages.Enqueue(new("Prontidão", "Servidor pronto", null, serverClock.Elapsed.TotalSeconds)); readySignal.TrySetResult(true); Notify("Servidor pronto (padrão configurado confirmado)"); }
                if (Matches(line, project.PluginPattern) && !loaded) { loaded = true; Stages.Enqueue(new("Plugin", "Plugin carregado", null, serverClock.Elapsed.TotalSeconds)); Notify("Plugin carregado (padrão configurado confirmado)"); }
            });
            var exited = server.WaitAsync(); var timeout = Task.Delay(TimeSpan.FromSeconds(project.ReadyTimeoutSeconds));
            await Task.WhenAny(exited, timeout, readySignal.Task);
            if (!ready) Stages.Enqueue(new("Prontidão", "Inconclusiva; padrão de prontidão não confirmado", null, 0));
            if (!loaded) Stages.Enqueue(new("Plugin", "Carregamento inconclusivo; consulte log/padrão", null, 0)); Changed?.Invoke();
            if (!exited.IsCompleted) Notify(ready ? "Servidor em execução. Acompanhe o log e use Parar ao terminar." : "Servidor em execução, prontidão inconclusiva.");
            int code = await exited;
            Stages.Enqueue(new("Servidor", cancel ? "Parado/cancelado" : "Encerrado", code, serverClock.Elapsed.TotalSeconds));
            if (!cancel && code != 0) throw new InvalidOperationException("Servidor encerrou com erro.");
            Notify(cancel ? "Sessão cancelada/parada." : "Sessão concluída.");
        }
        catch (OperationCanceledException) { Notify("Cancelamento solicitado. Processos ainda ativos precisam terminar normalmente ou de confirmação de parada forçada."); }
        catch (Exception ex) { Notify("Falha: " + Diagnose(Log.Text, WindowsMemory.Capture()) + "\n" + ex.Message); }
        finally
        {
            Running = false;
            store.Record(new(DateTime.UtcNow, "Minecraft / " + project.Name, State, Log.Text[^Math.Min(Log.Text.Length, 4000)..]));
            ReleaseLockIfStopped(); Changed?.Invoke();
        }
    }
    public void RequestStop()
    {
        cancel = true; StopAttempted = true;
        memoryAnswer?.TrySetResult(false); artifactAnswer?.TrySetCanceled();
        lock (gate) foreach (var process in owned) process.RequestStop();
        Notify("Parada normal solicitada aos processos desta sessão. Confirme a parada forçada somente se continuarem ativos.");
        ReleaseLockIfStopped();
    }
    public void ConfirmForce()
    {
        if (!StopAttempted) throw new InvalidOperationException("Solicite a parada normal antes.");
        lock (gate) foreach (var process in owned.Where(p => p.HasActiveProcesses)) process.ForceAfterConfirmation();
        Notify("Parada forçada confirmada apenas para os jobs desta sessão.");
    }
    public void ReleaseLockIfStopped() { if (!Running && !HasProcesses) { environmentLock?.Dispose(); environmentLock = null; } }
    public static bool Matches(string line, string pattern)
    {
        try { return pattern.Length > 0 && Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
    public static bool TestsPassed(string folder, DateTime? notBefore = null)
    {
        int tests = 0;
        foreach (var dir in new[] { Path.Combine(folder, "build", "test-results", "test"), Path.Combine(folder, "target", "surefire-reports") }.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(dir, "TEST-*.xml").Take(200))
        {
            if (notBefore is DateTime cutoff && File.GetLastWriteTimeUtc(file) < cutoff) continue;
            SafePaths.NoLinks(file);
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 });
            var xml = XDocument.Load(reader);
            var suites = xml.Root?.Name.LocalName == "testsuite" ? new[] { xml.Root } : xml.Root?.Elements("testsuite") ?? [];
            foreach (var suite in suites)
            {
                if (!int.TryParse(suite.Attribute("tests")?.Value, out int count) || !int.TryParse(suite.Attribute("failures")?.Value, out int failures)
                    || !int.TryParse(suite.Attribute("errors")?.Value, out int errors) || failures > 0 || errors > 0) return false;
                int skipped = 0;
                if (suite.Attribute("skipped") is { } skippedAttribute && !int.TryParse(skippedAttribute.Value, out skipped)) return false;
                if (count < 0 || skipped < 0 || skipped > count || failures < 0 || errors < 0) return false;
                tests += count - skipped;
            }
        }
        return tests > 0;
    }
    public static string Diagnose(string logs, MemorySample sample)
    {
        if (Regex.IsMatch(logs, "OutOfMemoryError|Java heap space|GC overhead limit", RegexOptions.IgnoreCase)) return "Erro de heap Java confirmado pelo log; revise -Xmx e o consumo do plugin.";
        if (Regex.IsMatch(logs, "UnsupportedClassVersionError|class file version|invalid target release|release version .* not supported", RegexOptions.IgnoreCase)) return "Incompatibilidade de Java/bytecode indicada pelo log.";
        if (Regex.IsMatch(logs, "Address already in use|bind failed|FAILED TO BIND", RegexOptions.IgnoreCase)) return "Conflito de porta indicado pelo log.";
        if (Regex.IsMatch(logs, "COMPILATION ERROR|compilation failed|error:.*(; expected|cannot find symbol|incompatible types)", RegexOptions.IgnoreCase)) return "Erro de compilação indicado pelo log.";
        if (Regex.IsMatch(logs, "There are test failures|FAILURE.*test|Failures: [1-9]|AssertionError", RegexOptions.IgnoreCase)) return "Falha de teste indicada pelo log.";
        if (Regex.IsMatch(logs, "could not be resolved|Could not resolve|Cannot access .*offline|has not been downloaded", RegexOptions.IgnoreCase)) return "Falha de resolução de dependência/repositório indicada pelo log; não é evidência de falta de RAM.";
        if (Regex.IsMatch(logs, "paging file is too small|insufficient memory|insufficient system resources|os::commit_memory|Native memory allocation.*failed", RegexOptions.IgnoreCase)) return "Falha de alocação/recursos do sistema indicada pelo log. " + MemoryEvidence(sample);
        return "Causa não confirmada. " + MemoryEvidence(sample) + " Consulte o trecho final do log.";
    }
    private static string MemoryEvidence(MemorySample sample) => $"{sample.TimeLabel}: RAM disponível {FileCleaner.FormatSize(sample.Available)}, margem comprometida {(sample.CommitHeadroom is long margin ? FileCleaner.FormatSize(margin) : "indisponível")}.";
    public void Dispose()
    {
        if (HasProcesses || Running) throw new InvalidOperationException("Pare os processos da sessão antes de descartá-la.");
        environmentLock?.Dispose(); lock (gate) foreach (var process in owned) process.Dispose(); Log.Dispose();
    }
}
