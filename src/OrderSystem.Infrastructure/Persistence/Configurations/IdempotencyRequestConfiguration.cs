using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Users;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRequestConfiguration
    : IEntityTypeConfiguration<IdempotencyRequest>
{
    public void Configure(EntityTypeBuilder<IdempotencyRequest> builder)
    {
        builder.ToTable("idempotency_requests", table =>
        {
            table.HasCheckConstraint(
                "ck_idempotency_requests_operation",
                "operation IN ('CreateOrder')");

            table.HasCheckConstraint(
                "ck_idempotency_requests_request_hash_length",
                "octet_length(request_hash) = 32");

            table.HasCheckConstraint(
                "ck_idempotency_requests_status",
                "status IN ('Processing', 'Completed')");

            table.HasCheckConstraint(
                "ck_idempotency_requests_retention",
                "expires_at > created_at AND delete_after > expires_at");

            table.HasCheckConstraint(
                "ck_idempotency_requests_completion_time",
                "completed_at IS NULL OR completed_at >= created_at");

            table.HasCheckConstraint(
                "ck_idempotency_requests_http_status_code",
                "http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599");

            table.HasCheckConstraint(
                "ck_idempotency_requests_response_body_size",
                "response_body_json IS NULL OR octet_length(response_body_json) <= 65536");

            table.HasCheckConstraint(
                "ck_idempotency_requests_lifecycle",
                """
                (status = 'Processing'
                    AND resource_id IS NULL
                    AND http_status_code IS NULL
                    AND response_body_json IS NULL
                    AND completed_at IS NULL)
                OR
                (status = 'Completed'
                    AND http_status_code BETWEEN 200 AND 299
                    AND response_body_json IS NOT NULL
                    AND completed_at IS NOT NULL)
                """);

            table.HasCheckConstraint(
                "ck_idempotency_requests_create_order_completed",
                """
                operation <> 'CreateOrder'
                OR status <> 'Completed'
                OR (resource_id IS NOT NULL AND http_status_code = 201)
                """);
        });

        builder.HasKey(request => request.Id)
            .HasName("pk_idempotency_requests");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(x => x.Operation)
            .HasColumnName("operation")
            .HasMaxLength(64)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(x => x.RequestHash)
            .HasColumnName("request_hash")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(16)
            .HasConversion<string>()
            .HasDefaultValue(IdempotencyRequestStatus.Processing)
            .IsRequired();

        builder.Property(x => x.ResourceId)
            .HasColumnName("resource_id")
            .HasColumnType("uuid");

        builder.Property(x => x.HttpStatusCode)
            .HasColumnName("http_status_code")
            .HasColumnType("smallint");

        builder.Property(x => x.ResponseBodyJson)
            .HasColumnName("response_body_json")
            .HasColumnType("text");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.DeleteAfter)
            .HasColumnName("delete_after")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_idempotency_requests_users_user_id");

        builder.HasIndex(x => new
        {
            x.UserId,
            x.Operation,
            x.IdempotencyKey
        })
            .IsUnique()
            .HasDatabaseName("uq_idempotency_requests_user_operation_key");

        builder.HasIndex(x => new { x.DeleteAfter, x.Id })
            .HasFilter("status = 'Completed'")
            .HasDatabaseName("ix_idempotency_requests_terminal_cleanup");
    }
}
