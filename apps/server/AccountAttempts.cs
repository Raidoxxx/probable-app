using System.Threading.RateLimiting;

namespace PCBoost.Server;

public sealed class AccountAttempts : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(email =>
        RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    public bool Allow(string email)
    {
        using var lease = limiter.AttemptAcquire(Db.SessionHash(email.Trim().ToUpperInvariant()));
        return lease.IsAcquired;
    }
    public void Dispose() => limiter.Dispose();
}
