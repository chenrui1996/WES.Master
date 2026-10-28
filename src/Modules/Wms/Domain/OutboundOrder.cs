using System.Text.Json;

namespace Wes.Wms.Domain;

internal enum OutboundOrderState { Draft, Approved, Executing, Completed, Cancelled, ClosedPartial }
internal enum OutboundBranch { DirectShipment, RequiresTransport }
internal enum OutboundFactKind { Created, Approved, Picked, ShortageReleased, Shipped, Cancelled, ClosedPartial }

internal sealed record OutboundPlanLine(Guid Id, Guid ExecutionId, Guid BalanceId, Guid ItemId, Guid UnitId, string Batch,
    int Precision, Guid ContainerId, Guid LocationId, OutboundBranch Branch, decimal PlannedQuantity);
internal sealed record OutboundDraftPlanLine(Guid Id, Guid BalanceId, string Quantity, int Branch);
internal sealed record OutboundPickInput(string ExpectedVersion, Guid LineId, string PickedQuantity, string ShortageQuantity, string Reason);
internal sealed record OutboundReleaseInput(string ExpectedVersion, Guid LineId, string Quantity, string Reason);
internal sealed record OutboundShipInput(string ExpectedVersion, Guid LineId, string Quantity);
internal sealed record OutboundStateInput(string ExpectedVersion, string Reason);
internal sealed record OutboundApprovalInput(string ExpectedVersion, IReadOnlyList<OutboundBalanceVersion> Balances);
internal sealed record OutboundBalanceVersion(Guid BalanceId, string ExpectedVersion);

