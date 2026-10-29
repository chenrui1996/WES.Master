using System.Text.Json;

namespace Wes.WorkflowForm.Definitions;

/// <summary>Finite graph compilation and deterministic selection, not an approval instance engine.</summary>
internal static class WorkflowValidation
{
    internal static DefinitionResult<CompiledWorkflow> Compile(
        WorkflowDefinition definition, FormSchema schema, IReadOnlySet<Guid> eligibleUsers)
    {
        var schemaResult = FormValidation.Schema(schema);
        if (!schemaResult.IsValid) return new(null, schemaResult.Issues);
        if (definition.TenantId != schema.TenantId || definition.BusinessType != schema.BusinessType ||
            definition.SchemaId != schema.Id || definition.SchemaVersion != schema.Version)
            return DefinitionResult<CompiledWorkflow>.Invalid("SchemaReference", "definition.schema");
        if (definition.Id == Guid.Empty || definition.Version < 1 || definition.Priority < 0)
            return DefinitionResult<CompiledWorkflow>.Invalid("Identity", "definition");
        if (definition.Nodes.Count is < 3 or > 100)
            return DefinitionResult<CompiledWorkflow>.Invalid("Topology", "definition.nodes");

        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
            if (!FormValidation.IsKey(node.Id) || !Enum.IsDefined(node.Kind) || !nodes.TryAdd(node.Id, node))
                return DefinitionResult<CompiledWorkflow>.Invalid("Node", "definition.nodes");
        if (nodes.Values.Count(node => node.Kind == NodeKind.Start) != 1 ||
            nodes.Values.Count(node => node.Kind == NodeKind.End) != 1 ||
            !nodes.Values.Any(node => node.Kind == NodeKind.Approval))
            return DefinitionResult<CompiledWorkflow>.Invalid("Topology", "definition.nodes");

        var fields = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var path = $"definition.nodes.{node.Id}";
            if (node.Kind == NodeKind.Approval)
            {
                if (!Enum.IsDefined(node.Mode) || node.Candidates.Count is < 1 or > 100 ||
                    node.Candidates.Any(user => user == Guid.Empty || !eligibleUsers.Contains(user)))
                    return DefinitionResult<CompiledWorkflow>.Invalid("Candidates", path);
            }
            else if (node.Candidates.Count != 0 || node.Mode != ApprovalMode.All)
                return DefinitionResult<CompiledWorkflow>.Invalid("Candidates", path);

            var validEdges = node.Kind switch
            {
                NodeKind.End => node.Edges.Count == 0,
                NodeKind.ExclusiveBranch => node.Edges.Count == 2 && node.Edges.Count(edge => edge.Condition is not null) == 1,
                _ => node.Edges.Count == 1 && node.Edges[0].Condition is null,
            };
            if (!validEdges || node.Edges.Any(edge => !nodes.ContainsKey(edge.Target)) ||
                node.Edges.Select(edge => edge.Target).Distinct(StringComparer.Ordinal).Count() != node.Edges.Count)
                return DefinitionResult<CompiledWorkflow>.Invalid("Edges", path);
            foreach (var edge in node.Edges)
            {
                if (edge.Condition is not { } condition) continue;
                if (!fields.TryGetValue(condition.Field, out var field) || !field.AllowCondition ||
                    field.Kind == FieldKind.File || field.Sensitivity == FieldSensitivity.Restricted ||
                    !Enum.IsDefined(condition.Operator) ||
                    (field.Kind != FieldKind.Decimal && condition.Operator is not (ConditionOperator.Equal or ConditionOperator.NotEqual)) ||
                    !FormValidation.Value(field, condition.Value, allowNull: false))
                    return DefinitionResult<CompiledWorkflow>.Invalid("Condition", path);
            }
        }

        // Acyclic, wholly reachable finite graphs with a single sink terminate; merges do not create parallel execution.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var start = nodes.Values.Single(node => node.Kind == NodeKind.Start);
        if (!Visit(start.Id) || visited.Count != nodes.Count)
            return DefinitionResult<CompiledWorkflow>.Invalid("Topology", "definition.nodes");

        var frozen = definition.Nodes.Select(node => node with
        {
            Candidates = Array.AsReadOnly(node.Candidates.Distinct().ToArray()),
            Edges = Array.AsReadOnly(node.Edges.Select(edge => edge with
            {
                Condition = edge.Condition is { } condition ? condition with { Value = condition.Value.Clone() } : null,
            }).ToArray()),
        }).ToArray();
        return DefinitionResult<CompiledWorkflow>.Valid(new(definition with { Nodes = Array.AsReadOnly(frozen) }, schemaResult.Value!));

