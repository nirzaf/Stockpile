using System.Globalization;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Validates and imports approved master data without partial mutation.</summary>
public sealed class MasterDataImportService(
    IRepository<UnitOfMeasure> units,
    IRepository<Item> items,
    IRepository<Company> companies,
    IRepository<Branch> branches,
    IRepository<Location> locations,
    IRepository<Supplier> suppliers,
    InventoryDbContext context,
    IUnitOfWork unitOfWork) : IMasterDataImportService
{
    public async Task<ImportUnitsResult> ImportUnitsAsync(
        ImportUnitsRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(request.DryRun, async () =>
        {
            await EnsureCompanyScopeAsync(request.CompanyId, cancellationToken);
            var rows = Parse(request.Csv);
            var results = new List<ImportRowResult>(rows.Count);
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingUnits = await context.UnitsOfMeasure.IgnoreQueryFilters()
                .Where(unit => unit.TenantId == context.CurrentTenantId)
                .AsNoTracking().ToListAsync(cancellationToken);
            var pending = new List<UnitOfMeasure>();

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var error = Validate(row, seenExternalIds, seenCodes);
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                var matches = existingUnits.Where(unit =>
                    Same(unit.ExternalId, row.ExternalId) || Same(unit.Code, row.Code)).ToList();
                if (matches.Count > 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "External ID or code is ambiguous in the current tenant."));
                    continue;
                }

                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    if (existing.IsDeleted)
                    {
                        results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                            "External ID or code already maps to a deleted unit."));
                        continue;
                    }
                    var unchanged = Same(existing.ExternalId, row.ExternalId) &&
                        Same(existing.Code, row.Code) && existing.Name == row.Name &&
                        existing.DecimalPlaces == row.DecimalPlaces &&
                        existing.IsWholeUnitOnly == row.IsWholeUnitOnly;
                    results.Add(new(row.RowNumber, row.ExternalId, unchanged ? "unchanged" : "rejected",
                        unchanged ? null : "External ID or code already maps to different unit data."));
                    continue;
                }

                pending.Add(new UnitOfMeasure
                {
                    ExternalId = row.ExternalId,
                    Code = row.Code,
                    Name = row.Name,
                    DecimalPlaces = row.DecimalPlaces,
                    IsWholeUnitOnly = row.IsWholeUnitOnly
                });
                results.Add(new(row.RowNumber, row.ExternalId, "created"));
            }

            if (results.Any(row => row.Status == "rejected"))
                return SummarizeUnits(request.DryRun, results);
            if (!request.DryRun)
                foreach (var unit in pending)
                    await units.AddAsync(unit);
            return SummarizeUnits(request.DryRun, results);
        }, cancellationToken);
    }

    public async Task<ImportItemsResult> ImportItemsAsync(
        ImportItemsRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(request.DryRun, async () =>
        {
            await EnsureCompanyScopeAsync(request.CompanyId, cancellationToken);
            var rows = ParseItems(request.Csv);
            var results = new List<ImportRowResult>(rows.Count);
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingItems = await context.Items.IgnoreQueryFilters()
                .Where(item => item.TenantId == context.CurrentTenantId)
                .AsNoTracking().ToListAsync(cancellationToken);
            var tenantUnits = await units.Query().ToListAsync(cancellationToken);
            var tenantSuppliers = await suppliers.Query().ToListAsync(cancellationToken);
            var pending = new List<Item>();

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var error = Validate(row, seenExternalIds, seenCodes);
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                var baseUnitId = ResolveUnit(row.BaseUnitExternalId, tenantUnits, out error);
                var purchaseUnitId = error is null
                    ? ResolveUnit(row.PurchaseUnitExternalId, tenantUnits, out error)
                    : null;
                var salesUnitId = error is null
                    ? ResolveUnit(row.SalesUnitExternalId, tenantUnits, out error)
                    : null;
                var supplierId = error is null
                    ? ResolveSupplier(row.SupplierExternalId, tenantSuppliers, out error)
                    : null;
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                try
                {
                    ItemQuantityConventions.Validate(new Item
                    {
                        BaseUnitId = baseUnitId,
                        PurchaseUnitId = purchaseUnitId,
                        SalesUnitId = salesUnitId,
                        PurchaseToBaseFactor = row.PurchaseToBaseFactor,
                        SalesToBaseFactor = row.SalesToBaseFactor,
                        QuantityPrecision = row.QuantityPrecision,
                        WholeUnitOnly = row.WholeUnitOnly
                    });
                }
                catch (ArgumentException exception)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", exception.Message));
                    continue;
                }

                if (baseUnitId is null && (purchaseUnitId.HasValue || salesUnitId.HasValue))
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "A base unit is required when a purchase or sales unit is configured."));
                    continue;
                }
                if (!purchaseUnitId.HasValue && row.PurchaseToBaseFactor != 1m)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "Purchase-to-base factor must be 1 when no purchase unit is configured."));
                    continue;
                }
                if (!salesUnitId.HasValue && row.SalesToBaseFactor != 1m)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "Sales-to-base factor must be 1 when no sales unit is configured."));
                    continue;
                }
                if (tenantUnits.Where(unit => unit.Id == baseUnitId || unit.Id == purchaseUnitId || unit.Id == salesUnitId)
                    .Any(unit => unit.IsWholeUnitOnly) && row.QuantityPrecision != 0)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "Items using a whole-unit-only unit must use zero quantity precision."));
                    continue;
                }

                var matches = existingItems.Where(item =>
                    Same(item.ExternalId, row.ExternalId) || Same(item.ItemCode, row.ItemCode)).ToList();
                if (matches.Count > 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "External ID or item code is ambiguous in the current tenant."));
                    continue;
                }

                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    if (existing.IsDeleted)
                    {
                        results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                            "External ID or item code already maps to a deleted item."));
                        continue;
                    }
                    var unchanged = Same(existing.ExternalId, row.ExternalId) &&
                        Same(existing.ItemCode, row.ItemCode) && existing.Description == row.Description &&
                        existing.Rate == row.Rate && existing.BaseUnitId == baseUnitId &&
                        existing.PurchaseUnitId == purchaseUnitId && existing.SalesUnitId == salesUnitId &&
                        existing.PurchaseToBaseFactor == row.PurchaseToBaseFactor &&
                        existing.SalesToBaseFactor == row.SalesToBaseFactor &&
                        existing.QuantityPrecision == row.QuantityPrecision &&
                        existing.WholeUnitOnly == row.WholeUnitOnly && existing.SupplierId == supplierId;
                    results.Add(new(row.RowNumber, row.ExternalId, unchanged ? "unchanged" : "rejected",
                        unchanged ? null : "External ID or item code already maps to different item data."));
                    continue;
                }

                pending.Add(new Item
                {
                    ExternalId = row.ExternalId,
                    ItemCode = row.ItemCode,
                    Description = row.Description,
                    Rate = row.Rate,
                    BaseUnitId = baseUnitId,
                    PurchaseUnitId = purchaseUnitId,
                    SalesUnitId = salesUnitId,
                    PurchaseToBaseFactor = row.PurchaseToBaseFactor,
                    SalesToBaseFactor = row.SalesToBaseFactor,
                    QuantityPrecision = row.QuantityPrecision,
                    WholeUnitOnly = row.WholeUnitOnly,
                    SupplierId = supplierId
                });
                results.Add(new(row.RowNumber, row.ExternalId, "created"));
            }

            if (results.Any(row => row.Status == "rejected"))
                return SummarizeItems(request.DryRun, results);
            if (!request.DryRun)
                foreach (var item in pending)
                    await items.AddAsync(item);
            return SummarizeItems(request.DryRun, results);
        }, cancellationToken);
    }

    public async Task<ImportCompaniesResult> ImportCompaniesAsync(
        ImportCompaniesRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(request.DryRun, async () =>
        {
            var rows = ParseCompanies(request.Csv);
            var results = new List<ImportRowResult>(rows.Count);
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingCompanies = await companies.Query().ToListAsync(cancellationToken);
            var pending = new List<Company>();

            foreach (var row in rows)
            {
                var error = Validate(row, seenExternalIds, seenCodes);
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                var matches = existingCompanies.Where(company =>
                    Same(company.ExternalId, row.ExternalId) || Same(company.Code, row.Code)).ToList();
                if (matches.Count > 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "External ID or code is ambiguous in the current tenant."));
                    continue;
                }

                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    var unchanged = Same(existing.ExternalId, row.ExternalId) && Same(existing.Code, row.Code) &&
                        existing.LegalName == row.LegalName && existing.TradingName == row.TradingName &&
                        existing.RegistrationNumber == row.RegistrationNumber &&
                        existing.TaxIdentifier == row.TaxIdentifier &&
                        existing.BaseCurrency == row.BaseCurrency && existing.CountryCode == row.CountryCode &&
                        existing.CurrencyScale == row.CurrencyScale && existing.IsActive == row.IsActive;
                    results.Add(new(row.RowNumber, row.ExternalId, unchanged ? "unchanged" : "rejected",
                        unchanged ? null : "External ID or code already maps to different company data."));
                    continue;
                }

                pending.Add(new Company
                {
                    ExternalId = row.ExternalId,
                    Code = row.Code,
                    LegalName = row.LegalName,
                    TradingName = row.TradingName,
                    RegistrationNumber = row.RegistrationNumber,
                    TaxIdentifier = row.TaxIdentifier,
                    BaseCurrency = row.BaseCurrency,
                    CountryCode = row.CountryCode,
                    CurrencyScale = row.CurrencyScale,
                    IsActive = row.IsActive
                });
                results.Add(new(row.RowNumber, row.ExternalId, "created"));
            }

            if (results.Any(row => row.Status == "rejected"))
                return SummarizeCompanies(request.DryRun, results);
            if (!request.DryRun)
                foreach (var company in pending)
                    await companies.AddAsync(company);
            return SummarizeCompanies(request.DryRun, results);
        }, cancellationToken);
    }

    public async Task<ImportBranchesResult> ImportBranchesAsync(
        ImportBranchesRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(request.DryRun, async () =>
        {
            var companyId = RequireCompanyScope(request.CompanyId);
            if (!request.DryRun)
                await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await EnsureCompanyScopeAsync(companyId, cancellationToken);
            var rows = ParseBranches(request.Csv);
            var results = new List<ImportRowResult>(rows.Count);
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingBranches = await branches.Query().ToListAsync(cancellationToken);
            var pending = new List<Branch>();

            foreach (var row in rows)
            {
                var error = Validate(row, seenExternalIds, seenCodes);
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                var matches = existingBranches.Where(branch =>
                    Same(branch.ExternalId, row.ExternalId) ||
                    (branch.CompanyId == companyId && Same(branch.Code, row.Code))).ToList();
                if (matches.Count > 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "External ID or code is ambiguous in the current tenant."));
                    continue;
                }

                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    var unchanged = existing.CompanyId == companyId && Same(existing.ExternalId, row.ExternalId) &&
                        Same(existing.Code, row.Code) && existing.Name == row.Name &&
                        existing.Address == row.Address && existing.TimeZoneId == row.TimeZoneId &&
                        existing.IsActive == row.IsActive;
                    results.Add(new(row.RowNumber, row.ExternalId, unchanged ? "unchanged" : "rejected",
                        unchanged ? null : "External ID or code already maps to different branch data."));
                    continue;
                }

                pending.Add(new Branch
                {
                    ExternalId = row.ExternalId,
                    CompanyId = companyId,
                    Code = row.Code,
                    Name = row.Name,
                    Address = row.Address,
                    TimeZoneId = row.TimeZoneId,
                    IsActive = row.IsActive
                });
                results.Add(new(row.RowNumber, row.ExternalId, "created"));
            }

            if (results.Any(row => row.Status == "rejected"))
                return SummarizeBranches(request.DryRun, results);
            if (!request.DryRun)
                foreach (var branch in pending)
                    await branches.AddAsync(branch);
            return SummarizeBranches(request.DryRun, results);
        }, cancellationToken);
    }

    public async Task<ImportLocationsResult> ImportLocationsAsync(
        ImportLocationsRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(request.DryRun, async () =>
        {
            var companyId = RequireCompanyScope(request.CompanyId);
            if (!request.DryRun)
                await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await EnsureCompanyScopeAsync(companyId, cancellationToken);
            var rows = ParseLocations(request.Csv);
            var results = new List<ImportRowResult>(rows.Count);
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingLocations = await context.Locations.IgnoreQueryFilters()
                .Where(location => location.TenantId == context.CurrentTenantId)
                .AsNoTracking().ToListAsync(cancellationToken);
            var tenantBranches = await branches.Query().ToListAsync(cancellationToken);
            var pending = new List<Location>();

            foreach (var row in rows)
            {
                var error = Validate(row, seenExternalIds);
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                var branchMatches = tenantBranches.Where(branch => branch.CompanyId == companyId &&
                    Same(branch.ExternalId, row.BranchExternalId)).ToList();
                if (branchMatches.Count != 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        branchMatches.Count == 0
                            ? $"Branch external ID '{row.BranchExternalId}' was not found in the company."
                            : "Branch external ID is ambiguous in the company."));
                    continue;
                }
                if (!branchMatches[0].IsActive)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "Locations must be assigned to an active branch."));
                    continue;
                }

                var matches = existingLocations.Where(location =>
                    Same(location.ExternalId, row.ExternalId)).ToList();
                if (matches.Count > 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "External ID is ambiguous in the current tenant."));
                    continue;
                }

                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    if (existing.IsDeleted)
                    {
                        results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                            "External ID already maps to a deleted location."));
                        continue;
                    }
                    var unchanged = existing.BranchId == branchMatches[0].Id &&
                        Same(existing.ExternalId, row.ExternalId) && existing.Name == row.Name &&
                        existing.Address == row.Address;
                    results.Add(new(row.RowNumber, row.ExternalId, unchanged ? "unchanged" : "rejected",
                        unchanged ? null : "External ID already maps to different location data."));
                    continue;
                }

                pending.Add(new Location
                {
                    ExternalId = row.ExternalId,
                    BranchId = branchMatches[0].Id,
                    Name = row.Name,
                    Address = row.Address
                });
                results.Add(new(row.RowNumber, row.ExternalId, "created"));
            }

            if (results.Any(row => row.Status == "rejected"))
                return SummarizeLocations(request.DryRun, results);
            if (!request.DryRun)
                foreach (var location in pending)
                    await locations.AddAsync(location);
            return SummarizeLocations(request.DryRun, results);
        }, cancellationToken);
    }

    public async Task<ImportSuppliersResult> ImportSuppliersAsync(
        ImportSuppliersRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(request.DryRun, async () =>
        {
            await EnsureCompanyScopeAsync(request.CompanyId, cancellationToken);
            var rows = ParseSuppliers(request.Csv);
            var results = new List<ImportRowResult>(rows.Count);
            var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingSuppliers = await context.Suppliers.IgnoreQueryFilters()
                .Where(supplier => supplier.TenantId == context.CurrentTenantId)
                .AsNoTracking().ToListAsync(cancellationToken);
            var pending = new List<Supplier>();

            foreach (var row in rows)
            {
                var error = Validate(row, seenExternalIds);
                if (error is not null)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                    continue;
                }

                var matches = existingSuppliers.Where(supplier =>
                    Same(supplier.ExternalId, row.ExternalId)).ToList();
                if (matches.Count > 1)
                {
                    results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                        "External ID is ambiguous in the current tenant."));
                    continue;
                }

                var existing = matches.SingleOrDefault();
                if (existing is not null)
                {
                    if (existing.IsDeleted)
                    {
                        results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                            "External ID already maps to a deleted supplier."));
                        continue;
                    }
                    var unchanged = Same(existing.ExternalId, row.ExternalId) && existing.Name == row.Name &&
                        existing.ContactPerson == row.ContactPerson && existing.Phone == row.Phone &&
                        existing.Email == row.Email && existing.Address == row.Address;
                    results.Add(new(row.RowNumber, row.ExternalId, unchanged ? "unchanged" : "rejected",
                        unchanged ? null : "External ID already maps to different supplier data."));
                    continue;
                }

                pending.Add(new Supplier
                {
                    ExternalId = row.ExternalId,
                    Name = row.Name,
                    ContactPerson = row.ContactPerson,
                    Phone = row.Phone,
                    Email = row.Email,
                    Address = row.Address
                });
                results.Add(new(row.RowNumber, row.ExternalId, "created"));
            }

            if (results.Any(row => row.Status == "rejected"))
                return SummarizeSuppliers(request.DryRun, results);
            if (!request.DryRun)
                foreach (var supplier in pending)
                    await suppliers.AddAsync(supplier);
            return SummarizeSuppliers(request.DryRun, results);
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(bool dryRun, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (dryRun)
            return await operation();

        T result = default!;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            // ponytail: one tenant-wide import lock; split by master only if onboarding throughput requires it.
            await unitOfWork.AcquireTenantOperationLockAsync("master-data-import", cancellationToken);
            result = await operation();
        }, cancellationToken);
        return result;
    }

    private async Task EnsureCompanyScopeAsync(int? companyId, CancellationToken cancellationToken)
    {
        if (!companyId.HasValue)
            return;
        if (companyId.Value <= 0 || !await companies.Query()
                .AnyAsync(company => company.Id == companyId.Value && company.IsActive, cancellationToken))
            throw new ArgumentException("An active company scope is required.", nameof(companyId));
    }

    private static int RequireCompanyScope(int? companyId) => companyId is > 0
        ? companyId.Value
        : throw new ArgumentException("CompanyId is required for this import.", nameof(companyId));

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? Validate(Row row, HashSet<string> externalIds, HashSet<string> codes)
    {
        if (row.CsvError is not null) return row.CsvError;
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 5 fields.";
        var error = ValidateExternalId(row.ExternalId, externalIds);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(row.Code) || row.Code.Length > 32)
            return "Code is required and must be at most 32 characters.";
        if (!codes.Add(row.Code)) return "Code is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 100)
            return "Name is required and must be at most 100 characters.";
        if (!row.DecimalPlacesParsed)
            return "Decimal places must be an integer between 0 and 6.";
        if (row.DecimalPlaces is < 0 or > 6)
            return "Decimal places must be between 0 and 6.";
        if (!row.IsWholeUnitOnlyParsed) return "Whole-unit-only must be true or false.";
        if (row.IsWholeUnitOnly && row.DecimalPlaces != 0)
            return "Whole-unit-only units must use zero decimal places.";
        return null;
    }

    private static List<Row> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new ArgumentException("CSV content is required.", nameof(csv));
        var records = Records(csv);
        if (records[0].Error is not null
            || records[0].Fields.Count != 5
            || !records[0].Fields.Select(value => value.Trim()).SequenceEqual(
                ["external_id", "code", "name", "decimal_places", "whole_unit_only"],
                StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("CSV header must be external_id,code,name,decimal_places,whole_unit_only.", nameof(csv));
        }

        var rows = new List<Row>();
        foreach (var record in records.Skip(1))
        {
            if (record.Error is not null)
            {
                rows.Add(new(record.RowNumber, string.Empty, string.Empty, string.Empty, 0, false,
                    CsvError: record.Error));
                continue;
            }

            if (record.Fields.Count != 5)
            {
                rows.Add(new(record.RowNumber, string.Empty, string.Empty, string.Empty, 0, false,
                    HasCorrectFieldCount: false));
                continue;
            }

            var decimalPlacesParsed = int.TryParse(
                record.Fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalPlaces);
            var wholeUnitOnlyParsed = bool.TryParse(record.Fields[4].Trim(), out var wholeUnitOnly);
            rows.Add(new(record.RowNumber, record.Fields[0].Trim(), record.Fields[1].Trim(),
                record.Fields[2].Trim(), decimalPlaces, wholeUnitOnly,
                DecimalPlacesParsed: decimalPlacesParsed,
                IsWholeUnitOnlyParsed: wholeUnitOnlyParsed));
        }

        return rows;
    }

    private static List<CsvRecord> ParseCsvRecords(string csv)
    {
        // Quoting state keeps commas and line breaks inside quoted fields from becoming record delimiters.
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var state = CsvFieldState.Start;
        string? error = null;
        var lineNumber = 1;
        var recordLineNumber = 1;
        var hasRecordContent = false;

        void CompleteRecord()
        {
            if (hasRecordContent || fields.Count > 0 || field.Length > 0 || error is not null)
            {
                fields.Add(field.ToString());
                records.Add(new(recordLineNumber, fields.ToArray(), error));
            }

            fields.Clear();
            field.Clear();
            state = CsvFieldState.Start;
            error = null;
            hasRecordContent = false;
        }

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (state == CsvFieldState.Quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        state = CsvFieldState.AfterQuote;
                    }
                }
                else if (character == '\r')
                {
                    field.Append(character);
                    if (index + 1 < csv.Length && csv[index + 1] == '\n')
                    {
                        field.Append('\n');
                        index++;
                    }
                    lineNumber++;
                }
                else
                {
                    field.Append(character);
                    if (character == '\n') lineNumber++;
                }

                continue;
            }

            if (character is '\r' or '\n')
            {
                CompleteRecord();
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                lineNumber++;
                recordLineNumber = lineNumber;
                continue;
            }

            if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                state = CsvFieldState.Start;
                hasRecordContent = true;
                continue;
            }

            if (state == CsvFieldState.Start && character == '"')
            {
                state = CsvFieldState.Quoted;
                hasRecordContent = true;
                continue;
            }

            if (state == CsvFieldState.AfterQuote)
            {
                error ??= "Only a comma or line break may follow a closing quote.";
            }
            else if (character == '"')
            {
                error ??= "A quote may only begin a quoted field.";
            }

            field.Append(character);
            state = CsvFieldState.Unquoted;
            hasRecordContent = true;
        }

        if (state == CsvFieldState.Quoted)
        {
            error ??= "A quoted CSV field is not closed.";
        }

        CompleteRecord();
        return records;
    }

    private static List<CsvRecord> Records(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            throw new ArgumentException("CSV content is required.", nameof(csv));
        var records = ParseCsvRecords(csv.TrimStart('\uFEFF'));
        if (records.Count < 2)
            throw new ArgumentException("CSV must include at least one data row.", nameof(csv));
        return records;
    }

    private static ImportUnitsResult SummarizeUnits(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static string? Validate(
        ItemRow row, HashSet<string> externalIds, HashSet<string> codes)
    {
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 11 or 12 fields.";
        var error = ValidateExternalId(row.ExternalId, externalIds);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(row.ItemCode) || row.ItemCode.Length > 50)
            return "Item code is required and must be at most 50 characters.";
        if (!codes.Add(row.ItemCode)) return "Item code is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 500)
            return "Description is required and must be at most 500 characters.";
        if (!row.RateParsed || row.Rate <= 0) return "Rate must be a positive decimal.";
        if (!row.PurchaseToBaseFactorParsed || row.PurchaseToBaseFactor <= 0)
            return "Purchase-to-base factor must be a positive decimal.";
        if (!row.SalesToBaseFactorParsed || row.SalesToBaseFactor <= 0)
            return "Sales-to-base factor must be a positive decimal.";
        if (!row.QuantityPrecisionParsed || row.QuantityPrecision is < 0 or > 6)
            return "Quantity precision must be between 0 and 6.";
        if (!row.WholeUnitOnlyParsed) return "Whole-unit-only must be true or false.";
        if (row.WholeUnitOnly && row.QuantityPrecision != 0)
            return "Whole-unit-only items must use zero quantity precision.";
        return null;
    }

    private static string? Validate(
        CompanyRow row, HashSet<string> externalIds, HashSet<string> codes)
    {
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 10 fields.";
        var error = ValidateExternalId(row.ExternalId, externalIds);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(row.Code) || row.Code.Length > 32)
            return "Code is required and must be at most 32 characters.";
        if (!codes.Add(row.Code)) return "Code is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.LegalName) || row.LegalName.Length > 200)
            return "Legal name is required and must be at most 200 characters.";
        if (string.IsNullOrWhiteSpace(row.BaseCurrency) || row.BaseCurrency.Length != 3 ||
            row.BaseCurrency.Any(character => character is < 'A' or > 'Z'))
            return "Base currency must be a three-letter ISO-style code.";
        if (!row.CurrencyScaleParsed || row.CurrencyScale is < 0 or > 4)
            return "Currency scale must be between 0 and 4.";
        if ((row.TradingName is not null && row.TradingName.Length > 200) ||
            (row.RegistrationNumber is not null && row.RegistrationNumber.Length > 100) ||
            (row.TaxIdentifier is not null && row.TaxIdentifier.Length > 100) ||
            (row.CountryCode is not null && row.CountryCode.Length > 2))
            return "Company optional fields exceed their maximum length.";
        if (!row.IsActiveParsed) return "Is active must be true or false.";
        return null;
    }

    private static string? Validate(
        BranchRow row, HashSet<string> externalIds, HashSet<string> codes)
    {
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 6 fields.";
        var error = ValidateExternalId(row.ExternalId, externalIds);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(row.Code) || row.Code.Length > 32)
            return "Code is required and must be at most 32 characters.";
        if (!codes.Add(row.Code)) return "Code is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 200)
            return "Name is required and must be at most 200 characters.";
        if (row.Address is not null && row.Address.Length > 500)
            return "Address must be at most 500 characters.";
        if (string.IsNullOrWhiteSpace(row.TimeZoneId) || row.TimeZoneId.Length > 100)
            return "Time zone is required and must be at most 100 characters.";
        if (!row.IsActiveParsed) return "Is active must be true or false.";
        return null;
    }

    private static string? Validate(LocationRow row, HashSet<string> externalIds)
    {
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 4 fields.";
        var error = ValidateExternalId(row.ExternalId, externalIds);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(row.BranchExternalId) || row.BranchExternalId.Length > 128)
            return "Branch external ID is required and must be at most 128 characters.";
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 200)
            return "Name is required and must be at most 200 characters.";
        if (row.Address is not null && row.Address.Length > 500)
            return "Address must be at most 500 characters.";
        return null;
    }

    private static string? Validate(SupplierRow row, HashSet<string> externalIds)
    {
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 6 fields.";
        var error = ValidateExternalId(row.ExternalId, externalIds);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 200)
            return "Supplier name is required and must be at most 200 characters.";
        if ((row.ContactPerson is not null && row.ContactPerson.Length > 200) ||
            (row.Phone is not null && row.Phone.Length > 50) ||
            (row.Email is not null && row.Email.Length > 200) ||
            (row.Address is not null && row.Address.Length > 500))
            return "Supplier fields exceed their maximum length.";
        if (row.Email is not null && (!row.Email.Contains('@') || row.Email.StartsWith('@') || row.Email.EndsWith('@')))
            return "Email must contain a valid address.";
        return null;
    }

    private static string? ValidateExternalId(string externalId, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 128)
            return "External ID is required and must be at most 128 characters.";
        return seen.Add(externalId) ? null : "External ID is duplicated in the import.";
    }

    private static int? ResolveUnit(
        string? externalId, IReadOnlyList<UnitOfMeasure> tenantUnits, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var matches = tenantUnits.Where(unit => Same(unit.ExternalId, externalId)).ToList();
        if (matches.Count == 0)
        {
            error = $"Unit external ID '{externalId}' was not found in the current tenant.";
            return null;
        }
        if (matches.Count > 1)
        {
            error = $"Unit external ID '{externalId}' is ambiguous in the current tenant.";
            return null;
        }
        return matches[0].Id;
    }

    private static int? ResolveSupplier(
        string? externalId, IReadOnlyList<Supplier> tenantSuppliers, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var matches = tenantSuppliers.Where(supplier => Same(supplier.ExternalId, externalId)).ToList();
        if (matches.Count == 0)
        {
            error = $"Supplier external ID '{externalId}' was not found in the current tenant.";
            return null;
        }
        if (matches.Count > 1)
        {
            error = $"Supplier external ID '{externalId}' is ambiguous in the current tenant.";
            return null;
        }
        return matches[0].Id;
    }

    private static List<ItemRow> ParseItems(string? csv)
    {
        var records = Records(csv);
        var legacyHeader = new[]
        {
            "external_id", "item_code", "description", "rate", "base_unit_external_id",
            "purchase_unit_external_id", "sales_unit_external_id", "purchase_to_base_factor",
            "sales_to_base_factor", "quantity_precision", "whole_unit_only"
        };
        var supplierHeader = legacyHeader.Append("supplier_external_id").ToArray();
        var header = records[0].Fields;
        var hasSupplier = HeaderEquals(header, supplierHeader);
        if (!hasSupplier && !HeaderEquals(header, legacyHeader))
            throw new ArgumentException(
                "CSV header must be external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only[,supplier_external_id].",
                nameof(csv));

        var rows = new List<ItemRow>();
        foreach (var record in records.Skip(1))
        {
            var values = record.Fields;
            if (record.Error is not null || values.Count != (hasSupplier ? 12 : 11))
            {
                rows.Add(new(record.RowNumber));
                continue;
            }
            var rateParsed = decimal.TryParse(values[3].Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var rate);
            var purchaseParsed = decimal.TryParse(values[7].Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var purchaseFactor);
            var salesParsed = decimal.TryParse(values[8].Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var salesFactor);
            var precisionParsed = int.TryParse(values[9].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var precision);
            var wholeParsed = bool.TryParse(values[10].Trim(), out var wholeUnitOnly);
            rows.Add(new(record.RowNumber, values[0].Trim(), values[1].Trim(), values[2].Trim(), rate, rateParsed,
                NullIfEmpty(values[4]), NullIfEmpty(values[5]), NullIfEmpty(values[6]), purchaseFactor,
                purchaseParsed, salesFactor, salesParsed, precision, precisionParsed, wholeUnitOnly,
                wholeParsed, hasSupplier ? NullIfEmpty(values[11]) : null, true));
        }
        return rows;
    }

    private static List<CompanyRow> ParseCompanies(string? csv)
    {
        var records = Records(csv);
        var header = new[] { "external_id", "code", "legal_name", "trading_name", "registration_number",
            "tax_identifier", "base_currency", "country_code", "currency_scale", "is_active" };
        if (!HeaderEquals(records[0].Fields, header))
            throw new ArgumentException("CSV header must be external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active.", nameof(csv));
        var rows = new List<CompanyRow>();
        foreach (var record in records.Skip(1))
        {
            var values = record.Fields;
            if (record.Error is not null || values.Count != header.Length) { rows.Add(new(record.RowNumber)); continue; }
            var scaleParsed = int.TryParse(values[8].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale);
            var activeParsed = bool.TryParse(values[9].Trim(), out var active);
            rows.Add(new(record.RowNumber, values[0].Trim(), values[1].Trim(), values[2].Trim(), NullIfEmpty(values[3]),
                NullIfEmpty(values[4]), NullIfEmpty(values[5]), values[6].Trim().ToUpperInvariant(),
                NullIfEmpty(values[7])?.ToUpperInvariant(), scale, scaleParsed, active, activeParsed, true));
        }
        return rows;
    }

    private static List<BranchRow> ParseBranches(string? csv)
    {
        var records = Records(csv);
        var header = new[] { "external_id", "code", "name", "address", "time_zone_id", "is_active" };
        if (!HeaderEquals(records[0].Fields, header))
            throw new ArgumentException("CSV header must be external_id,code,name,address,time_zone_id,is_active.", nameof(csv));
        var rows = new List<BranchRow>();
        foreach (var record in records.Skip(1))
        {
            var values = record.Fields;
            if (record.Error is not null || values.Count != header.Length) { rows.Add(new(record.RowNumber)); continue; }
            var activeParsed = bool.TryParse(values[5].Trim(), out var active);
            rows.Add(new(record.RowNumber, values[0].Trim(), values[1].Trim(), values[2].Trim(), NullIfEmpty(values[3]),
                values[4].Trim(), active, activeParsed, true));
        }
        return rows;
    }

    private static List<LocationRow> ParseLocations(string? csv)
    {
        var records = Records(csv);
        var header = new[] { "external_id", "branch_external_id", "name", "address" };
        if (!HeaderEquals(records[0].Fields, header))
            throw new ArgumentException("CSV header must be external_id,branch_external_id,name,address.", nameof(csv));
        var rows = new List<LocationRow>();
        foreach (var record in records.Skip(1))
        {
            var values = record.Fields;
            rows.Add(record.Error is not null || values.Count != header.Length
                ? new(record.RowNumber)
                : new(record.RowNumber, values[0].Trim(), values[1].Trim(), values[2].Trim(), NullIfEmpty(values[3]), true));
        }
        return rows;
    }

    private static List<SupplierRow> ParseSuppliers(string? csv)
    {
        var records = Records(csv);
        var header = new[] { "external_id", "name", "contact_person", "phone", "email", "address" };
        if (!HeaderEquals(records[0].Fields, header))
            throw new ArgumentException("CSV header must be external_id,name,contact_person,phone,email,address.", nameof(csv));
        var rows = new List<SupplierRow>();
        foreach (var record in records.Skip(1))
        {
            var values = record.Fields;
            rows.Add(record.Error is not null || values.Count != header.Length
                ? new(record.RowNumber)
                : new(record.RowNumber, values[0].Trim(), values[1].Trim(), NullIfEmpty(values[2]),
                    NullIfEmpty(values[3]), NullIfEmpty(values[4]), NullIfEmpty(values[5]), true));
        }
        return rows;
    }

    private static bool HeaderEquals(IReadOnlyList<string>? actual, IReadOnlyList<string> expected) =>
        actual is not null && actual.Count == expected.Count &&
        actual.Select(value => value.Trim()).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private enum CsvFieldState
    {
        Start,
        Unquoted,
        Quoted,
        AfterQuote
    }

    private sealed record CsvRecord(int RowNumber, IReadOnlyList<string> Fields, string? Error);

    private sealed record Row(
        int RowNumber,
        string ExternalId,
        string Code,
        string Name,
        int DecimalPlaces,
        bool IsWholeUnitOnly,
        bool HasCorrectFieldCount = true,
        bool DecimalPlacesParsed = true,
        bool IsWholeUnitOnlyParsed = true,
        string? CsvError = null);

    private static ImportItemsResult SummarizeItems(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static ImportCompaniesResult SummarizeCompanies(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static ImportBranchesResult SummarizeBranches(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static ImportLocationsResult SummarizeLocations(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static ImportSuppliersResult SummarizeSuppliers(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private sealed record ItemRow(
        int RowNumber,
        string ExternalId = "",
        string ItemCode = "",
        string Description = "",
        decimal Rate = 0,
        bool RateParsed = false,
        string? BaseUnitExternalId = null,
        string? PurchaseUnitExternalId = null,
        string? SalesUnitExternalId = null,
        decimal PurchaseToBaseFactor = 0,
        bool PurchaseToBaseFactorParsed = false,
        decimal SalesToBaseFactor = 0,
        bool SalesToBaseFactorParsed = false,
        int QuantityPrecision = 0,
        bool QuantityPrecisionParsed = false,
        bool WholeUnitOnly = false,
        bool WholeUnitOnlyParsed = false,
        string? SupplierExternalId = null,
        bool HasCorrectFieldCount = false);

    private sealed record CompanyRow(
        int RowNumber,
        string ExternalId = "",
        string Code = "",
        string LegalName = "",
        string? TradingName = null,
        string? RegistrationNumber = null,
        string? TaxIdentifier = null,
        string BaseCurrency = "",
        string? CountryCode = null,
        int CurrencyScale = 0,
        bool CurrencyScaleParsed = false,
        bool IsActive = false,
        bool IsActiveParsed = false,
        bool HasCorrectFieldCount = false);

    private sealed record BranchRow(
        int RowNumber,
        string ExternalId = "",
        string Code = "",
        string Name = "",
        string? Address = null,
        string TimeZoneId = "",
        bool IsActive = false,
        bool IsActiveParsed = false,
        bool HasCorrectFieldCount = false);

    private sealed record LocationRow(
        int RowNumber,
        string ExternalId = "",
        string BranchExternalId = "",
        string Name = "",
        string? Address = null,
        bool HasCorrectFieldCount = false);

    private sealed record SupplierRow(
        int RowNumber,
        string ExternalId = "",
        string Name = "",
        string? ContactPerson = null,
        string? Phone = null,
        string? Email = null,
        string? Address = null,
        bool HasCorrectFieldCount = false);
}
