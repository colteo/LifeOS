namespace LifeOS.Domain.Gym.Sessions;

// The prescribed rest that started when a set was completed. Derived from timestamps, never stored,
// so a client can show the remaining time after being backgrounded or restarted.
public sealed record RestPeriod(DateTimeOffset StartedAtUtc, int Seconds)
{
    public DateTimeOffset EndsAtUtc => StartedAtUtc.AddSeconds(Seconds);
}
