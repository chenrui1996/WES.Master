using System.Text.Json;
using Wes.WorkflowForm.Definitions;
using Xunit;

namespace Wes.Modules.Tests.WorkflowForm;

/// <summary>Exercises the pure T12 contract without substituting a database or authorization owner.</summary>
public sealed class DefinitionValidationTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid SchemaId = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Submitter = Guid.NewGuid();
    private static readonly Guid File = Guid.NewGuid();

    [Fact]
    [Trait("CaseId", "TC-T12-CORE-01")]
    public void LinearDefinition_DeduplicatesCandidatesAndFreezesInputs()
    {
        var candidates = new List<Guid> { User, User };
        var nodes = Linear().Nodes.ToList();
        nodes[1] = nodes[1] with { Candidates = candidates };
        var result = WorkflowValidation.Compile(Linear() with { Nodes = nodes }, Schema(), new HashSet<Guid> { User });
        Assert.True(result.IsValid);
        var compiled = Assert.IsType<CompiledWorkflow>(result.Value);
        nodes.Clear();
        candidates.Clear();
        Assert.Equal(3, compiled.Definition.Nodes.Count);
        Assert.Equal(User, Assert.Single(compiled.Definition.Nodes[1].Candidates));
        Assert.Equal(ApprovalMode.All, compiled.Definition.Nodes[1].Mode);
        Assert.Equal("approve", WorkflowValidation.Next(compiled, "start", []).Value?.NodeId);
        Assert.Equal("end", WorkflowValidation.Next(compiled, "approve", []).Value?.NodeId);
        Assert.Null(WorkflowValidation.Next(compiled, "end", []).Value?.NodeId);
    }

    [Theory]
    [InlineData("missing-end", "Topology")]
    [InlineData("duplicate-start", "Topology")]
    [InlineData("disconnected", "Topology")]
    [InlineData("cycle", "Topology")]
    [InlineData("parallel", "Edges")]
    [InlineData("dangling-edge", "Edges")]
    [InlineData("unknown-node", "Node")]
    [InlineData("unknown-mode", "Candidates")]
    [InlineData("empty-candidates", "Candidates")]
    [InlineData("ineligible-candidate", "Candidates")]
    [InlineData("empty-candidate-id", "Candidates")]
    [InlineData("duplicate-node", "Node")]
    [InlineData("wrong-schema", "SchemaReference")]
    [InlineData("wrong-version", "SchemaReference")]
    [InlineData("wrong-tenant", "SchemaReference")]
    [InlineData("wrong-business", "SchemaReference")]
    [InlineData("negative-priority", "Identity")]
    [InlineData("too-few-nodes", "Topology")]
    [InlineData("too-many-nodes", "Topology")]
    [Trait("CaseId", "TC-T12-CORE-02")]
    public void InvalidGraph_HasNoCompiledValue(string variant, string code)
    {
        var definition = Linear();
        var nodes = definition.Nodes.ToList();
        definition = variant switch
        {
            "wrong-schema" => definition with { SchemaId = Guid.NewGuid() },
            "wrong-version" => definition with { SchemaVersion = 2 },
            "wrong-tenant" => definition with { TenantId = Guid.NewGuid() },
            "wrong-business" => definition with { BusinessType = "Other" },
            "negative-priority" => definition with { Priority = -1 },
            _ => definition,
        };
        switch (variant)
        {
            case "missing-end": nodes.RemoveAt(2); break;
            case "duplicate-start": nodes.Add(nodes[0] with { Id = "start2" }); break;
            case "disconnected": nodes.Add(nodes[1] with { Id = "orphan" }); break;
            case "cycle": nodes[1] = nodes[1] with { Edges = [new("start")] }; break;
            case "parallel": nodes[1] = nodes[1] with { Edges = [new("end"), new("end")] }; break;
            case "dangling-edge": nodes[0] = nodes[0] with { Edges = [new("missing")] }; break;
            case "unknown-node": nodes[1] = nodes[1] with { Kind = (NodeKind)99 }; break;
            case "unknown-mode": nodes[1] = nodes[1] with { Mode = (ApprovalMode)99 }; break;
            case "empty-candidates": nodes[1] = nodes[1] with { Candidates = [] }; break;
            case "ineligible-candidate": nodes[1] = nodes[1] with { Candidates = [Submitter] }; break;
            case "empty-candidate-id": nodes[1] = nodes[1] with { Candidates = [Guid.Empty] }; break;
            case "duplicate-node": nodes.Add(nodes[1]); break;
            case "too-few-nodes": nodes = [nodes[0], nodes[2]]; break;
            case "too-many-nodes": nodes.AddRange(Enumerable.Range(0, 98).Select(i => nodes[1] with { Id = $"step{i}" })); break;
        }
        Invalid(WorkflowValidation.Compile(definition with { Nodes = nodes }, Schema(), new HashSet<Guid> { User }), code);
    }

    [Theory]
    [InlineData("\"6\"", "approve")]
    [InlineData("\"5\"", "end")]
    [InlineData("\"4\"", "end")]
    [Trait("CaseId", "TC-T12-CORE-03")]
    public void ExclusiveBranch_SelectsOnlyOneEdge(string quantity, string expected)
    {
        var compiled = CompiledBranch();
        var result = WorkflowValidation.Next(compiled, "branch", Pairs(("qty", quantity)));
        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Value?.NodeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("6")]
    [InlineData("\"6e0\"")]
    [InlineData("null")]
    [InlineData("\"1001\"")]
    public void BranchBadInput_IsNotTreatedAsOtherwise(string? value)
    {
        var data = value is null ? [] : Pairs(("qty", value));
        Invalid(WorkflowValidation.Next(CompiledBranch(), "branch", data), "ConditionInput");
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("unknown-operator")]
    [InlineData("bad-type")]
    [InlineData("missing-otherwise")]
    [InlineData("two-conditions")]
    [InlineData("sensitive")]
    [InlineData("not-whitelisted")]
    [InlineData("file")]
    public void UnsupportedCondition_CannotCompile(string variant)
    {
        var schema = Schema();
        var nodes = Branch().Nodes.ToList();
        var condition = nodes[1].Edges[0].Condition!;
        if (variant == "unknown-field") condition = condition with { Field = "WorkTable" };
        if (variant == "unknown-operator") condition = condition with { Operator = (ConditionOperator)99 };
        if (variant == "bad-type") condition = condition with { Value = J("5") };
        if (variant is "sensitive" or "not-whitelisted" or "file")
        {
            var field = Quantity() with
            {
                Sensitivity = variant == "sensitive" ? FieldSensitivity.Restricted : FieldSensitivity.Internal,
                AllowCondition = variant == "not-whitelisted" ? false : true,
            };
            if (variant == "file") field = field with { Kind = FieldKind.File, Precision = null, Minimum = null, Maximum = null };
            schema = schema with { Fields = [field] };
        }
        nodes[1] = nodes[1] with
        {
            Edges = variant switch
            {
                "missing-otherwise" => [new("approve", condition)],
                "two-conditions" => [new("approve", condition), new("end", condition)],
                _ => [new("approve", condition), new("end")],
            },
        };
        Assert.False(WorkflowValidation.Compile(Branch() with { Nodes = nodes }, schema, new HashSet<Guid> { User }).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CandidateBinding_RejectsSelfApprovalAndRevocation(int mode)
    {
        var nodes = Linear().Nodes.ToList();
        nodes[1] = nodes[1] with { Mode = (ApprovalMode)mode };
        var compiled = WorkflowValidation.Compile(Linear() with { Nodes = nodes }, Schema(), new HashSet<Guid> { User }).Value!;
        Invalid(WorkflowValidation.BindCandidates(compiled, "approve", User, new HashSet<Guid> { User }), "SelfApproval");
        Invalid(WorkflowValidation.BindCandidates(compiled, "approve", Submitter, new HashSet<Guid>()), "Candidates");
        var valid = WorkflowValidation.BindCandidates(compiled, "approve", Submitter, new HashSet<Guid> { User });
        Assert.Equal(User, Assert.Single(valid.Value!.Users));
        Invalid(WorkflowValidation.BindCandidates(compiled, "start", Submitter, new HashSet<Guid> { User }), "Node");
        Invalid(WorkflowValidation.Next(compiled, "missing", []), "Node");
    }

    [Fact]
    [Trait("CaseId", "TC-T12-CORE-04")]
    public void Matching_RequiresUniqueHighestPriorityAndOwnerScope()
    {
        var low = WorkflowValidation.Compile(Linear(), Schema(), new HashSet<Guid> { User }).Value!;
        var high = WorkflowValidation.Compile(Linear() with { Id = Guid.NewGuid(), Priority = 20 }, Schema(), new HashSet<Guid> { User }).Value!;
        foreach (var matches in new[] { new[] { low, high }, new[] { high, low } })
            Assert.Same(high, WorkflowValidation.Select(Tenant, "Transfer", matches).Value);
        Invalid(WorkflowValidation.Select(Tenant, "Transfer", []), "NotFound");
        var tied = WorkflowValidation.Compile(Linear() with { Id = Guid.NewGuid(), Priority = 20 }, Schema(), new HashSet<Guid> { User }).Value!;
        Invalid(WorkflowValidation.Select(Tenant, "Transfer", [high, tied]), "Ambiguous");
        Invalid(WorkflowValidation.Select(Guid.NewGuid(), "Transfer", [low]), "Scope");
        Invalid(WorkflowValidation.Select(Tenant, "Other", [low]), "Scope");
        Invalid(WorkflowValidation.Select(Tenant, "Transfer", [low, low]), "Duplicate");
    }

    [Fact]
    [Trait("CaseId", "TC-T12-CORE-05")]
    public void SchemaCapacity_OneHundredFieldsAcceptedNextRejected()
    {
        var fields = Enumerable.Range(0, 100).Select(i => Quantity() with { Key = $"qty{i}" }).ToList();
        Assert.True(FormValidation.Schema(Schema() with { Fields = fields }).IsValid);
        fields.Add(Quantity() with { Key = "extra" });
        Invalid(FormValidation.Schema(Schema() with { Fields = fields }), "Fields");
        Invalid(FormValidation.Schema(Schema() with { Fields = [] }), "Fields");
    }

    [Theory]
    [InlineData("key")]
    [InlineData("label")]
    [InlineData("duplicate")]
    [InlineData("unknown-type")]
    [InlineData("unknown-sensitivity")]
    [InlineData("precision")]
    [InlineData("bounds")]
    [InlineData("inapplicable-length")]
    [InlineData("text-no-length")]
    [InlineData("text-length-over")]
    [InlineData("type-change-default")]
    [InlineData("file-default")]
    [InlineData("required-null-default")]
    [InlineData("bound-overflow")]
    [InlineData("version")]
    [InlineData("tenant")]
    [InlineData("business")]
    public void InvalidSchema_IsNotPartiallyAccepted(string variant)
    {
        var field = variant switch
        {
            "key" => Quantity() with { Key = "qty;DROP TABLE" },
            "label" => Quantity() with { LabelKey = "<script>" },
            "unknown-type" => Quantity() with { Kind = (FieldKind)99 },
            "unknown-sensitivity" => Quantity() with { Sensitivity = (FieldSensitivity)99 },
            "precision" => Quantity() with { Precision = 7 },
            "bounds" => Quantity() with { Minimum = 11, Maximum = 10 },
            "inapplicable-length" => Quantity() with { MaxLength = 100 },
            "text-no-length" => new FormField("name", FieldKind.Text, "Name", true, true),
            "text-length-over" => new FormField("name", FieldKind.Text, "Name", true, true, MaxLength: 4097),
            "type-change-default" => new FormField("name", FieldKind.Boolean, "Name", true, true, DefaultValue: J("\"old\"")),
            "file-default" => new FormField("file", FieldKind.File, "File", false, true, DefaultValue: J($"\"{File}\"")),
            "required-null-default" => Quantity() with { DefaultValue = J("null") },
            "bound-overflow" => Quantity() with { Maximum = decimal.MaxValue },
            _ => Quantity(),
        };
        var schema = Schema() with { Fields = variant == "duplicate" ? [field, field] : [field] };
        schema = variant switch
        {
            "version" => schema with { Version = 0 },
            "tenant" => schema with { TenantId = Guid.Empty },
            "business" => schema with { BusinessType = "not a code" },
            _ => schema,
        };
        Assert.False(FormValidation.Schema(schema).IsValid);
        Assert.Null(FormValidation.Schema(schema).Value);
        Assert.All(FormValidation.Schema(schema).Issues, issue => Assert.False(string.IsNullOrEmpty(issue.Path)));
    }

    [Theory]
    [InlineData("\"0\"", false)]
    [InlineData("\"1\"", true)]
    [InlineData("\"1000\"", true)]
    [InlineData("\"1000.000001\"", false)]
    [InlineData("\"1.123456\"", true)]
    [InlineData("\"1.1234567\"", false)]
    [InlineData("\"1e2\"", false)]
    [InlineData("\" 1\"", false)]
    [InlineData("\"+1\"", false)]
    [InlineData("\"01\"", false)]
    [InlineData("\"1.\"", false)]
    [InlineData("\".1\"", false)]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("null", false)]
    [Trait("CaseId", "TC-T12-CORE-06")]
    public void DecimalValues_AreExactStringsWithoutRounding(string value, bool allowed)
    {
        var result = FormValidation.Data(Schema(), Access(), Pairs(("qty", value)), []);
        Assert.Equal(allowed, result.IsValid);
        if (allowed)
            Assert.Equal(J(value).GetString(), result.Value!.Values["qty"].GetString());
        else
            Assert.Null(result.Value);
    }

    [Fact]
    public void DecimalMaximumAndPrecision_AreNotFloatingPointApproximations()
    {
        var schema = Schema() with { Fields = [Quantity() with { Minimum = null, Maximum = null }] };
        Assert.True(FormValidation.Data(schema, Access(), Pairs(("qty", "\"9999999999999999999999.999999\"")), []).IsValid);
        Assert.False(FormValidation.Data(schema, Access(), Pairs(("qty", "\"10000000000000000000000\"")), []).IsValid);
        schema = schema with { Fields = [Quantity() with { Precision = 0, Minimum = -10, Maximum = 10 }] };
        Assert.True(FormValidation.Data(schema, Access(), Pairs(("qty", "\"-10\"")), []).IsValid);
        Assert.False(FormValidation.Data(schema, Access(), Pairs(("qty", "\"1.0\"")), []).IsValid);
    }

    [Fact]
    public void Data_RejectsUnknownDuplicateReadOnlyAndUnauthorizedFields()
    {
        Invalid(FormValidation.Data(Schema(), Access(), Pairs(("qty", "\"2\""), ("rogue", "true")), []), "UnknownField");
        Invalid(FormValidation.Data(Schema(), Access(), Pairs(("qty", "\"2\""), ("qty", "\"3\"")), []), "Duplicate");
        Invalid(FormValidation.Data(Schema(), Access(), [], []), "Required");
        Invalid(FormValidation.Data(Schema(), Access() with { WritableFields = new HashSet<string>() }, Pairs(("qty", "\"2\"")), []), "FieldPermission");
        Invalid(FormValidation.Data(Schema(), Access() with { TenantId = Guid.NewGuid() }, Pairs(("qty", "\"2\"")), []), "Scope");
        var schema = Schema() with { Fields = [Quantity() with { Writable = false }] };
        Invalid(FormValidation.Data(schema, Access(), Pairs(("qty", "\"2\"")), []), "ReadOnly");
        Invalid(FormValidation.Data(schema, Access(), [], []), "Required");
        Assert.True(FormValidation.Data(schema, Access(), [], Pairs(("qty", "\"2\""))).IsValid);
        Invalid(FormValidation.Data(Schema(), Access(), [], Pairs(("qty", "\"2\""))), "BusinessField");
        Invalid(FormValidation.Data(Schema(), Access(), Enumerable.Repeat(Pairs(("qty", "\"2\""))[0], 101).ToArray(), []), "Fields");
    }

    [Fact]
    public void FileReferences_NeedCurrentSameTenantOwnerProof()
    {
        var schema = Schema() with { Fields = [new("file", FieldKind.File, "File", true, true)] };
        var access = Access() with { WritableFields = new HashSet<string> { "file" } };
        Assert.True(FormValidation.Data(schema, access, Pairs(("file", $"\"{File}\"")), []).IsValid);
        Invalid(FormValidation.Data(schema, access with { AccessibleFiles = new HashSet<Guid>() }, Pairs(("file", $"\"{File}\"")), []), "FilePermission");
        Invalid(FormValidation.Data(schema, access with { TenantId = Guid.NewGuid() }, Pairs(("file", $"\"{File}\"")), []), "Scope");
        Invalid(FormValidation.Data(schema, access, Pairs(("file", "\"https://example.invalid/a\"")), []), "Value");
        Invalid(FormValidation.Data(schema, access, Pairs(("file", $"\"{Guid.Empty}\"")), []), "Value");
    }

    [Fact]
    public void DefaultsAndTypedValues_DoNotCoerceOrReplaceExplicitNull()
    {
        var schema = Schema() with
        {
            Fields =
            [
                new("name", FieldKind.Text, "Name", true, true, MaxLength: 3, DefaultValue: J("\"abc\"")),
                new("flag", FieldKind.Boolean, "Flag", false, true),
            ],
        };
        var access = Access() with { WritableFields = new HashSet<string> { "name", "flag" } };
        var result = FormValidation.Data(schema, access, Pairs(("flag", "false")), []);
        Assert.Equal("abc", result.Value!.Values["name"].GetString());
        Assert.False(result.Value.Values["flag"].GetBoolean());
        Assert.False(FormValidation.Data(schema, access, Pairs(("name", "null")), []).IsValid);
        Assert.False(FormValidation.Data(schema, access, Pairs(("name", "\"abcd\"")), []).IsValid);
        Assert.False(FormValidation.Data(schema, access, Pairs(("flag", "\"false\"")), []).IsValid);
        Assert.True(FormValidation.Data(schema, access, Pairs(("flag", "null")), []).IsValid);
        Invalid(FormValidation.Data(schema, access with { WritableFields = new HashSet<string> { "flag" } }, [], []), "FieldPermission");
    }

    [Fact]
    [Trait("CaseId", "TC-T12-CORE-07")]
    public void SchemaAndDataSnapshots_SurviveMutationDisposalAndNewVersion()
    {
        using var document = JsonDocument.Parse("\"2\"");
        var fields = new List<FormField> { Quantity() with { DefaultValue = document.RootElement } };
        var original = FormValidation.Schema(Schema() with { Fields = fields }).Value!;
        var data = FormValidation.Data(original, Access(), [], []).Value!;
        fields.Clear();
        document.Dispose();
        var changed = original with { Version = 2, Fields = [new("qty", FieldKind.Boolean, "Qty", true, true)] };
        Assert.True(FormValidation.Data(original, Access(), Pairs(("qty", "\"2\"")), []).IsValid);
        Assert.False(FormValidation.Data(changed, Access(), Pairs(("qty", "\"2\"")), []).IsValid);
        Assert.Equal("2", original.Fields[0].DefaultValue!.Value.GetString());
        Assert.Equal("2", data.Values["qty"].GetString());
        Assert.Throws<NotSupportedException>(() => ((IList<FormField>)original.Fields).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JsonElement>)data.Values).Clear());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("label")]
    [InlineData("business")]
    [InlineData("node")]
    public void TrailingNewline_IsNotPartOfAStableIdentifier(string target)
    {
        var schema = Schema();
        if (target == "key") schema = schema with { Fields = [Quantity() with { Key = "qty\n" }] };
        if (target == "label") schema = schema with { Fields = [Quantity() with { LabelKey = "Qty\n" }] };
        if (target == "business") schema = schema with { BusinessType = "Transfer\n" };
        if (target == "node")
        {
            var nodes = Linear().Nodes.ToList();
            nodes[0] = nodes[0] with { Id = "start\n" };
            Assert.False(WorkflowValidation.Compile(Linear() with { Nodes = nodes }, schema, new HashSet<Guid> { User }).IsValid);
        }
        else
            Assert.False(FormValidation.Schema(schema).IsValid);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\n\\t\"")]
    public void RequiredText_RejectsEmptyOrWhitespace(string text)
    {
        var schema = Schema() with { Fields = [new("qty", FieldKind.Text, "Qty", true, true, MaxLength: 20)] };
        Assert.False(FormValidation.Data(schema, Access(), Pairs(("qty", text)), []).IsValid);
    }

    [Theory]
    [InlineData(1, "\"5.0\"", "approve")]
    [InlineData(1, "\"4\"", "end")]
    [InlineData(2, "\"5\"", "end")]
    [InlineData(2, "\"4\"", "approve")]
    [InlineData(3, "\"6\"", "approve")]
    [InlineData(3, "\"5\"", "end")]
    [InlineData(4, "\"5\"", "approve")]
    [InlineData(4, "\"4\"", "end")]
    [InlineData(5, "\"4\"", "approve")]
    [InlineData(5, "\"5\"", "end")]
    [InlineData(6, "\"5\"", "approve")]
    [InlineData(6, "\"6\"", "end")]
    public void DecimalComparison_HonorsEachOperatorAtEquality(int operation, string value, string expected)
    {
        var nodes = Branch().Nodes.ToList();
        nodes[1] = nodes[1] with { Edges = [new("approve", new("qty", (ConditionOperator)operation, J("\"5\""))), new("end")] };
        var compiled = WorkflowValidation.Compile(Branch() with { Nodes = nodes }, Schema(), new HashSet<Guid> { User }).Value!;
        Assert.Equal(expected, WorkflowValidation.Next(compiled, "branch", Pairs(("qty", value))).Value?.NodeId);
    }

    [Theory]
    [InlineData("text", "\"ABC\"", "\"ABC\"", 1, "approve")]
    [InlineData("text", "\"ABC\"", "\"abc\"", 1, "end")]
    [InlineData("text", "\"ABC\"", "\"abc\"", 2, "approve")]
    [InlineData("bool", "true", "true", 1, "approve")]
    [InlineData("bool", "true", "false", 1, "end")]
    [InlineData("bool", "false", "true", 2, "approve")]
    public void NonNumericConditions_AreTypedAndOrdinal(string type, string expectedValue, string input, int operation, string target)
    {
        var field = type == "text"
            ? new FormField("qty", FieldKind.Text, "Qty", true, true, MaxLength: 20, AllowCondition: true)
            : new FormField("qty", FieldKind.Boolean, "Qty", true, true, AllowCondition: true);
        var nodes = Branch().Nodes.ToList();
        nodes[1] = nodes[1] with { Edges = [new("approve", new("qty", (ConditionOperator)operation, J(expectedValue))), new("end")] };
        var compiled = WorkflowValidation.Compile(Branch() with { Nodes = nodes }, Schema() with { Fields = [field] }, new HashSet<Guid> { User }).Value!;
        Assert.Equal(target, WorkflowValidation.Next(compiled, "branch", Pairs(("qty", input))).Value?.NodeId);
    }

    [Fact]
    public void BranchSnapshot_ClonesConditionAndEdgesBeforeDocumentDisposal()
    {
        using var document = JsonDocument.Parse("\"5\"");
        var edges = new List<WorkflowEdge> { new("approve", new("qty", ConditionOperator.GreaterThan, document.RootElement)), new("end") };
        var nodes = Branch().Nodes.ToList();
        nodes[1] = nodes[1] with { Edges = edges };
        var compiled = WorkflowValidation.Compile(Branch() with { Nodes = nodes }, Schema(), new HashSet<Guid> { User }).Value!;
        document.Dispose();
        edges.Clear();
        nodes.Clear();
        Assert.Equal("approve", WorkflowValidation.Next(compiled, "branch", Pairs(("qty", "\"6\""))).Value?.NodeId);
        Invalid(WorkflowValidation.Next(compiled, "branch", Pairs(("qty", "\"6\""), ("qty", "\"4\""))), "ConditionInput");
    }

    [Fact]
    public void CompleteOneHundredNodeChain_CanReachEndWithoutTruncation()
    {
        var nodes = new List<WorkflowNode> { new("start", NodeKind.Start, [new("step0")], []) };
        nodes.AddRange(Enumerable.Range(0, 98).Select(i =>
            new WorkflowNode($"step{i}", NodeKind.Approval, [new(i == 97 ? "end" : $"step{i + 1}")], [User])));
        nodes.Add(new("end", NodeKind.End, [], []));
        var compiled = WorkflowValidation.Compile(Linear() with { Nodes = nodes }, Schema(), new HashSet<Guid> { User }).Value!;
        var visited = new List<string>();
        string? current = "start";
        while (current is not null && visited.Count <= 100)
        {
            visited.Add(current);
            current = WorkflowValidation.Next(compiled, current, []).Value!.NodeId;
        }
        Assert.Equal(100, visited.Count);
        Assert.Equal("end", visited[^1]);
        Assert.Null(current);
    }

    [Fact]
    public void FullCapacityFormData_DoesNotTruncateAndUsesOrdinalFieldPermissions()
    {
        var fields = Enumerable.Range(0, 100).Select(i => Quantity() with { Key = $"qty{i}" }).ToArray();
        var values = fields.Select(field => KeyValuePair.Create(field.Key, J("\"1\""))).ToArray();
        var access = Access() with { WritableFields = fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal) };
        var result = FormValidation.Data(Schema() with { Fields = fields }, access, values, []);
        Assert.Equal(100, result.Value!.Values.Count);
        Invalid(FormValidation.Data(Schema(), Access() with { WritableFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "QTY" } },
            Pairs(("qty", "\"2\"")), []), "FieldPermission");
    }

    [Fact]
    public void SchemaIssuePaths_UseOriginalIndicesAfterInvalidKeys()
    {
        var result = FormValidation.Schema(Schema() with
        {
            Fields = [Quantity() with { Key = "bad key" }, Quantity() with { LabelKey = "<invalid>" }],
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "FieldKey" && issue.Path == "schema.fields[0]");
        Assert.Contains(result.Issues, issue => issue.Code == "Label" && issue.Path == "schema.fields[1]");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void StableKeys_EnforceDeclaredLengthWithoutTruncation(int length, bool allowed)
    {
        var result = FormValidation.Schema(Schema() with { Fields = [Quantity() with { Key = new string('a', length) }] });
        Assert.Equal(allowed, result.IsValid);
        if (allowed) Assert.Equal(length, result.Value!.Fields[0].Key.Length);
    }

    [Fact]
    public void TextMaximum_DoesNotTruncateValues()
    {
        var schema = Schema() with { Fields = [new("qty", FieldKind.Text, "Qty", true, true, MaxLength: 4096)] };
        var value = new string('a', 4096);
        var result = FormValidation.Data(schema, Access(), Pairs(("qty", JsonSerializer.Serialize(value))), []);
        Assert.Equal(value, result.Value!.Values["qty"].GetString());
        Assert.False(FormValidation.Data(schema, Access(), Pairs(("qty", JsonSerializer.Serialize(value + "a"))), []).IsValid);
    }

    [Fact]
    public void CandidateCapacity_RejectsOversizeRatherThanTakingFirstHundred()
    {
        var users = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        var nodes = Linear().Nodes.ToList();
        nodes[1] = nodes[1] with { Candidates = users };
        Assert.True(WorkflowValidation.Compile(Linear() with { Nodes = nodes }, Schema(), users.ToHashSet()).IsValid);
        users.Add(Guid.NewGuid());
        Invalid(WorkflowValidation.Compile(Linear() with { Nodes = nodes }, Schema(), users.ToHashSet()), "Candidates");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("bool")]
    public void RelationalOperators_AreNotDefinedForNonNumericFields(string type)
    {
        var field = type == "text"
            ? new FormField("qty", FieldKind.Text, "Qty", true, true, MaxLength: 20, AllowCondition: true)
            : new FormField("qty", FieldKind.Boolean, "Qty", true, true, AllowCondition: true);
        var nodes = Branch().Nodes.ToList();
        nodes[1] = nodes[1] with
        {
            Edges = [new("approve", new("qty", ConditionOperator.GreaterThan, J(type == "text" ? "\"abc\"" : "true"))), new("end")],
        };
        Invalid(WorkflowValidation.Compile(Branch() with { Nodes = nodes }, Schema() with { Fields = [field] }, new HashSet<Guid> { User }), "Condition");
    }

    private static FormField Quantity() => new("qty", FieldKind.Decimal, "Quantity", true, true, Precision: 6, Minimum: 0.000001m, Maximum: 1000, AllowCondition: true);
    private static FormSchema Schema() => new(Tenant, SchemaId, 1, "Transfer", [Quantity()]);
    private static FormAccess Access() => new(Tenant, new HashSet<string> { "qty" }, new HashSet<Guid> { File });
    private static WorkflowDefinition Linear() => new(Tenant, Guid.NewGuid(), 1, "Transfer", 10, SchemaId, 1,
    [
        new("start", NodeKind.Start, [new("approve")], []),
        new("approve", NodeKind.Approval, [new("end")], [User]),
        new("end", NodeKind.End, [], []),
    ]);
    private static WorkflowDefinition Branch() => Linear() with
    {
        Nodes =
        [
            new("start", NodeKind.Start, [new("branch")], []),
            new("branch", NodeKind.ExclusiveBranch, [new("approve", new("qty", ConditionOperator.GreaterThan, J("\"5\""))), new("end")], []),
            new("approve", NodeKind.Approval, [new("end")], [User]),
            new("end", NodeKind.End, [], []),
        ],
    };
    private static CompiledWorkflow CompiledBranch() => WorkflowValidation.Compile(Branch(), Schema(), new HashSet<Guid> { User }).Value!;
    private static JsonElement J(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static IReadOnlyList<KeyValuePair<string, JsonElement>> Pairs(params (string Key, string Json)[] values) =>
        values.Select(value => KeyValuePair.Create(value.Key, J(value.Json))).ToArray();
    private static void Invalid<T>(DefinitionResult<T> result, string code) where T : class
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Contains(result.Issues, issue => issue.Code == code && !string.IsNullOrWhiteSpace(issue.Path));
    }
}
