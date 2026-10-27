using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class InboundOrderTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _warehouse = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _receiptLocation = Guid.NewGuid();
    private readonly Guid _targetLocation = Guid.NewGuid();
    private readonly InboundPlanLine _line = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 5m);

    [Fact]
    public void PartialReceiptsKeepRemainderOpenAndSameLocationNeedsNoTransport()
    {
        var order = Order();
        Assert.Equal(InboundOrderState.Draft, order.State);
        Assert.Throws<InvalidOperationException>(() => order.Receive(Command(2m)));
        order.Approve();
        var first = order.Receive(Command(2m));
        Assert.True(first.Arrived);
        Assert.Null(first.TransportId);
        Assert.False(first.RequiresTransport);
        Assert.Equal(InboundOrderState.Executing, order.State);
        Assert.Equal(3m, order.Remaining(_line.Id));
        order.Receive(Command(3m));
        Assert.Equal(InboundOrderState.Completed, order.State);
        Assert.Equal(0m, order.Remaining(_line.Id));
    }

    [Fact]
    public void OverreceiptAndWrongDimensionsLeaveNoResidualReceiptOrVersionChange()
    {
        var order = Order(); order.Approve(); order.Receive(Command(2m));
        var version = order.Version;
        foreach (var invalid in new[] { Command(4m), Command(1m) with { ItemId = Guid.NewGuid() },
            Command(1m) with { UnitId = Guid.NewGuid() }, Command(1m) with { Batch = "other" },
            Command(0.5m), Command(0m), Command(-1m) })
            Assert.ThrowsAny<ArgumentException>(() => order.Receive(invalid));
        Assert.Single(order.Receipts);
        Assert.Equal(version, order.Version);
        Assert.Equal(3m, order.Remaining(_line.Id));
    }

    [Fact]
    public void OriginalOperationAndExecutionReplayEvenAfterCompletionButPayloadAndActorCannotChange()
    {
        var order = Order(); order.Approve(); var command = Command(5m);
        var first = order.Receive(command); var version = order.Version;
        Assert.Same(first, order.Receive(command));
        Assert.Same(first, order.Receive(command with { OperationId = Guid.NewGuid() }));
        Assert.Equal(version, order.Version);
        foreach (var invalid in new[] { command with { Quantity = 4m }, command with { ActorId = Guid.NewGuid() },
            command with { TargetLocationId = _targetLocation } })
            Assert.Throws<InvalidOperationException>(() => order.Receive(invalid));
        Assert.Single(order.Receipts);
    }

    [Fact]
    public void DifferentLocationRemainsPendingUntilExactLinkedTransportArrival()
    {
        var order = Order(); order.Approve();
        var receipt = order.Receive(Command(5m) with { TargetLocationId = _targetLocation });
        Assert.True(receipt.RequiresTransport);
        Assert.False(receipt.Arrived);
        Assert.Equal(InboundOrderState.Executing, order.State);
        var transport = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => order.ApplyArrival(receipt.Id, transport,
            receipt.ContainerId, _receiptLocation, _targetLocation));
        order.LinkTransport(receipt.Id, transport);
        order.LinkTransport(receipt.Id, transport);
        Assert.Throws<InvalidOperationException>(() => order.LinkTransport(receipt.Id, Guid.NewGuid()));
        foreach (var invalid in new[] {
            (Guid.NewGuid(), receipt.ContainerId, _receiptLocation, _targetLocation),
            (transport, Guid.NewGuid(), _receiptLocation, _targetLocation),
            (transport, receipt.ContainerId, _targetLocation, _receiptLocation) })
            Assert.Throws<InvalidOperationException>(() => order.ApplyArrival(receipt.Id,
                invalid.Item1, invalid.Item2, invalid.Item3, invalid.Item4));
        Assert.False(receipt.Arrived);
        order.ApplyArrival(receipt.Id, transport, receipt.ContainerId, _receiptLocation, _targetLocation);
        var version = order.Version;
        order.ApplyArrival(receipt.Id, transport, receipt.ContainerId, _receiptLocation, _targetLocation);
        Assert.Equal(version, order.Version);
        Assert.Equal(InboundOrderState.Completed, order.State);
    }

    [Fact]
    public void AllLinesAndEveryReceiptMustComplete()
    {
        var second = _line with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), PlannedQuantity = 1m };
        var order = new InboundOrder(_tenant, _warehouse, "IN-2", [_line, second]); order.Approve();
        order.Receive(Command(5m));
        Assert.Equal(InboundOrderState.Executing, order.State);
        order.Receive(Command(1m) with { LineId = second.Id, ItemId = second.ItemId });
        Assert.Equal(InboundOrderState.Completed, order.State);
    }

    [Fact]
    public void PlansRequireUniqueLinesAndExactBoundedPositiveQuantities()
    {
        Assert.Throws<ArgumentException>(() => new InboundOrder(_tenant, _warehouse, "IN-1", []));
        Assert.Throws<ArgumentException>(() => new InboundOrder(_tenant, _warehouse, "IN-1", [_line, _line]));
        Assert.Throws<ArgumentException>(() => new InboundOrder(_tenant, _warehouse, "IN-1", [_line with { PlannedQuantity = 0.1m }]));
        Assert.Throws<ArgumentException>(() => new InboundOrder(Guid.Empty, _warehouse, "IN-1", [_line]));
    }

    private InboundOrder Order() => new(_tenant, _warehouse, "IN-1", [_line]);
    private InboundReceiptCommand Command(decimal quantity) => new(Guid.NewGuid(), _actor, Guid.NewGuid(),
        _line.Id, _line.ItemId, _line.UnitId, _line.Batch, Guid.NewGuid(), _receiptLocation, _receiptLocation, quantity);
}
