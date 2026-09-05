using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence.Configurations;

internal sealed class ProcessedCommandConfiguration : EntityConfiguration<ProcessedCommand>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProcessedCommand> builder)
    {
        builder.ToTable("ProcessedCommands");

        builder.Property(c => c.ClientCommandId).IsRequired();

        builder.Property(c => c.CommandType)
            .HasMaxLength(FieldLengths.CommandType)
            .IsRequired();

        builder.Property(c => c.ActorType).IsRequired();
        builder.Property(c => c.ActorId);

        // The response as it was first sent. Unbounded: a floor-state body is a few kilobytes and
        // truncating one would make a replay answer differently from the original, which is the
        // one thing this table exists to prevent.
        builder.Property(c => c.ResponseJson)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(c => c.StatusCode).IsRequired();

        // The lookup, and the guarantee. A replay finds its original here; two copies of the same
        // command racing find that only one of them can insert, and the loser reads the winner's
        // answer rather than doing the work twice.
        builder.HasIndex(c => c.ClientCommandId)
            .IsUnique()
            .HasDatabaseName(DatabaseIndexNames.ProcessedCommandId);
    }
}
