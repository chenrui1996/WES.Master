using System.Text.Json;

namespace Wes.Wms.Domain;

/// <summary>WMS-owned durable header and plan; all progress requires a matching immutable local fact.</summary>
internal sealed class InboundDocument
{
    private InboundDocument() { }
    internal InboundDocument(Guid tenant, Guid operation, Guid actor, Guid warehouse, string code,
        IReadOnlyList<InboundPlanLine> lines, DateTimeOffset now)
    {
        if (operation == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("A trusted original creation is required.");
        var domain = new InboundOrder(tenant, warehouse, code, lines);
        Id = domain.Id; TenantId = tenant; CreationOperationId = operation; CreatorId = actor;
        WarehouseId = warehouse; Code = domain.Code; Plan = JsonSerializer.Serialize(domain.Lines);
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid CreationOperationId { get; private set; }
    public Guid CreatorId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Code { get; private set; } = "";
    public string Plan { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public InboundOrderState State { get; private set; }
    public int Sequence { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");

    internal InboundOrder Restore(IReadOnlyList<InboundFact> facts)
    {
        var lines = JsonSerializer.Deserialize<InboundPlanLine[]>(Plan)
            ?? throw new InvalidOperationException("The persisted inbound plan is missing.");
        var order = new InboundOrder(TenantId, WarehouseId, Code, lines);
        var expected = 0;
        string? precedingVersion = null;
        foreach (var fact in facts)
        {
            if (fact.OrderId != Id || fact.TenantId != TenantId || fact.Sequence != ++expected)
                throw new InvalidOperationException("Inbound history is incomplete or belongs to another order.");
            switch (fact.Kind)
            {
                case InboundFactKind.Revised:
                    var revision = fact.Revision();
                    if (precedingVersion is not null && revision.ExpectedVersion != precedingVersion)
                        throw new InvalidOperationException("Inbound revision does not follow its original version.");
                    order.Revise(revision.Lines);
                    break;
                case InboundFactKind.Approved:
                    order.Approve();
                    break;
                case InboundFactKind.Received:
                    order.Receive(fact.Receipt());
                    break;
                default:
                    throw new InvalidOperationException("Unsupported inbound history fact.");
            }
            precedingVersion = fact.AfterVersion;
        }
        if (expected != Sequence || order.State != State || (facts.Count > 0 && facts[^1].AfterVersion != Version))
            throw new InvalidOperationException("Inbound progress does not match its immutable history.");
        return order;
    }

    internal void Advance(InboundOrderState state)
    {
        if (Sequence == int.MaxValue) throw new InvalidOperationException("Inbound fact sequence exhausted.");
        Sequence++; State = state; Version = Guid.NewGuid().ToString("D");
    }
}

internal enum InboundFactKind { Approved, Received, Revised }

internal sealed record InboundPlanRevision(string ExpectedVersion, IReadOnlyList<InboundPlanLine> Lines);

/// <summary>Permanent actor/operation receipt; a received fact also retains the unique pending putaway tuple.</summary>
internal sealed class InboundFact
{
    private InboundFact() { }
    internal InboundFact(InboundDocument document, Guid operation, Guid actor, Guid correlation,
        InboundFactKind kind, DateTimeOffset now, InboundReceiptCommand? receipt = null, Guid? posting = null,
        InboundPlanRevision? revision = null)
    {
        if (new[] { operation, actor, correlation }.Contains(Guid.Empty))
            throw new ArgumentException("Inbound facts require complete trusted operation identities.");
        if (kind is not (InboundFactKind.Approved or InboundFactKind.Received or InboundFactKind.Revised)
            || (kind == InboundFactKind.Approved && (receipt is not null || posting is not null
                || document.State != InboundOrderState.Approved || document.Sequence < 1)))
            throw new ArgumentException("An approval requires an approved transition without a receipt or posting.");
        if (kind == InboundFactKind.Revised
            ? revision is null || receipt is not null || posting is not null || document.State != InboundOrderState.Draft
                || document.Sequence < 1 || !Guid.TryParseExact(revision.ExpectedVersion, "D", out var version) || version == Guid.Empty
            : revision is not null)
            throw new ArgumentException("A revision requires an exact original version and draft-only plan.");
        if (kind == InboundFactKind.Received && (receipt is null || posting is null
            || receipt.OperationId != operation || receipt.ActorId != actor))
            throw new ArgumentException("Receipt fact must match its original command and ledger posting.");
        TenantId = document.TenantId; OrderId = document.Id; Sequence = document.Sequence;
        OperationId = operation; ActorId = actor; CorrelationId = correlation; Kind = kind; OccurredAt = now;
        Payload = revision is not null ? JsonSerializer.Serialize(revision) : receipt is null ? "" : JsonSerializer.Serialize(receipt);
        ExecutionId = receipt?.ExecutionId; PostingId = posting; AfterVersion = document.Version;
    }

    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public int Sequence { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public InboundFactKind Kind { get; private set; }
    public string Payload { get; private set; } = "";
    public Guid? ExecutionId { get; private set; }
    public Guid? PostingId { get; private set; }
    public string AfterVersion { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }

    internal InboundPlanRevision Revision()
    {
        if (Kind != InboundFactKind.Revised || ExecutionId is not null || PostingId is not null)
            throw new InvalidOperationException("This fact is not a plan-only revision.");
        var revision = JsonSerializer.Deserialize<InboundPlanRevision>(Payload)
            ?? throw new InvalidOperationException("The persisted inbound revision is missing.");
        if (!Guid.TryParseExact(revision.ExpectedVersion, "D", out var version) || version == Guid.Empty || revision.Lines is null)
            throw new InvalidOperationException("The persisted inbound revision is invalid.");
        return revision;
    }

    internal InboundReceiptCommand Receipt()
    {
        if (Kind != InboundFactKind.Received) throw new InvalidOperationException("This fact is not a receipt.");
        var command = JsonSerializer.Deserialize<InboundReceiptCommand>(Payload)
            ?? throw new InvalidOperationException("The persisted inbound receipt tuple is missing.");
        if (command.OperationId != OperationId || command.ActorId != ActorId || command.ExecutionId != ExecutionId
            || PostingId is null || PostingId == Guid.Empty)
            throw new InvalidOperationException("The persisted inbound receipt does not match its immutable fact.");
        return command;
    }
}
