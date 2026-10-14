namespace NightShift.Core;

public sealed class ShiftState
{
    public bool Armed { get; private set; }
    public bool WaitingForQuota { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public void Arm(DateTimeOffset now, int hours)
    {
        Armed = true;
        EndsAt = now.AddHours(Math.Clamp(hours, 1, 24));
    }
    public void WaitForQuota(DateTimeOffset retryAt) { WaitingForQuota = true; RetryAt = retryAt; }
    public bool CanRetry(DateTimeOffset now) => Armed && WaitingForQuota && RetryAt <= now && EndsAt > now;
    public void Complete() { WaitingForQuota = false; RetryAt = null; }
    public void Stop() { Armed = false; Complete(); EndsAt = null; }
}
