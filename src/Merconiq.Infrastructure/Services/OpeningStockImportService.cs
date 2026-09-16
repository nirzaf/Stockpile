using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Infrastructure.Services;

/// <summary>Validates and replays owner-approved opening stock without inventing costs.</summary>
public sealed class OpeningStockImportService(
    InventoryDbContext context,
    ITenantContext tenantContext,
    IUnitOfWork unitOfWork,
    IHttpContextAccessor httpContextAccessor) : IOpeningStockImportService
{
    private static readonly string[] ExpectedHeader =
        ["external_reference", "item_external_id", "location_id", "quantity", "unit_cost"];

    public async Task<OpeningStockPreviewResult> PreviewAsync(
        OpeningStockPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = await ValidateCsvAsync(request.Csv, cancellationToken);
        return Summarize(validation);
    }

    public async Task<OpeningStockReplayResult> ReplayAsync(
        OpeningStockReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateReference(request.ImportReference, nameof(request.ImportReference));
        ValidateReference(request.ApprovalReference, nameof(request.ApprovalReference));
        if (!tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for opening stock replay.");

        var requestHash = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        OpeningStockReplayResult? replayResult = null;

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                var existing = await context.OpeningStockImports
                    .AsNoTracking()
                    .Include(import => import.Lines)
                    .SingleOrDefaultAsync(
                        import => import.ImportReference == request.ImportReference,
                        cancellationToken);
                if (existing is not null)
                {
                    EnsureSameRequest(existing, requestHash);
                    replayResult = AlreadyApplied(existing);
                    return;
                }

                var validation = await ValidateCsvAsync(request.Csv, cancellationToken);
                if (validation.Rejected > 0)
                {
                    replayResult = new(
                        request.ImportReference,
                        0,
                        false,
                        validation.Valid,
                        validation.Rejected,
                        validation.Results);
                    return;
                }

                var import = new OpeningStockImport
                {
                    ImportReference = request.ImportReference,
                    ApprovalReference = request.ApprovalReference,
                    RequestHash = requestHash,
                    ApprovedBy = httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System",
                    ApprovedAt = DateTime.UtcNow,
                    LineCount = validation.Valid
                };

                foreach (var group in validation.ValidRows.GroupBy(row => new { row.ItemId, row.LocationId }))
                {
                    var quantity = group.Sum(row => row.Quantity);
                    var stock = await context.StockInHand.SingleOrDefaultAsync(
                        candidate => candidate.ItemId == group.Key.ItemId &&
                                    candidate.LocationId == group.Key.LocationId &&
                                    candidate.BatchNumber == null &&
                                    candidate.ExpiryDate == null,
                        cancellationToken);
                    if (stock is null)
                    {
                        context.StockInHand.Add(new StockInHand
                        {
                            ItemId = group.Key.ItemId,
                            LocationId = group.Key.LocationId,
                            Quantity = quantity
                        });
                    }
                    else
                    {
                        stock.Quantity = checked(stock.Quantity + quantity);
                    }
                }

                foreach (var row in validation.ValidRows)
                {
                    import.Lines.Add(new OpeningStockImportLine
                    {
                        RowNumber = row.RowNumber,
                        ExternalReference = row.ExternalReference,
                        ItemId = row.ItemId,
                        LocationId = row.LocationId,
                        Quantity = row.Quantity,
                        UnitCost = row.UnitCost
                    });
                }

                context.OpeningStockImports.Add(import);
                replayResult = new(
                    request.ImportReference,
                    validation.Valid,
                    false,
                    validation.Valid,
                    0,
                    validation.Results);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            context.ChangeTracker.Clear();
            var existing = await context.OpeningStockImports
                .AsNoTracking()
                .Include(import => import.Lines)
                .SingleOrDefaultAsync(
                    import => import.ImportReference == request.ImportReference,
                    cancellationToken);
            if (existing is null)
                throw;

            EnsureSameRequest(existing, requestHash);
            replayResult = AlreadyApplied(existing);
        }

        return replayResult
            ?? throw new InvalidOperationException("Opening stock replay did not produce a result.");
    }

    private async Task<ValidationSnapshot> ValidateCsvAsync(
        string? csv,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for opening stock validation.");

        var rows = Parse(csv);
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
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<OpeningStockRowResult>(rows.Count);
        var validRows = new List<ValidatedOpeningRow>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = Validate(row, seenReferences, itemsByExternalId, locations);
            if (error is not null)
            {
                results.Add(new(row.RowNumber, row.ExternalReference, "rejected", error));
                continue;
            }

            var item = itemsByExternalId[row.ItemExternalId].Single();
            var locationId = int.Parse(row.LocationId, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var quantity = int.Parse(row.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var unitCost = decimal.Parse(row.UnitCost, NumberStyles.Number, CultureInfo.InvariantCulture);
            validRows.Add(new(row.RowNumber, row.ExternalReference, item.Id, locationId, quantity, unitCost));
            results.Add(new(row.RowNumber, row.ExternalReference, "valid"));
        }

        return new(
            results.Count(row => row.Status == "valid"),
            results.Count(row => row.Status == "rejected"),
            results,
            validRows);
    }

    private static OpeningStockPreviewResult Summarize(ValidationSnapshot validation) =>
        new(validation.Valid, validation.Rejected, validation.Results);

    private static string? Validate(
        OpeningRow row,
        HashSet<string> seenReferences,
        IReadOnlyDictionary<string, List<Item>> items,
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
        if (!items.TryGetValue(row.ItemExternalId, out var matches))
            return $"Item external ID '{row.ItemExternalId}' was not found in the current tenant.";
        if (matches.Count != 1)
            return $"Item external ID '{row.ItemExternalId}' is ambiguous in the current tenant.";
        if (!matches[0].IsActive)
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

    private static OpeningStockReplayResult AlreadyApplied(OpeningStockImport import) =>
        new(
            import.ImportReference,
            0,
            true,
            import.Lines.Count,
            0,
            import.Lines
                .OrderBy(line => line.RowNumber)
                .Select(line => new OpeningStockRowResult(line.RowNumber, line.ExternalReference, "valid"))
                .ToList());

    private static void EnsureSameRequest(OpeningStockImport existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The import reference was already used with different opening stock data.");
    }

    private static void ValidateReference(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("Reference is required and must be at most 128 characters.", parameterName);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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

    private sealed record ValidationSnapshot(
        int Valid,
        int Rejected,
        IReadOnlyList<OpeningStockRowResult> Results,
        IReadOnlyList<ValidatedOpeningRow> ValidRows);

    private sealed record ValidatedOpeningRow(
        int RowNumber,
        string ExternalReference,
        int ItemId,
        int LocationId,
        int Quantity,
        decimal UnitCost);

    private sealed record OpeningRow(
        int RowNumber,
        string ExternalReference = "",
        string ItemExternalId = "",
        string LocationId = "",
        string Quantity = "",
        string UnitCost = "",
        bool HasCorrectFieldCount = false);
}
