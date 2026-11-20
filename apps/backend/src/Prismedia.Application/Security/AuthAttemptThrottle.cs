using System.Collections.Concurrent;

namespace Prismedia.Application.Security;

/// <summary>
/// Small in-memory throttle for human credential attempts. Client buckets (remote address plus
/// username) lock quickly; a looser per-account limit, independent of the client address, slows
/// guessing spread across many addresses while keeping lockouts short so it is a weak lever for
/// locking a real user out.
/// </summary>
public sealed class AuthAttemptThrottle {
    private const int MaxFailures = 8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(2);

    /// <summary>Failures per account within <see cref="Window"/> before the account is briefly locked.</summary>
    public const int MaxAccountFailures = 30;
    private static readonly TimeSpan AccountLockout = TimeSpan.FromMinutes(1);
    private const string AccountBucketPrefix = "account:";

    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns whether the bucket is currently rate limited.</summary>
    public bool IsThrottled(string bucket) {
        var key = NormalizeBucket(bucket);
        if (!_attempts.TryGetValue(key, out var state)) {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (state.LockedUntil is { } lockedUntil && lockedUntil > now) {
            return true;
        }

        if (now - state.WindowStartedAt > Window) {
            _attempts.TryRemove(key, out _);
        }

        return false;
    }

    /// <summary>Returns whether the account is rate limited regardless of client address.</summary>
    public bool IsAccountThrottled(string? username) => IsThrottled(AccountBucket(username));

    /// <summary>Records a failed attempt against an account, independent of client address.</summary>
    public void RecordAccountFailure(string? username) =>
        RecordFailure(AccountBucket(username), MaxAccountFailures, AccountLockout);

    /// <summary>Records a failed auth attempt for a bucket.</summary>
    public void RecordFailure(string bucket) => RecordFailure(bucket, MaxFailures, Lockout);

    private void RecordFailure(string bucket, int maxFailures, TimeSpan lockout) {
        var key = NormalizeBucket(bucket);
        var now = DateTimeOffset.UtcNow;
        _attempts.AddOrUpdate(
            key,
            _ => new AttemptState(now, 1, null),
            (_, existing) => {
                var state = now - existing.WindowStartedAt > Window
                    ? new AttemptState(now, 0, null)
                    : existing;
                var failures = state.Failures + 1;
                return state with {
                    Failures = failures,
                    LockedUntil = failures >= maxFailures ? now + lockout : state.LockedUntil
                };
            });
    }

    /// <summary>Clears recorded failures after a successful auth attempt.</summary>
    public void RecordSuccess(string bucket) => _attempts.TryRemove(NormalizeBucket(bucket), out _);

    // Client buckets start with a remote address (or "unknown"), so account keys never collide.
    private static string AccountBucket(string? username) =>
        AccountBucketPrefix + (username?.Trim() ?? string.Empty);

    private static string NormalizeBucket(string bucket) =>
        string.IsNullOrWhiteSpace(bucket) ? "unknown" : bucket.Trim().ToLowerInvariant();

    private sealed record AttemptState(DateTimeOffset WindowStartedAt, int Failures, DateTimeOffset? LockedUntil);
}
