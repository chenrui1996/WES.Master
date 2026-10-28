using System.Text.Json;

namespace Wes.Wms.Domain;

internal enum TransferState { Draft, Approved, Executing, Completed, Cancelled, ClosedPartial }
internal enum TransferFactKind { Created, Approved, Dispatched, Arrived, Synced, Unknown, Closed }
internal enum TransferExecutionStatus { Unstarted, InTransit, Arrived, Unknown, Closed }
internal enum TransferPostingStatus { None, Pending, Synced, Failed }
internal sealed record TransferPlanLine(Guid Id, Guid ExecutionId, Guid SourceBalanceId, Guid TargetBalanceId,
    Guid ItemId, Guid UnitId, string Batch, int Precision, Guid ContainerId, Guid SourceLocationId, Guid TargetLocationId,
    int Branch, decimal PlannedQuantity);

internal sealed class TransferDocument
{
    private TransferDocument() { }
    internal TransferDocument(Guid tenant, Guid operation, Guid actor, Guid warehouse, string code,
        IReadOnlyList<TransferPlanLine> lines, DateTimeOffset now)
    {
        if (new[] { tenant, operation, actor, warehouse }.Contains(Guid.Empty)
            || lines.Count is < 1 or > 100 || lines.Select(row => row.Id).Distinct().Count() != lines.Count
            || lines.Select(row => row.ExecutionId).Distinct().Count() != lines.Count
            || lines.Select(row => row.ContainerId).Distinct().Count() != lines.Count
            || lines.Select(row => row.TargetLocationId).Distinct().Count() != lines.Count
            || lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }).Distinct().Count() != lines.Count * 2
            || lines.Any(row => new[] { row.Id, row.ExecutionId, row.SourceBalanceId, row.TargetBalanceId, row.ItemId,
                    row.UnitId, row.ContainerId, row.SourceLocationId, row.TargetLocationId }.Contains(Guid.Empty)
                || row.SourceLocationId == row.TargetLocationId || row.Branch is < 0 or > 1 || row.PlannedQuantity <= 0m
                || row.PlannedQuantity > StockMovementQuantity.MaximumValue
                || !QuantityPrecision.Create(row.Precision).CanRepresent(row.PlannedQuantity)))
            throw new ArgumentException("Transfers require complete trusted identities and unique legal whole-unit plans.");
        TenantId = tenant; Id = Guid.NewGuid(); CreationOperationId = operation; CreatorId = actor; WarehouseId = warehouse;
        Code = MasterDataValue.Code(code); Plan = JsonSerializer.Serialize(lines); CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid CreationOperationId { get; private set; }
    public Guid CreatorId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Code { get; private set; } = "";
    public string Plan { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public TransferState State { get; private set; }
    public int Sequence { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    internal IReadOnlyList<TransferPlanLine> Lines => JsonSerializer.Deserialize<TransferPlanLine[]>(Plan)
        ?? throw new InvalidOperationException("Transfer plan is absent.");
    internal void Advance(TransferState state)
    {
        Sequence = checked(Sequence + 1); State = state; Version = Guid.NewGuid().ToString("D");
    }
}

internal sealed class TransferFact
{
    private TransferFact() { }
    internal TransferFact(TransferDocument document, Guid operation, Guid actor, Guid correlation, TransferFactKind kind,
        Guid line, string input, DateTimeOffset now)
    {
        if (new[] { operation, actor, correlation }.Contains(Guid.Empty) || !Enum.IsDefined(kind)
            || string.IsNullOrEmpty(input) || input.Length > 65536
            || (kind is TransferFactKind.Created or TransferFactKind.Approved or TransferFactKind.Closed) != (line == Guid.Empty))
            throw new ArgumentException("Transfer facts require bounded immutable input and complete operation provenance.");
        TenantId = document.TenantId; OrderId = document.Id; OperationId = operation; ActorId = actor;
        CorrelationId = correlation; Kind = kind; LineId = line; Input = input; Sequence = document.Sequence;
        AfterVersion = document.Version; State = document.State; CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public TransferFactKind Kind { get; private set; }
    public Guid LineId { get; private set; }
    public string Input { get; private set; } = "";
    public int Sequence { get; private set; }
    public string AfterVersion { get; private set; } = "";
    public TransferState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

internal sealed record TransferProgress(TransferExecutionStatus Execution, TransferPostingStatus Posting)
{
    internal static TransferProgress For(IReadOnlyList<TransferFact> facts, Guid line)
    {
        var execution = TransferExecutionStatus.Unstarted;
        var posting = TransferPostingStatus.None;
        foreach (var fact in facts.OrderBy(row => row.Sequence))
        {
            if (fact.Kind == TransferFactKind.Closed && execution == TransferExecutionStatus.Unstarted)
                execution = TransferExecutionStatus.Closed;
            if (fact.LineId != line) continue;
            switch (fact.Kind)
            {
                case TransferFactKind.Dispatched when execution == TransferExecutionStatus.Unstarted:
                    execution = TransferExecutionStatus.InTransit; break;
                case TransferFactKind.Arrived when execution == TransferExecutionStatus.InTransit:
                    execution = TransferExecutionStatus.Arrived; posting = TransferPostingStatus.Pending; break;
                case TransferFactKind.Unknown when execution == TransferExecutionStatus.InTransit:
                    execution = TransferExecutionStatus.Unknown; break;
                case TransferFactKind.Synced when execution == TransferExecutionStatus.Arrived && posting == TransferPostingStatus.Pending:
                    posting = TransferPostingStatus.Synced; break;
                default: throw new InvalidOperationException("Transfer execution history is inconsistent.");
            }
        }
        return new(execution, posting);
    }

    internal static void Validate(TransferDocument document, IReadOnlyList<TransferFact> facts)
    {
        var ordered = facts.OrderBy(row => row.Sequence).ToArray();
        if (ordered.Length != document.Sequence || ordered.Length == 0 || ordered[0].Kind != TransferFactKind.Created
            || ordered[0].OperationId != document.CreationOperationId
            || ordered.Where((row, index) => row.Sequence != index + 1 || row.OrderId != document.Id
                || row.TenantId != document.TenantId).Any()
            || ordered[^1].AfterVersion != document.Version || ordered[^1].State != document.State)
            throw new InvalidOperationException("Transfer state must match its permanent history.");
        foreach (var line in document.Lines) _ = For(ordered, line.Id);
    }
}
