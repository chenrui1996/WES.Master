# T07 File Content Validation Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a deterministic Platform-owned validator for PAR-04 file size and basic content declaration consistency, without enabling upload or file availability.

**Architecture:** A pure validator receives a file name, declared media type, and already bounded byte memory, and returns a stable rejection decision without retaining input data or performing I/O. It is an independent T07 foundation only; T04/T06 package dependencies and actual storage/scanner integration remain open.

**Tech Stack:** .NET 10, C# nullable, xUnit, existing Platform module test project.

**Spec:** [T07 content validation design](../../implementation/83-t07-file-content-validation.md), [PAR-04](../../detailed-design/05-approved-rules-and-defaults.md), [Platform file lifecycle](../../modules/platform-and-security.md).

## Global Constraints

- Maximum single-file size is exactly 20 MiB (20,971,520 bytes); size is based on actual content bytes.
- Allowed formats are PDF, PNG, JPEG, and UTF-8 plain text; extension, declared MIME, and basic signature/encoding must agree.
- Executables, scripts, HTML/SVG, archives, and macro documents are rejected.
- Scanner unavailable or failed means no file is Available; this validator must never produce or persist an Available state.
- File storage and DB are not ACID; this plan adds no upload, database, storage, scanner, endpoint, or background task.
- T07 depends on T04 and T06; this isolated slice does not mark those dependencies or T07 complete.

---

## Files

- `src\Modules\Platform\Features\Files\FileContentValidator.cs`: stable decision enum/result and pure content validation.
- `tests\Modules\Platform\FileContentValidatorTests.cs`: focused vectors for accepted and rejected content.
- `docs\implementation\83-t07-file-content-validation.md`: design, scope, and explicit integration limits.
- `docs\implementation\84-t07-file-content-validation-tests.md`: test matrix and actual evidence.
- `docs\implementation\readiness-register.md`: record the verified foundation without upgrading T07 status.
- `docs\superpowers\plans\2026-10-23-wes-development-work-packages.md`: mark only this T07 sub-slice done.

## Task 1: Test PAR-04 acceptance and bounds

**Interfaces:** `FileContentValidator.Validate(string fileName, string declaredContentType, ReadOnlyMemory<byte> content)` returns `FileContentValidationResult`; `FileContentValidationResult.Rejection` is a stable rejection enum value and `IsAccepted` indicates `None`.

- [x] Add xUnit tests accepting canonical PDF, PNG, JPEG, and UTF-8 text vectors.
- [x] Add a valid PDF fixture exactly 20,971,520 bytes and a 20,971,521-byte variant; assert the boundary accepts and the next byte rejects.
- [x] Add rejection tests for empty content and extension/MIME/signature mismatches.
- [x] Run `dotnet test tests\Modules\Wes.Modules.Tests.csproj --filter FullyQualifiedName~FileContentValidatorTests`; initial test-first run failed to compile because the validator namespace/type did not exist.

## Task 2: Test disallowed content and metadata

- [x] Add rejection cases for unknown extensions, MIME parameters other than absent/UTF-8 on plain text, path separators, and control characters in file names.
- [x] Add malformed UTF-8 and NUL text cases plus representative HTML, SVG, script, and XML markup cases.
- [x] Add PE, ELF, OLE, ZIP, GZIP, RAR, and 7z magic-byte cases disguised as allowed extensions/MIME.
- [x] Add assertions that the result contains only the decision and does not echo any filename, MIME, or content.
- [x] Re-run the focused test after implementation; all cases pass.

## Task 3: Implement the pure validator

- [x] Add `FileContentRejection` values `None`, `Empty`, `TooLarge`, `InvalidFileName`, `UnsupportedExtension`, `InvalidMediaType`, `MediaTypeMismatch`, `ForbiddenSignature`, `SignatureMismatch`, `InvalidUtf8`, `ContainsNul`, and `DisallowedMarkup`.
- [x] Add `FileContentValidationResult(FileContentRejection Rejection)` with `IsAccepted => Rejection == FileContentRejection.None`.
- [x] Set `MaximumBytes = 20 * 1024 * 1024`; reject size before scanning bytes, and reject zero-length content.
- [x] Reject filename `/`, `\`, and control characters; inspect the extension case-insensitively and accept only `.pdf`, `.png`, `.jpg`, `.jpeg`, `.txt`.
- [x] Parse media types with `System.Net.Http.Headers.MediaTypeHeaderValue`; accept only the matching canonical type and, for `text/plain`, absent charset or UTF-8 charset. Reject other parameters.
- [x] Reject executable/OLE/archive signatures before accepted-format checks; then require `%PDF-`, PNG signature `89 50 4E 47 0D 0A 1A 0A`, JPEG prefix `FF D8 FF`, or strict UTF-8 text with no NUL or obvious active markup.
- [x] Do not log, retain, normalize into a new object key, persist, or call a scanner from the validator.
- [ ] Treat text marker checks as a limited defense-in-depth heuristic, not as reliable classification of arbitrary scripts or malware.

## Task 4: Verify and record evidence

- [x] Run `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --filter FullyQualifiedName~FileContentValidatorTests`; 38 passed, 0 failed, 0 skipped.
- [x] Run a Release build of the Platform/Host test dependency graph and modified-file `dotnet format whitespace --verify-no-changes`.
- [x] Run the complete solution serially with `dotnet test WES.slnx -c Release --no-restore -m:1`; 884 passed, 0 failed, 0 skipped. A preceding parallel run was affected by initially absent assets and MySQL SSL I/O; after restore, the serial rerun passed.
- [x] Run `git diff --check` and inspect that no runtime registration, endpoint, migration, storage, or scanner fallback was added.
- [x] Replace every “待执行” in the test record with the exact command and observed output; preserve scanner, storage, T04/T06, and T07 completion as blocked/not-run.
- [x] Update the work-package plan with a checked sub-slice entry while leaving T07’s full package unresolved.
- [x] Commit the verified slice with the required Co-authored-by trailer and push normally to `origin/main`; verify remote HEAD and a clean worktree.
