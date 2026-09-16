using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Validates and imports approved master data without partial mutation.</summary>
public sealed class MasterDataImportService(
    IRepository<UnitOfMeasure> units,
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

    private sealed record Row(int RowNumber, string ExternalId, string Code, string Name, int DecimalPlaces, bool IsWholeUnitOnly);
}
