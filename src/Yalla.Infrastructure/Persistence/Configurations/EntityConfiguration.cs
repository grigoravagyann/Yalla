using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Yalla.Domain.Common;

namespace Yalla.Infrastructure.Persistence.Configurations;

/// <summary>
/// Base for every entity configuration: it fixes the two things that must be true of all of them
/// and then hands over to the entity-specific mapping.
/// </summary>
/// <remarks>
/// Keys are <c>ValueGeneratedNever</c> because the application assigns UUIDv7 values itself;
/// letting SQL Server default them would produce random Guids and fragment the clustered index.
/// Being abstract, this class is skipped by <c>ApplyConfigurationsFromAssembly</c>.
/// </remarks>
internal abstract class EntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : Entity
{
    public void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedNever();

        builder.Property(e => e.CreatedAtUtc)
            .IsRequired();

        ConfigureEntity(builder);
    }

    protected abstract void ConfigureEntity(EntityTypeBuilder<TEntity> builder);
}
