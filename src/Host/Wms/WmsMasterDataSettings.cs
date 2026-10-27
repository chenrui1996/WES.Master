namespace Wes.Host.Wms;

/// <summary>Deployment-only, default-disabled assembly; warehouse creation is separately enabled and never implies operational capability.</summary>
public sealed record WmsMasterDataSettings(bool Enabled, bool WarehouseCreationEnabled = false)
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
        return new(enabled, creationEnabled);
    }
}
