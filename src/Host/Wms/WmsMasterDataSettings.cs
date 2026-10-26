namespace Wes.Host.Wms;

/// <summary>Deployment-only, default-disabled assembly of read-only master-data endpoints; not an operational capability.</summary>
public sealed record WmsMasterDataSettings(bool Enabled)
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
        return new(enabled);
    }
}
