using Serilog.Core;
using Serilog.Events;

namespace Wes.Host.Runtime;

internal sealed class TransportLogAllowlist : ILogEventEnricher
{
    // ASP.NET adds RequestPath through an ambient scope even when our template never logs it.
    // Strip inherited properties at the final enrichment stage before any sink receives the event.
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var source)
            || source is not ScalarValue { Value: string category }
            || !(category.StartsWith("Wes.Host.Runtime.", StringComparison.Ordinal)
                || category.StartsWith("Wes.Platform.Infrastructure.Health.", StringComparison.Ordinal)
                || category.StartsWith("Wes.Platform.Features.Identity.", StringComparison.Ordinal)))
        {
            return;
        }

        foreach (var name in logEvent.Properties.Keys.ToArray())
        {
            if (name is not ("CorrelationId" or "SourceContext" or "Code" or "StatusCode" or "Method" or "RouteTemplate" or "DriverCode"))
            {
                logEvent.RemovePropertyIfPresent(name);
            }
        }
    }
}
