namespace Wes.Wms.Domain;

/// <summary>Permanent logical location transition; never an inventory or device-control fact.</summary>
internal sealed class LocationStateChange
{
    private LocationStateChange() { }
    internal LocationStateChange(Guid operation, Guid actor, Guid correlation, WarehouseLocation location,
        string beforeVersion, bool beforeEnabled, string reason, DateTimeOffset now)
    {
        if (new[] { operation, actor, correlation }.Contains(Guid.Empty)
            || !Guid.TryParseExact(beforeVersion, "D", out var version) || version == Guid.Empty
            || beforeVersion == location.Version || beforeEnabled == location.Enabled || !WarehouseStateChange.ValidReason(reason))
            throw new ArgumentException("A location transition requires complete identities and an exact prior state with bounded reason.");
        TenantId = location.TenantId; LocationId = location.Id; WarehouseId = location.WarehouseId;
        OperationId = operation; ActorId = actor; CorrelationId = correlation;
        BeforeVersion = beforeVersion; BeforeEnabled = beforeEnabled;
        AfterVersion = location.Version; Enabled = location.Enabled; Reason = reason; OccurredAt = now.ToUniversalTime();
    }

    public Guid TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid LocationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string BeforeVersion { get; private set; } = "";
    public bool BeforeEnabled { get; private set; }
    public string AfterVersion { get; private set; } = "";
    public bool Enabled { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}
