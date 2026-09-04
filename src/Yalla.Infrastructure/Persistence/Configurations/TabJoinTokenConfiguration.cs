using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class TabJoinTokenConfiguration : EntityConfiguration<TabJoinToken>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TabJoinToken> builder)
    {
        builder.ToTable("TabJoinTokens");

        builder.Property(t => t.Token)
            .HasMaxLength(FieldLengths.JoinToken)
            .IsRequired();

        builder.Property(t => t.CreatedByParticipantId)
            .IsRequired();

        builder.Property(t => t.ExpiresAtUtc).IsRequired();
        builder.Property(t => t.RevokedAtUtc);

        builder.HasOne(t => t.Tab)
            .WithMany(tab => tab.JoinTokens)
            .HasForeignKey(t => t.TabId)
            .OnDelete(DeleteBehavior.Restrict);

        // The token is looked up on its own when someone follows an invitation, so it is unique
        // system-wide rather than per tab.
        builder.HasIndex(t => t.Token)
            .IsUnique();
    }
}
