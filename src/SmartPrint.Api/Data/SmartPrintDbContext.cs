using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Data;

public sealed class SmartPrintDbContext(DbContextOptions<SmartPrintDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AgentDevice> AgentDevices => Set<AgentDevice>();
    public DbSet<AgentEnrollmentCode> AgentEnrollmentCodes => Set<AgentEnrollmentCode>();
    public DbSet<ShopProfile> ShopProfiles => Set<ShopProfile>();
    public DbSet<ShopPricingProfile> ShopPricingProfiles => Set<ShopPricingProfile>();
    public DbSet<ShopPaymentProfile> ShopPaymentProfiles => Set<ShopPaymentProfile>();
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<SubscriptionPlanVersion> SubscriptionPlanVersions => Set<SubscriptionPlanVersion>();
    public DbSet<PlatformSubscriptionAudit> PlatformSubscriptionAudits => Set<PlatformSubscriptionAudit>();
    public DbSet<PrinterDevice> PrinterDevices => Set<PrinterDevice>();
    public DbSet<QrStation> QrStations => Set<QrStation>();
    public DbSet<CustomerPrintSession> CustomerPrintSessions => Set<CustomerPrintSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<PrintPayment> PrintPayments => Set<PrintPayment>();
    public DbSet<PrintDocument> PrintDocuments => Set<PrintDocument>();
    public DbSet<LocalPrintUploadTicket> LocalPrintUploadTickets => Set<LocalPrintUploadTicket>();
    public DbSet<PrintJobEvent> PrintJobEvents => Set<PrintJobEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            entity.Property(x => x.BusinessName).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<AgentDevice>(entity =>
        {
            entity.ToTable("AgentDevices");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.DeviceName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.FingerprintHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SecretHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LocalEndpointsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.FingerprintHash })
                .IsUnique()
                .HasFilter("\"RevokedUtc\" IS NULL");
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentEnrollmentCode>(entity =>
        {
            entity.ToTable("AgentEnrollmentCodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExpiresUtc, x.UsedUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShopProfile>(entity =>
        {
            entity.ToTable("ShopProfiles");
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.DisplayName).HasMaxLength(180).IsRequired();
            entity.Property(x => x.LogoUrl).HasMaxLength(1000);
            entity.Property(x => x.AccentColor).HasMaxLength(7).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.TimeZone).HasMaxLength(80).IsRequired();
            entity.HasOne<Tenant>().WithOne().HasForeignKey<ShopProfile>(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShopPricingProfile>(entity =>
        {
            entity.ToTable("ShopPricingProfiles");
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithOne().HasForeignKey<ShopPricingProfile>(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShopPaymentProfile>(entity =>
        {
            entity.ToTable("ShopPaymentProfiles");
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.UpiId).HasMaxLength(160);
            entity.Property(x => x.PayeeName).HasMaxLength(120);
            entity.HasOne<Tenant>().WithOne().HasForeignKey<ShopPaymentProfile>(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.ToTable("SubscriptionPlans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<SubscriptionPlanVersion>(entity =>
        {
            entity.ToTable("SubscriptionPlanVersions", table =>
            {
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_Version", "\"Version\" > 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_Status", "\"Status\" IN ('Draft', 'Published', 'Retired')");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_Currency", "char_length(\"Currency\") = 3 AND \"Currency\" = upper(\"Currency\") AND \"Currency\" ~ '^[A-Z]{3}'");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_MonthlyPriceMinor", "\"MonthlyPriceMinor\" IS NULL OR \"MonthlyPriceMinor\" >= 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_AnnualPriceMinor", "\"AnnualPriceMinor\" IS NULL OR \"AnnualPriceMinor\" >= 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_MaxPrinters", "\"MaxPrinters\" IS NULL OR \"MaxPrinters\" > 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_MaxAgentDevices", "\"MaxAgentDevices\" IS NULL OR \"MaxAgentDevices\" > 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_MaxQrStations", "\"MaxQrStations\" IS NULL OR \"MaxQrStations\" > 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_MaxMonthlyPrintJobs", "\"MaxMonthlyPrintJobs\" IS NULL OR \"MaxMonthlyPrintJobs\" > 0");
                table.HasCheckConstraint("CK_SubscriptionPlanVersions_MaxDocumentBytes", "\"MaxDocumentBytes\" IS NULL OR \"MaxDocumentBytes\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.HasIndex(x => new { x.PlanId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.PlanId, x.Status });
            entity.HasOne<SubscriptionPlan>().WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantSubscription>(entity =>
        {
            entity.ToTable("TenantSubscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.BillingInterval).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => x.PlanVersionId);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SubscriptionPlanVersion>().WithMany()
                .HasForeignKey(x => x.PlanVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlatformSubscriptionAudit>(entity =>
        {
            entity.ToTable("PlatformSubscriptionAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PreviousStatus).HasMaxLength(24);
            entity.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.BillingInterval).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.PaymentReference).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CreatedUtc });
            entity.HasIndex(x => x.AssignedSubscriptionId).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TenantSubscription>().WithMany().HasForeignKey(x => x.AssignedSubscriptionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TenantSubscription>().WithMany().HasForeignKey(x => x.PreviousSubscriptionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SubscriptionPlanVersion>().WithMany().HasForeignKey(x => x.PlanVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PrinterDevice>(entity =>
        {
            entity.ToTable("PrinterDevices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IsDefault).HasDefaultValue(false);
            entity.Property(x => x.Manufacturer).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ConnectionType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.OutputMode).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ConnectionAddress).HasMaxLength(255);
            entity.Property(x => x.QueueName).HasMaxLength(160);
            entity.Property(x => x.CapabilitiesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => new { x.TenantId, x.AgentDeviceId, x.QueueName })
                .IsUnique()
                .HasFilter("\"AgentDeviceId\" IS NOT NULL AND \"QueueName\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
            entity.HasOne<AgentDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.AgentDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QrStation>(entity =>
        {
            entity.ToTable("QrStations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.PublicCodeHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PublicCodeHint).HasMaxLength(8).IsRequired();
            entity.HasIndex(x => x.PublicCodeHash).IsUnique();
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => new { x.TenantId, x.IsEnabled });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<PrinterDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PrinterDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerPrintSession>(entity =>
        {
            entity.ToTable("CustomerPrintSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => new { x.TenantId, x.ExpiresUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<QrStation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.QrStationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.ExpiresUtc });
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PrintJob>(entity =>
        {
            entity.ToTable("PrintJobs");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasOne<AgentDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.ClaimedByAgentDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.JobNumber).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ColourMode).HasMaxLength(12).IsRequired();
            entity.Property(x => x.PaperSize).HasMaxLength(12).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(80);
            entity.Property(x => x.AgentDispatchAttempts).HasDefaultValue(0);
            entity.Property(x => x.PrintDispatchAttempts).HasDefaultValue(0);
            entity.Property(x => x.SpoolerJobName).HasMaxLength(180);
            entity.Property(x => x.SpoolerStatus).HasMaxLength(80);
            entity.Property(x => x.OutputType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.OutputFilePath).HasMaxLength(1024);
            entity.Property(x => x.OutputFileName).HasMaxLength(255);
            entity.Property(x => x.OutputSha256).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.CreatedUtc });
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.JobNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status, x.PrinterDeviceId, x.AgentLeaseExpiresUtc });
            entity.HasIndex(x => new { x.TenantId, x.ClaimedByAgentDeviceId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.Status, x.PrintDispatchLeaseExpiresUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CustomerPrintSession>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CustomerSessionId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<QrStation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.QrStationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PrinterDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PrinterDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PrintPayment>(entity =>
        {
            entity.ToTable("PrintPayments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Method).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.UpiIdSnapshot).HasMaxLength(160);
            entity.Property(x => x.PayeeNameSnapshot).HasMaxLength(120);
            entity.Property(x => x.PaymentReference).HasMaxLength(100);
            entity.Property(x => x.RejectionReason).HasMaxLength(300);
            entity.Property(x => x.UpdatedUtc).IsConcurrencyToken();
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.HasIndex(x => new { x.TenantId, x.PrintJobId, x.CreatedUtc });
            entity.HasIndex(x => new { x.TenantId, x.PrintJobId })
                .IsUnique()
                .HasFilter("\"Status\" IN ('AWAITING_CUSTOMER_PAYMENT','CUSTOMER_REPORTED','CASH_AWAITING_PAYMENT')");
            entity.HasOne<PrintJob>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PrintJobId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PrintJobEvent>(entity =>
        {
            entity.ToTable("PrintJobEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PreviousStatus).HasMaxLength(40);
            entity.Property(x => x.NewStatus).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ActorType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(80);
            entity.HasIndex(x => new { x.TenantId, x.PrintJobId, x.CreatedUtc });
            entity.HasOne<PrintJob>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PrintJobId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PrintDocument>(entity =>
        {
            entity.ToTable("PrintDocuments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.MediaType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.FileExtension).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StorageKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.StorageMode).HasMaxLength(20).IsRequired();
            entity.Property(x => x.PageSizesJson).HasColumnType("jsonb").IsRequired();
            entity.HasOne<AgentDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.AgentDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.AgentDeviceId })
                .HasFilter("\"AgentDeviceId\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.PrintJobId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Sha256 });
            entity.HasOne<PrintJob>().WithOne().HasForeignKey<PrintDocument>(x => new { x.TenantId, x.PrintJobId })
                .HasPrincipalKey<PrintJob>(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);


        });
        modelBuilder.Entity<LocalPrintUploadTicket>(entity =>
        {
            entity.ToTable("LocalPrintUploadTickets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.JobNumber).HasMaxLength(32).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.GrantHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.MediaType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.FileExtension).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.GrantHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CustomerSessionId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.PrintJobId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AgentDeviceId, x.ExpiresUtc, x.CompletedUtc });
            entity.HasIndex(x => new { x.TenantId, x.QrStationId });
            entity.HasIndex(x => new { x.TenantId, x.PrinterDeviceId });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CustomerPrintSession>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CustomerSessionId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<QrStation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.QrStationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PrinterDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.PrinterDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentDevice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.AgentDeviceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
