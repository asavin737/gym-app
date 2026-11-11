using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        // решение: очередь хранится в waitlist_entries, а не в enrollments.
        // Карточка брони её не читает, отмена брони и выход из очереди остаются разными маршрутами.
        builder.ToTable("waitlist_entries");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.EnrollmentId).HasColumnName("enrollment_id");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder
            .HasOne(x => x.Class)
            .WithMany()
            .HasForeignKey(x => x.ClassId);

        builder
            .HasOne<Enrollment>()
            .WithMany()
            .HasForeignKey(x => x.EnrollmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ClassId, x.Status, x.CreatedAt, x.Id });

        builder
            .HasIndex(x => new { x.UserId, x.ClassId })
            .IsUnique()
            .HasFilter("status = 'waiting'");
    }
}