/// <summary>Immutable WMS-owned outbound plan; progress is reconstructed from retained operation facts.</summary>
internal sealed class OutboundDocument
{
    private OutboundDocument() { }
    internal OutboundDocument(Guid tenant, Guid operation, Guid actor, Guid warehouse, string code,
        IReadOnlyList<OutboundPlanLine> lines, DateTimeOffset now)
    {
        if (new[] { tenant, operation, actor, warehouse }.Contains(Guid.Empty))
            throw new ArgumentException("Outbound plans require complete trusted identities.");
        var plan = lines.Take(101).ToArray();
        if (plan.Length is < 1 or > 100 || plan.Select(row => row.Id).Distinct().Count() != plan.Length
            || plan.Select(row => row.BalanceId).Distinct().Count() != plan.Length)
            throw new ArgumentException("Outbound plans require one to one hundred unique lines and source balances.");
        foreach (var line in plan)
        {
            if (new[] { line.Id, line.ExecutionId, line.BalanceId, line.ItemId, line.UnitId, line.ContainerId, line.LocationId }
                    .Contains(Guid.Empty)
                || line.Branch is not (OutboundBranch.DirectShipment or OutboundBranch.RequiresTransport)
                || line.PlannedQuantity <= 0m || line.PlannedQuantity > StockMovementQuantity.MaximumValue
                || !QuantityPrecision.Create(line.Precision).CanRepresent(line.PlannedQuantity))
                throw new ArgumentException("Outbound lines require legal dimensions and positive representable quantities.");
            _ = InventoryQuantity.Create(line.PlannedQuantity);
        }
        Id = Guid.NewGuid(); TenantId = tenant; CreationOperationId = operation; CreatorId = actor; WarehouseId = warehouse;
        Code = MasterDataValue.Code(code); Plan = JsonSerializer.Serialize(plan); CreatedAt = now.ToUniversalTime();
    }

    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid CreationOperationId { get; private set; }
    public Guid CreatorId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Code { get; private set; } = "";
    public string Plan { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public OutboundOrderState State { get; private set; }
    public int Sequence { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");

    internal IReadOnlyList<OutboundPlanLine> Lines => JsonSerializer.Deserialize<OutboundPlanLine[]>(Plan)
        ?? throw new InvalidOperationException("The persisted outbound plan is missing.");

    internal void Advance(OutboundOrderState state)
    {
        if (Sequence == int.MaxValue) throw new InvalidOperationException("Outbound fact sequence exhausted.");
        Sequence++; State = state; Version = Guid.NewGuid().ToString("D");
    }
}

/// <summary>Append-only operation fact; the canonical original input and resulting version are never rewritten.</summary>
internal sealed class OutboundFact
{
    internal const int MaximumInputLength = 65536;
    private OutboundFact() { }
    internal OutboundFact(OutboundDocument document, Guid operation, Guid actor, Guid correlation, OutboundFactKind kind,
        string input, DateTimeOffset now)
    {
        if (new[] { operation, actor, correlation }.Contains(Guid.Empty) || string.IsNullOrEmpty(input) || input.Length > MaximumInputLength)
            throw new ArgumentException("Outbound facts require bounded original input and trusted identities.");
        TenantId = document.TenantId; OrderId = document.Id; Sequence = document.Sequence; OperationId = operation;
        ActorId = actor; CorrelationId = correlation; Kind = kind; Input = input; AfterVersion = document.Version;
        CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public int Sequence { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public OutboundFactKind Kind { get; private set; }
    public string Input { get; private set; } = "";
    public string AfterVersion { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}

internal static class OutboundHistory
{
    internal static void Validate(OutboundDocument document, IReadOnlyList<OutboundFact> facts)
    {
        var ordered = facts.OrderBy(row => row.Sequence).ToArray();
        if (ordered.Length != document.Sequence || ordered.Where((row, index) =>
                row.OrderId != document.Id || row.TenantId != document.TenantId || row.Sequence != index + 1
                || index == ordered.Length - 1 && row.AfterVersion != document.Version).Any())
            throw new InvalidOperationException("Outbound progress does not match its immutable history.");
        if (ordered.Length == 0 || ordered[0].Kind != OutboundFactKind.Created)
            throw new InvalidOperationException("Outbound history must begin with its original creation fact.");
        var plan = document.Lines;
        var shipped = plan.ToDictionary(line => line.Id, _ => 0m);
        var state = OutboundOrderState.Draft;
        foreach (var fact in ordered)
        {
            switch (fact.Kind)
            {
                case OutboundFactKind.Created when fact.Sequence == 1:
                    break;
                case OutboundFactKind.Approved when state == OutboundOrderState.Draft:
                    state = OutboundOrderState.Approved;
                    break;
                case OutboundFactKind.Picked when state is OutboundOrderState.Approved or OutboundOrderState.Executing:
                    _ = JsonSerializer.Deserialize<OutboundPickInput>(fact.Input)
                        ?? throw new InvalidOperationException("Outbound pick history is invalid.");
                    state = OutboundOrderState.Executing;
                    break;
                case OutboundFactKind.ShortageReleased when state is OutboundOrderState.Approved or OutboundOrderState.Executing:
                    _ = JsonSerializer.Deserialize<OutboundReleaseInput>(fact.Input)
                        ?? throw new InvalidOperationException("Outbound shortage history is invalid.");
                    break;
                case OutboundFactKind.Shipped when state is OutboundOrderState.Approved or OutboundOrderState.Executing:
                    var shipment = JsonSerializer.Deserialize<OutboundShipInput>(fact.Input)
                        ?? throw new InvalidOperationException("Outbound shipment history is invalid.");
                    if (!shipped.ContainsKey(shipment.LineId)
                        || !decimal.TryParse(shipment.Quantity, System.Globalization.NumberStyles.AllowDecimalPoint,
                            System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0m)
                        throw new InvalidOperationException("Outbound shipment history has an invalid line or quantity.");
                    shipped[shipment.LineId] += quantity;
                    state = shipped.All(row => row.Value == plan.Single(line => line.Id == row.Key).PlannedQuantity)
                        ? OutboundOrderState.Completed : OutboundOrderState.Executing;
                    break;
                case OutboundFactKind.Cancelled when state is OutboundOrderState.Draft or OutboundOrderState.Approved or OutboundOrderState.Executing:
                    state = OutboundOrderState.Cancelled;
                    break;
                case OutboundFactKind.ClosedPartial when state == OutboundOrderState.Executing:
                    state = OutboundOrderState.ClosedPartial;
                    break;
                default:
                    throw new InvalidOperationException("Outbound history contains an illegal state transition.");
            }
        }
        if (document.State != state)
            throw new InvalidOperationException("Outbound state does not match its immutable operation history.");
    }
}
