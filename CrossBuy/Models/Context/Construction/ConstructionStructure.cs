using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CrossBuy.Models.Context.Construction;

// R7 structural foundation. Deploy the authored schema and enroll both read and write
// company isolation before activating these dimensions.
public sealed class ConstructionStructureSettings
{
    public int CompanyID { get; set; }
    public int MaxWbsDepth { get; set; }
    public string Reason { get; set; } = "";
    public int UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public sealed class WbsNode
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Path { get; set; } = "";
    public int Depth { get; set; }
    public int Sequence { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? ResponsibleEmployeeId { get; set; }
    public string ProgressMethod { get; set; } = "";
    public bool OwnsBudget { get; set; }
    public bool OwnsCost { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public sealed class ConstructionCostCode
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Category { get; set; } = "";
    public int? DefaultAccountId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool AllowDirectPosting { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public sealed class ConstructionSite
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Address { get; set; }
    public int? WarehouseId { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public static class ConstructionStructureVocabulary
{
    public static bool IsCostCategory(string? value) => value is "Material" or "Labor" or "Equipment"
        or "Subcontractor" or "SiteExpense" or "Transportation" or "Accommodation" or "Overhead"
        or "Contingency" or "Other";
    public static bool IsProgressMethod(string? value) => value is "Quantity" or "Milestone"
        or "WeightedActivity" or "ManualApproved";
    public static bool CanTransition(string from, string to) => (from,to) is
        ("Draft","Active") or ("Draft","Archived") or ("Active","Suspended") or ("Active","Completed")
        or ("Suspended","Active") or ("Suspended","Archived") or ("Completed","Archived");
}

public static class ConstructionStructureModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<Project>().HasAlternateKey(x=>new{x.ID,x.CompanyID});
        builder.Entity<Warehouse>().HasAlternateKey(x=>new{x.ID,x.CompanyID});
        builder.Entity<CrossBuy.Models.Context.Admin.Employee>().HasAlternateKey(x=>new{x.ID,x.EmpCompanyID});
        builder.Entity<ConstructionStructureSettings>(e=>
        {
            e.ToTable("ConstructionStructureSettings",t=>{if(sqlServer)t.UseSqlOutputClause(false);}); e.HasKey(x=>x.CompanyID);
            e.Property(x=>x.CompanyID).ValueGeneratedNever();
            e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
            Version(e,sqlServer);
        });
        builder.Entity<WbsNode>(e=>
        {
            e.ToTable("ConstructionWbsNodes",t=>{if(sqlServer)t.UseSqlOutputClause(false);}); e.HasKey(x=>x.ID);
            e.HasAlternateKey(x=>new{x.ID,x.ProjectId,x.CompanyID});
            e.HasIndex(x=>new{x.CompanyID,x.ProjectId,x.Code}).IsUnique();
            e.HasOne<ConstructionStructureSettings>().WithMany().HasForeignKey(x=>x.CompanyID).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.CompanyID})
                .HasPrincipalKey(x=>new{x.ID,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x=>new{x.ParentId,x.ProjectId,x.CompanyID})
                .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CrossBuy.Models.Context.Admin.Employee>().WithMany().HasForeignKey(x=>new{x.ResponsibleEmployeeId,x.CompanyID})
                .HasPrincipalKey(x=>new{x.ID,x.EmpCompanyID}).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.Code).HasMaxLength(50).IsRequired();
            e.Property(x=>x.Name).HasMaxLength(200).IsRequired(); e.Property(x=>x.NameEn).HasMaxLength(200);
            e.Property(x=>x.Path).HasMaxLength(1000).IsRequired(); e.Property(x=>x.Status).HasMaxLength(20).IsRequired();
            e.Property(x=>x.ProgressMethod).HasMaxLength(30).IsRequired();
            e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
            Version(e,sqlServer);
        });
        builder.Entity<ConstructionCostCode>(e=>
        {
            e.ToTable("ConstructionCostCodes",t=>{if(sqlServer)t.UseSqlOutputClause(false);}); e.HasKey(x=>x.ID);
            e.HasAlternateKey(x=>new{x.ID,x.CompanyID,x.Category});
            e.HasIndex(x=>new{x.CompanyID,x.Code}).IsUnique();
            e.HasOne<ConstructionCostCode>().WithMany().HasForeignKey(x=>new{x.ParentId,x.CompanyID,x.Category})
                .HasPrincipalKey(x=>new{x.ID,x.CompanyID,x.Category}).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x=>x.DefaultAccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.Code).HasMaxLength(50).IsRequired();
            e.Property(x=>x.Name).HasMaxLength(200).IsRequired(); e.Property(x=>x.NameEn).HasMaxLength(200);
            e.Property(x=>x.Category).HasMaxLength(30).IsRequired();
            e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
            Version(e,sqlServer);
        });
        builder.Entity<ConstructionSite>(e=>
        {
            e.ToTable("ConstructionSites"); e.HasKey(x=>x.ID);
            e.HasAlternateKey(x=>new{x.ID,x.ProjectId,x.CompanyID});
            e.HasIndex(x=>new{x.CompanyID,x.ProjectId,x.Code}).IsUnique();
            e.HasOne<Project>().WithMany().HasForeignKey(x=>new{x.ProjectId,x.CompanyID})
                .HasPrincipalKey(x=>new{x.ID,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x=>new{x.WarehouseId,x.CompanyID})
                .HasPrincipalKey(x=>new{x.ID,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.Code).HasMaxLength(50).IsRequired();
            e.Property(x=>x.Name).HasMaxLength(200).IsRequired(); e.Property(x=>x.NameEn).HasMaxLength(200);
            e.Property(x=>x.Address).HasMaxLength(1000);
            e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
            Version(e,sqlServer);
        });
    }

    private static void Version<T>(EntityTypeBuilder<T> e,bool sqlServer) where T:class
    {
        var version=e.Property<byte[]>("RowVersion");
        if(sqlServer)version.IsRowVersion().HasColumnType("rowversion");
        else version.IsConcurrencyToken().ValueGeneratedNever();
    }
}
