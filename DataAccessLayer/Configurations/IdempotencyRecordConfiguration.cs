using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        // решение: ключ принадлежит пользователю и области enrollment или waitlist,
        // между записью и очередью он не общий. Другой request_hash не заменяет сохранённый ответ.
        // решение: ключ хранится как text без предела длины, уникальный индекс строится по sha256.
        builder.ToTable("idempotency_records");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Scope).HasColumnName("scope").IsRequired();
        builder.Property(x => x.Key).HasColumnName("key").HasColumnType("text").IsRequired();
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.StatusCode).HasColumnName("status_code");
        builder.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasIndex(x => new { x.UserId, x.Scope, x.KeyHash }).IsUnique();
    }
}
