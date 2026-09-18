using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Services;

/// <summary>Atomic transfer-order lifecycle, reservation, and dispatch operations.</summary>
public sealed class TransferOrderService(
    IRepository<TransferOrder> orderRepository,
    IRepository<TransferOrderLine> lineRepository,
    IRepository<TransferTransitEntry> transitRepository,
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
    ILogger<TransferOrderService> logger,
    IRepository<TransferTransitSettlement>? settlementRepository = null) : ITransferOrderService
{
    private const string DocumentType = "TransferOrder";
    private const string RequestScope = "TransferOrder.Create";
    private const string NumberPrefix = "TO-";
    private const int RecentOrderLimit = 100;
    private static readonly DateTimeOffset ReservationUntilResolution =
        new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private readonly IRepository<TransferTransitSettlement>? _settlementRepository = settlementRepository;

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
        var settlements = _settlementRepository is null
            ? Array.Empty<TransferTransitSettlement>()
            : (await _settlementRepository.FindAsync(
                settlement => orderIds.Contains(settlement.TransferOrderId), cancellationToken)).ToArray();
        var linesByOrder = lines.GroupBy(line => line.TransferOrderId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Id).ToArray());
        var identitiesById = identities.ToDictionary(identity => identity.Id);
        var settledByLine = settlements
            .Where(settlement => settlement.SettlementType != TransferTransitSettlementType.Returned)
            .GroupBy(settlement => settlement.TransferOrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(settlement => settlement.Quantity));

        return orders.Select(order => ToView(
                order,
                identitiesById.TryGetValue(order.DocumentId, out var identity)
                    ? identity
                    : throw new InvalidOperationException("Transfer-order document identity not found."),
                linesByOrder.TryGetValue(order.Id, out var orderLines)
                    ? orderLines
                    : Array.Empty<TransferOrderLine>(),
                settledByLine))
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
        var originalLineIds = Array.Empty<int>();

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

            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await unitOfWork.AcquireLocationLocksAsync(
                [order.FromLocationId, order.ToLocationId], cancellationToken);
            var currentLines = lineRepository.Query()
                .Where(line => line.TransferOrderId == id)
                .OrderBy(line => line.Id)
                .ToArray();
            if (currentLines.Any(line => line.DispatchedQuantity > 0))
                throw new InvalidOperationException("A transfer order cannot be amended after any quantity is dispatched.");
            if (order.Status is not (TransferOrderStatus.Draft or TransferOrderStatus.Approved))
                throw new InvalidOperationException("Only draft or approved transfer orders can be amended.");

            var existingLines = currentLines.ToList();
            originalLineIds = existingLines.Select(line => line.Id).ToArray();
            var requestedExistingIds = lines
                .Where(line => line.LineId.HasValue)
                .Select(line => line.LineId!.Value)
                .ToArray();
            if (requestedExistingIds.Distinct().Count() != requestedExistingIds.Length)
                throw new InvalidOperationException("An amendment cannot include the same transfer-order line more than once.");

            var existingLinesById = existingLines.ToDictionary(line => line.Id);
            if (requestedExistingIds.Any(lineId => !existingLinesById.ContainsKey(lineId)))
                throw new InvalidOperationException("An amendment can only retain lines that belong to this transfer order.");

            if (AmendmentMatches(order, existingLines, request, lines))
                return;

            var retainedLineIds = requestedExistingIds.ToHashSet();
            var removedLines = existingLines.Where(line => !retainedLineIds.Contains(line.Id)).ToArray();
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

            if (removedLines.Length > 0)
            {
                var removedDocumentLineIds = removedLines.Select(line => line.DocumentLineId).ToHashSet();
                var documentLineIdentities = (await lineIdentityRepository.FindAsync(identity =>
                        identity.DocumentId == order.DocumentId && identity.LineType == "TransferOrderLine"))
                    .Where(identity => removedDocumentLineIds.Contains(identity.Id))
                    .ToDictionary(identity => identity.Id);

                if (documentLineIdentities.Count != removedLines.Length)
                    throw new InvalidOperationException("A transfer-order line is missing its document-line identity.");

                foreach (var removedLine in removedLines)
                {
                    await lineRepository.DeleteAsync(removedLine);
                    await lineIdentityRepository.DeleteAsync(documentLineIdentities[removedLine.DocumentLineId]);
                }
            }

            foreach (var input in lines)
            {
                if (input.LineId is not int lineId)
                {
                    var newLine = new TransferOrderLine
                    {
                        TenantId = tenantContext.TenantId,
                        TransferOrderId = order.Id,
                        TransferOrder = order,
                        ItemId = input.ItemId,
                        Quantity = input.Quantity,
                        BatchNumber = NormalizeBatchNumber(input.BatchNumber),
                        ExpiryDate = StockLotExpiryDate.Normalize(input.ExpiryDate)
                    };
                    var lineIdentity = DocumentLineIdentity.Create(
                        newLine.DocumentLineId,
                        order.DocumentId,
                        tenantContext.TenantId,
                        order.CompanyId,
                        "TransferOrderLine");
                    newLine.DocumentLineIdentity = lineIdentity;
                    await lineIdentityRepository.AddAsync(lineIdentity);
                    await lineRepository.AddAsync(newLine);
                    continue;
                }

                var line = existingLinesById[lineId];
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
        }, cancellationToken, () => VerifyAmendmentAsync(id, request, lines, originalLineIds));
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
            if (order.Status is TransferOrderStatus.InTransit or
                TransferOrderStatus.PartiallyReceived or TransferOrderStatus.Completed)
                throw new InvalidOperationException("A transfer order cannot be approved after dispatch or settlement.");

            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await unitOfWork.AcquireLocationLocksAsync([order.FromLocationId, order.ToLocationId], cancellationToken);
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

            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            await unitOfWork.AcquireLocationLocksAsync(
                [order.FromLocationId, order.ToLocationId], cancellationToken);
            var currentOrder = orderRepository.Query().SingleOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("Transfer order not found.");
            var dispatched = (await transitRepository.FindAsync(entry => entry.TransferOrderId == id)).Any();
            if (dispatched || lineRepository.Query().Any(line =>
                    line.TransferOrderId == id && line.DispatchedQuantity > 0))
                throw new InvalidOperationException("A transfer order cannot be cancelled after any quantity is dispatched.");
            if (currentOrder.Status == TransferOrderStatus.Cancelled)
                return;

            if (currentOrder.Status == TransferOrderStatus.Approved)
            {
                var controlledScope = mutationScope with { AllowControlledTransferReservation = true };
                var lines = lineRepository.Query().Where(line => line.TransferOrderId == id).ToArray();
                foreach (var line in lines)
                {
                    await stockService.ReleaseReservationAsync(
                        line.ReservationSourceLineReference,
                        "Transfer order cancelled.",
                        controlledScope);
                }
            }

            currentOrder.Status = TransferOrderStatus.Cancelled;
            await documentIdentityService.TransitionLifecycleAsync(
                currentOrder.DocumentId,
                DocumentLifecycleStatus.Cancelled,
                cancellationToken);
            await orderRepository.UpdateAsync(currentOrder);
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.Cancelled",
                new { TransferOrderId = currentOrder.Id, currentOrder.DocumentId }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken, () => VerifyStatusAsync(id, TransferOrderStatus.Cancelled));
    }

    public async Task<TransferDispatchView> DispatchAsync(
        int id,
        int lineId,
        int quantity,
        string idempotencyKey,
        string dispatchedBy,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0 || lineId <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Dispatch quantity must be positive.");
        ValidateIdempotencyKey(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchedBy);
        if (dispatchedBy.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(dispatchedBy), "Dispatcher identity cannot exceed 256 characters.");
        var requestHash = HashDispatchRequest(id, lineId, quantity);
        TransferDispatchView? result = null;

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            var previous = (await transitRepository.FindAsync(entry =>
                    entry.TransferOrderLineId == lineId && entry.IdempotencyKey == idempotencyKey))
                .SingleOrDefault();
            if (previous is not null)
            {
                if (!string.Equals(previous.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("The dispatch idempotency key was already used with a different request.");
                if (mutationScope.CompanyId != previous.CompanyId ||
                    mutationScope.Reauthorize is not null && !await mutationScope.Reauthorize())
                    throw new UnauthorizedAccessException("Company posting access is required to replay this dispatch.");
                result = ToDispatchView(previous);
                return;
            }

            var initialOrder = orderRepository.Query().SingleOrDefault(order => order.Id == id)
                ?? throw new KeyNotFoundException("Transfer order not found.");
            await unitOfWork.AcquireLocationLocksAsync(
                [initialOrder.FromLocationId, initialOrder.ToLocationId], cancellationToken);

            var order = orderRepository.Query().SingleOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("Transfer order not found.");
            var line = lineRepository.Query().SingleOrDefault(candidate =>
                candidate.Id == lineId && candidate.TransferOrderId == id)
                ?? throw new KeyNotFoundException("Transfer-order line not found.");
            if (order.Status is not (TransferOrderStatus.Approved or
                TransferOrderStatus.InTransit or TransferOrderStatus.PartiallyReceived))
                throw new InvalidOperationException("Only an open transfer order can be dispatched.");
            if (mutationScope.CompanyId != order.CompanyId)
                throw new UnauthorizedAccessException("The dispatch scope does not match the transfer-order company.");
            if (mutationScope.Reauthorize is not null && !await mutationScope.Reauthorize())
                throw new UnauthorizedAccessException("Company posting access changed before dispatch.");
            if (checked(line.DispatchedQuantity + quantity) > line.Quantity)
                throw new StockAvailabilityConflictException("Dispatch exceeds the transfer-order line quantity.");

            var orderLines = lineRepository.Query()
                .Where(candidate => candidate.TransferOrderId == id)
                .Select(candidate => new TransferOrderLineRequest(
                    candidate.ItemId, candidate.Quantity, candidate.BatchNumber, candidate.ExpiryDate))
                .ToArray();
            await ValidateReferencesAsync(new CreateTransferOrderRequest(
                order.CompanyId,
                order.FromLocationId,
                order.ToLocationId,
                orderLines,
                order.Notes));

            var controlledScope = mutationScope with { AllowControlledTransferReservation = true };
            var movement = await stockService.DispatchReservationAsync(
                line.ReservationSourceLineReference,
                quantity,
                order.ToLocationId,
                $"Transfer order {order.DocumentId.Value:N} dispatch.",
                controlledScope,
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var entry = new TransferTransitEntry
            {
                TransferOrderId = order.Id,
                TransferOrderLineId = line.Id,
                SourceDocumentLineId = line.DocumentLineId,
                CompanyId = order.CompanyId,
                ItemId = line.ItemId,
                FromLocationId = order.FromLocationId,
                ToLocationId = order.ToLocationId,
                StockTransactionId = movement.StockTransactionId,
                Quantity = movement.Quantity,
                BatchNumber = movement.BatchNumber,
                ExpiryDate = StockLotExpiryDate.Normalize(movement.ExpiryDate),
                UnitCost = movement.UnitCost,
                TotalValue = movement.TotalValue,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                DispatchedBy = dispatchedBy.Trim(),
                DispatchedAt = now,
                TenantId = tenantContext.TenantId
            };
            await transitRepository.AddAsync(entry);
            line.DispatchedQuantity = checked(line.DispatchedQuantity + quantity);
            await lineRepository.UpdateAsync(line);
            if (order.Status == TransferOrderStatus.Approved)
            {
                order.Status = TransferOrderStatus.InTransit;
                await orderRepository.UpdateAsync(order);
            }
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.Dispatched",
                new
                {
                    TransferOrderId = order.Id,
                    order.DocumentId,
                    TransferOrderLineId = line.Id,
                    SourceDocumentLineId = line.DocumentLineId.Value,
                    order.CompanyId,
                    order.FromLocationId,
                    order.ToLocationId,
                    Quantity = quantity
                }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            result = ToDispatchView(entry);
        }, cancellationToken, async () =>
        {
            var committed = (await transitRepository.FindAsync(entry =>
                    entry.TransferOrderLineId == lineId && entry.IdempotencyKey == idempotencyKey))
                .SingleOrDefault();
            return committed is not null && committed.RequestHash == requestHash;
        });

        return await GetDispatchByKeyAsync(id, lineId, idempotencyKey, cancellationToken)
            ?? result
            ?? throw new InvalidOperationException("The committed transfer dispatch could not be read back.");
    }

    public async Task<TransferTransitSettlementView> ResolveTransitAsync(
        int id,
        int lineId,
        int transitEntryId,
        TransferTransitSettlementRequest request,
        string idempotencyKey,
        string settledBy,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (id <= 0 || lineId <= 0 || transitEntryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (request.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Quantity), "Settlement quantity must be positive.");
        if (!Enum.IsDefined(request.SettlementType))
            throw new ArgumentException("Settlement type is invalid.", nameof(request));
        ValidateIdempotencyKey(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(settledBy);
        if (settledBy.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(settledBy), "Settlement identity cannot exceed 256 characters.");
        if (request.BatchNumber?.Length > 100)
            throw new ArgumentOutOfRangeException(nameof(request.BatchNumber));
        if (request.Reason?.Length > 500 || request.Notes?.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(request), "Reason and notes cannot exceed 500 characters.");
        if (request.SettlementType == TransferTransitSettlementType.Quarantined &&
            string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("A reason is required for quarantined transit stock.", nameof(request));
        if (request.SettlementType == TransferTransitSettlementType.Returned &&
            (request.BatchNumber is not null || request.ExpiryDate.HasValue || request.Reason is not null))
            throw new ArgumentException("Returns cannot provide destination lot details or a quarantine reason.", nameof(request));

        var normalizedRequest = request with
        {
            BatchNumber = NormalizeBatchNumber(request.BatchNumber),
            ExpiryDate = StockLotExpiryDate.Normalize(request.ExpiryDate),
            Reason = NormalizeNotes(request.Reason),
            Notes = NormalizeNotes(request.Notes)
        };
        var requestHash = HashTransitSettlementRequest(
            id, lineId, transitEntryId, normalizedRequest);
        TransferTransitSettlementView? result = null;

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
            var initialEntry = (await transitRepository.FindAsync(entry => entry.Id == transitEntryId, cancellationToken))
                .SingleOrDefault()
                ?? throw new KeyNotFoundException("Transfer transit entry not found.");
            await unitOfWork.AcquireLocationLocksAsync(
                [initialEntry.FromLocationId, initialEntry.ToLocationId], cancellationToken);

            var previous = (await _settlementRepositoryOrThrow().FindAsync(settlement =>
                    settlement.TransferTransitEntryId == transitEntryId &&
                    settlement.IdempotencyKey == idempotencyKey, cancellationToken))
                .SingleOrDefault();
            if (previous is not null)
            {
                if (!string.Equals(previous.RequestHash, requestHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("The transit idempotency key was already used with a different request.");
                if (mutationScope.CompanyId != previous.CompanyId ||
                    mutationScope.Reauthorize is not null && !await mutationScope.Reauthorize())
                    throw new UnauthorizedAccessException("Company posting access is required to replay this settlement.");
                result = ToSettlementView(previous);
                return;
            }

            var entry = (await transitRepository.FindAsync(candidate => candidate.Id == transitEntryId, cancellationToken))
                .SingleOrDefault()
                ?? throw new KeyNotFoundException("Transfer transit entry not found.");
            if (entry.TransferOrderId != id || entry.TransferOrderLineId != lineId)
                throw new KeyNotFoundException("Transfer transit entry not found.");
            if (mutationScope.CompanyId != entry.CompanyId)
                throw new UnauthorizedAccessException("The settlement scope does not match the transfer company.");
            if (mutationScope.Reauthorize is not null && !await mutationScope.Reauthorize())
                throw new UnauthorizedAccessException("Company posting access changed before settlement.");

            var order = (await orderRepository.FindAsync(candidate => candidate.Id == id, cancellationToken))
                .SingleOrDefault()
                ?? throw new KeyNotFoundException("Transfer order not found.");
            var line = (await lineRepository.FindAsync(candidate =>
                    candidate.Id == lineId && candidate.TransferOrderId == id, cancellationToken))
                .SingleOrDefault()
                ?? throw new KeyNotFoundException("Transfer-order line not found.");
            if (order.Status is TransferOrderStatus.Draft or TransferOrderStatus.Cancelled or TransferOrderStatus.Completed)
                throw new InvalidOperationException("Only an open dispatched transfer order can be settled.");
            if (line.ItemId != entry.ItemId || line.DocumentLineId != entry.SourceDocumentLineId)
                throw new InvalidOperationException("Transfer transit lineage is inconsistent.");
            if (entry.CompanyId != order.CompanyId || entry.FromLocationId != order.FromLocationId ||
                entry.ToLocationId != order.ToLocationId)
                throw new InvalidOperationException("Transfer transit ownership is inconsistent.");

            var existingSettlements = await _settlementRepositoryOrThrow().FindAsync(
                settlement => settlement.TransferTransitEntryId == transitEntryId, cancellationToken);
            var settledQuantity = existingSettlements.Sum(settlement => settlement.Quantity);
            var remaining = entry.Quantity - settledQuantity;
            if (normalizedRequest.Quantity > remaining)
                throw new StockAvailabilityConflictException(
                    $"Settlement quantity exceeds the remaining transit quantity of {Math.Max(0, remaining)}.");
            if (normalizedRequest.SettlementType is TransferTransitSettlementType.Received or
                TransferTransitSettlementType.Quarantined)
            {
                if (normalizedRequest.BatchNumber != entry.BatchNumber ||
                    normalizedRequest.ExpiryDate != StockLotExpiryDate.Normalize(entry.ExpiryDate))
                    throw new InvalidOperationException("Destination lot details must match the dispatched source lot.");
            }

            var isReturn = normalizedRequest.SettlementType == TransferTransitSettlementType.Returned;
            var remainingValue = entry.TotalValue - existingSettlements.Sum(settlement => settlement.TotalValue);
            var settlementValue = normalizedRequest.Quantity == remaining
                ? remainingValue
                : Math.Min(
                    Round(entry.TotalValue * normalizedRequest.Quantity / entry.Quantity),
                    remainingValue);
            var settlementUnitCost = Round(settlementValue / normalizedRequest.Quantity);
            var movement = await stockService.PostTransferTransitMovementAsync(
                new TransferTransitStockMovementRequest(
                    entry.ItemId,
                    isReturn ? entry.ToLocationId : entry.FromLocationId,
                    isReturn ? entry.FromLocationId : entry.ToLocationId,
                    normalizedRequest.Quantity,
                    entry.BatchNumber,
                    entry.ExpiryDate,
                    settlementUnitCost,
                    settlementValue,
                    CreateTransitMovementReference(entry.Id, idempotencyKey, normalizedRequest.SettlementType),
                    normalizedRequest.Notes ?? (isReturn ? "Transfer transit returned." : "Transfer transit received."),
                    isReturn ? TransactionType.TransferReturn : TransactionType.TransferReceipt,
                    normalizedRequest.SettlementType == TransferTransitSettlementType.Quarantined
                        ? normalizedRequest.Reason
                        : null,
                    mutationScope),
                cancellationToken);

            var settlement = new TransferTransitSettlement
            {
                TransferTransitEntryId = entry.Id,
                TransferOrderId = entry.TransferOrderId,
                TransferOrderLineId = entry.TransferOrderLineId,
                SourceDocumentLineId = entry.SourceDocumentLineId,
                CompanyId = entry.CompanyId,
                ItemId = entry.ItemId,
                FromLocationId = entry.FromLocationId,
                ToLocationId = entry.ToLocationId,
                StockTransactionId = movement.StockTransactionId,
                Quantity = movement.Quantity,
                SettlementType = normalizedRequest.SettlementType,
                BatchNumber = movement.BatchNumber,
                ExpiryDate = movement.ExpiryDate,
                UnitCost = movement.UnitCost,
                TotalValue = movement.TotalValue,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                SettledBy = settledBy.Trim(),
                SettledAt = NormalizeDatabaseTimestamp(DateTimeOffset.UtcNow),
                Reason = normalizedRequest.Reason,
                TenantId = tenantContext.TenantId
            };
            await _settlementRepositoryOrThrow().AddAsync(settlement);
            var allTransit = await transitRepository.FindAsync(candidate => candidate.TransferOrderId == id, cancellationToken);
            var allSettlements = await _settlementRepositoryOrThrow().FindAsync(
                candidate => candidate.TransferOrderId == id, cancellationToken);
            var allSettled = allSettlements.Sum(candidate => candidate.Quantity) + settlement.Quantity;
            var physicallyReceived = allSettlements
                .Where(candidate => candidate.SettlementType != TransferTransitSettlementType.Returned)
                .Sum(candidate => candidate.Quantity) +
                (settlement.SettlementType == TransferTransitSettlementType.Returned ? 0 : settlement.Quantity);
            var totalDispatched = allTransit.Sum(candidate => candidate.Quantity);
            var totalOrdered = (await lineRepository.FindAsync(candidate => candidate.TransferOrderId == id, cancellationToken))
                .Sum(candidate => candidate.Quantity);
            order.Status = allSettled >= totalDispatched && totalDispatched >= totalOrdered
                ? TransferOrderStatus.Completed
                : physicallyReceived > 0 ? TransferOrderStatus.PartiallyReceived : TransferOrderStatus.InTransit;
            await orderRepository.UpdateAsync(order);
            await webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(tenantContext,
                "TransferOrder.TransitSettled",
                new
                {
                    TransferOrderId = order.Id,
                    order.DocumentId,
                    TransferTransitEntryId = entry.Id,
                    settlement.TransferOrderLineId,
                    settlement.SettlementType,
                    settlement.Quantity,
                    settlement.TotalValue,
                    settlement.Reason
                }));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            result = ToSettlementView(settlement);
        }, cancellationToken, async () =>
        {
            var committed = (await _settlementRepositoryOrThrow().FindAsync(settlement =>
                    settlement.TransferTransitEntryId == transitEntryId &&
                    settlement.IdempotencyKey == idempotencyKey, cancellationToken))
                .SingleOrDefault();
            return committed is not null && committed.RequestHash == requestHash;
        });

        return result ?? throw new InvalidOperationException("The committed transit settlement could not be read back.");
    }

    public async Task<TransferDispatchView?> GetDispatchByKeyAsync(
        int id,
        int lineId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0 || lineId <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        ValidateIdempotencyKey(idempotencyKey);
        return (await transitRepository.FindAsync(entry =>
                entry.TransferOrderId == id && entry.TransferOrderLineId == lineId &&
                entry.IdempotencyKey == idempotencyKey,
                cancellationToken))
            .Select(ToDispatchView)
            .SingleOrDefault();
    }

    private async Task<TransferOrderView> ToViewAsync(TransferOrder order, CancellationToken cancellationToken)
    {
        var identity = (await documentRepository.FindAsync(document => document.Id == order.DocumentId))
            .SingleOrDefault()
            ?? throw new InvalidOperationException("Transfer-order document identity not found.");
        var lines = (await lineRepository.FindAsync(line => line.TransferOrderId == order.Id))
            .OrderBy(line => line.Id)
            .ToArray();
        var settlements = _settlementRepository is null
            ? Array.Empty<TransferTransitSettlement>()
            : (await _settlementRepository.FindAsync(
                settlement => settlement.TransferOrderId == order.Id, cancellationToken)).ToArray();
        var settledByLine = settlements
            .Where(settlement => settlement.SettlementType != TransferTransitSettlementType.Returned)
            .GroupBy(settlement => settlement.TransferOrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(settlement => settlement.Quantity));
        return ToView(order, identity, lines, settledByLine);
    }

    private static TransferOrderView ToView(
        TransferOrder order,
        DocumentIdentity identity,
        IReadOnlyCollection<TransferOrderLine> lines,
        IReadOnlyDictionary<int, int>? settledByLine = null) =>
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
                line.ReservationSourceLineReference,
                line.DispatchedQuantity,
                settledByLine is not null && settledByLine.TryGetValue(line.Id, out var receivedQuantity)
                    ? receivedQuantity
                    : 0)).ToArray());

    private IRepository<TransferTransitSettlement> _settlementRepositoryOrThrow() =>
        _settlementRepository ?? throw new InvalidOperationException(
            "Transfer transit settlement persistence is not configured.");

    private static TransferTransitSettlementView ToSettlementView(TransferTransitSettlement settlement) =>
        new(
            settlement.Id,
            settlement.TransferTransitEntryId,
            settlement.TransferOrderId,
            settlement.TransferOrderLineId,
            settlement.SourceDocumentLineId.Value,
            settlement.CompanyId,
            settlement.ItemId,
            settlement.FromLocationId,
            settlement.ToLocationId,
            settlement.StockTransactionId,
            settlement.Quantity,
            settlement.SettlementType,
            settlement.BatchNumber,
            settlement.ExpiryDate,
            settlement.UnitCost,
            settlement.TotalValue,
            settlement.IdempotencyKey,
            settlement.SettledBy,
            settlement.SettledAt,
            settlement.Reason);

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

    private static DateTimeOffset NormalizeDatabaseTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private static decimal Round(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);

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
        IReadOnlyCollection<TransferOrderLineRequest> lines,
        IReadOnlyCollection<int> originalLineIds)
    {
        var order = (await orderRepository.FindAsync(order => order.Id == id)).SingleOrDefault();
        if (order is null || order.Status != TransferOrderStatus.Draft ||
            order.CompanyId != request.CompanyId || order.FromLocationId != request.FromLocationId ||
            order.ToLocationId != request.ToLocationId || order.Notes != NormalizeNotes(request.Notes))
            return false;

        var persistedLines = (await lineRepository.FindAsync(line => line.TransferOrderId == id))
            .ToDictionary(line => line.Id);
        if (persistedLines.Count != lines.Count)
            return false;

        var originalIds = originalLineIds.ToHashSet();
        var requestedExistingLines = lines
            .Where(line => line.LineId.HasValue)
            .ToDictionary(line => line.LineId!.Value);
        if (requestedExistingLines.Keys.Any(lineId => !originalIds.Contains(lineId)) ||
            originalIds.Except(requestedExistingLines.Keys).Any(persistedLines.ContainsKey))
            return false;

        foreach (var (lineId, input) in requestedExistingLines)
        {
            if (!persistedLines.TryGetValue(lineId, out var persistedLine) || !LineMatches(persistedLine, input))
                return false;
        }

        var unmatchedNewInputs = lines.Where(line => !line.LineId.HasValue).ToList();
        foreach (var newLine in persistedLines.Values.Where(line => !originalIds.Contains(line.Id)))
        {
            var matchIndex = unmatchedNewInputs.FindIndex(input => LineMatches(newLine, input));
            if (matchIndex < 0)
                return false;
            unmatchedNewInputs.RemoveAt(matchIndex);
        }

        return unmatchedNewInputs.Count == 0;
    }

    private static bool AmendmentMatches(
        TransferOrder order,
        IReadOnlyCollection<TransferOrderLine> existingLines,
        CreateTransferOrderRequest request,
        IReadOnlyCollection<TransferOrderLineRequest> lines) =>
        order.Notes == NormalizeNotes(request.Notes) &&
        lines.Count == existingLines.Count && lines.All(input =>
            input.LineId is int lineId && existingLines.Any(line =>
                line.Id == lineId && LineMatches(line, input)));

    private static bool LineMatches(TransferOrderLine line, TransferOrderLineRequest input) =>
        line.ItemId == input.ItemId && line.Quantity == input.Quantity &&
        line.BatchNumber == NormalizeBatchNumber(input.BatchNumber) &&
        line.ExpiryDate == StockLotExpiryDate.Normalize(input.ExpiryDate);

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

    private static string HashDispatchRequest(int orderId, int lineId, int quantity)
    {
        var payload = new StringBuilder();
        AppendField(payload, orderId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, lineId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, quantity.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static TransferDispatchView ToDispatchView(TransferTransitEntry entry) => new(
        entry.Id,
        entry.TransferOrderId,
        entry.TransferOrderLineId,
        entry.SourceDocumentLineId.Value,
        entry.CompanyId,
        entry.ItemId,
        entry.FromLocationId,
        entry.ToLocationId,
        entry.StockTransactionId,
        entry.Quantity,
        entry.BatchNumber,
        StockLotExpiryDate.Normalize(entry.ExpiryDate),
        entry.UnitCost,
        entry.TotalValue,
        entry.IdempotencyKey,
        entry.DispatchedBy,
        entry.DispatchedAt);

    private static string HashTransitSettlementRequest(
        int orderId,
        int lineId,
        int transitEntryId,
        TransferTransitSettlementRequest request)
    {
        var payload = new StringBuilder();
        AppendField(payload, orderId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, lineId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, transitEntryId.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, request.Quantity.ToString(CultureInfo.InvariantCulture));
        AppendField(payload, request.SettlementType.ToString());
        AppendField(payload, request.BatchNumber);
        AppendField(payload, request.ExpiryDate?.ToString("O", CultureInfo.InvariantCulture));
        AppendField(payload, request.Reason);
        AppendField(payload, request.Notes);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static string CreateTransitMovementReference(
        int transitEntryId,
        string idempotencyKey,
        TransferTransitSettlementType settlementType)
    {
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))[..24];
        return $"TransferTransit:{transitEntryId}:{settlementType}:{keyDigest}";
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
