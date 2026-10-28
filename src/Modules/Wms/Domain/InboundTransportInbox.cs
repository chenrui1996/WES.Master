using System.Text.Json;
using Wes.Wms.Features.Inbound;

namespace Wes.Wms.Domain;

internal sealed class InboundTransportInbox
{
    private InboundTransportInbox() { }
    internal InboundTransportInbox(InboundTransportCompletion completion)
    {
        TenantId = completion.TenantId; EventId = completion.EventId; CompletionId = completion.CompletionId;
        IntentId = completion.IntentId; ExecutionId = completion.ExecutionId; Body = JsonSerializer.Serialize(completion);
    }
    public Guid TenantId { get; private set; }
    public Guid EventId { get; private set; }
    public Guid CompletionId { get; private set; }
    public Guid IntentId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string Body { get; private set; } = "";
    public Guid? PostingId { get; private set; }
    public bool Applied { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    internal InboundTransportCompletion Completion() => JsonSerializer.Deserialize<InboundTransportCompletion>(Body)
        ?? throw new InvalidOperationException("The persisted completion is missing.");
    internal void Apply(Guid posting)
    {
        if (Applied || posting == Guid.Empty) throw new InvalidOperationException("Completion application is permanent.");
        Applied = true; PostingId = posting; Version = Guid.NewGuid().ToString("D");
    }
}
