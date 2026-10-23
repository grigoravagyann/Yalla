using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Identity;
using Yalla.Domain.Messages;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : EntityConfiguration<Conversation>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Conversation> builder)
    {
        // Exactly one subject: a booking's thread or an order's, never both and never neither.
        builder.ToTable("Conversations", t => t.HasCheckConstraint(
            "CK_Conversations_OneSubject",
            "([ReservationId] IS NOT NULL AND [TakeawayOrderId] IS NULL) OR ([ReservationId] IS NULL AND [TakeawayOrderId] IS NOT NULL)"));

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Reservation>()
            .WithMany()
            .HasForeignKey(c => c.ReservationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TakeawayOrder>()
            .WithMany()
            .HasForeignKey(c => c.TakeawayOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DinerUser>()
            .WithMany()
            .HasForeignKey(c => c.DinerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.Messages)
            .WithOne()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One thread per booking and one per order. Created under the subject's row lock; these are the backstop.
        builder.HasIndex(c => c.ReservationId)
            .IsUnique()
            .HasFilter("[ReservationId] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.ConversationPerReservation);

        builder.HasIndex(c => c.TakeawayOrderId)
            .IsUnique()
            .HasFilter("[TakeawayOrderId] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.ConversationPerTakeawayOrder);

        // The tablet's inbox and the floor's badge: a branch's threads by their last message.
        builder.HasIndex(c => new { c.BranchId, c.LastMessageUtc })
            .HasDatabaseName("IX_Conversations_BranchId_LastMessageUtc");

        // The diner's unread list: one account's threads.
        builder.HasIndex(c => new { c.DinerUserId, c.LastMessageUtc })
            .HasFilter("[DinerUserId] IS NOT NULL")
            .HasDatabaseName("IX_Conversations_DinerUserId_LastMessageUtc");
    }
}

internal sealed class ConversationMessageConfiguration : EntityConfiguration<ConversationMessage>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ConversationMessage> builder)
    {
        builder.ToTable("ConversationMessages", t =>
        {
            t.HasCheckConstraint("CK_ConversationMessages_Author", "[Author] BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_ConversationMessages_Kind", "[Kind] BETWEEN 1 AND 4");

            // Only a typed message has words; every other kind is structured and rendered by the reader.
            t.HasCheckConstraint(
                "CK_ConversationMessages_Body",
                "([Kind] = 1 AND [Body] IS NOT NULL) OR ([Kind] <> 1 AND [Body] IS NULL)");
            t.HasCheckConstraint("CK_ConversationMessages_LateMinutes", "[LateMinutes] IS NULL OR [LateMinutes] BETWEEN 1 AND 120");
        });

        builder.Property(m => m.Body).HasMaxLength(ConversationRules.MaxTextLength);
        builder.Property(m => m.StaffName).HasMaxLength(FieldLengths.PersonName);

        // The tie-breaker for messages of one instant - an answer and its status line. Assigned by the database.
        builder.Property(m => m.Sequence).UseIdentityColumn();

        builder.HasOne<StaffMember>()
            .WithMany()
            .HasForeignKey(m => m.StaffMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        // A thread's messages in order - written, then the database's tie-break - for every read of a thread, its last
        // message and the unread counts.
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAtUtc, m.Sequence })
            .HasDatabaseName("IX_ConversationMessages_ConversationId_CreatedAtUtc_Sequence");

        // A retried send answers the message the first one made: one per thread, author and command.
        builder.HasIndex(m => new { m.ConversationId, m.Author, m.ClientCommandId })
            .IsUnique()
            .HasFilter("[ClientCommandId] IS NOT NULL")
            .HasDatabaseName(DatabaseIndexNames.ConversationMessageClientCommand);
    }
}
