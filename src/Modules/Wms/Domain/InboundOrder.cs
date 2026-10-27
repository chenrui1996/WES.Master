namespace Wes.Wms.Domain;

internal enum InboundOrderState { Draft, Approved, Executing, Completed }

internal sealed record InboundPlanLine(Guid Id, Guid ItemId, Guid UnitId, string Batch, int Precision, decimal PlannedQuantity);

internal sealed record InboundReceiptCommand(Guid OperationId, Guid ActorId, Guid ExecutionId,
    Guid LineId, Guid ItemId, Guid UnitId, string Batch, Guid ContainerId,
    Guid ReceiptLocationId, Guid TargetLocationId, decimal Quantity);

/// <summary>Owner-local received unit; this domain fact must commit with its ledger entry, never before it.</summary>
internal sealed class InboundReceipt(InboundReceiptCommand command)
{
    internal Guid Id { get; } = command.ExecutionId;
    internal InboundReceiptCommand Original { get; } = command;
    internal Guid ContainerId => Original.ContainerId;
    internal bool RequiresTransport => Original.ReceiptLocationId != Original.TargetLocationId;
    internal Guid? TransportId { get; private set; }
    internal bool Arrived { get; private set; } = command.ReceiptLocationId == command.TargetLocationId;

    internal void LinkTransport(Guid transport)
    {
        if (!RequiresTransport || transport == Guid.Empty || TransportId.HasValue && TransportId != transport)
            throw new InvalidOperationException("Receipt requires one fixed transport identity for a different target.");
        TransportId = transport;
    }

    internal bool ApplyArrival(Guid transport, Guid container, Guid source, Guid target)
    {
        if (!RequiresTransport || !TransportId.HasValue || TransportId != transport || ContainerId != container
            || Original.ReceiptLocationId != source || Original.TargetLocationId != target)
            throw new InvalidOperationException("Arrival does not match the linked transport, container and exact locations.");
        if (Arrived) return false;
        Arrived = true;
        return true;
    }
}

/// <summary>
/// Internal inbound state machine. Persistence, current authorization and ledger atomicity belong to its application owner.
/// </summary>
internal sealed class InboundOrder
{
    private readonly List<InboundReceipt> _receipts = [];
    internal InboundOrder(Guid tenant, Guid warehouse, string code, IEnumerable<InboundPlanLine> lines)
    {
        if (tenant == Guid.Empty || warehouse == Guid.Empty)
            throw new ArgumentException("Inbound orders require an explicit tenant and warehouse.");
        ArgumentNullException.ThrowIfNull(lines);
        var plan = lines.Take(101).ToArray();
        if (plan.Length is < 1 or > 100 || plan.Select(line => line.Id).Distinct().Count() != plan.Length)
            throw new ArgumentException("Inbound orders require one to one hundred uniquely identified lines.");
        foreach (var line in plan)
        {
            if (new[] { line.Id, line.ItemId, line.UnitId }.Contains(Guid.Empty)
                || !ValidBatch(line.Batch) || line.PlannedQuantity <= 0m
                || !QuantityPrecision.Create(line.Precision).CanRepresent(line.PlannedQuantity))
                throw new ArgumentException("Inbound plans require exact dimensions and positive representable quantities.");
            _ = InventoryQuantity.Create(line.PlannedQuantity);
        }
        TenantId = tenant; WarehouseId = warehouse; Code = MasterDataValue.Code(code);
        Lines = Array.AsReadOnly(plan);
        Receipts = _receipts.AsReadOnly();
    }

    internal Guid Id { get; } = Guid.NewGuid();
    internal Guid TenantId { get; }
    internal Guid WarehouseId { get; }
    internal string Code { get; }
    internal string Version { get; private set; } = Guid.NewGuid().ToString("D");
    internal InboundOrderState State { get; private set; }
    internal IReadOnlyList<InboundPlanLine> Lines { get; private set; }
    internal IReadOnlyList<InboundReceipt> Receipts { get; }

    internal void Revise(IReadOnlyList<InboundPlanLine> lines)
    {
        if (State != InboundOrderState.Draft)
            throw new InvalidOperationException("Only a draft inbound order can be revised.");
        Lines = new InboundOrder(TenantId, WarehouseId, Code, lines).Lines;
        Touch();
    }

    internal void Approve()
    {
        if (State != InboundOrderState.Draft)
            throw new InvalidOperationException("Only a draft inbound order can be approved.");
        State = InboundOrderState.Approved; Touch();
    }

    internal decimal Remaining(Guid lineId)
    {
        var line = Lines.SingleOrDefault(item => item.Id == lineId)
            ?? throw new ArgumentException("The inbound line does not belong to this order.");
        return line.PlannedQuantity - _receipts.Where(item => item.Original.LineId == lineId).Sum(item => item.Original.Quantity);
    }

    internal InboundReceipt Receive(InboundReceiptCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (new[] { command.OperationId, command.ActorId, command.ExecutionId, command.LineId,
            command.ItemId, command.UnitId, command.ContainerId, command.ReceiptLocationId, command.TargetLocationId }.Contains(Guid.Empty))
            throw new ArgumentException("Receipt requires the complete original nonempty identity tuple.");
        var existing = _receipts.Where(item => item.Original.OperationId == command.OperationId
            || item.Original.ExecutionId == command.ExecutionId).ToArray();
        if (existing.Length > 0)
        {
            // Execution deduplication survives a different operation key, but never changes any business semantics.
            if (existing.Length != 1 || existing[0].Original with { OperationId = command.OperationId } != command)
                throw new InvalidOperationException("Receipt identity conflicts with the original operation.");
            return existing[0];
        }
        if (State is not (InboundOrderState.Approved or InboundOrderState.Executing))
            throw new InvalidOperationException("Only an approved open inbound order accepts new receipts.");
        var line = Lines.SingleOrDefault(item => item.Id == command.LineId)
            ?? throw new ArgumentException("The inbound line does not belong to this order.");
        if (command.ItemId != line.ItemId || command.UnitId != line.UnitId || command.Batch != line.Batch
            || command.Quantity > Remaining(line.Id))
            throw new ArgumentException("Receipt dimensions must match and zero overreceipt tolerance applies.");
        _ = StockMovementQuantity.Create(command.Quantity, QuantityPrecision.Create(line.Precision));
        var receipt = new InboundReceipt(command);
        _receipts.Add(receipt);
        Recalculate(); Touch();
        return receipt;
    }

    internal void LinkTransport(Guid receiptId, Guid transportId)
    {
        var receipt = Find(receiptId);
        var original = receipt.TransportId;
        receipt.LinkTransport(transportId);
        if (original != receipt.TransportId) Touch();
    }

    internal void ApplyArrival(Guid receiptId, Guid transportId, Guid containerId, Guid sourceLocationId, Guid targetLocationId)
    {
        if (!Find(receiptId).ApplyArrival(transportId, containerId, sourceLocationId, targetLocationId)) return;
        Recalculate(); Touch();
    }

    private InboundReceipt Find(Guid id) => _receipts.SingleOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("The receipt does not belong to this inbound order.");
    private void Recalculate() => State = Lines.All(line => Remaining(line.Id) == 0m) && _receipts.All(receipt => receipt.Arrived)
        ? InboundOrderState.Completed : InboundOrderState.Executing;
    private void Touch() => Version = Guid.NewGuid().ToString("D");
    private static bool ValidBatch(string? batch) => batch is { Length: <= 128 }
        && batch == batch.Trim() && !batch.Any(char.IsControl);
}
