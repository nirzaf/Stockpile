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
    IHttpContextAccessor httpContextAccessor,
    IStockService? stockService = null) : IOpeningStockImportService
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
        var requestedCutoverAt = request.CutoverAt;
        var cutoverAt = (requestedCutoverAt ?? DateTime.UtcNow).ToUniversalTime();
        if (cutoverAt > DateTime.UtcNow)
            throw new ArgumentException("Cutover instant cannot be in the future.", nameof(request));
        request = request with { CutoverAt = cutoverAt };
        ValidateReference(request.ImportReference, nameof(request.ImportReference));
        ValidateReference(request.ApprovalReference, nameof(request.ApprovalReference));
        if (!tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for opening stock replay.");

        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            request with { CutoverAt = requestedCutoverAt.HasValue ? cutoverAt : null })));
        OpeningStockReplayResult? replayResult = null;

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await unitOfWork.AcquireTenantOperationLockAsync("opening-stock-baseline", cancellationToken);
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

                if (await context.OpeningStockImports.AnyAsync(cancellationToken))
                    throw new InvalidOperationException("The tenant already has an approved opening baseline.");

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

                await unitOfWork.AcquireLocationLocksAsync(
                    validation.ValidRows.Select(row => row.LocationId).Distinct().Order().ToArray(),
                    cancellationToken);

                var import = new OpeningStockImport
                {
                    ImportReference = request.ImportReference,
                    ApprovalReference = request.ApprovalReference,
                    RequestHash = requestHash,
                    ApprovedBy = httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System",
                    ApprovedAt = DateTime.UtcNow,
                    CutoverAt = cutoverAt,
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
                    else if (stock.Quantity != quantity)
                    {
                        throw new InvalidOperationException(
                            $"Opening baseline quantity for item {group.Key.ItemId} at location {group.Key.LocationId} " +
                            $"does not reconcile with current stock ({stock.Quantity} versus approved {quantity}).");
                    }

                    if (await context.StockValuationBuckets.AnyAsync(bucket =>
                            bucket.ItemId == group.Key.ItemId && bucket.LocationId == group.Key.LocationId,
                            cancellationToken))
                    {
                        throw new InvalidOperationException(
                            $"Opening baseline cannot be applied to an already-valued stock bucket for item {group.Key.ItemId} " +
                            $"at location {group.Key.LocationId}.");
                    }

                    var bucket = new StockValuationBucket
                    {
                        ItemId = group.Key.ItemId,
                        LocationId = group.Key.LocationId,
                        Quantity = quantity,
                        Value = Round(group.Sum(row => row.Quantity * row.UnitCost))
                    };
                    context.StockValuationBuckets.Add(bucket);

                    foreach (var row in group)
                    {
                        var transaction = new StockTransaction
                        {
                            ItemId = row.ItemId,
                            FromLocationId = row.LocationId,
                            ToLocationId = row.LocationId,
                            Quantity = row.Quantity,
                            TransactionType = TransactionType.Opening,
                            TransactionDate = cutoverAt,
                            Notes = $"Opening baseline {request.ImportReference}; source {row.ExternalReference}"
                        };
                        context.StockTransactions.Add(transaction);
                        import.Lines.Add(new OpeningStockImportLine
                        {
                            RowNumber = row.RowNumber,
                            ExternalReference = row.ExternalReference,
                            ItemId = row.ItemId,
                            LocationId = row.LocationId,
                            Quantity = row.Quantity,
                            UnitCost = row.UnitCost,
                            StockTransaction = transaction
                        });
                        context.StockValuationEntries.Add(new StockValuationEntry
                        {
                            StockTransaction = transaction,
                            ItemId = row.ItemId,
                            LocationId = row.LocationId,
                            EntryType = StockValuationEntryType.Receipt,
                            Quantity = row.Quantity,
                            UnitCost = Round(row.UnitCost),
                            TotalValue = Round(row.Quantity * row.UnitCost)
                        });
                    }
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

    public async Task<OpeningStockReversalResult> ReverseAsync(
        OpeningStockReversalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateReference(request.ImportReference, nameof(request.ImportReference));
        ValidateReference(request.CorrectionReference, nameof(request.CorrectionReference));
        ValidateReference(request.ApprovalReference, nameof(request.ApprovalReference));
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
            throw new ArgumentException("A correction reason is required and must be at most 500 characters.", nameof(request));
        if (!tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for opening stock reversal.");
        if (stockService is null)
            throw new InvalidOperationException("Stock reversal persistence is not configured.");

        OpeningStockReversalResult? result = null;
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await unitOfWork.AcquireTenantOperationLockAsync(
                $"opening-stock-reversal-correction:{request.CorrectionReference}", cancellationToken);
            await unitOfWork.AcquireTenantOperationLockAsync(
                $"opening-stock-reversal-import:{request.ImportReference}", cancellationToken);

            var existingCorrection = await context.OpeningStockCorrections
                .AsNoTracking()
                .SingleOrDefaultAsync(correction =>
                    correction.CorrectionReference == request.CorrectionReference,
                    cancellationToken);
            if (existingCorrection is not null)
            {
                if (!string.Equals(existingCorrection.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("The correction reference was already used with different data.");
                result = new(request.CorrectionReference, existingCorrection.LineCount, true);
                return;
            }

            var import = await context.OpeningStockImports
                .Include(value => value.Lines)
                .SingleOrDefaultAsync(value => value.ImportReference == request.ImportReference, cancellationToken)
                ?? throw new KeyNotFoundException("Opening baseline was not found.");
            await unitOfWork.AcquireLocationLocksAsync(
                import.Lines.Select(line => line.LocationId).Distinct().ToArray(), cancellationToken);
            if (await context.OpeningStockCorrections.AnyAsync(
                    correction => correction.OpeningStockImportId == import.Id, cancellationToken))
            {
                throw new InvalidOperationException("The opening baseline has already been reversed.");
            }

            foreach (var line in import.Lines.OrderBy(line => line.RowNumber))
            {
                await stockService.SellStockAsync(
                    line.ItemId,
                    line.LocationId,
                    line.Quantity,
                    $"Opening baseline reversal {request.CorrectionReference}; {request.Reason}");
            }

            context.OpeningStockCorrections.Add(new OpeningStockCorrection
            {
                OpeningStockImportId = import.Id,
                CorrectionReference = request.CorrectionReference,
                ApprovalReference = request.ApprovalReference,
                RequestHash = requestHash,
                Reason = request.Reason.Trim(),
                CorrectedBy = httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System",
                CorrectedAt = DateTime.UtcNow,
                LineCount = import.Lines.Count
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            result = new(request.CorrectionReference, import.Lines.Count, false);
        }, cancellationToken);

        return result
            ?? throw new InvalidOperationException("Opening stock reversal did not produce a result.");
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
        var existingStock = await context.StockInHand
            .AsNoTracking()
            .Where(stock => stock.BatchNumber == null && stock.ExpiryDate == null)
            .ToDictionaryAsync(stock => (stock.ItemId, stock.LocationId), stock => stock.Quantity, cancellationToken);
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

        var discrepancies = validRows
            .GroupBy(row => (row.ItemId, row.LocationId))
            .Select(group =>
            {
                var approved = group.Sum(row => row.Quantity);
                var current = existingStock.GetValueOrDefault(group.Key);
                return new OpeningStockDiscrepancy(group.Key.ItemId, group.Key.LocationId, current, approved, approved - current);
            })
            .Where(discrepancy => discrepancy.Difference != 0 &&
                                  existingStock.ContainsKey((discrepancy.ItemId, discrepancy.LocationId)))
            .ToArray();

        return new(
            results.Count(row => row.Status == "valid"),
            results.Count(row => row.Status == "rejected"),
            results,
            validRows,
            discrepancies);
    }

    private static OpeningStockPreviewResult Summarize(ValidationSnapshot validation) =>
        new(validation.Valid, validation.Rejected, validation.Results, validation.Discrepancies);

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

    private static decimal Round(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);

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
        IReadOnlyList<ValidatedOpeningRow> ValidRows,
        IReadOnlyList<OpeningStockDiscrepancy> Discrepancies);

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
