using System.Globalization;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Validates and imports approved master data without partial mutation.</summary>
public sealed class MasterDataImportService(
    IRepository<UnitOfMeasure> units,
    IRepository<Item> items,
    IUnitOfWork unitOfWork) : IMasterDataImportService
{
    public async Task<ImportUnitsResult> ImportUnitsAsync(
        ImportUnitsRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = Parse(request.Csv);
        var results = new List<ImportRowResult>(rows.Count);
        var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var existing = await units.Query().SingleOrDefaultAsync(
                unit => unit.ExternalId == row.ExternalId || unit.Code == row.Code, cancellationToken);
            if (existing is not null)
            {
                var matches = existing.ExternalId.Equals(row.ExternalId, StringComparison.OrdinalIgnoreCase)
                    && existing.Code.Equals(row.Code, StringComparison.OrdinalIgnoreCase)
                    && existing.Name == row.Name
                    && existing.DecimalPlaces == row.DecimalPlaces
                    && existing.IsWholeUnitOnly == row.IsWholeUnitOnly;
                results.Add(new(row.RowNumber, row.ExternalId, matches ? "unchanged" : "rejected",
                    matches ? null : "External ID or code already maps to different unit data."));
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
            return Summarize(request.DryRun, results);
        if (!request.DryRun)
        {
            foreach (var unit in pending)
                await units.AddAsync(unit);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        return Summarize(request.DryRun, results);
    }

    public async Task<ImportItemsResult> ImportItemsAsync(
        ImportItemsRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = ParseItems(request.Csv);
        var results = new List<ImportRowResult>(rows.Count);
        var seenExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Item>();
        var existingItems = await items.Query().ToListAsync(cancellationToken);
        var tenantUnits = await units.Query().ToListAsync(cancellationToken);

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
            if (error is not null)
            {
                results.Add(new(row.RowNumber, row.ExternalId, "rejected", error));
                continue;
            }

            var existingMatches = existingItems.Where(item =>
                string.Equals(item.ExternalId, row.ExternalId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.ItemCode, row.ItemCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (existingMatches.Count > 1)
            {
                results.Add(new(row.RowNumber, row.ExternalId, "rejected",
                    "External ID or item code is ambiguous in the current tenant."));
                continue;
            }
            var existing = existingMatches.SingleOrDefault();
            if (existing is not null)
            {
                var matches = string.Equals(existing.ExternalId, row.ExternalId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.ItemCode, row.ItemCode, StringComparison.OrdinalIgnoreCase)
                    && existing.Description == row.Description
                    && existing.Rate == row.Rate
                    && existing.BaseUnitId == baseUnitId
                    && existing.PurchaseUnitId == purchaseUnitId
                    && existing.SalesUnitId == salesUnitId
                    && existing.PurchaseToBaseFactor == row.PurchaseToBaseFactor
                    && existing.SalesToBaseFactor == row.SalesToBaseFactor
                    && existing.QuantityPrecision == row.QuantityPrecision
                    && existing.WholeUnitOnly == row.WholeUnitOnly;
                results.Add(new(row.RowNumber, row.ExternalId, matches ? "unchanged" : "rejected",
                    matches ? null : "External ID or item code already maps to different item data."));
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
                WholeUnitOnly = row.WholeUnitOnly
            });
            results.Add(new(row.RowNumber, row.ExternalId, "created"));
        }

        if (results.Any(row => row.Status == "rejected"))
            return SummarizeItems(request.DryRun, results);
        if (!request.DryRun)
        {
            foreach (var item in pending)
                await items.AddAsync(item);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        return SummarizeItems(request.DryRun, results);
    }

    private static string? Validate(Row row, HashSet<string> externalIds, HashSet<string> codes)
    {
        if (string.IsNullOrWhiteSpace(row.ExternalId) || row.ExternalId.Length > 128) return "External ID is required and must be at most 128 characters.";
        if (!externalIds.Add(row.ExternalId)) return "External ID is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.Code) || row.Code.Length > 32) return "Code is required and must be at most 32 characters.";
        if (!codes.Add(row.Code)) return "Code is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 100) return "Name is required and must be at most 100 characters.";
        if (row.DecimalPlaces is < 0 or > 6) return "Decimal places must be between 0 and 6.";
        return null;
    }

    private static List<Row> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new ArgumentException("CSV content is required.", nameof(csv));
        var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !lines[0].Trim().Equals("external_id,code,name,decimal_places,whole_unit_only", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CSV header must be external_id,code,name,decimal_places,whole_unit_only.", nameof(csv));
        var rows = new List<Row>();
        for (var index = 1; index < lines.Length; index++)
        {
            var values = lines[index].Split(',');
            if (values.Length != 5) { rows.Add(new(index + 1, string.Empty, string.Empty, string.Empty, -1, false)); continue; }
            _ = int.TryParse(values[3].Trim(), out var decimalPlaces);
            _ = bool.TryParse(values[4].Trim(), out var wholeUnitOnly);
            rows.Add(new(index + 1, values[0].Trim(), values[1].Trim(), values[2].Trim(), decimalPlaces, wholeUnitOnly));
        }
        return rows;
    }

    private static ImportUnitsResult Summarize(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static ImportItemsResult SummarizeItems(bool dryRun, IReadOnlyList<ImportRowResult> rows) =>
        new(dryRun, rows.Count(row => row.Status == "created"), rows.Count(row => row.Status == "unchanged"),
            rows.Count(row => row.Status == "rejected"), rows);

    private static string? Validate(
        ItemRow row, HashSet<string> externalIds, HashSet<string> codes)
    {
        if (!row.HasCorrectFieldCount) return "CSV row must contain exactly 11 fields.";
        if (string.IsNullOrWhiteSpace(row.ExternalId) || row.ExternalId.Length > 128)
            return "External ID is required and must be at most 128 characters.";
        if (!externalIds.Add(row.ExternalId)) return "External ID is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.ItemCode) || row.ItemCode.Length > 50)
            return "Item code is required and must be at most 50 characters.";
        if (!codes.Add(row.ItemCode)) return "Item code is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 500)
            return "Description is required and must be at most 500 characters.";
        if (!row.RateParsed || row.Rate < 0) return "Rate must be a non-negative decimal.";
        if (!row.PurchaseToBaseFactorParsed || row.PurchaseToBaseFactor <= 0)
            return "Purchase-to-base factor must be a positive decimal.";
        if (!row.SalesToBaseFactorParsed || row.SalesToBaseFactor <= 0)
            return "Sales-to-base factor must be a positive decimal.";
        if (!row.QuantityPrecisionParsed || row.QuantityPrecision is < 0 or > 6)
            return "Quantity precision must be between 0 and 6.";
        if (!row.WholeUnitOnlyParsed) return "Whole-unit-only must be true or false.";
        return null;
    }

    private static int? ResolveUnit(
        string? externalId, IReadOnlyList<UnitOfMeasure> tenantUnits, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var matches = tenantUnits.Where(unit =>
            string.Equals(unit.ExternalId, externalId, StringComparison.OrdinalIgnoreCase)).ToList();
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

    private static List<ItemRow> ParseItems(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new ArgumentException("CSV content is required.", nameof(csv));
        var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var expectedHeader = new[]
        {
            "external_id", "item_code", "description", "rate", "base_unit_external_id",
            "purchase_unit_external_id", "sales_unit_external_id", "purchase_to_base_factor",
            "sales_to_base_factor", "quantity_precision", "whole_unit_only"
        };
        var header = ParseCsvLine(lines[0]);
        if (header is null || header.Count != expectedHeader.Length ||
            !header.Select(value => value.Trim()).SequenceEqual(expectedHeader, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                "CSV header must be external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only.",
                nameof(csv));

        var rows = new List<ItemRow>();
        for (var index = 1; index < lines.Length; index++)
        {
            var values = ParseCsvLine(lines[index]);
            if (values is null || values.Count != expectedHeader.Length)
            {
                rows.Add(new(index + 1));
                continue;
            }

            var rateParsed = decimal.TryParse(values[3].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate);
            var purchaseFactorParsed = decimal.TryParse(values[7].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var purchaseFactor);
            var salesFactorParsed = decimal.TryParse(values[8].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var salesFactor);
            var precisionParsed = int.TryParse(values[9].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var precision);
            var wholeUnitOnlyParsed = bool.TryParse(values[10].Trim(), out var wholeUnitOnly);
            rows.Add(new(index + 1, values[0].Trim(), values[1].Trim(), values[2].Trim(), rate, rateParsed,
                NullIfEmpty(values[4]), NullIfEmpty(values[5]), NullIfEmpty(values[6]), purchaseFactor,
                purchaseFactorParsed, salesFactor, salesFactorParsed, precision, precisionParsed,
                wholeUnitOnly, wholeUnitOnlyParsed, true));
        }
        return rows;
    }

    private static List<string>? ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }
        if (quoted) return null;
        values.Add(value.ToString());
        return values;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Row(int RowNumber, string ExternalId, string Code, string Name, int DecimalPlaces, bool IsWholeUnitOnly);

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
        bool HasCorrectFieldCount = false);
}
