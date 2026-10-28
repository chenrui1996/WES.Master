using System.Text.Json;

namespace Wes.Wms.Domain;

internal enum StockCountState { Draft, Counting, Ready, Completed, Invalidated }
internal enum StockCountAction { Create, Start, Record, Adjust, Invalidate }
internal sealed record StockCountLine(Guid Id, Guid BalanceId, Guid ItemId, Guid UnitId, string Batch, int Precision,
    Guid ContainerId, decimal BookQuantity, string BookVersion, decimal? Quantity = null, Guid? CountedBy = null,
    DateTimeOffset? CountedAt = null);

internal sealed class StockCount
{
    private StockCount() { }
    internal StockCount(Guid tenant, Guid creator, CountScope scope, DateTimeOffset at)
    {
        TenantId = tenant; Id = Guid.NewGuid(); CreatorId = creator; Code = scope.Code;
        WarehouseId = scope.Warehouse; LocationId = scope.Location; CreatedAt = at;
    }
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid CreatorId { get; private set; }
    public string Code { get; private set; } = "";
    public Guid WarehouseId { get; private set; }
    public Guid LocationId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public StockCountState State { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    public int Sequence { get; private set; }
    public Guid? Generation { get; private set; }
    public Guid? LockOwnerId { get; private set; }
    public DateTimeOffset? SnapshotAt { get; private set; }
    public string LinesJson { get; private set; } = "[]";
    internal StockCountLine[] Lines => JsonSerializer.Deserialize<StockCountLine[]>(LinesJson)
        ?? throw new InvalidOperationException("Count snapshot is corrupt.");
    internal bool Active => State is StockCountState.Counting or StockCountState.Ready;
    internal void Start(Guid actor, DateTimeOffset at, IEnumerable<StockBalance> balances)
    {
        Generation = Guid.NewGuid(); LockOwnerId = actor; SnapshotAt = at; State = StockCountState.Counting;
        LinesJson = JsonSerializer.Serialize(balances.OrderBy(row => row.Id).Select(row =>
            new StockCountLine(Guid.NewGuid(), row.Id, row.ItemId, row.UnitId, row.Batch, row.Precision,
                row.ContainerId, row.Quantity, row.Version)));
    }
    internal void Record(Guid line, decimal quantity, Guid actor, DateTimeOffset at)
    {
        var lines = Lines.Select(row => row.Id == line ? row with { Quantity = quantity, CountedBy = actor, CountedAt = at } : row).ToArray();
        LinesJson = JsonSerializer.Serialize(lines);
        State = lines.All(row => row.Quantity.HasValue) ? StockCountState.Ready : StockCountState.Counting;
    }
    internal void End(bool completed)
    {
        State = completed ? StockCountState.Completed : StockCountState.Invalidated;
        LockOwnerId = null;
        if (!completed)
            LinesJson = JsonSerializer.Serialize(Lines.Select(row => row with { Quantity = null, CountedBy = null, CountedAt = null }));
    }
    internal void Advance() { Version = Guid.NewGuid().ToString("D"); Sequence++; }
}

internal sealed record CountScope(string Code, Guid Warehouse, Guid Location);

internal sealed class StockCountFact
{
    private StockCountFact() { }
    internal StockCountFact(StockCount plan, Guid operation, Guid actor, Guid correlation, StockCountAction action,
        string input, DateTimeOffset at)
    {
        TenantId = plan.TenantId; Id = Guid.NewGuid(); PlanId = plan.Id; OperationId = operation; ActorId = actor;
        CorrelationId = correlation; Action = action; Input = input; AfterVersion = plan.Version;
        Sequence = plan.Sequence; Generation = plan.Generation; State = plan.State; LinesJson = plan.LinesJson; OccurredAt = at;
    }
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid PlanId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public StockCountAction Action { get; private set; }
    public string Input { get; private set; } = "";
    public string AfterVersion { get; private set; } = "";
    public int Sequence { get; private set; }
    public Guid? Generation { get; private set; }
    public StockCountState State { get; private set; }
    public string LinesJson { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}
