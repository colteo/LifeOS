using LifeOS.Domain.Journal;
using LifeOS.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LifeOS.Infrastructure.Persistence.Configurations;

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public const string TableName = "journal_entries";

    // Serves the user's timeline already in its order (newest first) and keyset paging through it.
    public const string TimelineIndexName = "ix_journal_entries_user_timeline";

    public const string UserForeignKeyName = "FK_journal_entries_users_user_id";

    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_journal_entries_content",
                $"length(btrim(content)) > 0 AND char_length(content) <= {JournalEntry.ContentMaxLength}");
            table.HasCheckConstraint("ck_journal_entries_title",
                "title IS NULL OR length(btrim(title)) > 0");
            // The domain's range; also excludes the infinities Npgsql maps the DateTimeOffset endpoints to.
            table.HasCheckConstraint("ck_journal_entries_occurred_at",
                "occurred_at_utc >= TIMESTAMPTZ '0001-01-02 00:00:00+00' AND occurred_at_utc < TIMESTAMPTZ '9999-12-31 00:00:00+00'");
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // User-owned data: deleting the user deletes their journal (ADR-006).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(entry => entry.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(entry => entry.Title)
            .HasColumnName("title")
            .HasMaxLength(JournalEntry.TitleMaxLength);

        builder.Property(entry => entry.Content)
            .HasColumnName("content")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(entry => entry.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(entry => entry.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(entry => new { entry.UserId, entry.OccurredAtUtc, entry.CreatedAtUtc, entry.Id })
            .IsDescending(false, true, true, true)
            .HasDatabaseName(TimelineIndexName);
    }
}
