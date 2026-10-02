namespace LifeOS.Domain.Gym.Exercises;

// A reusable, user-owned exercise. Programs reference it by id (never by name), so future history,
// records and media can attach to the same identity. Names are unique per user, ignoring case;
// that is enforced by Application and the database.
public sealed class Exercise
{
    private Exercise(Guid id, Guid userId, string name, DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        Name = name;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    // The owning LifeOS user.
    public Guid UserId { get; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Exercise Create(Guid userId, string name, DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Exercise(Guid.CreateVersion7(), userId, name.Trim(), createdAtUtc.ToUniversalTime());
    }
}
