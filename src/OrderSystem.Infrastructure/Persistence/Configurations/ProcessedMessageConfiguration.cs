using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderSystem.Domain.Notifications;

namespace OrderSystem.Infrastructure.Persistence.Configurations;

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages", table =>
            table.HasCheckConstraint(
                "ck_processed_messages_consumer_name_not_blank",
                "length(btrim(consumer_name)) > 0"));

        builder.HasKey(message => new { message.MessageId, message.ConsumerName })
            .HasName("pk_processed_messages");

        builder.Property(message => message.MessageId)
            .HasColumnName("message_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(message => message.ConsumerName)
            .HasColumnName("consumer_name")
            .HasMaxLength(ProcessedMessage.MaximumConsumerNameLength)
            .IsRequired();

        builder.Property(message => message.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(message => new
        {
            message.ProcessedAt,
            message.MessageId,
            message.ConsumerName
        })
            .HasDatabaseName("ix_processed_messages_processed_at");
    }
}
