using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed class InboundDraftAccess(WmsDbContext database, IInboundDraftAuthorization authorization,
    TimeProvider clock, ILogger<InboundDraftAccess> logger)
{
    private static readonly string[] WriteFields = ["Code", "WarehouseId", "Lines"];
    private static readonly string[] WarehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];

    internal async Task<InboundDraftResult> CreateAsync(InboundSubject subject, InboundDraftCreation request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Draft creation requires a fresh independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(InboundDraftDecision.Forbidden);
        var lines = Parse(request);
        if (lines is null) return Rejected(InboundDraftDecision.Invalid);
        try
        {
            var before = await AuthorizeAsync(subject, request.WarehouseId, ct);
            if (before != InboundDraftDecision.Created) return Rejected(before);
            var result = await new InboundWriter(database, clock).CreateAsync(request.OperationId, subject.ActorId,
                request.WarehouseId, request.Code, lines, ct,
                async token => await AuthorizeAsync(subject, request.WarehouseId, token) == InboundDraftDecision.Created);
            ct.ThrowIfCancellationRequested();
            if (result.Decision == InboundWriteDecision.Replayed)
            {
                var current = await AuthorizeAsync(subject, request.WarehouseId, ct);
                if (current != InboundDraftDecision.Created) return Rejected(current);
            }
            return result.Decision switch
            {
                InboundWriteDecision.Committed => new(InboundDraftDecision.Created, result.Id),
                InboundWriteDecision.Replayed => new(InboundDraftDecision.Replayed, result.Id),
                InboundWriteDecision.Forbidden => Rejected(InboundDraftDecision.Forbidden),
                InboundWriteDecision.NotFound => Rejected(InboundDraftDecision.NotFound),
                InboundWriteDecision.Conflict => Rejected(InboundDraftDecision.Conflict),
                _ => throw new InvalidOperationException("Unknown inbound draft creation outcome.")
            };
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inbound draft failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (WmsDatabaseException)
        {
            logger.LogError("WMS inbound draft failed {Code}", "DatabaseUnavailable");
            throw;
        }
    }

    private async Task<InboundDraftDecision> AuthorizeAsync(InboundSubject subject, Guid warehouse, CancellationToken ct)
    {
        var write = await authorization.WriteFieldsAsync(subject, ct);
        ct.ThrowIfCancellationRequested();
        if (!Complete(write, WriteFields)) return InboundDraftDecision.Forbidden;
        var creator = await (from item in database.Warehouses.AsNoTracking()
                             join change in database.MasterDataChanges.AsNoTracking()
                                 on new { item.TenantId, Id = item.Id } equals new { change.TenantId, Id = change.WarehouseId }
                             where item.Id == warehouse && change.LocationId == null
                             select (Guid?)change.ActorId).SingleOrDefaultAsync(ct);
        if (creator is null) return InboundDraftDecision.NotFound;
        var parent = await authorization.WarehouseFieldsAsync(subject, creator.Value, ct);
        ct.ThrowIfCancellationRequested();
        return Complete(parent, WarehouseFields) ? InboundDraftDecision.Created : InboundDraftDecision.Forbidden;
    }

    private static bool Complete(IReadOnlySet<string>? fields, string[] required) =>
        fields is null ? throw new InvalidOperationException("Draft authority returned no decision.")
            : required.All(fields.ToHashSet(StringComparer.Ordinal).Contains);

    private static InboundPlanLine[]? Parse(InboundDraftCreation request)
    {
        if (request.OperationId == Guid.Empty || request.WarehouseId == Guid.Empty || !MasterDataValue.IsCode(request.Code)
            || request.Lines is null || request.Lines.Count is < 1 or > 100)
            return null;
        var plan = new List<InboundPlanLine>();
        var ids = new HashSet<Guid>();
        foreach (var line in request.Lines)
        {
            if (line is null || line.Id == Guid.Empty || !ids.Add(line.Id) || line.ItemId == Guid.Empty || line.UnitId == Guid.Empty
                || line.Precision is < 0 or > 6 || line.Batch is not { Length: <= 128 }
                || line.Batch != line.Batch.Trim() || line.Batch.Any(char.IsControl)
                || !TryQuantity(line.Planned, out var quantity)
                || !QuantityPrecision.Create(line.Precision).CanRepresent(quantity)) return null;
            // Equivalent quantity strings must retain the same persisted plan on original-operation recovery.
            plan.Add(new(line.Id, line.ItemId, line.UnitId, line.Batch, line.Precision,
                decimal.Parse(quantity.ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)));
        }
        return plan.ToArray();
    }

    private static bool TryQuantity(string? text, out decimal quantity)
    {
        quantity = 0m;
        if (text is null || text.Length is < 1 or > 29) return false;
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (whole.Length is < 1 or > 22 || whole.Length > 1 && whole[0] == '0'
            || whole.Any(character => character is < '0' or > '9')
            || dot >= 0 && fraction.Length is < 1 or > 6
            || fraction.Any(character => character is < '0' or > '9')) return false;
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity)
            && quantity > 0m && quantity <= InventoryQuantity.MaximumValue;
    }

    private InboundDraftResult Rejected(InboundDraftDecision decision)
    {
        logger.LogWarning("WMS inbound draft rejected {Code}", decision.ToString());
        return new(decision);
    }
}
