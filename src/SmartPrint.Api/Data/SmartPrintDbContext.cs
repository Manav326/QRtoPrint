using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Data;

public sealed class SmartPrintDbContext(DbContextOptions<SmartPrintDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ShopProfile> ShopProfiles => Set<ShopProfile>();
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
    public DbSet<PrinterDevice> PrinterDevices => Set<PrinterDevice>();
    public DbSet<QrStation> QrStations => Set<QrStation>();
    public DbSet<CustomerPrintSession> CustomerPrintSessions => Set<CustomerPrintSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();

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

        modelBuilder.Entity<TenantSubscription>(entity =>
        {
            entity.ToTable("TenantSubscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.BillingInterval).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PrinterDevice>(entity =>
        {
            entity.ToTable("PrinterDevices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Manufacturer).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ConnectionType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ConnectionAddress).HasMaxLength(255);
            entity.Property(x => x.QueueName).HasMaxLength(160);
            entity.Property(x => x.CapabilitiesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
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
            entity.HasIndex(x => new { x.TenantId, x.IsEnabled });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<PrinterDevice>().WithMany().HasForeignKey(x => x.PrinterDeviceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CustomerPrintSession>(entity =>
        {
            entity.ToTable("CustomerPrintSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExpiresUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<QrStation>().WithMany().HasForeignKey(x => x.QrStationId).OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(x => x.JobNumber).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.FailureCode).HasMaxLength(80);
            entity.HasIndex(x => new { x.TenantId, x.CreatedUtc });
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.JobNumber }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CustomerPrintSession>().WithMany().HasForeignKey(x => x.CustomerSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<QrStation>().WithMany().HasForeignKey(x => x.QrStationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PrinterDevice>().WithMany().HasForeignKey(x => x.PrinterDeviceId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
