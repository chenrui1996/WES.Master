namespace Wes.Wms.Features.MasterData;

/// <summary>Original logical-state operation and expected warehouse version; authority never comes from this input.</summary>
public sealed record WarehouseStateCommand(Guid OperationId, string ExpectedVersion, bool Enabled, string Reason);

/// <summary>Only Changed or Replayed carries the original permanent receipt.</summary>
public enum WarehouseStateDecision
{
    /// <summary>The logical state transition committed.</summary>
    Changed,
    /// <summary>The exact original operation already committed.</summary>
    Replayed,
    /// <summary>Current trusted subject or complete fields do not authorize the resource.</summary>
    Forbidden,
    /// <summary>Input is malformed or unbounded.</summary>
    Invalid,
    /// <summary>The resource and its creation provenance do not exist in this tenant.</summary>
    NotFound,
    /// <summary>The original operation differs, version changed or target state is already current.</summary>
    Conflict,
    /// <summary>Enabled child locations, stock, reservations or active inbound prevent disabling.</summary>
    Blocked
}

/// <summary>Permanent original transition version, not a claim about the current state or physical readiness.</summary>
public sealed record WarehouseStateResult(WarehouseStateDecision Decision, Guid? Id = null, string? Version = null);
