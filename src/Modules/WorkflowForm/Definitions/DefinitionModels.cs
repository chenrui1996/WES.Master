using System.Text.Json;

namespace Wes.WorkflowForm.Definitions;

internal enum FieldKind { Text = 1, Decimal, Boolean, File }
internal enum FieldSensitivity { Internal, Public, Restricted }
internal enum NodeKind { Start = 1, End, Approval, ExclusiveBranch }
internal enum ApprovalMode { All, Any }
internal enum ConditionOperator { Equal = 1, NotEqual, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual }

/// <summary>Internal definition input, not a published version or an external authorization contract.</summary>
internal sealed record FormField(
    string Key, FieldKind Kind, string LabelKey, bool Required, bool Writable,
    int? MaxLength = null, int? Precision = null, decimal? Minimum = null, decimal? Maximum = null,
    JsonElement? DefaultValue = null, FieldSensitivity Sensitivity = FieldSensitivity.Internal,
    bool AllowCondition = false);

internal sealed record FormSchema(Guid TenantId, Guid Id, int Version, string BusinessType, IReadOnlyList<FormField> Fields);

/// <summary>Trusted owner proof for this invocation; never bind this context from a client request.</summary>
internal sealed record FormAccess(Guid TenantId, IReadOnlySet<string> WritableFields, IReadOnlySet<Guid> AccessibleFiles);

internal sealed record FormData(IReadOnlyDictionary<string, JsonElement> Values);
internal sealed record WorkflowCondition(string Field, ConditionOperator Operator, JsonElement Value);
internal sealed record WorkflowEdge(string Target, WorkflowCondition? Condition = null);
internal sealed record WorkflowNode(
    string Id, NodeKind Kind, IReadOnlyList<WorkflowEdge> Edges, IReadOnlyList<Guid> Candidates,
    ApprovalMode Mode = ApprovalMode.All);
internal sealed record WorkflowDefinition(
    Guid TenantId, Guid Id, int Version, string BusinessType, int Priority,
    Guid SchemaId, int SchemaVersion, IReadOnlyList<WorkflowNode> Nodes);
internal sealed record CompiledWorkflow(WorkflowDefinition Definition, FormSchema Schema);
internal sealed record NextNode(string? NodeId);
internal sealed record CandidateBinding(IReadOnlyList<Guid> Users);
internal sealed record DefinitionIssue(string Code, string Path);

/// <summary>Invalid input yields explicit issues and no partial successful value.</summary>
internal sealed record DefinitionResult<T>(T? Value, IReadOnlyList<DefinitionIssue> Issues) where T : class
{
    internal bool IsValid => Value is not null && Issues.Count == 0;
    internal static DefinitionResult<T> Valid(T value) => new(value, []);
    internal static DefinitionResult<T> Invalid(string code, string path) => new(null, [new(code, path)]);
}
