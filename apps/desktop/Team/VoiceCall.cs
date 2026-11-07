using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PCBoost.Team;

// Voz em PCM 16 kHz / 16 bits / mono, quadros de 40 ms (1280 bytes ≈ 32 KB/s por quem fala).
// Silêncio não é enviado (porta de ruído simples), então só quem fala consome banda.
public sealed class VoiceCall : IDisposable
{
    public static readonly WaveFormat Format = new(16000, 16, 1);
    private readonly AudioSendQueue sender;
    private readonly WaveInEvent input;
    private readonly WaveOutEvent output;
    private readonly MixingSampleProvider mixer;
    private readonly Dictionary<long, (BufferedWaveProvider Buffer, ISampleProvider Input)> voices = [];
    private readonly Dictionary<long, DateTime> lastHeard = [];
    private readonly object gate = new();
    private int hangFrames;
    public bool Muted { get => sender.Muted; set => sender.Muted = value; }
    public event Action<Exception>? Failed;
    public bool Deafened { get; set; }
    public float Gate { get; set; } = 0.012f;
    public double InputLevel { get; private set; }
    public bool Speaking { get; private set; }

    public static bool HasMicrophone => WaveInEvent.DeviceCount > 0;

    public VoiceCall(Func<byte[], Task> send)
    {
        mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Format.SampleRate, 1)) { ReadFully = true };
        output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        input = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 40, NumberOfBuffers = 3 };
        sender = new AudioSendQueue(send);
        try { output.Init(new VolumeGuard(mixer)); }
        catch { sender.Dispose(); input.Dispose(); output.Dispose(); throw; }
        input.DataAvailable += Captured;
        sender.Failed += e => Failed?.Invoke(e);
        input.RecordingStopped += (_, e) => { if (e.Exception is not null) Failed?.Invoke(e.Exception); };
        output.PlaybackStopped += (_, e) => { if (e.Exception is not null) Failed?.Invoke(e.Exception); };
    }
    public void Start()
    {
        output.Play();
        if (HasMicrophone) input.StartRecording();
    }

    private void Captured(object? sender, WaveInEventArgs e)
    {
        double sum = 0; int samples = e.BytesRecorded / 2;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2) { double s = BitConverter.ToInt16(e.Buffer, i) / 32768d; sum += s * s; }
        InputLevel = samples == 0 ? 0 : Math.Sqrt(sum / samples);
        if (Muted) { Speaking = false; return; }
        if (InputLevel >= Gate) hangFrames = 8; else if (hangFrames > 0) hangFrames--;
        Speaking = hangFrames > 0;
        if (!Speaking) return;
        var frame = new byte[e.BytesRecorded]; Buffer.BlockCopy(e.Buffer, 0, frame, 0, e.BytesRecorded);
        this.sender.Enqueue(frame);
    }

    public void Receive(long userId, byte[] frame)
    {
        if (frame.Length == 0 || frame.Length > 8192 || frame.Length % 2 != 0) return;
        lock (gate)
        {
            lastHeard[userId] = DateTime.UtcNow;
            if (Deafened) return;
            if (!voices.TryGetValue(userId, out var voice))
            {
                var created = new BufferedWaveProvider(Format) { BufferDuration = TimeSpan.FromMilliseconds(800), DiscardOnBufferOverflow = true, ReadFully = true };
                voice = (created, created.ToSampleProvider()); voices[userId] = voice; mixer.AddMixerInput(voice.Input);
            }
            var buffer = voice.Buffer;
            // Evita atraso acumulado: se o buffer passar de 400 ms, descarta o excedente.
            if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(400)) buffer.ClearBuffer();
            buffer.AddSamples(frame, 0, frame.Length);
        }
    }
    public bool IsTalking(long userId) { lock (gate) return lastHeard.TryGetValue(userId, out var at) && DateTime.UtcNow - at < TimeSpan.FromMilliseconds(350); }
    public void Forget(long userId)
    {
        lock (gate)
        {
            lastHeard.Remove(userId);
            if (voices.Remove(userId, out var voice)) mixer.RemoveMixerInput(voice.Input);
        }
    }

    public void Dispose()
    {
        sender.Dispose();
        input.DataAvailable -= Captured;
        try { input.StopRecording(); } catch (Exception) { }
        input.Dispose();
        try { output.Stop(); } catch (Exception) { }
        output.Dispose();
    }

    // Limita picos da soma de várias vozes.
    private sealed class VolumeGuard(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            for (int i = offset; i < offset + read; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            return read;
        }
    }
}
