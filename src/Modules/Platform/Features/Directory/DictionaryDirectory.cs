using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;

namespace Wes.Platform.Features.Directory;

public sealed partial class DirectoryService
{
    /// <summary>Creates a stable business code; labels are separately versioned via its entry fence.</summary>
    public Task<DirectoryWriteResult> CreateDictionaryAsync(TenantContext context, string category, string code,
        string languageKey, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty,
                ["Category", "Code", "LanguageKey"], cancellationToken)) return Denied();
            var entry = new DictionaryEntry(database.TenantId, category, code, languageKey);
            database.DictionaryEntries.Add(entry);
            return Audit(context, entry.Id, null, entry.Version, PlatformAuditAction.DictionaryChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.CreateDictionary, [category, code, languageKey],
            _ => AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty,
                ["Category", "Code", "LanguageKey"], cancellationToken));

    /// <summary>Changes a plain-text language label under the stable entry's version; codes/keys cannot be renamed.</summary>
    public Task<DirectoryWriteResult> ChangeTranslationAsync(TenantContext context, Guid entryId, string? expectedVersion,
        string language, string label, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty,
                ["Label"], cancellationToken)) return Denied();
            var entry = await database.DictionaryEntries.SingleOrDefaultAsync(x => x.Id == entryId, cancellationToken);
            if (entry is null) return Denied();
            var before = entry.Version;
            var decision = entry.Touch(expectedVersion);
            if (decision != SettingDecision.Changed) return VersionResult(decision);
            var canonical = DirectoryValue.Language(language);
            var translation = await database.DictionaryTranslations.SingleOrDefaultAsync(
                x => x.EntryId == entryId && x.Language == canonical, cancellationToken);
            if (translation is null) database.DictionaryTranslations.Add(new DictionaryTranslation(database.TenantId, entry.Id, canonical, label));
            else translation.Change(label);
            return Audit(context, entry.Id, before, entry.Version, PlatformAuditAction.DictionaryChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.ChangeTranslation, [entryId, expectedVersion, language, label],
            _ => AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty, ["Label"], cancellationToken));

    /// <summary>Adds/removes an explicit durable owner reference under the entry CAS fence, serialized with deletion.</summary>
    public Task<DirectoryWriteResult> ChangeDictionaryReferenceAsync(TenantContext context, Guid entryId, string? expectedVersion,
        Guid referenceId, bool referenced, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (referenceId == Guid.Empty) throw new ArgumentException("A stable referring owner identity is required.");
            if (!await AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty,
                ["References"], cancellationToken)) return Denied();
            var entry = await database.DictionaryEntries.SingleOrDefaultAsync(x => x.Id == entryId, cancellationToken);
            if (entry is null) return Denied();
            var before = entry.Version;
            var decision = entry.Touch(expectedVersion);
            if (decision != SettingDecision.Changed) return VersionResult(decision);
            var reference = await database.DictionaryReferences.SingleOrDefaultAsync(
                x => x.EntryId == entryId && x.ReferenceId == referenceId, cancellationToken);
            if (referenced && reference is null) database.DictionaryReferences.Add(new DictionaryReference(database.TenantId, entryId, referenceId));
            else if (!referenced && reference is not null) database.DictionaryReferences.Remove(reference);
            return Audit(context, entry.Id, before, entry.Version, PlatformAuditAction.DictionaryChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.ChangeDictionaryReference, [entryId, expectedVersion, referenceId, referenced],
            _ => AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty, ["References"], cancellationToken));

    /// <summary>Deletes only an unreferenced stable entry and its labels; references are also protected by restrictive database foreign keys.</summary>
    public Task<DirectoryWriteResult> DeleteDictionaryAsync(TenantContext context, Guid entryId, string? expectedVersion,
        Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty,
                ["Delete"], cancellationToken)) return Denied();
            var entry = await database.DictionaryEntries.SingleOrDefaultAsync(x => x.Id == entryId, cancellationToken);
            if (entry is null) return Denied();
            var before = entry.Version;
            var decision = entry.Touch(expectedVersion);
            if (decision != SettingDecision.Changed) return VersionResult(decision);
            if (await database.DictionaryReferences.AnyAsync(x => x.EntryId == entryId, cancellationToken))
                return new(DirectoryDecision.Referenced);
            database.DictionaryTranslations.RemoveRange(await database.DictionaryTranslations.Where(x => x.EntryId == entryId)
                .ToListAsync(cancellationToken));
            database.DictionaryEntries.Remove(entry);
            return Audit(context, entry.Id, before, entry.Version, PlatformAuditAction.DictionaryChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.DeleteDictionary, [entryId, expectedVersion],
            _ => AllowedAsync(context, "platform.dictionary.write", Guid.Empty, Guid.Empty, ["Delete"], cancellationToken));

    /// <summary>Reads codes unchanged with requested/default/key language fallback; label metadata is available only with Label permission.</summary>
    public async Task<DirectoryPage<DictionaryDirectoryRow>> ReadDictionaryAsync(TenantContext context, string category,
        string language, int offset = 0, int limit = 50, CancellationToken cancellationToken = default, bool? missingTranslation = null)
    {
        GuardRead();
        Bounds(offset, limit);
        var canonical = DirectoryValue.Language(language);
        var categoryCode = DirectoryValue.Code(category);
        var fields = Fields(context, "platform.dictionary.read", Guid.Empty, Guid.Empty,
            await Authorization.ReadGrantsAsync(context, cancellationToken));
        if (!fields.Contains("Id") || !fields.Contains("Category")) return new(false, []);
        if (missingTranslation.HasValue && !fields.Contains("Label")) return new(false, []);
        var fallback = await database.DirectoryTenants.AsNoTracking().Select(x => x.DefaultLanguage).SingleAsync(cancellationToken);
        var query = database.DictionaryEntries.AsNoTracking().Where(x => x.Category == categoryCode);
        if (missingTranslation.HasValue)
            query = query.Where(entry => !database.DictionaryTranslations.Any(text => text.EntryId == entry.Id
                && (text.Language == canonical || text.Language == fallback)) == missingTranslation.Value);
        var entries = await query
            .OrderBy(x => x.Code).ThenBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        var ids = entries.Select(x => x.Id).ToArray();
        var translations = fields.Contains("Label") ? await database.DictionaryTranslations.AsNoTracking()
            .Where(AnyId<DictionaryTranslation>(x => x.EntryId, ids)).Where(x => x.Language == canonical || x.Language == fallback)
            .ToListAsync(cancellationToken) : [];
        return new(true, entries.Select(entry =>
        {
            var translation = translations.FirstOrDefault(x => x.EntryId == entry.Id && x.Language == canonical)
                ?? translations.FirstOrDefault(x => x.EntryId == entry.Id && x.Language == fallback);
            return new DictionaryDirectoryRow(entry.Id, Pick(fields, "Category", entry.Category), Pick(fields, "Code", entry.Code),
                Pick(fields, "LanguageKey", entry.LanguageKey), fields.Contains("Label") ? translation?.Value ?? $"[{entry.LanguageKey}]" : null,
                fields.Contains("Label") ? translation?.Language : null, fields.Contains("Label") ? translation is null : null,
                Pick(fields, "Version", entry.Version));
        }).ToImmutableArray());
    }
}
