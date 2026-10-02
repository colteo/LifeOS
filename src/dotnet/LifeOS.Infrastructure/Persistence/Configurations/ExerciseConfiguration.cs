using LifeOS.Domain.Gym.Exercises;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class ExerciseConfiguration : IEntityTypeConfiguration<Exercise>
{
    // Case-insensitive name uniqueness per user: (user_id, lower(name)).
    // EF Core cannot model an expression index, so this index is NOT part of the EF model: it is
    // created by raw SQL in the AddGymPrograms migration. ExerciseRepository recognizes violations
    // by this name.
    public const string NameIndexName = "ux_exercises_user_name";

    public void Configure(EntityTypeBuilder<Exercise> builder)
    {
        builder.ToTable("exercises");

        builder.HasKey(exercise => exercise.Id);

        builder.Property(exercise => exercise.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(exercise => exercise.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Target of the composite (exercise_id, user_id) foreign key: a block can only reference an
        // exercise of its own user.
        builder.HasAlternateKey(exercise => new { exercise.Id, exercise.UserId });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(exercise => exercise.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(exercise => exercise.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(exercise => exercise.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
