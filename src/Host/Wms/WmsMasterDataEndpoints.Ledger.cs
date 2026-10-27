using System.Globalization;
using System.Text.RegularExpressions;
using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Inventory;
using Wes.Wms.Features.Ledger;

namespace Wes.Host.Wms;

public static partial class WmsMasterDataEndpoints
{
    private static readonly Regex StockLedgerUtc = new(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static async Task<IResult> QueryStockLedgerAsync(StockLedgerQuery query, string? from, string? before, HttpContext http,
        StockLedgerDatabase database, CancellationToken ct)
    {
        if (!TryStockLedgerUtc(from, out var start) || !TryStockLedgerUtc(before, out var end))
            return Results.BadRequest();
        query = query with { From = start, Before = end };
        var context = IdentityRuntime.Subject(http.User);
        try
        {
            await using var owner = database.Open(context.TenantId);
            var result = await owner.QueryAsync(new(context.TenantId, context.ActorId, context.SessionId), query, ct);
            return result.Decision switch
            {
                InventoryReadDecision.Success => Results.Ok(new { result.Rows, result.Offset, result.Limit }),
                InventoryReadDecision.Forbidden => Results.Forbid(),
                InventoryReadDecision.Invalid => Results.BadRequest(),
                _ => throw new InvalidOperationException("Unknown ledger read decision.")
            };
        }

        catch (WmsDatabaseException error)
        {
            return Results.Problem(statusCode: error.IsTransient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                title: "WMS ledger access failed.");
        }
    }

    private static bool TryStockLedgerUtc(string? raw, out DateTimeOffset? value)
    {
        value = null;
        if (raw is null) return true;
        if (!StockLedgerUtc.IsMatch(raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private sealed class StockLedgerDatabase(IdentitySettings settings, StockLedgerAuthority authorization, ILoggerFactory logs)
    {
        internal WmsStockLedgerScope Open(Guid tenant) => new(settings.ConnectionString, tenant, authorization, logs);
    }

    private sealed class StockLedgerAuthority(IdentityDatabase database) : IStockLedgerAuthorization
    {
        public async Task<IReadOnlySet<string>> ReadAllFieldsAsync(InventorySubject subject, CancellationToken cancellationToken)
        {
            await using var owner = database.Open(subject.TenantId);
            return await owner.VisibleTenantResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                "wms.ledger.read", cancellationToken);
        }
    }
}
