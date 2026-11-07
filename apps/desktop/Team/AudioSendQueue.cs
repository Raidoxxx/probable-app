using System.Threading.Channels;

namespace PCBoost.Team;

// Cinco quadros (200 ms); captura nunca espera pela rede e há somente um envio em andamento.
internal sealed class AudioSendQueue : IDisposable
{
    private readonly Channel<(byte[] Frame, int Generation)> queue = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(5)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource stop = new();
    private readonly Task consumer;
    private volatile bool muted;
    private int generation;
    private int disposed;
    public event Action<Exception>? Failed;
    public bool Muted { get => muted; set { if (value) Interlocked.Increment(ref generation); muted = value; } }
    public AudioSendQueue(Func<byte[], Task> send) => consumer = Task.Run(async () =>
    {
        try
        {
            await foreach (var frame in queue.Reader.ReadAllAsync(stop.Token))
            {
                if (muted || frame.Generation != Volatile.Read(ref generation)) continue;
                Task pending = send(frame.Frame);
                try { await pending.WaitAsync(TimeSpan.FromSeconds(2), stop.Token); }
                catch
                {
                    // O transporte pode concluir depois do timeout; observa também uma falha tardia.
                    _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { queue.Writer.TryComplete(); Failed?.Invoke(e); }
    });
    public bool Enqueue(byte[] frame) => !muted && queue.Writer.TryWrite((frame, Volatile.Read(ref generation)));
    public Task Completion => consumer;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete(); stop.Cancel();
        _ = consumer.ContinueWith(t => { _ = t.Exception; stop.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
