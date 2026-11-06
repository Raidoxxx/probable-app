using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCBoost;

public static class SafePaths
{
    public static void NoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Arquivo com vínculo não é permitido: " + full);
        for (var directory = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Pasta com vínculo não é permitida: " + directory.FullName);
    }
    public static string Executable(string value, string cwd)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n', '\0', '"']) >= 0) throw new InvalidDataException("Informe um executável válido.");
        var candidates = new List<string> { Path.IsPathRooted(value) ? value : Path.Combine(cwd, value) };
        if (!Path.IsPathRooted(value) && !value.Contains('\\') && !value.Contains('/'))
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                foreach (string extension in new[] { "", ".exe", ".cmd", ".bat" }) candidates.Add(Path.Combine(directory.Trim('"'), value + extension));
        string path = candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Executável não encontrado: " + value);
        path = Path.GetFullPath(path); NoLinks(path);
        if (!new[] { ".exe", ".cmd", ".bat" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Use um executável .exe ou wrapper .cmd/.bat.");
        return path;
    }
    public static string Quote(string value)
    {
        if (value.Contains('\0')) throw new InvalidDataException("Argumento contém caractere inválido.");
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { text.Append('\\', slashes * 2 + 1); text.Append(c); }
            else { text.Append('\\', slashes); text.Append(c); }
            slashes = 0;
        }
        text.Append('\\', slashes * 2); return text.Append('"').ToString();
    }
    public static (string Exe, string CommandLine) Command(CommandSpec spec, string cwd)
    {
        if (spec.Arguments is null || spec.Arguments.Any(a => a is null)) throw new InvalidDataException("Os argumentos precisam ser uma lista de textos.");
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        var exe = Executable(spec.Executable, cwd);
        if (Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return (exe, Quote(exe) + " " + string.Join(" ", spec.Arguments.Select(Quote)));
        var forbidden = new[] { '\r', '\n', '\0', '"', '&', '|', '<', '>', '^', '%', '!' };
        if (exe.IndexOfAny(forbidden) >= 0 || spec.Arguments.Any(a => a.IndexOfAny(forbidden) >= 0))
            throw new InvalidDataException("Wrappers .bat/.cmd não aceitam metacaracteres de shell nos caminhos/argumentos. Use argumentos separados e um caminho simples.");
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return (cmd, Quote(cmd) + " /d /s /c \"\"" + exe + "\" " + string.Join(" ", spec.Arguments.Select(a => "\"" + a + "\"")) + "\"");
    }
}

// Start suspended, assign a private job, then resume: descendants cannot escape ownership before assignment.
// Jobs have NO kill-on-close flag. Force is an explicit, separate operation after RequestStop.
public sealed class OwnedProcess : IDisposable
{
    private static readonly object StartGate = new();
    [StructLayout(LayoutKind.Sequential)] private struct Security { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup
    {
        public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref Security security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, IntPtr buffer, uint length, out uint returned);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle nativeProcess;
    private readonly CancellationTokenSource readers = new();
    private readonly StreamWriter input;
    private readonly List<Task> pumps = [];
    public Process Process { get; }
    public bool StopRequested { get; private set; }
    public string Kind { get; }
    public bool HasActiveProcesses
    {
        get
        {
            IntPtr data = Marshal.AllocHGlobal(48);
            try { return !QueryInformationJobObject(job, 1, data, 48, out _) || Marshal.ReadInt32(data, 40) > 0; }
            finally { Marshal.FreeHGlobal(data); }
        }
    }
    private OwnedProcess(SafeFileHandle job, Process process, SafeProcessHandle nativeProcess, SafeFileHandle writeInput, SafeFileHandle output, SafeFileHandle error,
        string kind, Action<string> log)
    {
        this.job = job; this.nativeProcess = nativeProcess; Process = process; Kind = kind;
        input = new StreamWriter(new FileStream(writeInput, FileAccess.Write)) { AutoFlush = true };
        pumps.Add(Pump(output, "stdout", log)); pumps.Add(Pump(error, "stderr", log));
    }
    private async Task Pump(SafeFileHandle pipe, string stream, Action<string> log)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(pipe, FileAccess.Read), Encoding.UTF8, true);
            var buffer = new char[2048]; var line = new StringBuilder();
            while (!readers.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), readers.Token);
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n' || line.Length >= 8192) { log($"[{Kind}/{stream}] {line.ToString().TrimEnd('\r')}"); line.Clear(); }
                    else line.Append(buffer[i]);
                }
            }
            if (line.Length > 0) log($"[{Kind}/{stream}] {line}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
    }
    public static OwnedProcess Start(CommandSpec spec, string cwd, string kind, Action<string> log, string? javaHome = null)
    {
        lock (StartGate) return StartCore(spec, cwd, kind, log, javaHome);
    }
    private static OwnedProcess StartCore(CommandSpec spec, string cwd, string kind, Action<string> log, string? javaHome)
    {
        var command = SafePaths.Command(spec, cwd);
        var security = new Security { Size = Marshal.SizeOf<Security>(), Inherit = true };
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, job = null;
        ProcessInfo info = default; IntPtr environment = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, ref security, 0) || !CreatePipe(out outputRead, out outputWrite, ref security, 0)
                || !CreatePipe(out errorRead, out errorWrite, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetHandleInformation(inputWrite, 1, 0) || !SetHandleInformation(outputRead, 1, 0) || !SetHandleInformation(errorRead, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            job = CreateJobObject(IntPtr.Zero, null); if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var vars = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string?)p.Value ?? "", StringComparer.OrdinalIgnoreCase);
            if (javaHome is not null) { vars["JAVA_HOME"] = javaHome; vars["PATH"] = Path.Combine(javaHome, "bin") + ";" + vars.GetValueOrDefault("PATH", ""); }
            string block = string.Join('\0', vars.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Flags = 0x100, Input = inputRead.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(), Error = errorWrite.DangerousGetHandle() };
            if (!CreateProcess(command.Exe, new StringBuilder(command.CommandLine), IntPtr.Zero, IntPtr.Zero, true, 0x08000404, environment, cwd, ref startup, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, info.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var managed = Process.GetProcessById((int)info.Id); _ = managed.Handle;
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var native = new SafeProcessHandle(info.Process, true);
            var result = new OwnedProcess(job, managed, native, inputWrite, outputRead, errorRead, kind, log);
            info.Process = IntPtr.Zero;
            job = null; inputWrite = outputRead = errorRead = null;
            return result;
        }
        catch { if (info.Process != IntPtr.Zero) TerminateProcess(info.Process, 1); throw; }
        finally
        {
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread); if (info.Process != IntPtr.Zero) CloseHandle(info.Process);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose(); job?.Dispose();
        }
    }
    public async Task<int> WaitAsync()
    {
        await Process.WaitForExitAsync();
        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(1000));
        if (!GetExitCodeProcess(nativeProcess, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)code);
    }
    public void RequestStop()
    {
        StopRequested = true;
        try { if (Kind == "Servidor") input.WriteLine("stop"); else input.Close(); } catch (IOException) { }
        try { if (!Process.HasExited) Process.CloseMainWindow(); } catch (InvalidOperationException) { }
    }
    public void ForceAfterConfirmation()
    {
        if (!StopRequested) throw new InvalidOperationException("Solicite primeiro a parada normal.");
        if (!TerminateJobObject(job, 130)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() { readers.Cancel(); input.Dispose(); job.Dispose(); nativeProcess.Dispose(); Process.Dispose(); }
}
