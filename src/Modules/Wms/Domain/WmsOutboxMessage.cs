using System.Text.Json;

namespace Wes.Wms.Domain;

/// <summary>Initial owner-local staging state; no transport or publisher transitions are implemented by this slice.</summary>
public enum WmsOutboxStatus
{
    /// <summary>The source fact committed and still requires a separately approved publisher.</summary>
    Pending
}

/// <summary>Immutable safe event staged atomically with a new WMS master-data creation fact.</summary>
public sealed class WmsOutboxMessage
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);
    private WmsOutboxMessage() { }

    internal WmsOutboxMessage(MasterDataChange change)
    {
        TenantId = change.TenantId; EventId = change.OperationId;
        ContractName = "wms.master-data.created"; SchemaVersion = 1; Owner = "Wms";
        AggregateId = change.LocationId ?? change.WarehouseId; AggregateVersion = change.AfterVersion;
        CorrelationId = change.CorrelationId; OccurredAt = change.OccurredAt;
        Payload = JsonSerializer.Serialize(new CreationPayload(change.LocationId.HasValue ? "Location" : "Warehouse",
            change.WarehouseId, change.LocationId, change.PackId, change.SchemaVersion, change.Code, false), PayloadOptions);
        Status = WmsOutboxStatus.Pending;
    }

    /// <summary>Trusted WMS tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable event identity equal to its one-resource source operation identity.</summary>
    public Guid EventId { get; private set; }
    /// <summary>Allowlisted nonphysical event contract.</summary>
    public string ContractName { get; private set; } = "";
    /// <summary>Payload contract version, initially one.</summary>
    public int SchemaVersion { get; private set; }
    /// <summary>Producer owner, always Wms.</summary>
    public string Owner { get; private set; } = "";
    /// <summary>Created warehouse or location identity, never an inventory authority grant.</summary>
    public Guid AggregateId { get; private set; }
    /// <summary>Exact source creation version.</summary>
    public string AggregateVersion { get; private set; } = "";
    /// <summary>Original safe request correlation.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>No predecessor event for this direct creation command.</summary>
    public Guid? CausationId { get; private set; }
    /// <summary>Original UTC fact time, not publisher time.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    /// <summary>Fixed safe identity/schema/code projection without actor, session, name or device instructions.</summary>
    public string Payload { get; private set; } = "";
    /// <summary>Pending only until a future owner publisher is separately implemented.</summary>
    public WmsOutboxStatus Status { get; private set; }

    internal bool Matches(MasterDataChange change)
    {
        var expected = new WmsOutboxMessage(change);
        return TenantId == expected.TenantId && EventId == expected.EventId && ContractName == expected.ContractName
            && SchemaVersion == expected.SchemaVersion && Owner == expected.Owner && AggregateId == expected.AggregateId
            && AggregateVersion == expected.AggregateVersion && CorrelationId == expected.CorrelationId
            && CausationId is null && OccurredAt == expected.OccurredAt && Payload == expected.Payload
            && Status == WmsOutboxStatus.Pending;
    }

    private sealed record CreationPayload(string Kind, Guid WarehouseId, Guid? LocationId, Guid? PackId,
        string? SchemaVersion, string Code, bool Enabled);
}
