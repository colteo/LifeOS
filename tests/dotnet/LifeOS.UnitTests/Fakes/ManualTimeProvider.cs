namespace LifeOS.UnitTests.Fakes;

// A clock the test moves forward explicitly.
internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
