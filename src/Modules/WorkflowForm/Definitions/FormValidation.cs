using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wes.WorkflowForm.Definitions;

/// <summary>Pure version-bound schema and form-data validation; no persistence or permission acquisition.</summary>
internal static class FormValidation
{
    private const decimal MaximumDecimal = 9999999999999999999999.999999m;
    private static readonly Regex KeyPattern = new("\\A[A-Za-z][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex LabelPattern = new("\\A[A-Za-z][A-Za-z0-9_.]*\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex DecimalPattern = new("\\A-?(0|[1-9][0-9]{0,21})(\\.[0-9]{1,6})?\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsKey(string value) => value.Length is >= 1 and <= 64 && KeyPattern.IsMatch(value);

    internal static DefinitionResult<FormSchema> Schema(FormSchema schema)
    {
        if (schema.TenantId == Guid.Empty || schema.Id == Guid.Empty || schema.Version < 1 || !IsKey(schema.BusinessType))
            return DefinitionResult<FormSchema>.Invalid("Identity", "schema");
        if (schema.Fields.Count is < 1 or > 100)
            return DefinitionResult<FormSchema>.Invalid("Fields", "schema.fields");

        var issues = new List<DefinitionIssue>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < schema.Fields.Count; index++)
        {
            var field = schema.Fields[index];
            var path = $"schema.fields[{index}]";
            if (!IsKey(field.Key) || !keys.Add(field.Key))
                issues.Add(new("FieldKey", path));
            if (field.LabelKey.Length is < 1 or > 128 || !LabelPattern.IsMatch(field.LabelKey))
                issues.Add(new("Label", path));
            if (!Enum.IsDefined(field.Kind) || !Enum.IsDefined(field.Sensitivity))
                issues.Add(new("FieldType", path));
            if (field.AllowCondition && (field.Kind == FieldKind.File || field.Sensitivity == FieldSensitivity.Restricted))
                issues.Add(new("ConditionField", path));

            var constraintsValid = field.Kind switch
            {
                FieldKind.Text => field.MaxLength is >= 1 and <= 4096 && field.Precision is null &&
                    field.Minimum is null && field.Maximum is null,
                FieldKind.Decimal => field.MaxLength is null && field.Precision is >= 0 and <= 6 &&
                    ValidBound(field.Minimum, field.Precision.Value) && ValidBound(field.Maximum, field.Precision.Value) &&
                    (field.Minimum is null || field.Maximum is null || field.Minimum <= field.Maximum),
                FieldKind.Boolean or FieldKind.File => field.MaxLength is null && field.Precision is null &&
                    field.Minimum is null && field.Maximum is null,
                _ => false,
            };
            if (!constraintsValid)
                issues.Add(new("Constraints", path));
            if (field.DefaultValue is { } value && (field.Kind == FieldKind.File || !Value(field, value)))
                issues.Add(new("Default", path));
        }

        if (issues.Count > 0) return new(null, issues.AsReadOnly());
        var fields = schema.Fields.Select(field => field with { DefaultValue = field.DefaultValue?.Clone() }).ToArray();
        return DefinitionResult<FormSchema>.Valid(schema with { Fields = Array.AsReadOnly(fields) });
    }

    internal static DefinitionResult<FormData> Data(
        FormSchema schema, FormAccess access,
        IReadOnlyList<KeyValuePair<string, JsonElement>> values,
        IReadOnlyList<KeyValuePair<string, JsonElement>> businessValues)
    {
        var schemaResult = Schema(schema);
        if (!schemaResult.IsValid) return new(null, schemaResult.Issues);
        if (access.TenantId != schema.TenantId)
            return DefinitionResult<FormData>.Invalid("Scope", "data.tenant");
        if (values.Count > 100 || businessValues.Count > 100)
            return DefinitionResult<FormData>.Invalid("Fields", "data");

        var fields = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var issues = new List<DefinitionIssue>();
        AddInput(values, false);
        AddInput(businessValues, true);
        var writable = access.WritableFields.ToHashSet(StringComparer.Ordinal);
        var output = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            var path = $"data.{field.Key}";
            var present = input.TryGetValue(field.Key, out var value);
            if (!present && field.Writable && field.DefaultValue is { } defaultValue)
            {
                value = defaultValue;
                present = true;
            }
            if (!present)
            {
                if (field.Required) issues.Add(new("Required", path));
                continue;
            }
            if (field.Writable && !writable.Contains(field.Key))
                issues.Add(new("FieldPermission", path));
            if (!Value(field, value))
            {
                issues.Add(new("Value", path));
                continue;
            }
            if (field.Kind == FieldKind.File && value.ValueKind != JsonValueKind.Null &&
                !access.AccessibleFiles.Contains(Guid.ParseExact(value.GetString()!, "D")))
                issues.Add(new("FilePermission", path));
            output.Add(field.Key, value.Clone());
        }
        return issues.Count == 0
            ? DefinitionResult<FormData>.Valid(new(new ReadOnlyDictionary<string, JsonElement>(output)))
            : new(null, issues.AsReadOnly());

        void AddInput(IReadOnlyList<KeyValuePair<string, JsonElement>> entries, bool business)
        {
            foreach (var (key, value) in entries)
            {
                var path = $"data.{key}";
                if (!fields.TryGetValue(key, out var field))
                    issues.Add(new("UnknownField", path));
                else if (business && field.Writable)
                    issues.Add(new("BusinessField", path));
                else if (!business && !field.Writable)
                    issues.Add(new("ReadOnly", path));
                if (!input.TryAdd(key, value))
                    issues.Add(new("Duplicate", path));
            }
        }
    }

    internal static bool Value(FormField field, JsonElement value, bool allowNull = true)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return allowNull && !field.Required;
        return field.Kind switch
        {
            FieldKind.Text => value.ValueKind == JsonValueKind.String && field.MaxLength is { } length &&
                value.GetString()!.Length <= length && (!field.Required || !string.IsNullOrWhiteSpace(value.GetString())),
            FieldKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FieldKind.File => value.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(value.GetString(), "D", out var id) && id != Guid.Empty,
            FieldKind.Decimal => TryDecimal(value, field.Precision, out var number) &&
                (field.Minimum is null || number >= field.Minimum) && (field.Maximum is null || number <= field.Maximum),
            _ => false,
        };
    }

    internal static bool TryDecimal(JsonElement value, int? precision, out decimal number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.String || precision is not (>= 0 and <= 6)) return false;
        var text = value.GetString()!;
        if (text.Length > 30 || !DecimalPattern.IsMatch(text)) return false;
        var point = text.IndexOf('.', StringComparison.Ordinal);
        if (point >= 0 && text.Length - point - 1 > precision.Value) return false;
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    private static bool ValidBound(decimal? value, int precision) => value is null ||
        (value >= -MaximumDecimal && value <= MaximumDecimal && decimal.Round(value.Value, precision) == value);
}
