using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;

namespace CrossBuy.Models.Context.Construction;

/// <summary>Structured daily site record. It records evidence; it does not post stock or accounting entries.</summary>
public sealed class ConstructionDailySiteReport
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public DateTime ReportDate { get; set; }
    public int RevisionNo { get; set; } = 1;
    public int? AmendsReportId { get; set; }
    public string? AmendmentReason { get; set; }
    public string? WeatherCondition { get; set; }
    public decimal? TemperatureC { get; set; }
    public bool RainfallStopped { get; set; }
    public TimeSpan? WorkStartTime { get; set; }
    public TimeSpan? WorkEndTime { get; set; }
    public decimal? TotalWorkingHours { get; set; }
    public int ShiftCount { get; set; } = 1;
    public string Status { get; set; } = "Draft";
    public string? NextDayPlan { get; set; }
    public string? GeneralRemarks { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Workforce evidence; employee and subcontractor identities remain owned by their source modules.</summary>
public sealed class ConstructionDsrManpowerLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public string Source { get; set; } = "Subcontractor";
    public string Trade { get; set; } = "";
    public int? EmployeeId { get; set; }
    public int? SubcontractId { get; set; }
    public int Headcount { get; set; } = 1;
    public int? WbsNodeId { get; set; }
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Equipment utilization evidence; it never duplicates the fixed-asset or maintenance masters.</summary>
public sealed class ConstructionDsrEquipmentLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public int? FixedAssetId { get; set; }
    public int? SubcontractId { get; set; }
    public string? EquipmentDescription { get; set; }
    public int? OperatorEmployeeId { get; set; }
    public int? WbsNodeId { get; set; }
    public decimal WorkingHours { get; set; }
    public decimal IdleHours { get; set; }
    public decimal DowntimeHours { get; set; }
    public string? DowntimeReason { get; set; }
    public decimal? MeterStart { get; set; }
    public decimal? MeterEnd { get; set; }
    public decimal? FuelQuantity { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Daily executed quantity evidence; this is not a progress certificate or payment approval.</summary>
public sealed class ConstructionDsrQuantityLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public int WbsNodeId { get; set; }
    public int BoqItemId { get; set; }
    public decimal QuantityExecuted { get; set; }
    public string Unit { get; set; } = "";
    public string? Location { get; set; }
    public string? Remarks { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Read-only inventory provenance captured in a site report; stock movement remains owned by Inventory.</summary>
public sealed class ConstructionDsrMaterialLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public string Direction { get; set; } = "Consumed";
    public int? GoodsReceiptLineId { get; set; }
    public int? ProjectMaterialIssueLineId { get; set; }
    public int ItemId { get; set; }
    public decimal Quantity { get; set; }
    public int? WbsNodeId { get; set; }
    public string? DeliveryNoteNo { get; set; }
    public string? Remarks { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Site events, delay and safety observations; this is evidence, not a claim or a safety-case system.</summary>
public sealed class ConstructionDsrEventLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public string EventType { get; set; } = "Delay";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "Medium";
    public string? ResponsibleParty { get; set; }
    public int? WbsNodeId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? NeededByDate { get; set; }
    public bool Reportable { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Reference to an existing company-library file; file bytes remain owned by File Manager.</summary>
public sealed class ConstructionDsrAttachment
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public int LibraryItemId { get; set; }
    public string Kind { get; set; } = "Document";
    public string? Caption { get; set; }
    public int? WbsNodeId { get; set; }
    public DateTime? TakenAt { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTime AddedAt { get; set; }
    public int AddedBy { get; set; }
}

/// <summary>Site visitor log. It records names supplied for the visit and is not an identity system.</summary>
public sealed class ConstructionDsrVisitorLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public string VisitorName { get; set; } = "";
    public string? Organisation { get; set; }
    public string Purpose { get; set; } = "";
    public DateTime? TimeIn { get; set; }
    public DateTime? TimeOut { get; set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

/// <summary>Site instruction evidence; it does not itself create or approve an RFI or variation.</summary>
public sealed class ConstructionDsrInstructionLine
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int SiteId { get; set; }
    public int ReportId { get; set; }
    public string Source { get; set; } = "Internal";
    public string? InstructionReference { get; set; }
    public string Description { get; set; } = "";
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}

public static class ConstructionDailySiteReportModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<CrossBuy.Models.Context.Accounting.BoqItem>()
            .HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
        builder.Entity<ConstructionDailySiteReport>(e =>
        {
            e.ToTable("ConstructionDailySiteReports", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasAlternateKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID });
            e.HasAlternateKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID, x.ReportDate });
            e.HasIndex(x => new { x.CompanyID, x.ProjectId, x.SiteId, x.ReportDate, x.RevisionNo }).IsUnique();
            e.HasOne<Project>().WithMany().HasForeignKey(x => new { x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionSite>().WithMany().HasForeignKey(x => new { x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionDailySiteReport>().WithMany().HasForeignKey(x => new { x.AmendsReportId, x.SiteId, x.ProjectId, x.CompanyID, x.ReportDate })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID, x.ReportDate }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.AmendmentReason).HasMaxLength(1000);
            e.Property(x => x.WeatherCondition).HasMaxLength(80);
            e.Property(x => x.NextDayPlan).HasMaxLength(2000);
            e.Property(x => x.GeneralRemarks).HasMaxLength(4000);
            e.Property(x => x.TemperatureC).HasPrecision(9, 3);
            e.Property(x => x.TotalWorkingHours).HasPrecision(9, 3);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrManpowerLine>(e =>
        {
            e.ToTable("ConstructionDsrManpowerLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany().HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.Trade).HasMaxLength(100).IsRequired();
            e.Property(x => x.RegularHours).HasPrecision(9, 3);
            e.Property(x => x.OvertimeHours).HasPrecision(9, 3);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrEquipmentLine>(e =>
        {
            e.ToTable("ConstructionDsrEquipmentLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany().HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.EquipmentDescription).HasMaxLength(200);
            e.Property(x => x.DowntimeReason).HasMaxLength(1000);
            e.Property(x => x.WorkingHours).HasPrecision(9, 3);
            e.Property(x => x.IdleHours).HasPrecision(9, 3);
            e.Property(x => x.DowntimeHours).HasPrecision(9, 3);
            e.Property(x => x.MeterStart).HasPrecision(18, 3);
            e.Property(x => x.MeterEnd).HasPrecision(18, 3);
            e.Property(x => x.FuelQuantity).HasPrecision(18, 3);
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrQuantityLine>(e =>
        {
            e.ToTable("ConstructionDsrQuantityLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany().HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CrossBuy.Models.Context.Accounting.BoqItem>().WithMany()
                .HasForeignKey(x => new { x.BoqItemId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.QuantityExecuted).HasPrecision(18, 3);
            e.Property(x => x.Unit).HasMaxLength(40).IsRequired();
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.HasIndex(x => new { x.CompanyID, x.ReportId, x.BoqItemId, x.WbsNodeId });
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrMaterialLine>(e =>
        {
            e.ToTable("ConstructionDsrMaterialLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany().HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GoodsReceiptLine>().WithMany().HasForeignKey(x => x.GoodsReceiptLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProjectMaterialIssueLine>().WithMany().HasForeignKey(x => x.ProjectMaterialIssueLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Direction).HasMaxLength(20).IsRequired();
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.DeliveryNoteNo).HasMaxLength(100);
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.HasIndex(x => new { x.CompanyID, x.ReportId, x.Direction });
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrEventLine>(e =>
        {
            e.ToTable("ConstructionDsrEventLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany().HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.EventType).HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Severity).HasMaxLength(20).IsRequired();
            e.Property(x => x.ResponsibleParty).HasMaxLength(100);
            e.HasIndex(x => new { x.CompanyID, x.ReportId, x.EventType });
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrAttachment>(e =>
        {
            e.ToTable("ConstructionDsrAttachments");
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany()
                .HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID })
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Caption).HasMaxLength(250);
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Latitude).HasPrecision(9, 6);
            e.Property(x => x.Longitude).HasPrecision(9, 6);
            e.HasOne<WbsNode>().WithMany().HasForeignKey(x => new { x.WbsNodeId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CompanyID, x.ReportId, x.LibraryItemId }).IsUnique();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });

        builder.Entity<ConstructionDsrVisitorLine>(e =>
        {
            e.ToTable("ConstructionDsrVisitorLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany()
                .HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID })
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.VisitorName).HasMaxLength(150).IsRequired();
            e.Property(x => x.Organisation).HasMaxLength(150);
            e.Property(x => x.Purpose).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.CompanyID, x.ReportId });
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });

        builder.Entity<ConstructionDsrInstructionLine>(e =>
        {
            e.ToTable("ConstructionDsrInstructionLines", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasOne<ConstructionDailySiteReport>().WithMany()
                .HasForeignKey(x => new { x.ReportId, x.SiteId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.SiteId, x.ProjectId, x.CompanyID })
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.InstructionReference).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => new { x.CompanyID, x.ReportId });
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
            Version(e, sqlServer);
        });
    }

    private static void Version<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e, bool sqlServer) where T : class
    {
        var version = e.Property<byte[]>("RowVersion");
        if (sqlServer) version.IsRowVersion().HasColumnType("rowversion");
        else version.IsConcurrencyToken().ValueGeneratedNever();
    }
}
