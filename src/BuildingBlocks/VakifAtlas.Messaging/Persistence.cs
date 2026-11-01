using Microsoft.EntityFrameworkCore;

namespace VakifAtlas.Messaging;

/// <summary>Event awaiting publish, written in the same transaction as the business data so neither is lost alone.</summary>
public sealed class OutboxMessage
{
    /// <summary>Event id, also sent as the AMQP message id.</summary>
    public Guid Id { get; set; }

    /// <summary>Event routing key.</summary>
    public required string Type { get; set; }

    /// <summary>Event serialized as JSON.</summary>
    public required string Payload { get; set; }

    /// <summary>Time the event happened.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>W3C <c>traceparent</c> that links the consumer trace to the producing request.</summary>
    public string? TraceParent { get; set; }

    /// <summary>Time the broker confirmed the publish, or null while pending.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Number of failed publish attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time of the next publish attempt after a failure.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Last publish error, truncated to 2000 characters.</summary>
    public string? LastError { get; set; }
}

/// <summary>Processed event record that keeps at-least-once delivery from applying an event twice.</summary>
public sealed class InboxMessage
{
    /// <summary>Id of the processed event.</summary>
    public Guid MessageId { get; set; }

    /// <summary>Name of the handler that processed the event.</summary>
    public required string Consumer { get; set; }

    /// <summary>Time the event was processed.</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Maps the outbox and inbox tables into a service DbContext.</summary>
public static class MessagingModelBuilderExtensions
{
    /// <summary>Adds the <c>outbox_messages</c> and <c>inbox_messages</c> tables to the model.</summary>
    /// <param name="modelBuilder">Model builder of the service DbContext.</param>
    public static ModelBuilder AddMessagingTables(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(200);
            entity.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.TraceParent).HasColumnName("trace_parent").HasMaxLength(64);
            entity.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            entity.Property(x => x.Attempts).HasColumnName("attempts");
            entity.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);
            entity.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_outbox_pending").HasFilter("processed_at IS NULL");
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(x => new { x.MessageId, x.Consumer });
            entity.Property(x => x.MessageId).HasColumnName("message_id");
            entity.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(200);
            entity.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        return modelBuilder;
    }
}
