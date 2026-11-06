using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCBoost;

internal static class IntegrationTests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static async Task Eventually(Func<bool> condition, string reason, int seconds = 60)
    {
        var clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(100);
        Check(condition(), reason);
    }
    private static async Task RunCommand(CommandSpec command, string cwd, string? home = null)
    {
        var lines = new StringBuilder();
        using var child = OwnedProcess.Start(command, cwd, "Preparação do teste", line => { lock (lines) lines.AppendLine(line); }, home);
        int code = await child.WaitAsync(); Check(code == 0, "Fixture command failed: " + lines);
    }
    public static async Task Run()
    {
        var report = new List<string>();
        string root = Path.Combine(AppContext.BaseDirectory, "verification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root); var store = new LocalStore(Path.Combine(root, "state"));
        var settings = new BoosterSettings(); store.Save(settings); Check(store.Load().SampleSeconds == 5, "Settings persistence");
        var actual = WindowsMemory.Capture();
        Check(actual.Total > actual.Available && actual.CommitLimit >= actual.Committed && actual.PagedPool is > 0 && actual.NonpagedPool is > 0, "Native commit and pool counters");
        Check(actual.Standby is null && actual.HardwareReserved is null, "Unsupported metrics must remain unavailable");
        report.Add("PASS Windows nativo: físico, comprometimento/limite, pools; ausência explícita de standby/hardware reservado.");
        DateTime now = DateTime.UtcNow; int processScans = 0; var cursor = now;
        var sampler = new TelemetryService(store, settings, () => actual with { At = cursor }, () => { processScans++; return []; });
        sampler.Sample(true); cursor = now.AddSeconds(5); sampler.Sample(true); cursor = now.AddSeconds(20); sampler.Sample(true);
        Check(processScans == 2, "Do not scan all processes on every chart sample"); sampler.Flush();
        Check(store.ReadList<MemorySample>("memory.json").Count == 3, "Persistent memory history");
        var low = actual with { Available = 100 * 1048576L, Committed = 99 * 1048576L, CommitLimit = 100 * 1048576L };
        var history = Enumerable.Range(0, 40).Select(i => low with { At = now.AddSeconds(i * 5) }).ToList();
        var alerts = MemoryDiagnosis.Evaluate(history, settings);
        Check(alerts.Any(a => a.Contains("abaixo")) && alerts.Any(a => a.Contains("Comprometimento")), "Sustained low RAM and commit alerts");
        Check(!MemoryDiagnosis.Evaluate([low with { At = now }, low with { At = now.AddHours(1) }], settings).Any(a => a.Contains("abaixo")), "History gap must not imply continuous low RAM");
        Check(MemoryDiagnosis.Evaluate([actual with { At = now, NonpagedPool = 1 }, actual with { At = now.AddHours(2), NonpagedPool = 4L * 1073741824 }], settings).Any(a => a.Contains("cresceu")), "Nonpaged pool growth rule");
        report.Add("PASS histórico, retenção por coletor, frequência separada de processos e regras com lacunas/tendência.");
        string reportFixture = Path.Combine(root, "report-fixture"), reportDirectory = Path.Combine(reportFixture, "build", "test-results", "test");
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "TEST-skipped.xml"), "<testsuite tests=\"1\" skipped=\"1\" failures=\"0\" errors=\"0\" />");
        Check(!MinecraftSession.TestsPassed(reportFixture), "All-skipped tests are not execution evidence");
        report.Add("PASS relatórios com todos os testes ignorados não confirmam testes executados.");
        store.Write("memory.json", new[] { actual with { At = now.AddHours(-30) }, actual with { At = now } });
        Check(new TelemetryService(store, settings).Samples.Count == 1, "Expired samples must not be retained");
        var rootRow = new ProcessRow(100001, "parent", "parent.exe", @"C:\fixture\parent.exe", now.AddMinutes(-2), 100, 1, false, "", 1) { PrivateBytes = 104857600, PrivateCommit = 209715200 };
        var childRow = rootRow with { Id = 100002, Name = "child", StartedUtc = now.AddMinutes(-1) };
        Check(ProcessFamily.Group([new(rootRow, 0, null), new(childRow, rootRow.Id, null)]).Count == 1, "Known child process family grouping");
        Check(ProcessFamily.Group([new(rootRow with { StartedUtc = now }, 0, null), new(childRow, rootRow.Id, null)]).Count == 2, "Do not group a reused/newer parent PID");
        var current = Monitor.Capture(new HashSet<string>()).Processes;
        Check(ProfileService.Review(current, [], false).Count == 0, "Preserve by default");
        var self = current.Single(p => p.Id == Environment.ProcessId);
        Check(!ProfileService.Review(current, [new() { Path = self.Path!, Action = ProfileAction.FecharComAutorizacao }], false).Any(c => c.Pid == self.Id), "Protect booster itself");
        var unchanged = await ProfileService.ApplyAsync([], () => []); Check(unchanged.Actions.Count == 0, "Empty reviewed selection must not close apps");
        report.Add("PASS perfis preservam tudo por padrão, protegem o booster e não agem sobre seleção vazia.");

        using (var owner = OwnedProcess.Start(new() { Executable = Environment.ProcessPath!, Arguments = ["--test-child"] }, AppContext.BaseDirectory, "Fixture", _ => { }))
        {
            Check(owner.HasActiveProcesses, "Owned native job must contain the suspended/resumed child");
            bool refused = false; try { owner.ForceAfterConfirmation(); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Force must require a prior normal stop attempt"); owner.RequestStop(); owner.ForceAfterConfirmation();
            Check(await owner.WaitAsync() == 130, "Force should terminate only the fixture job");
        }
        report.Add("PASS lançamento suspenso + job privado e bloqueio de parada forçada antes da tentativa normal.");
        bool injectionRefused = false;
        string bat = Path.Combine(root, "wrapper.cmd"); File.WriteAllText(bat, "@echo off\r\necho wrapper-ok\r\n");
        try { SafePaths.Command(new() { Executable = bat, Arguments = ["a & whoami"] }, root); } catch (InvalidDataException) { injectionRefused = true; }
        Check(injectionRefused, "Shell metacharacters must not cross wrapper boundary");
        await RunCommand(new() { Executable = bat, Arguments = ["safe arg"] }, root);
        report.Add("PASS execução de wrapper .cmd com argumento separado e rejeição de metacaracteres.");

        string java = SafePaths.Executable("java.exe", root), javaHome = Directory.GetParent(Path.GetDirectoryName(java)!)!.FullName;
        string project = Path.Combine(root, "maven-project"), server = Path.Combine(root, "dedicated-server");
        Directory.CreateDirectory(project); Directory.CreateDirectory(server);
        Directory.CreateDirectory(Path.Combine(project, "src", "main", "java", "fixture"));
        Directory.CreateDirectory(Path.Combine(project, "src", "main", "resources"));
        Directory.CreateDirectory(Path.Combine(project, "src", "test", "java", "fixture"));
        File.WriteAllText(Path.Combine(project, "pom.xml"), """
        <project xmlns="http://maven.apache.org/POM/4.0.0"><modelVersion>4.0.0</modelVersion><groupId>fixture</groupId><artifactId>fixture-plugin</artifactId><version>1.0.0</version>
        <properties><maven.compiler.release>17</maven.compiler.release><project.build.sourceEncoding>UTF-8</project.build.sourceEncoding></properties>
        <dependencies><dependency><groupId>org.junit.jupiter</groupId><artifactId>junit-jupiter</artifactId><version>5.12.2</version><scope>test</scope></dependency></dependencies>
        <build><plugins><plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-compiler-plugin</artifactId><version>3.13.0</version></plugin><plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-resources-plugin</artifactId><version>3.3.1</version></plugin><plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-jar-plugin</artifactId><version>3.4.1</version></plugin><plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-surefire-plugin</artifactId><version>3.5.2</version></plugin></plugins></build></project>
        """);
        File.WriteAllText(Path.Combine(project, "src", "main", "java", "fixture", "Demo.java"), "package fixture; public class Demo { public static int answer() { return 42; } }");
        File.WriteAllText(Path.Combine(project, "src", "main", "resources", "plugin.yml"), "name: Fixture\nmain: fixture.Demo\nversion: 1.0.0\n");
        File.WriteAllText(Path.Combine(project, "src", "test", "java", "fixture", "DemoTest.java"), "package fixture; import org.junit.jupiter.api.Test; import static org.junit.jupiter.api.Assertions.*; public class DemoTest { @Test void provesAnswer() { assertEquals(42, Demo.answer()); } }");
        string serverSource = Path.Combine(root, "FixtureServer.java");
        File.WriteAllText(serverSource, """
        import java.io.*; import java.net.*; import java.nio.file.*; import java.util.zip.*;
        public class FixtureServer { public static void main(String[] args) throws Exception {
          int port = Integer.parseInt(Files.readString(Path.of("fixture.port")).trim());
          try (ServerSocket socket = new ServerSocket(port); BufferedReader in = new BufferedReader(new InputStreamReader(System.in))) {
            try (ZipFile plugin = new ZipFile("plugins/plugin.jar")) { if (plugin.getEntry("plugin.yml") == null) throw new IllegalStateException("No plugin metadata"); }
            System.out.println("PLUGIN-LOADED-FIXTURE"); System.out.println("READY-FIXTURE"); System.out.flush();
            String line; while ((line = in.readLine()) != null) { if (line.equals("stop") && !Files.exists(Path.of("ignore-stop"))) break; }
          } System.out.println("STOPPED-FIXTURE");
        } }
        """);
        await RunCommand(new() { Executable = Path.Combine(javaHome, "bin", "javac.exe"), Arguments = [serverSource] }, root, javaHome);
        string serverJar = Path.Combine(root, "fixture-server.jar");
        await RunCommand(new() { Executable = Path.Combine(javaHome, "bin", "jar.exe"), Arguments = ["--create", "--file", serverJar, "--main-class", "FixtureServer", "-C", root, "FixtureServer.class"] }, root, javaHome);
        int port;
        using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) { listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)listener.LocalEndPoint!).Port; }
        File.WriteAllText(Path.Combine(server, "fixture.port"), port.ToString());
        var config = ProjectValidation.Detect(project, java, server); config.ServerJar = serverJar;
        config.Build = new() { Executable = "mvn.cmd", Arguments = ["-o", "-B", "-ntp", "package", "-DskipTests"] };
        config.Tests = new() { Executable = "mvn.cmd", Arguments = ["-o", "-B", "-ntp", "test"] };
        config.XmsMb = 128; config.XmxMb = 256; config.NativeMarginMb = 128; config.BuildAvailableMb = 1; config.CommitMarginMb = 1;
        config.Ports = "tcp:" + port; config.ReadyPattern = "READY-FIXTURE"; config.PluginPattern = "PLUGIN-LOADED-FIXTURE"; config.ReadyTimeoutSeconds = 10;
        var check = ProjectValidation.Check(config); Check(check.EnvironmentValid, "Real Java/Maven fixture preflight: " + string.Join(" ", check.Notes));
        var pressure = ProjectValidation.Check(config, capture: () => actual with { Available = 0, Committed = actual.CommitLimit, CommitLimit = actual.CommitLimit });
        Check(pressure.EnvironmentValid && !pressure.MemoryEnough, "Low memory must offer only explicit override");
        using (var occupied = new TcpListener(IPAddress.Any, port))
        {
            occupied.Start(); Check(!ProjectValidation.Check(config).EnvironmentValid, "Occupied port must block execution");
        }
        Directory.CreateDirectory(Path.Combine(server, "plugins"));
        using (var zip = ZipFile.Open(Path.Combine(server, "plugins", "plugin.jar"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("plugin.yml").Open())) writer.Write("name: Previous\n");
        var run = new MinecraftSession(store); var running = run.RunAsync(config);
        await Eventually(() => run.Stages.Any(s => s.Stage == "Prontidão" && s.Result == "Servidor pronto") || running.IsCompleted, "Pipeline did not reach server readiness", 120);
        if (running.IsCompleted) throw new Exception("Real Maven/JDK pipeline failed: " + run.State + "\n" + run.Log.Text);
        Check(run.Stages.Any(s => s.Result == "Testes passaram"), "Require actual JUnit success reports");
        Check(run.Stages.Any(s => s.Result == "Plugin carregado"), "Configured plugin marker must be evidenced");
        Check(Directory.GetFiles(Path.Combine(server, ".pcboost-backups")).Length == 1, "Previous plugin backup");
        var duplicate = new MinecraftSession(store); bool locked = false;
        try { await duplicate.RunAsync(config); } catch (IOException) { locked = true; }
        Check(locked, "Concurrent sessions in same environment must be blocked"); duplicate.Dispose();
        run.RequestStop(); await running.WaitAsync(TimeSpan.FromSeconds(15));
        await Eventually(() => !run.HasProcesses, "Normal server stop must complete", 15); run.ReleaseLockIfStopped();
        File.WriteAllText(Path.Combine(root, "pipeline.log"), run.Log.Text); run.Dispose();
        report.Add("PASS Maven 3.9.9 offline real: package + testes JUnit; JDK real; instalação de JAR com backup; servidor Java simulador pronto; marca de plugin confirmada; lock concorrente; parada normal via stdin.");

        var artifact = Directory.GetFiles(Path.Combine(project, "target"), "fixture-plugin-*.jar").Single();
        string target = Path.Combine(server, "plugins", "plugin.jar"); byte[] original = File.ReadAllBytes(target); File.SetAttributes(target, FileAttributes.ReadOnly);
        bool copyFailed = false;
        try { PluginInstaller.Install(artifact, config); } catch (UnauthorizedAccessException) { copyFailed = true; } catch (IOException) { copyFailed = true; }
        finally { File.SetAttributes(target, FileAttributes.Normal); }
        Check(copyFailed && File.ReadAllBytes(target).SequenceEqual(original), "Failed replacement must preserve previous plugin");
        Check(MinecraftSession.Diagnose("java.lang.OutOfMemoryError: Java heap space", actual).StartsWith("Erro de heap"), "Heap failure classification");
        Check(MinecraftSession.Diagnose("UnsupportedClassVersionError class file version 65", actual).Contains("Incompatibilidade"), "Java compatibility classification");
        Check(MinecraftSession.Diagnose("Address already in use", actual).Contains("porta"), "Port failure classification");
        Check(MinecraftSession.Diagnose("COMPILATION ERROR", actual).Contains("compilação"), "Compilation classification");
        Check(MinecraftSession.Diagnose("There are test failures", actual).Contains("Falha de teste"), "Test classification");
        Check(MinecraftSession.Diagnose("Unknown error", actual).Contains("Causa não confirmada"), "Do not invent cause without evidence");
        report.Add("PASS preservação de plugin em falha de cópia, classificação de heap/Java/porta/compilação/teste e evidência inconclusiva.");
        Check(!MinecraftSession.TestsPassed(project, DateTime.UtcNow.AddHours(1)), "Old JUnit reports must not prove the current test stage");
        string demoPath = Path.Combine(project, "src", "main", "java", "fixture", "Demo.java");
        string validCode = File.ReadAllText(demoPath);
        File.WriteAllText(demoPath, "package fixture; public class Demo { this is invalid Java syntax; }");
        var badBuild = new MinecraftSession(store); var badBuildTask = badBuild.RunAsync(config);
        await badBuildTask.WaitAsync(TimeSpan.FromSeconds(60));
        Check(badBuild.State.Contains("compilação", StringComparison.OrdinalIgnoreCase), "Real javac compilation failure must be classified from its log: " + badBuild.State);
        Check(File.ReadAllBytes(target).SequenceEqual(original), "Failed build must never replace installed plugin");
        await Eventually(() => !badBuild.HasProcesses, "Failed build native job must finish", 15);
        badBuild.ReleaseLockIfStopped(); badBuild.Dispose(); File.WriteAllText(demoPath, validCode);
        report.Add("PASS erro real de compilação Maven/javac identificado, plugin anterior intacto e relatórios antigos recusados como evidência.");
        string gradleCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle", "wrapper", "dists");
        string? gradle = Environment.GetEnvironmentVariable("PCBOOST_TEST_GRADLE");
        if (string.IsNullOrWhiteSpace(gradle) && Directory.Exists(gradleCache))
            gradle = Directory.EnumerateFiles(gradleCache, "gradle.bat", SearchOption.AllDirectories).OrderDescending().FirstOrDefault();
        if (gradle is not null)
        {
            string gradleProject = Path.Combine(root, "gradle-project"); Directory.CreateDirectory(gradleProject);
            File.WriteAllText(Path.Combine(gradleProject, "settings.gradle"), "rootProject.name = 'fixture-plugin'\n");
            File.WriteAllText(Path.Combine(gradleProject, "build.gradle"), """
            plugins { id 'java' }
            repositories { mavenLocal() }
            dependencies { testImplementation 'org.junit.jupiter:junit-jupiter:5.12.2'; testRuntimeOnly 'org.junit.platform:junit-platform-launcher:1.12.2' }
            java { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
            test { useJUnitPlatform() }
            """);
            foreach (string source in Directory.EnumerateFiles(Path.Combine(project, "src"), "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(gradleProject, Path.GetRelativePath(project, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
            }
            File.WriteAllText(Path.Combine(gradleProject, "gradlew.bat"), "@echo off\r\nset \"GRADLE_USER_HOME=" + Path.Combine(root, "gradle-cache") + "\"\r\ncall \"" + gradle + "\" %*\r\n");
            var gradleConfig = ProjectValidation.Detect(gradleProject, java, server);
            Check(gradleConfig.Build.Executable == "gradlew.bat", "Gradle wrapper detection");
            gradleConfig.Build.Arguments = ["--offline", "--no-daemon", "--max-workers=1", "jar"];
            gradleConfig.Tests.Arguments = ["--offline", "--no-daemon", "--max-workers=1", "test"];
            gradleConfig.ServerJar = serverJar; gradleConfig.XmsMb = 128; gradleConfig.XmxMb = 256;
            gradleConfig.NativeMarginMb = 128; gradleConfig.BuildAvailableMb = 1; gradleConfig.CommitMarginMb = 1;
            gradleConfig.Ports = config.Ports; gradleConfig.ReadyPattern = config.ReadyPattern; gradleConfig.PluginPattern = config.PluginPattern;
            var gradleSession = new MinecraftSession(store); var gradleTask = gradleSession.RunAsync(gradleConfig);
            await Eventually(() => gradleSession.Stages.Any(s => s.Result == "Servidor pronto") || gradleTask.IsCompleted, "Gradle pipeline timeout", 180);
            if (gradleTask.IsCompleted) throw new Exception("Gradle pipeline failed: " + gradleSession.State + "\n" + gradleSession.Log.Text);
            Check(gradleSession.Stages.Any(s => s.Result == "Testes passaram"), "Real Gradle JUnit XML evidence");
            gradleSession.RequestStop(); await gradleTask.WaitAsync(TimeSpan.FromSeconds(15));
            await Eventually(() => !gradleSession.HasProcesses, "Gradle session must stop its own processes", 15);
            gradleSession.ReleaseLockIfStopped(); File.WriteAllText(Path.Combine(root, "gradle-pipeline.log"), gradleSession.Log.Text); gradleSession.Dispose();
            report.Add("PASS Gradle real offline: detecção de wrapper, jar, testes JUnit e relatórios XML, instalação e servidor Java simulador, parada normal. Distribuição: " + gradle);
        }
        else report.Add("SKIP Gradle real: configure PCBOOST_TEST_GRADLE com o caminho absoluto de gradle.bat para incluir este teste.");
        using (var sentinel = OwnedProcess.Start(new() { Executable = Environment.ProcessPath!, Arguments = ["--test-child"] }, AppContext.BaseDirectory, "Sentinela de teste", _ => { }))
        {
            var cancelConfig = System.Text.Json.JsonSerializer.Deserialize<ProjectConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
            cancelConfig.Build = new() { Executable = Environment.ProcessPath!, Arguments = ["--test-child"] };
            var cancellable = new MinecraftSession(store); var cancelTask = cancellable.RunAsync(cancelConfig);
            await Eventually(() => cancellable.State == "Build em andamento" && cancellable.OwnedIds.Count() >= 2, "Controlled build did not start", 15);
            cancellable.RequestStop(); Check(cancellable.StopAttempted, "Normal stop request must be recorded");
            cancellable.ConfirmForce(); await cancelTask.WaitAsync(TimeSpan.FromSeconds(15));
            await Eventually(() => !cancellable.HasProcesses, "Forced cancellation did not release session job", 10);
            Check(sentinel.HasActiveProcesses, "Cancelling a session must not affect another native job");
            cancellable.ReleaseLockIfStopped(); cancellable.Dispose();
            sentinel.RequestStop(); sentinel.ForceAfterConfirmation(); await sentinel.WaitAsync();
        }
        report.Add("PASS cancelamento durante build controlado, tentativa normal antes de confirmação forçada e preservação de job independente.");
        store.Record(new(DateTime.UtcNow, "Verificação", "Concluída", string.Join("\n", report)));
        report.Add("LIMITAÇÃO: FixtureServer é um simulador Java de protocolo/log, não Paper/PowerNukkitX. Nenhum plugin Minecraft real foi fornecido ou validado em servidor real.");
        report.Add("Artefatos e log reproduzíveis: " + root);
        File.WriteAllText("integration-test-result.txt", string.Join("\n", report));
    }
}
