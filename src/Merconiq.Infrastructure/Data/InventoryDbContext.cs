using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Merconiq.Infrastructure.Data;

/// <summary>
/// Entity Framework Core context for the inventory system. Extends <see cref="IdentityDbContext{TUser}"/>
/// and additionally handles <see cref="AuditableEntity"/> timestamps, soft-delete translation,
/// and a two-phase audit log pipeline that captures old / new / changed values for every save.
/// </summary>
public class InventoryDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ITenantContext _tenantContext;

    public InventoryDbContext(
        DbContextOptions<InventoryDbContext> options,
        ITenantContext tenantContext,
        IHttpContextAccessor? httpContextAccessor = null) : base(options)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Tenant selected for this request or explicitly-created work scope.</summary>
    public string CurrentTenantId => _tenantContext.TenantId;

    /// <summary>Catalog of items.</summary>
    public DbSet<Item> Items { get; set; } = null!;
    public DbSet<UnitOfMeasure> UnitsOfMeasure { get; set; } = null!;

    /// <summary>Current stock-in-hand per item per location.</summary>
    public DbSet<StockInHand> StockInHand { get; set; } = null!;
    public DbSet<StockCount> StockCounts { get; set; } = null!;
    public DbSet<StockCountLine> StockCountLines { get; set; } = null!;
    public DbSet<StockCountObservation> StockCountObservations { get; set; } = null!;
    public DbSet<StockCountVariance> StockCountVariances { get; set; } = null!;

    /// <summary>Tenant-scoped stock reservations.</summary>
    public DbSet<StockReservation> StockReservations { get; set; } = null!;
    public DbSet<TransferOrder> TransferOrders { get; set; } = null!;
    public DbSet<TransferOrderLine> TransferOrderLines { get; set; } = null!;
    public DbSet<TransferTransitEntry> TransferTransitEntries { get; set; } = null!;
    public DbSet<TransferTransitSettlement> TransferTransitSettlements { get; set; } = null!;

    /// <summary>Lot-specific allocations belonging to source-line stock reservations.</summary>
    public DbSet<StockReservationAllocation> StockReservationAllocations { get; set; } = null!;

    /// <summary>Current weighted-average stock valuation per item and location.</summary>
    public DbSet<StockValuationBucket> StockValuationBuckets { get; set; } = null!;

    /// <summary>Append-only valuation postings linked to stock movements.</summary>
    public DbSet<StockValuationEntry> StockValuationEntries { get; set; } = null!;

    /// <summary>Storage locations.</summary>
    public DbSet<Location> Locations { get; set; } = null!;

    /// <summary>Suppliers of items.</summary>
    public DbSet<Supplier> Suppliers { get; set; } = null!;

    /// <summary>Company-scoped customer master records.</summary>
    public DbSet<Customer> Customers { get; set; } = null!;

    /// <summary>Purchase orders.</summary>
    public DbSet<PurchaseOrder> PurchaseOrders { get; set; } = null!;

    /// <summary>Purchase order line items.</summary>
    public DbSet<OrderDetail> OrderDetails { get; set; } = null!;

    /// <summary>Effective-dated tenant tax policies.</summary>
    public DbSet<TaxRule> TaxRules { get; set; } = null!;

    /// <summary>Historical stock movements (receive, transfer, sell).</summary>
    public DbSet<StockTransaction> StockTransactions { get; set; } = null!;

    /// <summary>Append-only audit log.</summary>
    public DbSet<AuditLog> AuditLogs { get; set; } = null!;

    /// <summary>Webhook subscriptions for outbound event notifications.</summary>
    public DbSet<WebhookSubscription> WebhookSubscriptions { get; set; } = null!;

    /// <summary>Durable outbound webhook delivery attempts.</summary>
    public DbSet<WebhookDelivery> WebhookDeliveries { get; set; } = null!;

    /// <summary>Durable API idempotency claims and results.</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords { get; set; } = null!;

    /// <summary>Approved opening-stock replay records.</summary>
    public DbSet<OpeningStockImport> OpeningStockImports { get; set; } = null!;
    public DbSet<OpeningStockImportLine> OpeningStockImportLines { get; set; } = null!;
    public DbSet<OpeningStockCorrection> OpeningStockCorrections { get; set; } = null!;

    /// <summary>Tenant-owned legal and trading companies.</summary>
    public DbSet<Company> Companies { get; set; } = null!;

    /// <summary>Company-owned operating branches.</summary>
    public DbSet<Branch> Branches { get; set; } = null!;
    /// <summary>Company-scoped user capability grants.</summary>
    public DbSet<CompanyMembership> CompanyMemberships { get; set; } = null!;
    public DbSet<DocumentNumberSequence> DocumentNumberSequences { get; set; } = null!;
    public DbSet<DocumentIdentity> DocumentIdentities { get; set; } = null!;
    public DbSet<DocumentLineIdentity> DocumentLineIdentities { get; set; } = null!;
    public DbSet<DocumentLineLink> DocumentLineLinks { get; set; } = null!;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditLogsAreAppendOnly();
        EnsureValuationEntriesAreAppendOnly();
        EnsureDocumentLineLinksAreAppendOnly();
        EnsureStockTransactionsAreAppendOnly();
        EnsureStockCountRecordsAreAppendOnly();
        EnsureStockCountVariancesAreAppendOnly();
        EnsureTransferTransitEntriesAreAppendOnly();
        EnsureTransferTransitSettlementsAreAppendOnly();
        NormalizeStockLotExpiryDates();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAuditLogsAreAppendOnly();
        EnsureValuationEntriesAreAppendOnly();
        EnsureDocumentLineLinksAreAppendOnly();
        EnsureStockTransactionsAreAppendOnly();
        EnsureStockCountRecordsAreAppendOnly();
        EnsureStockCountVariancesAreAppendOnly();
        EnsureTransferTransitEntriesAreAppendOnly();
        EnsureTransferTransitSettlementsAreAppendOnly();
        NormalizeStockLotExpiryDates();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Saves pending changes, stamping <see cref="AuditableEntity"/> timestamps, translating
    /// soft-delete <see cref="EntityState.Deleted"/> entries to a flag flip, and emitting
    /// <see cref="AuditLog"/> rows.
    /// </summary>
    /// <remarks>
    /// Audit log uses a two-phase write: phase one captures original / current values from the
    /// change tracker (running before <c>base.SaveChangesAsync</c>), phase two resolves any
    /// temporary primary keys that the database assigned during insert and writes the
    /// final <see cref="AuditLog"/> rows. Splitting the work lets us record auto-generated
    /// identities in the audit trail without a round-trip to the database to look them up.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var currentUser = _httpContextAccessor?.HttpContext?.User?.Identity?.Name ?? "System";
        return SaveChangesAsAsync(currentUser, cancellationToken);
    }

    /// <summary>Saves changes with a server-verified actor label for audit entries.</summary>
    public async Task<int> SaveChangesAsAsync(
        string auditUsername,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(auditUsername))
        {
            throw new ArgumentException("An authenticated audit identity is required.", nameof(auditUsername));
        }

        EnsureAuditLogsAreAppendOnly();
        EnsureValuationEntriesAreAppendOnly();
        EnsureDocumentLineLinksAreAppendOnly();
        EnsureStockTransactionsAreAppendOnly();
        EnsureStockCountRecordsAreAppendOnly();
        EnsureStockCountVariancesAreAppendOnly();
        EnsureTransferTransitEntriesAreAppendOnly();
        EnsureTransferTransitSettlementsAreAppendOnly();
        NormalizeStockLotExpiryDates();
        var currentUser = auditUsername.Trim();
        var utcNow = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<ITenantScoped>()
                     .Where(entry => entry.State != EntityState.Unchanged && entry.State != EntityState.Detached))
        {
            // Tenant ownership is taken from the authenticated request, never from client input.
            entry.Entity.TenantId = CurrentTenantId;
        }

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = utcNow;
                    entry.Entity.CreatedBy = currentUser;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = utcNow;
                    entry.Entity.UpdatedBy = currentUser;
                    break;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Merconiq.Core.Interfaces.ISoftDelete>())
        {
            if (entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                entry.Entity.IsDeleted = true;
            }
        }

        var auditEntries = OnBeforeSaveChanges(currentUser);
        var result = await base.SaveChangesAsync(cancellationToken);

        if (auditEntries.Any())
        {
            await OnAfterSaveChanges(auditEntries, cancellationToken);
        }

        return result;
    }

    private void EnsureAuditLogsAreAppendOnly()
    {
        if (ChangeTracker.Entries<AuditLog>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit logs are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureValuationEntriesAreAppendOnly()
    {
        if (ChangeTracker.Entries<StockValuationEntry>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Stock valuation entries are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureDocumentLineLinksAreAppendOnly()
    {
        if (ChangeTracker.Entries<DocumentLineLink>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Document line links are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureStockTransactionsAreAppendOnly()
    {
        if (ChangeTracker.Entries<StockTransaction>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Stock transactions are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureStockCountRecordsAreAppendOnly()
    {
        if (ChangeTracker.Entries<StockCount>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<StockCountLine>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<StockCountObservation>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Stock-count snapshots and observations are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureStockCountVariancesAreAppendOnly()
    {
        if (ChangeTracker.Entries<StockCountVariance>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Stock-count variances are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureTransferTransitEntriesAreAppendOnly()
    {
        if (ChangeTracker.Entries<TransferTransitEntry>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Transfer transit entries are append-only and cannot be updated or deleted.");
        }
    }

    private void EnsureTransferTransitSettlementsAreAppendOnly()
    {
        if (ChangeTracker.Entries<TransferTransitSettlement>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Transfer transit settlements are append-only and cannot be updated or deleted.");
        }
    }

    private void NormalizeStockLotExpiryDates()
    {
        foreach (var entry in ChangeTracker.Entries<StockInHand>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            entry.Entity.ExpiryDate = StockLotExpiryDate.Normalize(entry.Entity.ExpiryDate);

        foreach (var entry in ChangeTracker.Entries<StockReservation>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            entry.Entity.ExpiryDate = StockLotExpiryDate.Normalize(entry.Entity.ExpiryDate);

        foreach (var entry in ChangeTracker.Entries<StockReservationAllocation>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            entry.Entity.ExpiryDate = StockLotExpiryDate.Normalize(entry.Entity.ExpiryDate);

        foreach (var entry in ChangeTracker.Entries<StockTransaction>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            entry.Entity.ExpiryDate = StockLotExpiryDate.Normalize(entry.Entity.ExpiryDate);
    }

    private List<AuditEntry> OnBeforeSaveChanges(string username)
    {
        ChangeTracker.DetectChanges();
        var auditEntries = new List<AuditEntry>();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog || entry.State == EntityState.Detached || entry.State == EntityState.Unchanged)
            {
                continue;
            }

            var auditEntry = new AuditEntry(entry)
            {
                TableName = entry.Entity.GetType().Name,
                UserId = username,
                Action = entry.State switch
                {
                    EntityState.Added => "Insert",
                    EntityState.Deleted => "Delete",
                    _ => null!
                }
            };

            foreach (var property in entry.Properties)
            {
                string propertyName = property.Metadata.Name;
                if (property.Metadata.IsPrimaryKey())
                {
                    if (property.IsTemporary)
                    {
                        auditEntry.TemporaryProperties.Add(property);
                    }
                    else
                    {
                        auditEntry.KeyValues[propertyName] = property.CurrentValue ?? "";
                    }
                    continue;
                }

                switch (entry.State)
                {
                    case EntityState.Added:
                        auditEntry.Action = "Insert";
                        auditEntry.NewValues[propertyName] = property.CurrentValue ?? "";
                        break;

                    case EntityState.Deleted:
                        auditEntry.Action = "Delete";
                        auditEntry.OldValues[propertyName] = property.OriginalValue ?? "";
                        break;

                    case EntityState.Modified:
                        if (property.IsModified)
                        {
                            auditEntry.Action = "Update";
                            auditEntry.ChangedColumns.Add(propertyName);
                            auditEntry.OldValues[propertyName] = property.OriginalValue ?? "";
                            auditEntry.NewValues[propertyName] = property.CurrentValue ?? "";
                        }
                        break;
                }
            }

            if (!string.IsNullOrEmpty(auditEntry.Action))
            {
                auditEntries.Add(auditEntry);
            }
        }

        return auditEntries;
    }

    private async Task OnAfterSaveChanges(List<AuditEntry> auditEntries, CancellationToken cancellationToken)
    {
        foreach (var auditEntry in auditEntries)
        {
            foreach (var prop in auditEntry.TemporaryProperties)
            {
                auditEntry.KeyValues[prop.Metadata.Name] = prop.CurrentValue ?? "";
            }
            AuditLogs.Add(auditEntry.ToAuditLog());
        }
        await base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<Item>(entity =>
        {
            entity.HasQueryFilter(e => !e.IsDeleted && e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.ItemCode }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.Barcode }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
            entity.Property(e => e.ItemCode).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(128);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Barcode).HasMaxLength(100);
            entity.Property(e => e.Rate).HasColumnType("decimal(18,2)");
            entity.Property(e => e.PurchaseToBaseFactor).HasColumnType("decimal(18,6)");
            entity.Property(e => e.SalesToBaseFactor).HasColumnType("decimal(18,6)");
            entity.HasOne(e => e.BaseUnit).WithMany()
                .HasForeignKey(e => new { e.BaseUnitId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.PurchaseUnit).WithMany()
                .HasForeignKey(e => new { e.PurchaseUnitId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.SalesUnit).WithMany()
                .HasForeignKey(e => new { e.SalesUnitId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.SupplierId);
            entity.Property(e => e.ReorderLevel).HasDefaultValue(10);

            entity.HasOne(i => i.Supplier)
                  .WithMany(s => s.Items)
                  .HasForeignKey(i => i.SupplierId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UnitOfMeasure>(entity =>
        {
            entity.HasQueryFilter(e => !e.IsDeleted && e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
            entity.HasIndex(e => new { e.Id, e.TenantId }).IsUnique();
            entity.Property(e => e.DecimalPlaces).HasDefaultValue(0);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasQueryFilter(e => !e.IsDeleted && e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(128);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ContactPerson).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Address).HasMaxLength(500);
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasQueryFilter(e => !e.IsDeleted && e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(128);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.HasIndex(e => new { e.TenantId, e.BranchId });
            entity.HasOne(e => e.Branch)
                  .WithMany(b => b.Locations)
                  .HasForeignKey(e => new { e.BranchId, e.TenantId })
                  .HasPrincipalKey(b => new { b.Id, b.TenantId })
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(128);
            entity.Property(e => e.Code).HasMaxLength(32).IsRequired();
            entity.Property(e => e.LegalName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TradingName).HasMaxLength(200);
            entity.Property(e => e.RegistrationNumber).HasMaxLength(100);
            entity.Property(e => e.TaxIdentifier).HasMaxLength(100);
            entity.Property(e => e.BaseCurrency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.CurrencyScale);
            entity.ToTable("Companies", table => table.HasCheckConstraint(
                "CK_Companies_CurrencyScale",
                "\"CurrencyScale\" IS NULL OR \"CurrencyScale\" BETWEEN 0 AND 4"));
            entity.Property(e => e.CountryCode).HasMaxLength(2);
            entity.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
            entity.HasIndex(e => new { e.Id, e.TenantId }).IsUnique();
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CustomerCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ContactEmail).HasMaxLength(254);
            entity.Property(e => e.ContactPhone).HasMaxLength(40);
            entity.Property(e => e.BillingAddress).HasMaxLength(500);
            entity.Property(e => e.ShippingAddress).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CustomerCode })
                .IsUnique()
                .HasDatabaseName("UX_Customers_TenantId_CompanyId_CustomerCode");
            entity.ToTable("Customers", table =>
            {
                table.HasCheckConstraint(
                    "CK_Customers_NormalizedCustomerCode",
                    "\"CustomerCode\" <> '' AND \"CustomerCode\" = btrim(\"CustomerCode\") AND \"CustomerCode\" = upper(\"CustomerCode\")");
                table.HasCheckConstraint(
                    "CK_Customers_PaymentTermDays",
                    "\"PaymentTermDays\" IS NULL OR \"PaymentTermDays\" BETWEEN 0 AND 3650");
            });
            entity.HasOne(e => e.Company)
                .WithMany(company => company.Customers)
                .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                .HasPrincipalKey(company => new { company.Id, company.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Branch>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExternalId).HasMaxLength(128);
            entity.Property(e => e.Code).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Code }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ExternalId }).IsUnique();
            entity.HasIndex(e => new { e.Id, e.TenantId }).IsUnique();
            entity.HasOne(e => e.Company)
                  .WithMany(c => c.Branches)
                  .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                  .HasPrincipalKey(c => new { c.Id, c.TenantId })
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CompanyMembership>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(450).IsRequired();
            entity.Property(e => e.Capabilities).HasConversion<int>().IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.UserId }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.UserId, e.IsActive });
            entity.HasOne(e => e.Company)
                .WithMany(c => c.Memberships)
                .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                .HasPrincipalKey(c => new { c.Id, c.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany(u => u.CompanyMemberships)
                .HasForeignKey(e => new { e.UserId, e.TenantId })
                .HasPrincipalKey(u => new { u.Id, u.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockInHand>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.TenantId);
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.ReservedQuantity).HasDefaultValue(0).IsRequired();
            entity.Property(e => e.QuarantinedQuantity).HasDefaultValue(0).IsRequired();
            entity.ToTable("StockInHand", table => table.HasCheckConstraint(
                "CK_StockInHand_AvailableQuantity",
                "\"Quantity\" >= 0 AND \"ReservedQuantity\" >= 0 AND \"QuarantinedQuantity\" >= 0 AND \"ReservedQuantity\" + \"QuarantinedQuantity\" <= \"Quantity\""));
            entity.HasIndex(e => new { e.TenantId, e.ItemId, e.LocationId, e.BatchNumber, e.ExpiryDate }).IsUnique();

            entity.HasOne(s => s.Item)
                  .WithMany(i => i.StockInHands)
                  .HasForeignKey(s => s.ItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Location)
                  .WithMany(l => l.StockInHands)
                  .HasForeignKey(s => new { s.LocationId, s.TenantId })
                  .HasPrincipalKey(l => new { l.Id, l.TenantId })
                  .OnDelete(DeleteBehavior.Cascade);

            // PostgreSQL exposes the inserting transaction's xmin (xid) as a hidden
            // system column on every row. Mapping it to the CLR Version property and
            // marking it as a concurrency token gives us optimistic concurrency with
            // zero schema, trigger, or stored-procedure cost — EF Core compares xmin
            // on UPDATE and raises a concurrency exception if the row was modified.
            entity.Property(s => s.Version)
                  .HasColumnName("xmin")
                  .HasColumnType("xid")
                  .ValueGeneratedOnAddOrUpdate()
                  .IsConcurrencyToken();
        });

        modelBuilder.Entity<StockCount>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.Property(e => e.SnapshotAtUtc).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.LocationId, e.SnapshotAtUtc });
            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Company)
                .WithMany()
                .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockCountLine>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ItemCodeSnapshot).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ItemDescriptionSnapshot).HasMaxLength(500).IsRequired();
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.ToTable("StockCountLines", table => table.HasCheckConstraint(
                "CK_StockCountLines_SnapshotQuantity",
                "\"SnapshotQuantity\" >= 0"));
            entity.HasOne(e => e.StockCount)
                .WithMany(e => e.Lines)
                .HasForeignKey(e => new { e.StockCountId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => new { e.ItemId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockCountObservation>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.ToTable("StockCountObservations", table => table.HasCheckConstraint(
                "CK_StockCountObservations_CountedQuantity",
                "\"CountedQuantity\" >= 0"));
            entity.HasOne(e => e.StockCountLine)
                .WithOne(e => e.Observation)
                .HasForeignKey<StockCountObservation>(e => new { e.StockCountLineId, e.TenantId })
                .HasPrincipalKey<StockCountLine>(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockCountVariance>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ApprovedUnitCost).HasColumnType("decimal(18,6)");
            entity.Property(e => e.ValueAdjustment).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => new { e.StockCountLineId, e.TenantId }).IsUnique();
            entity.HasIndex(e => new { e.StockTransactionId, e.TenantId })
                .IsUnique()
                .HasFilter("\"StockTransactionId\" IS NOT NULL");
            entity.ToTable("StockCountVariances", table =>
            {
                table.HasCheckConstraint(
                    "CK_StockCountVariances_Quantities",
                    "\"ExpectedCurrentQuantity\" >= 0 AND \"CountedQuantity\" >= 0 AND \"DeltaQuantity\" = \"CountedQuantity\" - \"ExpectedCurrentQuantity\"");
                table.HasCheckConstraint(
                    "CK_StockCountVariances_Reason",
                    "length(trim(\"Reason\")) > 0");
                table.HasCheckConstraint(
                    "CK_StockCountVariances_ApprovedUnitCost",
                    "\"ApprovedUnitCost\" IS NULL OR \"ApprovedUnitCost\" >= 0");
            });
            entity.HasOne(e => e.StockCountLine)
                .WithOne(line => line.Variance)
                .HasForeignKey<StockCountVariance>(e => new { e.StockCountLineId, e.TenantId })
                .HasPrincipalKey<StockCountLine>(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.StockTransaction)
                .WithMany()
                .HasForeignKey(e => new { e.StockTransactionId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockReservation>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SourceLineReference).HasMaxLength(128).IsRequired();
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.ResolutionReason).HasMaxLength(500);
            entity.Property(e => e.ExpiryExceptionReason).HasMaxLength(500);
            entity.HasIndex(e => new { e.TenantId, e.SourceLineReference }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ItemId, e.LocationId, e.BatchNumber, e.ExpiryDate, e.Status });
            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => e.ItemId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<StockReservationAllocation>(entity =>
        {
            entity.HasQueryFilter(allocation => allocation.TenantId == CurrentTenantId);
            entity.Property(allocation => allocation.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(allocation => allocation.BatchNumber).HasMaxLength(100);
            entity.Property(allocation => allocation.ExpiryDate).HasColumnType("timestamp with time zone");
            entity.Property(allocation => allocation.ExpiryExceptionReason).HasMaxLength(500);
            entity.HasIndex(allocation => new { allocation.TenantId, allocation.ReservationId, allocation.Ordinal })
                .IsUnique();
            entity.HasIndex(allocation => new { allocation.TenantId, allocation.ReservationId });
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_StockReservationAllocations_Quantities",
                "\"Quantity\" > 0 AND \"ConsumedQuantity\" >= 0 AND \"ConsumedQuantity\" <= \"Quantity\""));
            entity.HasOne(allocation => allocation.Reservation)
                .WithMany(reservation => reservation.Allocations)
                .HasForeignKey(allocation => new { allocation.ReservationId, allocation.TenantId })
                .HasPrincipalKey(reservation => new { reservation.Id, reservation.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(allocation => allocation.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<TransferOrder>(entity =>
        {
            entity.ToTable("TransferOrders", table => table.HasCheckConstraint(
                "CK_TransferOrders_DistinctLocations", "\"FromLocationId\" <> \"ToLocationId\""));
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.Property(e => e.DocumentId)
                .HasConversion(id => id.Value, value => new DocumentIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(e => e.DocumentId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Status });
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.HasOne(e => e.DocumentIdentity)
                .WithOne()
                .HasForeignKey<TransferOrder>(e => new { e.DocumentId, e.TenantId })
                .HasPrincipalKey<DocumentIdentity>(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Company)
                .WithMany()
                .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.FromLocation)
                .WithMany()
                .HasForeignKey(e => new { e.FromLocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ToLocation)
                .WithMany()
                .HasForeignKey(e => new { e.ToLocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransferOrderLine>(entity =>
        {
            entity.ToTable("TransferOrderLines", table =>
            {
                table.HasCheckConstraint("CK_TransferOrderLines_PositiveQuantity", "\"Quantity\" > 0");
                table.HasCheckConstraint("CK_TransferOrderLines_DispatchedQuantity", "\"DispatchedQuantity\" >= 0 AND \"DispatchedQuantity\" <= \"Quantity\"");
            });
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.Property(e => e.DocumentLineId)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(e => e.DocumentLineId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.DispatchedQuantity).IsRequired();
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.HasOne(e => e.TransferOrder)
                .WithMany(e => e.Lines)
                .HasForeignKey(e => new { e.TransferOrderId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.DocumentLineIdentity)
                .WithOne()
                .HasForeignKey<TransferOrderLine>(e => new { e.DocumentLineId, e.TenantId })
                .HasPrincipalKey<DocumentLineIdentity>(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => new { e.ItemId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransferTransitEntry>(entity =>
        {
            entity.ToTable("TransferTransitEntries", table =>
            {
                table.HasCheckConstraint("CK_TransferTransitEntries_PositiveQuantity", "\"Quantity\" > 0");
                table.HasCheckConstraint("CK_TransferTransitEntries_NonNegativeValue", "\"UnitCost\" >= 0 AND \"TotalValue\" >= 0");
            });
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SourceDocumentLineId)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(e => e.SourceDocumentLineId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.UnitCost).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TotalValue).HasColumnType("decimal(18,6)");
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DispatchedBy).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.TransferOrderLineId, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.TransferOrderId, e.DispatchedAt });
            entity.HasIndex(e => new { e.TenantId, e.StockTransactionId }).IsUnique();
            entity.HasOne<TransferOrder>()
                .WithMany()
                .HasForeignKey(e => new { e.TransferOrderId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TransferOrderLine>()
                .WithMany()
                .HasForeignKey(e => new { e.TransferOrderLineId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DocumentLineIdentity>()
                .WithMany()
                .HasForeignKey(e => new { e.SourceDocumentLineId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Company>()
                .WithMany()
                .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Item>()
                .WithMany()
                .HasForeignKey(e => new { e.ItemId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>()
                .WithMany()
                .HasForeignKey(e => new { e.FromLocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>()
                .WithMany()
                .HasForeignKey(e => new { e.ToLocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StockTransaction>()
                .WithMany()
                .HasForeignKey(e => new { e.StockTransactionId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransferTransitSettlement>(entity =>
        {
            entity.ToTable("TransferTransitSettlements", table =>
            {
                table.HasCheckConstraint("CK_TransferTransitSettlements_PositiveQuantity", "\"Quantity\" > 0");
                table.HasCheckConstraint("CK_TransferTransitSettlements_NonNegativeValue", "\"UnitCost\" >= 0 AND \"TotalValue\" >= 0");
                table.HasCheckConstraint(
                    "CK_TransferTransitSettlements_DocumentIdentityPair",
                    "(\"DocumentId\" IS NULL AND \"DocumentLineId\" IS NULL) OR (\"DocumentId\" IS NOT NULL AND \"DocumentLineId\" IS NOT NULL)");
            });
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DocumentId)
                .HasConversion(
                    id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DocumentIdentityId(value.Value) : (DocumentIdentityId?)null)
                .ValueGeneratedNever();
            entity.Property(e => e.DocumentId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.DocumentLineId)
                .HasConversion(
                    id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DocumentLineIdentityId(value.Value) : (DocumentLineIdentityId?)null)
                .ValueGeneratedNever();
            entity.Property(e => e.DocumentLineId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.SourceDocumentLineId)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(e => e.SourceDocumentLineId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.SettlementType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.UnitCost).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TotalValue).HasColumnType("decimal(18,6)");
            entity.Property(e => e.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SettledBy).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.HasIndex(e => new { e.TenantId, e.DocumentId })
                .IsUnique()
                .HasFilter("\"DocumentId\" IS NOT NULL");
            entity.HasIndex(e => new { e.TenantId, e.DocumentLineId })
                .IsUnique()
                .HasFilter("\"DocumentLineId\" IS NOT NULL");
            entity.HasIndex(e => new { e.TenantId, e.TransferTransitEntryId, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.TransferOrderId, e.SettledAt });
            entity.HasOne(e => e.DocumentIdentity)
                .WithOne()
                .HasForeignKey<TransferTransitSettlement>(e => new { e.DocumentId, e.TenantId })
                .HasPrincipalKey<DocumentIdentity>(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.DocumentLineIdentity)
                .WithOne()
                .HasForeignKey<TransferTransitSettlement>(e => new { e.DocumentId, e.DocumentLineId, e.TenantId })
                .HasPrincipalKey<DocumentLineIdentity>(e => new { e.DocumentId, e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TransferTransitEntry>()
                .WithMany()
                .HasForeignKey(e => new { e.TransferTransitEntryId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TransferOrder>()
                .WithMany()
                .HasForeignKey(e => new { e.TransferOrderId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TransferOrderLine>()
                .WithMany()
                .HasForeignKey(e => new { e.TransferOrderLineId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DocumentLineIdentity>()
                .WithMany()
                .HasForeignKey(e => new { e.SourceDocumentLineId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Company>()
                .WithMany()
                .HasForeignKey(e => new { e.CompanyId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Item>()
                .WithMany()
                .HasForeignKey(e => new { e.ItemId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>()
                .WithMany()
                .HasForeignKey(e => new { e.FromLocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Location>()
                .WithMany()
                .HasForeignKey(e => new { e.ToLocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StockTransaction>()
                .WithMany()
                .HasForeignKey(e => new { e.StockTransactionId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockValuationBucket>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Value).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => new { e.TenantId, e.ItemId, e.LocationId }).IsUnique();
            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => new { e.ItemId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<StockValuationEntry>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.UnitCost).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TotalValue).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => new { e.TenantId, e.ItemId, e.LocationId });
            entity.HasIndex(e => new { e.TenantId, e.StockTransactionId }).IsUnique();
            entity.HasOne(e => e.StockTransaction)
                .WithMany()
                .HasForeignKey(e => new { e.StockTransactionId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => new { e.ItemId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrder>(entity =>
        {
            entity.ToTable("PurchaseOrders", table => table.HasCheckConstraint(
                "CK_PurchaseOrders_ApprovedCommercialVersion",
                "\"Status\" <> 'Approved' OR (\"ApprovedCommercialVersion\" IS NOT NULL AND \"ApprovedCommercialVersion\" = \"CommercialVersion\" AND \"ApprovedCommercialSnapshotJson\" IS NOT NULL)"));
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.TenantId, e.PONumber }).IsUnique();
            entity.Property(e => e.PONumber).HasMaxLength(50).IsRequired();
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.NetAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.CurrencyScale).IsRequired();
            entity.Property(e => e.CalculationVersion).IsRequired();
            entity.Property(e => e.CommercialVersion).HasDefaultValue(1).IsRequired();
            entity.Property(e => e.ApprovedCommercialSnapshotJson).HasColumnType("jsonb");
            entity.Property(e => e.DeliveryTerms).HasMaxLength(1000);
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.DocumentId)
                .HasConversion(id => id.Value, value => new DocumentIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(e => e.DocumentId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.PONumber).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.HasIndex(e => e.SupplierId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.OrderDate);

            entity.HasOne(po => po.DocumentIdentity)
                .WithOne(identity => identity.PurchaseOrder)
                .HasForeignKey<PurchaseOrder>(po => new { po.DocumentId, po.TenantId })
                .HasPrincipalKey<DocumentIdentity>(identity => new { identity.Id, identity.TenantId })
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(po => po.Supplier)
                  .WithMany(s => s.PurchaseOrders)
                  .HasForeignKey(po => po.SupplierId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderDetail>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.TenantId);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(20,4)");
            entity.Property(e => e.DocumentLineId)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(e => e.DocumentLineId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

            entity.HasOne(od => od.PurchaseOrder)
                  .WithMany(po => po.OrderDetails)
                  .HasForeignKey(od => od.PurchaseOrderId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(od => od.Item)
                  .WithMany(i => i.OrderDetails)
                  .HasForeignKey(od => od.ItemId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.Property(e => e.DiscountPercent).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TaxRatePercent).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TaxCategory).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.TaxMode).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.Direction).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.CurrencyScale).IsRequired();
            entity.Property(e => e.CalculationVersion).IsRequired();
            entity.Property(e => e.NetAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TaxableAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(18,6)");
            entity.Property(e => e.GrossAmount).HasColumnType("decimal(18,6)");

            entity.HasOne(od => od.TaxRule)
                .WithMany()
                .HasForeignKey(od => od.TaxRuleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(od => od.DocumentLineIdentity)
                .WithOne(line => line.OrderDetail)
                .HasForeignKey<OrderDetail>(od => new { od.DocumentLineId, od.TenantId })
                .HasPrincipalKey<DocumentLineIdentity>(line => new { line.Id, line.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaxRule>(entity =>
        {
            entity.HasQueryFilter(rule => rule.TenantId == CurrentTenantId);
            entity.Property(rule => rule.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(rule => rule.Code).HasMaxLength(64).IsRequired();
            entity.Property(rule => rule.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(rule => rule.RatePercent).HasColumnType("decimal(18,6)");
            entity.Property(rule => rule.CalculationMode).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(rule => rule.EffectiveFromUtc).IsRequired();
            entity.HasIndex(rule => new { rule.TenantId, rule.Code, rule.EffectiveFromUtc }).IsUnique();
            entity.HasIndex(rule => new { rule.TenantId, rule.Code, rule.IsActive });
        });

        modelBuilder.Entity<DocumentIdentity>(entity =>
        {
            entity.HasQueryFilter(document => document.TenantId == CurrentTenantId);
            entity.Property(document => document.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(document => document.Id)
                .HasConversion(id => id.Value, value => new DocumentIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(document => document.DocumentType).HasMaxLength(64).IsRequired();
            entity.Property(document => document.HumanNumber).HasMaxLength(50).IsRequired();
            entity.Property(document => document.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(document => document.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.Property(document => document.RequestScope).HasMaxLength(256).IsRequired();
            entity.Property(document => document.RequestKey).HasMaxLength(200);
            entity.Property(document => document.RequestHash).HasMaxLength(64);
            entity.HasAlternateKey(document => new { document.Id, document.TenantId });
            entity.HasIndex(document => new { document.TenantId, document.DocumentType, document.Period, document.HumanNumber })
                .IsUnique()
                .HasFilter("\"CompanyId\" IS NULL");
            entity.HasIndex(document => new { document.TenantId, document.CompanyId, document.DocumentType, document.Period, document.HumanNumber })
                .IsUnique()
                .HasFilter("\"CompanyId\" IS NOT NULL");
            entity.HasIndex(document => new { document.TenantId, document.RequestScope, document.RequestKey })
                .IsUnique()
                .HasFilter("\"RequestKey\" IS NOT NULL");
            entity.Property(document => document.Id).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(document => document.DocumentType).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(document => document.HumanNumber).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(document => document.Period).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(document => document.RequestScope).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(document => document.RequestKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(document => document.RequestHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.HasOne(document => document.Company)
                .WithMany()
                .HasForeignKey(document => new { document.CompanyId, document.TenantId })
                .HasPrincipalKey(company => new { company.Id, company.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentLineIdentity>(entity =>
        {
            entity.HasQueryFilter(line => line.TenantId == CurrentTenantId);
            entity.Property(line => line.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(line => line.Id)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(line => line.DocumentId)
                .HasConversion(id => id.Value, value => new DocumentIdentityId(value))
                .ValueGeneratedNever();
            entity.Property(line => line.LineType).HasMaxLength(64).IsRequired();
            entity.HasAlternateKey(line => new { line.Id, line.TenantId });
            entity.HasOne(line => line.DocumentIdentity)
                .WithMany(document => document.Lines)
                .HasForeignKey(line => new { line.DocumentId, line.TenantId })
                .HasPrincipalKey(document => new { document.Id, document.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(line => line.Id).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(line => line.DocumentId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(line => line.LineType).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });

        modelBuilder.Entity<DocumentLineLink>(entity =>
        {
            entity.HasQueryFilter(link => link.TenantId == CurrentTenantId);
            entity.Property(link => link.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(link => link.SourceDocumentId)
                .HasConversion(id => id.Value, value => new DocumentIdentityId(value));
            entity.Property(link => link.SourceLineId)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value));
            entity.Property(link => link.TargetDocumentId)
                .HasConversion(id => id.Value, value => new DocumentIdentityId(value));
            entity.Property(link => link.TargetLineId)
                .HasConversion(id => id.Value, value => new DocumentLineIdentityId(value));
            entity.Property(link => link.RelationshipType).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasIndex(link => new
            {
                link.TenantId,
                link.CompanyId,
                link.SourceDocumentId,
                link.SourceLineId,
                link.TargetDocumentId,
                link.TargetLineId,
                link.RelationshipType
            }).IsUnique();
            entity.HasOne(link => link.SourceLine)
                .WithMany()
                .HasForeignKey(link => new { link.SourceDocumentId, link.SourceLineId, link.TenantId })
                .HasPrincipalKey(line => new { line.DocumentId, line.Id, line.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(link => link.TargetLine)
                .WithMany()
                .HasForeignKey(link => new { link.TargetDocumentId, link.TargetLineId, link.TenantId })
                .HasPrincipalKey(line => new { line.DocumentId, line.Id, line.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Company>()
                .WithMany()
                .HasForeignKey(link => new { link.CompanyId, link.TenantId })
                .HasPrincipalKey(company => new { company.Id, company.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockTransaction>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasAlternateKey(e => new { e.Id, e.TenantId });
            entity.HasIndex(e => e.TenantId);
            entity.Property(e => e.TransactionType).HasConversion<string>().HasMaxLength(50).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.ExpiryExceptionReason).HasMaxLength(500);
            entity.Property(e => e.QuarantineReason).HasMaxLength(500);
            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.SourceLineReference).HasMaxLength(128);
            entity.Property(e => e.ReturnDisposition).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.UnitCost).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => e.TransactionDate);
            entity.HasIndex(e => e.ItemId);
            entity.HasIndex(e => new { e.ItemId, e.TransactionDate });
            entity.HasIndex(e => new { e.TenantId, e.SourceLineReference })
                  .IsUnique()
                  .HasFilter("\"SourceLineReference\" IS NOT NULL");

            entity.HasOne(st => st.OriginalTransaction)
                  .WithMany()
                  .HasForeignKey(st => new { st.OriginalTransactionId, st.TenantId })
                  .HasPrincipalKey(st => new { st.Id, st.TenantId })
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(st => st.Item)
                  .WithMany(i => i.StockTransactions)
                  .HasForeignKey(st => st.ItemId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(st => st.FromLocation)
                  .WithMany()
                  .HasForeignKey(st => new { st.FromLocationId, st.TenantId })
                  .HasPrincipalKey(l => new { l.Id, l.TenantId })
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(st => st.ToLocation)
                  .WithMany()
                  .HasForeignKey(st => new { st.ToLocationId, st.TenantId })
                  .HasPrincipalKey(l => new { l.Id, l.TenantId })
                  // Keep tenant identity non-null and preserve transfer history if a location
                  // is hard-deleted. Normal location deletion is soft-delete only.
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<WebhookSubscription>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.LeaseToken).IsConcurrencyToken();
            entity.Property(e => e.LastResponse).HasMaxLength(4096);
            entity.Property(e => e.LastError).HasMaxLength(4096);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => new { e.EventId, e.SubscriptionId }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            entity.HasIndex(e => e.LeaseToken).IsUnique();
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Scope).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.ResponseBody).HasMaxLength(16384);
            entity.Property(e => e.LastError).HasMaxLength(4096);
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.HasIndex(e => new { e.TenantId, e.Scope, e.Key }).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<OpeningStockImport>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ImportReference).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ApprovalReference).HasMaxLength(128).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ApprovedBy).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.ImportReference }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ApprovalReference }).IsUnique();
            entity.HasIndex(e => e.TenantId).IsUnique();
            entity.Property(e => e.Id).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.TenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ImportReference).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ApprovalReference).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.RequestHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ApprovedBy).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ApprovedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.CutoverAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.LineCount).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });

        modelBuilder.Entity<OpeningStockImportLine>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ExternalReference).HasMaxLength(128).IsRequired();
            entity.Property(e => e.UnitCost).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => new { e.OpeningStockImportId, e.ExternalReference }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ItemId, e.LocationId });
            entity.HasOne(e => e.OpeningStockImport)
                .WithMany(e => e.Lines)
                .HasForeignKey(e => new { e.OpeningStockImportId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.StockTransaction)
                .WithMany()
                .HasForeignKey(e => new { e.StockTransactionId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => e.ItemId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => new { e.LocationId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Id).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.TenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.OpeningStockImportId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.StockTransactionId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.RowNumber).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ExternalReference).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ItemId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.LocationId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.Quantity).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.UnitCost).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });

        modelBuilder.Entity<OpeningStockCorrection>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CorrectionReference).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ApprovalReference).HasMaxLength(128).IsRequired();
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CorrectedBy).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.CorrectionReference }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.ApprovalReference }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.OpeningStockImportId }).IsUnique();
            entity.HasOne(e => e.OpeningStockImport)
                .WithMany()
                .HasForeignKey(e => new { e.OpeningStockImportId, e.TenantId })
                .HasPrincipalKey(e => new { e.Id, e.TenantId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Id).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.TenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.OpeningStockImportId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.CorrectionReference).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.ApprovalReference).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.RequestHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.Reason).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.CorrectedBy).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.CorrectedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(e => e.LineCount).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });

        modelBuilder.Entity<DocumentNumberSequence>(entity =>
        {
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId);
            entity.Property(e => e.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DocumentType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Prefix).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.DocumentType, e.Period }).IsUnique();
        });
    }
}

/// <summary>
/// Internal helper that captures per-property old / new values during the audit pipeline
/// and converts them into <see cref="AuditLog"/> rows.
/// </summary>
public class AuditEntry
{
    public AuditEntry(EntityEntry entry)
    {
        Entry = entry;
    }

    public EntityEntry Entry { get; }
    public string UserId { get; set; } = null!;
    public string TableName { get; set; } = null!;
    public string Action { get; set; } = null!;
    public Dictionary<string, object> KeyValues { get; } = new();
    public Dictionary<string, object> OldValues { get; } = new();
    public Dictionary<string, object> NewValues { get; } = new();
    public List<PropertyEntry> TemporaryProperties { get; } = new();
    public List<string> ChangedColumns { get; } = new();

    public AuditLog ToAuditLog()
    {
        return new AuditLog
        {
            TenantId = Entry.Entity is ITenantScoped tenant ? tenant.TenantId : "default",
            EntityName = TableName,
            Action = Action,
            Username = UserId,
            Timestamp = DateTime.UtcNow,
            KeyValues = KeyValues.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(KeyValues),
            OldValues = OldValues.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(OldValues),
            NewValues = NewValues.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(NewValues),
            ChangedColumns = ChangedColumns.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(ChangedColumns)
        };
    }
}