        bool Visit(string id)
        {
            if (active.Contains(id)) return false;
            if (!visited.Add(id)) return true;
            active.Add(id);
            foreach (var edge in nodes[id].Edges)
                if (!Visit(edge.Target)) return false;
            active.Remove(id);
            return true;
        }
    }

    internal static DefinitionResult<NextNode> Next(
        CompiledWorkflow workflow, string nodeId,
        IReadOnlyList<KeyValuePair<string, JsonElement>> businessValues)
    {
        var node = workflow.Definition.Nodes.SingleOrDefault(node => node.Id == nodeId);
        if (node is null) return DefinitionResult<NextNode>.Invalid("Node", "node");
        if (node.Kind == NodeKind.End) return DefinitionResult<NextNode>.Valid(new(null));
        if (node.Kind != NodeKind.ExclusiveBranch) return DefinitionResult<NextNode>.Valid(new(node.Edges[0].Target));
        if (businessValues.Count > 100)
            return DefinitionResult<NextNode>.Invalid("ConditionInput", "condition.data");
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, input) in businessValues)
            if (!inputs.TryAdd(key, input))
                return DefinitionResult<NextNode>.Invalid("ConditionInput", $"condition.{key}");
        var edge = node.Edges.Single(edge => edge.Condition is not null);
        var condition = edge.Condition!;
        var field = workflow.Schema.Fields.Single(field => field.Key == condition.Field);
        if (!inputs.TryGetValue(condition.Field, out var value) || !FormValidation.Value(field, value, allowNull: false))
            return DefinitionResult<NextNode>.Invalid("ConditionInput", $"condition.{condition.Field}");
        var comparison = field.Kind switch
        {
            FieldKind.Decimal => CompareNumbers(value, condition.Value, field.Precision),
            FieldKind.Text => string.Compare(value.GetString(), condition.Value.GetString(), StringComparison.Ordinal),
            FieldKind.Boolean => value.GetBoolean().CompareTo(condition.Value.GetBoolean()),
            _ => throw new InvalidOperationException("Compiled workflow contains an unsupported condition field."),
        };
        var matched = condition.Operator switch
        {
            ConditionOperator.Equal => comparison == 0,
            ConditionOperator.NotEqual => comparison != 0,
            ConditionOperator.GreaterThan => comparison > 0,
            ConditionOperator.GreaterOrEqual => comparison >= 0,
            ConditionOperator.LessThan => comparison < 0,
            ConditionOperator.LessOrEqual => comparison <= 0,
            _ => throw new InvalidOperationException("Compiled workflow contains an unsupported condition operator."),
        };
        return DefinitionResult<NextNode>.Valid(new(matched ? edge.Target : node.Edges.Single(edge => edge.Condition is null).Target));
    }

    internal static DefinitionResult<CandidateBinding> BindCandidates(
        CompiledWorkflow workflow, string nodeId, Guid submitter, IReadOnlySet<Guid> eligibleUsers)
    {
        var node = workflow.Definition.Nodes.SingleOrDefault(node => node.Id == nodeId);
        if (node?.Kind != NodeKind.Approval)
            return DefinitionResult<CandidateBinding>.Invalid("Node", "node");
        if (submitter == Guid.Empty)
            return DefinitionResult<CandidateBinding>.Invalid("Identity", "submitter");
        if (node.Candidates.Contains(submitter))
            return DefinitionResult<CandidateBinding>.Invalid("SelfApproval", $"nodes.{nodeId}.candidates");
        if (node.Candidates.Any(user => !eligibleUsers.Contains(user)))
            return DefinitionResult<CandidateBinding>.Invalid("Candidates", $"nodes.{nodeId}.candidates");
        return DefinitionResult<CandidateBinding>.Valid(new(node.Candidates));
    }

    internal static DefinitionResult<CompiledWorkflow> Select(
        Guid tenantId, string businessType, IReadOnlyList<CompiledWorkflow> matches)
    {
        if (tenantId == Guid.Empty || !FormValidation.IsKey(businessType) ||
            matches.Any(item => item.Definition.TenantId != tenantId || item.Definition.BusinessType != businessType))
            return DefinitionResult<CompiledWorkflow>.Invalid("Scope", "matches");
        if (matches.Count == 0) return DefinitionResult<CompiledWorkflow>.Invalid("NotFound", "matches");
        if (matches.Select(item => (item.Definition.Id, item.Definition.Version)).Distinct().Count() != matches.Count)
            return DefinitionResult<CompiledWorkflow>.Invalid("Duplicate", "matches");
        var highest = matches.Max(item => item.Definition.Priority);
        var winners = matches.Where(item => item.Definition.Priority == highest).ToArray();
        return winners.Length == 1
            ? DefinitionResult<CompiledWorkflow>.Valid(winners[0])
            : DefinitionResult<CompiledWorkflow>.Invalid("Ambiguous", "matches.priority");
    }

    private static int CompareNumbers(JsonElement left, JsonElement right, int? precision)
    {
        if (!FormValidation.TryDecimal(left, precision, out var leftNumber) ||
            !FormValidation.TryDecimal(right, precision, out var rightNumber))
            throw new InvalidOperationException("Compiled workflow contains an invalid decimal condition.");
        return leftNumber.CompareTo(rightNumber);
    }
}
