using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Services;

/// <summary>Atomic transfer-order creation, approval, amendment and cancellation.</summary>
public sealed class TransferOrderService(
    IRepository<TransferOrder> orderRepository,
    IRepository<TransferOrderLine> lineRepository,
    IRepository<DocumentIdentity> documentRepository,
    IRepository<DocumentLineIdentity> lineIdentityRepository,
    IRepository<Company> companyRepository,
    IRepository<Branch> branchRepository,
    IRepository<Location> locationRepository,
    IRepository<Item> itemRepository,
    IUnitOfWork unitOfWork,
    IDocumentIdentityService documentIdentityService,
    IStockService stockService,
    ITenantContext tenantContext,
    IWebhookDispatcher webhookDispatcher,
    ILogger<TransferOrderService> logger) : ITransferOrderService
{
    private const string DocumentType = "TransferOrder";
    private const string RequestScope = "TransferOrder.Create";
    private const string NumberPrefix = "TO-";
    private const int RecentOrderLimit = 100;
    private static readonly DateTimeOffset ReservationUntilResolution =
        new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public async Task<TransferOrderView?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));

        var order = await orderRepository.GetByIdAsync(id);
        return order is null ? null : await ToViewAsync(order, cancellationToken);
    }

    public async Task<IReadOnlyList<TransferOrderView>> GetRecentForCompaniesAsync(
        IReadOnlyCollection<int> companyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companyIds);
        var scopedCompanyIds = companyIds.Where(companyId => companyId > 0).Distinct().ToArray();
        if (scopedCompanyIds.Length == 0)
            return Array.Empty<TransferOrderView>();

        var orders = (await orderRepository.FindPageAsync(
                order => scopedCompanyIds.Contains(order.CompanyId),
                query => query.OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id),
                RecentOrderLimit))
            .ToArray();
        if (orders.Length == 0)
            return Array.Empty<TransferOrderView>();

        var orderIds = orders.Select(order => order.Id).ToArray();
        var documentIds = orders.Select(order => order.DocumentId).Distinct().ToArray();
        var lines = await lineRepository.FindAsync(
            line => orderIds.Contains(line.TransferOrderId), cancellationToken);
        var identities = await documentRepository.FindAsync(
            document => documentIds.Contains(document.Id), cancellationToken);
        var linesByOrder = lines.GroupBy(line => line.TransferOrderId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToArray());
        var identitiesById = identities.ToDictionary(identity => identity.Id);

        return orders.Select(order => ToView(
                order,
                identitiesById.TryGetValue(order.DocumentId, out var identity)
                    ? identity
                    : throw new InvalidOperationException("Transfer-order document identity not found."),
                linesByOrder.TryGetValue(order.Id, out var orderLines)
                    ? orderLines
                    : Array.Empty<TransferOrderLine>()))
            .ToArray();
    }

    public async Task<TransferOrderView> CreateAsync(
        CreateTransferOrderRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);
        ValidateHeader(request.CompanyId, request.FromLocationId, request.ToLocationId);
        var lines = ValidateLines(request.Lines);
        if (lines.Any(line => line.LineId.HasValue))
            throw new ArgumentException("Create requests cannot supply existing transfer-order line IDs.");
        var requestHash = HashRequest(request, lines);
        TransferOrder? result = null;

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await unitOfWork.AcquireLocationLocksAsync(
                [request.FromLocationId, request.ToLocationId],
                cancellationToken);

            var identity = await documentIdentityService.CreateNumberedAsync(
                request.CompanyId,
                DocumentType,
                DateTime.UtcNow.Year,
                NumberPrefix,
                RequestScope,
                idempotencyKey,
                requestHash,
                cancellationToken);

            result = (await orderRepository.FindAsync(order => order.DocumentId == identity.Id))
                .SingleOrDefault();
            if (result is not null)
                return;

            await ValidateReferencesAsync(request);
            result = new TransferOrder
            {
                CompanyId = request.CompanyId,
                FromLocationId = request.FromLocationId,
                ToLocationId = request.ToLocationId,
                Notes = NormalizeNotes(request.Notes),
                TenantId = tenantContext.TenantId
            };
            result.AttachDocumentIdentity(identity);
            foreach (var input in lines)
            {
                var line = new TransferOrderLine
                {
                    ItemId = input.ItemId,
                    Quantity = input.Quantity,
                    BatchNumber = NormalizeBatchNumber(input.BatchNumber),
                    ExpiryDate = StockLotExpiryDate.Normalize(input.ExpiryDate),
                    TenantId = tenantContext.TenantId,
                    TransferOrder = result
                };
                result.Lines.Add(line);
                var lineIdentity = DocumentLineIdentity.Create(
                    line.DocumentLineId,
                    result.DocumentId,
                    tenantContext.TenantId,
                    request.CompanyId,
                    "TransferOrderLine");
                line.DocumentLineIdentity = lineIdentity;
                await lineIdentityRepository.AddAsync(lineIdentity);
            }

            await orderRepository.AddAsync(result);
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.Created",
                new { result.CompanyId, result.FromLocationId, result.ToLocationId, result.DocumentId }));
        }, cancellationToken);

        logger.LogInformation("Created transfer order {TransferOrderId}", result!.Id);
        return await ToViewAsync(result, cancellationToken);
    }

    public async Task AmendAsync(
        int id,
        CreateTransferOrderRequest request,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateHeader(request.CompanyId, request.FromLocationId, request.ToLocationId);
        var lines = ValidateLines(request.Lines);

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await unitOfWork.AcquireLocationLocksAsync(
                [request.FromLocationId, request.ToLocationId],
                cancellationToken);
            await ValidateReferencesAsync(request);
            var order = await orderRepository.GetByIdAsync(id)
                ?? throw new KeyNotFoundException("Transfer order not found.");
            ValidateOwnership(order, request);
            if (order.Status == TransferOrderStatus.Cancelled)
                throw new InvalidOperationException("Cancelled transfer orders cannot be amended.");

            var existingLines = (await lineRepository.FindAsync(line => line.TransferOrderId == id))
                .OrderBy(line => line.Id)
                .ToList();
            if (existingLines.Count != lines.Count || lines.Any(line => !line.LineId.HasValue))
                throw new InvalidOperationException("An amendment must retain every existing transfer-order line identity.");
            var requestedIds = lines.Select(line => line.LineId!.Value).OrderBy(lineId => lineId).ToArray();
            if (!requestedIds.SequenceEqual(existingLines.Select(line => line.Id).OrderBy(lineId => lineId)))
                throw new InvalidOperationException("An amendment must retain every existing transfer-order line identity.");
            if (AmendmentMatches(order, existingLines, request, lines))
                return;

            if (order.Status == TransferOrderStatus.Approved)
            {
                var controlledScope = mutationScope with { AllowControlledTransferReservation = true };
                foreach (var line in existingLines)
                {
                    await stockService.ReleaseReservationAsync(
                        line.ReservationSourceLineReference,
                        "Transfer order amended; approval invalidated.",
                        controlledScope);
                    line.SetReservationVersionForAmendment();
                }
                order.Status = TransferOrderStatus.Draft;
            }

            foreach (var input in lines)
            {
                var line = existingLines.Single(existing => existing.Id == input.LineId);
                line.ItemId = input.ItemId;
                line.Quantity = input.Quantity;
                line.BatchNumber = NormalizeBatchNumber(input.BatchNumber);
                line.ExpiryDate = StockLotExpiryDate.Normalize(input.ExpiryDate);
                await lineRepository.UpdateAsync(line);
            }
            order.FromLocationId = request.FromLocationId;
            order.ToLocationId = request.ToLocationId;
            order.Notes = NormalizeNotes(request.Notes);
            await orderRepository.UpdateAsync(order);
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.Amended",
                new { TransferOrderId = order.Id, order.DocumentId, order.Status }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken, () => VerifyAmendmentAsync(id, request, lines));
    }

    public async Task ApproveAsync(
        int id,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var order = await orderRepository.GetByIdAsync(id)
                ?? throw new KeyNotFoundException("Transfer order not found.");
            if (order.Status == TransferOrderStatus.Cancelled)
                throw new InvalidOperationException("Cancelled transfer orders cannot be approved.");
            if (order.Status == TransferOrderStatus.Approved)
                return;

            var lines = (await lineRepository.FindAsync(line => line.TransferOrderId == id))
                .OrderBy(line => line.Id)
                .ToList();
            if (lines.Count == 0)
                throw new InvalidOperationException("A transfer order must contain at least one line before approval.");

            var lineRequests = lines.Select(line => new TransferOrderLineRequest(
                line.ItemId, line.Quantity, line.BatchNumber, line.ExpiryDate)).ToArray();
            await ValidateReferencesAsync(new CreateTransferOrderRequest(
                order.CompanyId,
                order.FromLocationId,
                order.ToLocationId,
                lineRequests,
                order.Notes));
            await unitOfWork.AcquireLocationLocksAsync([order.FromLocationId, order.ToLocationId], cancellationToken);
            var controlledScope = mutationScope with { AllowControlledTransferReservation = true };
            foreach (var line in lines)
            {
                await stockService.CreateReservationAsync(new CreateStockReservationRequest(
                    line.ItemId,
                    order.FromLocationId,
                    line.Quantity,
                    line.ReservationSourceLineReference,
                    line.BatchNumber,
                    line.ExpiryDate,
                    ReservationUntilResolution), controlledScope);
            }

            order.Status = TransferOrderStatus.Approved;
            await documentIdentityService.TransitionLifecycleAsync(
                order.DocumentId,
                DocumentLifecycleStatus.Active,
                cancellationToken);
            await orderRepository.UpdateAsync(order);
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.Approved",
                new { TransferOrderId = order.Id, order.DocumentId, order.FromLocationId, order.ToLocationId }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken, () => VerifyStatusAsync(id, TransferOrderStatus.Approved));
    }

    public async Task CancelAsync(
        int id,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var order = await orderRepository.GetByIdAsync(id)
                ?? throw new KeyNotFoundException("Transfer order not found.");
            if (order.Status == TransferOrderStatus.Cancelled)
                return;

            if (order.Status == TransferOrderStatus.Approved)
            {
                var controlledScope = mutationScope with { AllowControlledTransferReservation = true };
                var lines = await lineRepository.FindAsync(line => line.TransferOrderId == id);
                foreach (var line in lines)
                {
                    await stockService.ReleaseReservationAsync(
                        line.ReservationSourceLineReference,
                        "Transfer order cancelled.",
                        controlledScope);
                }
            }

            order.Status = TransferOrderStatus.Cancelled;
            await documentIdentityService.TransitionLifecycleAsync(
                order.DocumentId,
                DocumentLifecycleStatus.Cancelled,
                cancellationToken);
            await orderRepository.UpdateAsync(order);
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.Cancelled",
                new { TransferOrderId = order.Id, order.DocumentId }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken, () => VerifyStatusAsync(id, TransferOrderStatus.Cancelled));
    }

    private async Task<TransferOrderView> ToViewAsync(TransferOrder order, CancellationToken cancellationToken)
    {
        var identity = (await documentRepository.FindAsync(document => document.Id == order.DocumentId))
            .SingleOrDefault()
            ?? throw new InvalidOperationException("Transfer-order document identity not found.");
        var lines = (await lineRepository.FindAsync(line => line.TransferOrderId == order.Id))
            .OrderBy(line => line.Id)
            .ToArray();
        return ToView(order, identity, lines);
    }

    private static TransferOrderView ToView(
        TransferOrder order,
        DocumentIdentity identity,
        IReadOnlyCollection<TransferOrderLine> lines) =>
        new(
            order.Id,
            order.DocumentId.Value,
            identity.HumanNumber,
            order.CompanyId,
            order.FromLocationId,
            order.ToLocationId,
            order.OrderDate,
            order.Status,
            order.Notes,
            lines.Select(line => new TransferOrderLineView(
                line.Id,
                line.DocumentLineId.Value,
                line.ItemId,
                line.Quantity,
                line.BatchNumber,
                line.ExpiryDate,
                line.ReservationSourceLineReference)).ToArray());

    private void ValidateHeader(int companyId, int fromLocationId, int toLocationId)
    {
        if (companyId <= 0 || fromLocationId <= 0 || toLocationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId), "Company and location IDs must be positive.");
        if (fromLocationId == toLocationId)
            throw new ArgumentException("Source and destination locations must be different.");
    }

    private IReadOnlyList<TransferOrderLineRequest> ValidateLines(IReadOnlyCollection<TransferOrderLineRequest> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
            throw new ArgumentException("A transfer order must contain at least one line.", nameof(lines));
        if (lines.Count > 100)
            throw new ArgumentException("A transfer order cannot contain more than 100 lines.", nameof(lines));
        foreach (var line in lines)
        {
            if (line.ItemId <= 0 || line.Quantity <= 0)
                throw new ArgumentException("Transfer-order item IDs and quantities must be positive.");
            if (line.LineId is <= 0)
                throw new ArgumentException("Transfer-order line IDs must be positive when supplied.");
            if (line.BatchNumber?.Length > 100)
                throw new ArgumentException("Batch numbers cannot exceed 100 characters.");
            if (line.ExpiryDate.HasValue && StockLotExpiryDate.Normalize(line.ExpiryDate) < DateTime.UtcNow.Date)
                throw new ArgumentException("Expired lots cannot be used in transfer orders.");
        }
        return lines.ToArray();
    }

    private void ValidateOwnership(TransferOrder order, CreateTransferOrderRequest request)
    {
        if (order.CompanyId != request.CompanyId || order.FromLocationId != request.FromLocationId ||
            order.ToLocationId != request.ToLocationId)
            throw new InvalidOperationException("A transfer-order amendment cannot change its company or locations.");
    }

    private async Task ValidateReferencesAsync(CreateTransferOrderRequest request)
    {
        var lines = request.Lines;
        var company = (await companyRepository.FindAsync(company =>
                company.Id == request.CompanyId && company.IsActive)).SingleOrDefault();
        if (company is null)
            throw new InvalidOperationException("An active company in the current tenant is required.");

        var locations = (await locationRepository.FindAsync(location =>
                (location.Id == request.FromLocationId || location.Id == request.ToLocationId) &&
                !location.IsDeleted)).ToList();
        if (locations.Count != 2 || locations.Any(location => !location.BranchId.HasValue))
            throw new InvalidOperationException("Both transfer locations must be active, branch-owned locations.");

        var branchIds = locations.Select(location => location.BranchId!.Value).Distinct().ToArray();
        var branches = (await branchRepository.FindAsync(branch =>
                branchIds.Contains(branch.Id) && branch.IsActive)).ToDictionary(branch => branch.Id);
        if (branches.Count != branchIds.Length || locations.Any(location =>
                !branches.TryGetValue(location.BranchId!.Value, out var branch) ||
                branch.CompanyId != request.CompanyId))
            throw new InvalidOperationException("Both transfer locations must belong to the requested active company.");

        var itemIds = lines.Select(line => line.ItemId).Distinct().ToArray();
        var items = await itemRepository.FindAsync(item =>
            itemIds.Contains(item.Id) && item.IsActive && !item.IsDeleted);
        if (items.Select(item => item.Id).Distinct().Count() != itemIds.Length)
            throw new InvalidOperationException("Every transfer-order item must be active in the current tenant.");
    }

    private static string? NormalizeNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

    private static string? NormalizeBatchNumber(string? batchNumber) =>
        string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();

    private static void ValidateIdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(key), "An idempotency key cannot exceed 200 characters.");
    }

    private async Task<bool> VerifyStatusAsync(int id, TransferOrderStatus expectedStatus)
    {
        var order = (await orderRepository.FindAsync(order => order.Id == id)).SingleOrDefault();
        return order?.Status == expectedStatus;
    }

    private async Task<bool> VerifyAmendmentAsync(
        int id,
        CreateTransferOrderRequest request,
        IReadOnlyCollection<TransferOrderLineRequest> lines)
    {
        var order = (await orderRepository.FindAsync(order => order.Id == id)).SingleOrDefault();
        if (order is null || order.Status != TransferOrderStatus.Draft)
            return false;

        var persistedLines = (await lineRepository.FindAsync(line => line.TransferOrderId == id))
            .ToDictionary(line => line.Id);
        return AmendmentMatches(order, persistedLines.Values, request, lines);
    }

    private static bool AmendmentMatches(
        TransferOrder order,
        IReadOnlyCollection<TransferOrderLine> existingLines,
        CreateTransferOrderRequest request,
        IReadOnlyCollection<TransferOrderLineRequest> lines) =>
        order.Notes == NormalizeNotes(request.Notes) &&
        lines.Count == existingLines.Count && lines.All(input =>
            input.LineId is int lineId && existingLines.Any(line =>
                line.Id == lineId && line.ItemId == input.ItemId && line.Quantity == input.Quantity &&
                line.BatchNumber == NormalizeBatchNumber(input.BatchNumber) &&
                line.ExpiryDate == StockLotExpiryDate.Normalize(input.ExpiryDate)));

    private static string HashRequest(CreateTransferOrderRequest request, IEnumerable<TransferOrderLineRequest> lines)
    {
        var orderedLines = lines.OrderBy(line => line.LineId ?? 0).ThenBy(line => line.ItemId).ToArray();
        var payload = new StringBuilder();
        AppendField(payload, request.CompanyId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, request.FromLocationId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, request.ToLocationId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, NormalizeNotes(request.Notes));
        AppendField(payload, orderedLines.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var line in orderedLines)
        {
            AppendField(payload, line.LineId?.ToString(CultureInfo.InvariantCulture));
            AppendField(payload, line.ItemId.ToString(CultureInfo.InvariantCulture));
            AppendField(payload, line.Quantity.ToString(CultureInfo.InvariantCulture));
            AppendField(payload, NormalizeBatchNumber(line.BatchNumber));
            AppendField(payload, StockLotExpiryDate.Normalize(line.ExpiryDate)?.ToString("O", CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static void AppendField(StringBuilder payload, string? value)
    {
        if (value is null)
        {
            payload.Append("-1:");
            return;
        }

        payload.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}
