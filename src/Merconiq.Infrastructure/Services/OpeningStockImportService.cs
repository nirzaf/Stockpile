using System.Globalization;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Read-only validation for owner-approved opening stock cutovers.</summary>
public sealed class OpeningStockImportService(
    InventoryDbContext context,
    ITenantContext tenantContext) : IOpeningStockImportService
{
    private static readonly string[] ExpectedHeader =
        ["external_reference", "item_external_id", "location_id", "quantity", "unit_cost"];

    public async Task<OpeningStockPreviewResult> PreviewAsync(
        OpeningStockPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for opening stock validation.");

        var rows = Parse(request.Csv);
        var items = await context.Items
            .AsNoTracking()
            .Where(item => item.ExternalId != null)
            .ToListAsync(cancellationToken);
        var locations = await context.Locations
            .AsNoTracking()
            .ToDictionaryAsync(location => location.Id, cancellationToken);
        var itemsByExternalId = items
            // ExternalId uniqueness is case-sensitive in the tenant-keyed database index.
            // Keep lookup semantics aligned so permitted case variants remain distinct IDs.
            .GroupBy(item => item.ExternalId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<OpeningStockRowResult>(rows.Count);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = Validate(row, seenReferences, itemsByExternalId, locations);
            results.Add(error is null
                ? new(row.RowNumber, row.ExternalReference, "valid")
                : new(row.RowNumber, row.ExternalReference, "rejected", error));
        }

        return new(
            results.Count(row => row.Status == "valid"),
            results.Count(row => row.Status == "rejected"),
            results);
    }

    private static string? Validate(
        OpeningRow row,
        HashSet<string> seenReferences,
        IReadOnlyDictionary<string, Item> items,
        IReadOnlyDictionary<int, Location> locations)
    {
        if (!row.HasCorrectFieldCount)
            return "CSV row must contain exactly 5 fields.";
        if (string.IsNullOrWhiteSpace(row.ExternalReference) || row.ExternalReference.Length > 128)
            return "External reference is required and must be at most 128 characters.";
        if (!seenReferences.Add(row.ExternalReference))
            return "External reference is duplicated in the import.";
        if (string.IsNullOrWhiteSpace(row.ItemExternalId) || row.ItemExternalId.Length > 128)
            return "Item external ID is required and must be at most 128 characters.";
        if (!items.TryGetValue(row.ItemExternalId, out var item))
            return $"Item external ID '{row.ItemExternalId}' was not found in the current tenant.";
        if (!item.IsActive)
            return $"Item external ID '{row.ItemExternalId}' is inactive.";
        if (!int.TryParse(row.LocationId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var locationId) || locationId <= 0)
            return "Location ID must be a positive integer.";
        if (!locations.ContainsKey(locationId))
            return $"Location ID '{row.LocationId}' was not found in the current tenant.";
        if (!int.TryParse(row.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            return "Quantity must be a positive integer.";
        if (!decimal.TryParse(row.UnitCost, NumberStyles.Number, CultureInfo.InvariantCulture, out var unitCost) || unitCost < 0)
            return "Unit cost is required and must be a non-negative decimal.";

        return null;
    }

    private static List<OpeningRow> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            throw new ArgumentException("CSV content is required.", nameof(csv));

        var lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var header = ParseCsvLine(lines[0]);
        if (lines.Length < 2 || header is null ||
            !header.Select(value => value.Trim()).SequenceEqual(ExpectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "CSV header must be external_reference,item_external_id,location_id,quantity,unit_cost.",
                nameof(csv));
        }

        var rows = new List<OpeningRow>();
        for (var index = 1; index < lines.Length; index++)
        {
            var values = ParseCsvLine(lines[index]);
            if (values is null || values.Count != ExpectedHeader.Length)
            {
                rows.Add(new(index + 1));
                continue;
            }

            rows.Add(new(
                index + 1,
                values[0].Trim(),
                values[1].Trim(),
                values[2].Trim(),
                values[3].Trim(),
                values[4].Trim(),
                true));
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

        if (quoted)
            return null;
        values.Add(value.ToString());
        return values;
    }

    private sealed record OpeningRow(
        int RowNumber,
        string ExternalReference = "",
        string ItemExternalId = "",
        string LocationId = "",
        string Quantity = "",
        string UnitCost = "",
        bool HasCorrectFieldCount = false);
}
