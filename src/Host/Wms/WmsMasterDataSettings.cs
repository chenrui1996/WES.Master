namespace Wes.Host.Wms;

/// <summary>Default-disabled deployment assembly; independent write toggles never imply inventory or physical-control capability.</summary>
public sealed record WmsMasterDataSettings(bool Enabled, bool WarehouseCreationEnabled = false, bool LocationCreationEnabled = false,
    bool InboundDraftCreationEnabled = false, bool InboundDraftRevisionEnabled = false, bool WarehouseStateEnabled = false,
    bool LocationStateEnabled = false, bool StockResourceRegistrationEnabled = false, bool InboundApprovalEnabled = false,
    bool InboundReceiptEnabled = false, bool OutboundEnabled = false, bool CountsEnabled = false)
{
    /// <summary>Rejects malformed supplied toggles and requires authenticated identity when explicitly enabled.</summary>
    public static WmsMasterDataSettings Load(IConfiguration configuration)
    {
        var value = configuration["Wes:WmsMasterData:Enabled"];
        var enabled = false;
        if (value is not null && !bool.TryParse(value, out enabled))
            throw new InvalidOperationException("WMS master-data deployment configuration is invalid.");
        if (enabled && (!bool.TryParse(configuration["Wes:Identity:Enabled"], out var identity) || !identity))
            throw new InvalidOperationException("WMS master-data reads require identity deployment.");
        var creationValue = configuration["Wes:WmsMasterData:WarehouseCreationEnabled"];
        var creationEnabled = false;
        if (creationValue is not null && !bool.TryParse(creationValue, out creationEnabled))
            throw new InvalidOperationException("WMS warehouse creation deployment configuration is invalid.");
        if (creationEnabled && !enabled)
            throw new InvalidOperationException("WMS warehouse creation requires master-data and identity deployment.");
        var locationValue = configuration["Wes:WmsMasterData:LocationCreationEnabled"];
        var locationEnabled = false;
        if (locationValue is not null && !bool.TryParse(locationValue, out locationEnabled))
            throw new InvalidOperationException("WMS location creation deployment configuration is invalid.");
        if (locationEnabled && !enabled)
            throw new InvalidOperationException("WMS location creation requires master-data and identity deployment.");
        var inboundValue = configuration["Wes:WmsMasterData:InboundDraftCreationEnabled"];
        var inboundEnabled = false;
        if (inboundValue is not null && !bool.TryParse(inboundValue, out inboundEnabled))
            throw new InvalidOperationException("WMS inbound draft creation deployment configuration is invalid.");
        if (inboundEnabled && !enabled)
            throw new InvalidOperationException("WMS inbound draft creation requires master-data and identity deployment.");
        var revisionValue = configuration["Wes:WmsMasterData:InboundDraftRevisionEnabled"];
        var revisionEnabled = false;
        if (revisionValue is not null && !bool.TryParse(revisionValue, out revisionEnabled))
            throw new InvalidOperationException("WMS inbound draft revision deployment configuration is invalid.");
        if (revisionEnabled && !enabled)
            throw new InvalidOperationException("WMS inbound draft revision requires master-data and identity deployment.");
        var stateValue = configuration["Wes:WmsMasterData:WarehouseStateEnabled"];
        var stateEnabled = false;
        if (stateValue is not null && !bool.TryParse(stateValue, out stateEnabled))
            throw new InvalidOperationException("WMS warehouse state deployment configuration is invalid.");
        if (stateEnabled && !enabled)
            throw new InvalidOperationException("WMS warehouse state changes require master-data and identity deployment.");
        var locationStateValue = configuration["Wes:WmsMasterData:LocationStateEnabled"];
        var locationStateEnabled = false;
        if (locationStateValue is not null && !bool.TryParse(locationStateValue, out locationStateEnabled))
            throw new InvalidOperationException("WMS location state deployment configuration is invalid.");
        if (locationStateEnabled && !enabled)
            throw new InvalidOperationException("WMS location state changes require master-data and identity deployment.");
        var resourceValue = configuration["Wes:WmsMasterData:StockResourceRegistrationEnabled"];
        var resourceEnabled = false;
        if (resourceValue is not null && !bool.TryParse(resourceValue, out resourceEnabled))
            throw new InvalidOperationException("WMS stock resource registration deployment configuration is invalid.");
        if (resourceEnabled && !enabled)
            throw new InvalidOperationException("WMS stock resource registration requires master-data and identity deployment.");
        var approvalValue = configuration["Wes:WmsMasterData:InboundApprovalEnabled"];
        var approvalEnabled = false;
        if (approvalValue is not null && !bool.TryParse(approvalValue, out approvalEnabled))
            throw new InvalidOperationException("WMS inbound approval deployment configuration is invalid.");
        if (approvalEnabled && !enabled)
            throw new InvalidOperationException("WMS inbound approval requires master-data and identity deployment.");
        var receiptValue = configuration["Wes:WmsMasterData:InboundReceiptEnabled"];
        var receiptEnabled = false;
        if (receiptValue is not null && !bool.TryParse(receiptValue, out receiptEnabled))
            throw new InvalidOperationException("WMS inbound receipt deployment configuration is invalid.");
        if (receiptEnabled && !enabled)
            throw new InvalidOperationException("WMS inbound receipt requires master-data and identity deployment.");
        var outboundValue = configuration["Wes:WmsMasterData:OutboundEnabled"];
        var outboundEnabled = false;
        if (outboundValue is not null && !bool.TryParse(outboundValue, out outboundEnabled))
            throw new InvalidOperationException("WMS outbound deployment configuration is invalid.");
        if (outboundEnabled && !enabled)
            throw new InvalidOperationException("WMS outbound requires master-data and identity deployment.");
        var countsValue = configuration["Wes:WmsMasterData:CountsEnabled"];
        var countsEnabled = false;
        if (countsValue is not null && !bool.TryParse(countsValue, out countsEnabled))
            throw new InvalidOperationException("WMS counts deployment configuration is invalid.");
        if (countsEnabled && !enabled)
            throw new InvalidOperationException("WMS counts require master-data and identity deployment.");
        return new(enabled, creationEnabled, locationEnabled, inboundEnabled, revisionEnabled, stateEnabled, locationStateEnabled,
            resourceEnabled, approvalEnabled, receiptEnabled, outboundEnabled, countsEnabled);
    }
}
