namespace Wes.Host.Notifications;

/// <summary>Independent default-false HTTP assembly; never implies source admission, external transport or migration approval.</summary>
public sealed record NotificationsSettings(bool Enabled)
{
    /// <summary>Rejects malformed switches, enabled identity absence and unsupported external-adapter claims.</summary>
    public static NotificationsSettings Load(IConfiguration configuration)
    {
        if (configuration.GetSection("Wes:Notifications").GetChildren().Any(section =>
                section.Key is not ("Enabled" or "EmailEnabled" or "SignalREnabled")))
            throw new InvalidOperationException("Notifications deployment configuration has an unknown setting.");
        var supplied = configuration["Wes:Notifications:Enabled"];
        var enabled = false;
        if (supplied is not null && !bool.TryParse(supplied, out enabled))
            throw new InvalidOperationException("Notifications deployment configuration is invalid.");
        if (enabled && (!bool.TryParse(configuration["Wes:Identity:Enabled"], out var identity) || !identity))
            throw new InvalidOperationException("Notifications requires authenticated identity deployment.");
        foreach (var key in new[] { "EmailEnabled", "SignalREnabled" })
        {
            var value = configuration[$"Wes:Notifications:{key}"];
            if (value is not null && (!bool.TryParse(value, out var requested) || requested))
                throw new InvalidOperationException("Notifications external delivery adapter is unavailable.");
        }
        return new(enabled);
    }
}
