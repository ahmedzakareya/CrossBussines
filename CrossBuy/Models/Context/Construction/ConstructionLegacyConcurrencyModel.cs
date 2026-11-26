using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrossBuy.Models.Context.Construction;

/// <summary>
/// R7 native SQL Server concurrency. SQLite tests retain their C1 application tokens;
/// they cannot establish native rowversion correctness. Qualify this mapping on SQL Server.
/// </summary>
public static class ConstructionLegacyConcurrencyModel
{
    public static void Configure(ModelBuilder builder, bool sqlServer)
    {
        builder.Entity<Accounting.BoqItem>().Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        builder.Entity<Accounting.BoqItem>().Property(x => x.Status).HasMaxLength(20);
        builder.Entity<Accounting.BoqItem>().Property(x => x.RetiredReason).HasMaxLength(500);
        foreach (var name in new[] { "SnapshotContractedQuantity", "SnapshotContractedRate", "SnapshotCertifiedQuantity", "SnapshotCertifiedValue" })
            builder.Entity<Accounting.ProgressBillingLine>().Property<decimal?>(name).HasPrecision(19, 4);
        Configure(builder.Entity<Accounting.BoqItem>(), sqlServer);
        Configure(builder.Entity<Accounting.ProgressBilling>(), sqlServer);
        Configure(builder.Entity<Accounting.ProgressBillingLine>(), sqlServer);
        Configure(builder.Entity<Accounting.VariationOrder>(), sqlServer);
        Configure(builder.Entity<Accounting.SubcontractBilling>(), sqlServer);
        if (sqlServer)
            builder.Entity<Accounting.ProgressBillingLine>().ToTable(t => t.UseSqlOutputClause(false));
    }

    private static void Configure<TEntity>(EntityTypeBuilder<TEntity> entity, bool sqlServer)
        where TEntity : class
    {
        if (sqlServer)
            entity.Property<byte[]>("RowVersion").IsRowVersion().HasColumnType("rowversion");
        else
            entity.Ignore("RowVersion");
    }
}
