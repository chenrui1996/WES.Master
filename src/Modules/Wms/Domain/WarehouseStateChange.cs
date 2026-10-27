namespace Wes.Wms.Domain;

/// <summary>Permanent logical-state operation receipt; never an inventory, child-location or physical-control fact.</summary>
internal sealed class WarehouseStateChange
{
    private WarehouseStateChange() { }
    internal WarehouseStateChange(Guid operation, Guid actor, Guid correlation, Warehouse warehouse,
        string beforeVersion, bool beforeEnabled, string reason, DateTimeOffset now)
    {
        if (new[] { operation, actor, correlation }.Contains(Guid.Empty)
            || !Guid.TryParseExact(beforeVersion, "D", out var version) || version == Guid.Empty
            || beforeVersion == warehouse.Version || beforeEnabled == warehouse.Enabled || !ValidReason(reason))
            throw new ArgumentException("A warehouse state fact requires its exact prior state, identities and bounded reason.");
        TenantId = warehouse.TenantId; WarehouseId = warehouse.Id;
        OperationId = operation; ActorId = actor; CorrelationId = correlation;
        BeforeVersion = beforeVersion; BeforeEnabled = beforeEnabled;
        AfterVersion = warehouse.Version; Enabled = warehouse.Enabled; Reason = reason; OccurredAt = now.ToUniversalTime();
    }

    public Guid TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string BeforeVersion { get; private set; } = "";
    public bool BeforeEnabled { get; private set; }
    public string AfterVersion { get; private set; } = "";
    public bool Enabled { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }

    internal static bool ValidReason(string? reason) => reason is { Length: > 0 and <= 256 }
        && reason == reason.Trim() && !reason.Any(char.IsControl);
}
