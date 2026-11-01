# T14 alarm console verification evidence

- Date: 2026-11-01; scope: [193](193-t14-alarm-console-design.md).
- Only authorized isolated `wes_test` fixtures; no production connection, device operation or production migration.
- This is the alarm console software slice, not complete T14, source authentication, device recovery, email, SignalR, Jobs, announcements or cache acceptance.

## Implementation and contracts

Notifications owns immutable occurrences, distinct memberships, aggregation buckets, operator lifecycle, permanent operation receipts/audits and independent InApp delivery attempts. Host uses current Platform owner APIs, never Platform persistence. Nine authenticated strict/no-store routes are independently gated by default-false `Wes:Notifications:Enabled`.

Full field permission unions are 19 alarm and 11 delivery fields, with current creator/department and alarm-read intersection for deliveries. Action unions include the corresponding projection and `OperationId,ExpectedVersion,Reason`; silence also `Seconds`; manual input includes `OperationId`. No automatic grant. Platform's bounded field checker is called in batches of at most 16, intersecting creator sets from **every** batch; a successful first batch cannot grant the rest.

Raw ingress remains compatible and immutable. Thirty-second received-time buckets retain every distinct membership and late source timestamp; High/Critical is immediate and cannot be silenced. Confirmation/manual clear do not change device state, inventory, reservations or issues. Ordinary InApp Pending records become Sent only through bounded authorized delivery queries; no worker or online push is claimed. Sent is persistence, not read acknowledgement; manual clear does not erase notifications.

Three owner migrations add console tables, JavaScript-safe numeric version counters, and receipt capacity. Up applies only in test fixtures; Down is inspected, never executed.

## Recorded verification

The following implementation-agent runs are reported evidence; parent final verification is recorded separately below, not inferred from those reports.

| Runner | Recorded result |
|---|---|
| Actual initial console schema test | 1 failed before implementation |
| Release `AlarmConsoleTests` + `AlarmOccurrenceTests` | 85 passed: 41 console + 44 raw regression |
| Release `NotificationsHttpTests` | 29 passed, including actual HTTPS/MySQL Chromium |
| Architecture | 29 passed |
| Whole Release build | 0 warnings, 0 errors |
| EF drift, actual Up/Down SQL and binary metadata | passed; Down not executed |

Provider cases cover 29/30/31-second buckets, late source timestamps, high immediate delivery, independent attempt rollback/recovery, permanent CAS retry, immutable facts, tenant/creator/field filtering and revoke-after-save rollback. HTTP tests include real Platform current department/field authorization, default-disabled composition, strict JSON and safe failures.

Recorded failure sequence: the initial `AuthorizedMySqlHasAlarmAggregateSchema` failed because the model lacked `ntf_alarms`, then passed after implementation. The first HTTP run passed 5/failed 23 because the fixture lacked an eligible password credential; correcting the fixture preserved production authorization. The next run passed 23/failed 5 because the actual Platform creator API rejects projection sizes above 16; batching/intersecting all fields fixed the adapter and produced 28/28. The first actual browser reached manual 200 Applied and list 200 but rejected UUID-string versions; the safe numeric-counter migration aligned the public contract and produced browser 1/1, HTTP 29/29.

Receipt capacity is preventive coverage, **not recorded RED**: escaped JSON expands a legal 512-character Chinese summary beyond the earlier 2048-character receipt bound. The 4096 bound and `MaximumUnicodeSummaryAndReasonPreservePermanentEscapedBodyWithoutProviderTruncation` verify Applied/Replayed with bodies exceeding 2048, without weakening input length or exact replay.

## Client verification and browser

Initial new client files and navigation produced missing-file/route failures before implementation. A later Vitest `it.each` fixture mistakenly expanded an empty array into absent arguments; changing the case to an object containing `rows` corrected the test harness, not application behavior.

Before final authorization-union alignment, all 52 frontend files / 1273 tests and strict `vue-tsc` plus Vite build passed. The last complete-row UI addition passed 103 focused tests/build; exact backend field-union alignment subsequently passed 108 focused tests. These are overlapping historical runs, not cumulative coverage.

Native Chromium mock fixtures verified literal script-like summary text, preview and layout at widths 375/768/1440 and 200% root font size. Integrated browser mock routing was inconclusive (pending/aborted requests); native Playwright was the working alternative. PowerShell-piped Chinese selectors were corrupted, so the temporary native fixture used ASCII CSS selectors. Mock browser evidence does not substitute for actual HTTP/Platform/MySQL.

The persistent actual browser test logs in through real HTTPS, creates a manual High alarm, checks immutable literal summary and disabled silence, commits acknowledgement then drops its response, manually replays the exact original body/key, clears only the Manual alarm and reads independent delivery/attempts. The Host runner supplies isolated tenant/grants and cleans owned rows and its server.

## Parent final verification

Parent verification uses persistent TRX counters, not agent claims. The first whole-solution invocation finished Route/Persistence/Architecture but its shell handle was unavailable on resumption; no result exists for its remaining projects, so they are rerun explicitly.

| Parent final runner | Result |
|---|---|
| Complete Persistence | 1689 passed, 0 failed/skipped |
| Complete Route | 228 passed, 0 failed/skipped |
| Complete Architecture | 29 passed, 0 failed/skipped |
| Focused console + raw owner rerun | 85 passed, 0 failed/skipped |
| Focused Notifications HTTP, including final built-client actual HTTPS/MySQL Chromium | 29 passed, 0 failed/skipped |
| Full frontend final | 52 files, 1313 passed |
| Whole Release build | 0 warnings, 0 errors |
| Final `vue-tsc --noEmit` + Vite production build | passed |

Complete Modules finished **1377 passed / 2 failed / 0 skipped** (1379 total). No green whole-suite claim is made:

- `MessageCountsHttpDoesNotReturnPartialProjection("LeasedCount")` failed in the existing `SignInAsync` fixture, expected login200 but received500 before the counts request. All seven missing-field cases passed in the narrow rerun; the intermittent login root cause is **unresolved**, not claimed fixed.
- `WmsStockLedgerRejectsInvalidQueriesExplicitly("?kind=3")` expected400 but got200; this reproduces in the narrow rerun. The unchanged approved `StockLedgerKind` already defines AdjustmentIn=3, while the unchanged old invalid-query test still rejects it. Notifications work did not change WMS/Platform or these tests. Follow-up is recorded in TodoList, not a rollback of approved adjustment behavior.
- Combined narrow baseline recheck: **23 passed / 1 failed**. These are unrelated baseline checks, not failed console cases or evidence that the whole suite is green.

The standalone DirectoryReceipts project is **not in WES.slnx** and links the same directory receipt fixtures already run by full Persistence. Its optional invocation failed for absent Release output, then absent restore assets; it was not restored or treated as an additional passing project. Full solution coverage consists of the four projects above, not that duplicate runner.

Final Release EF pending-model check passed; incremental Up/Down SQL was generated outside the repository and inspected for binary charsets/collations, bigint versions and receipt4096. Down was never executed. Formatting all changed C#, including generated migrations, first found three migration encoding errors; the existing formatter corrected only those encodings. Full changed-file format verification and the subsequent whole Release build passed (0 warnings/errors). No behavior was changed by formatting.

After formatting and rebuilding, the parent repeated console/raw **85**, Notifications HTTP including actual browser **29**, and Architecture **29**: all passed, zero failed/skipped. These reruns overlap the table; they do not erase the full Modules baseline failures. All test-owned servers stopped; no helper is left running.

Final changed-document check: 13 Markdown files, 956 local targets, 10 unchanged historical external missing targets, **0 new broken targets**. `git diff --check` passed. Verified basic slice is published through a normal `HEAD:main` push with the required co-author trailer; final remote hash/clean-worktree verification is the release gate, not permission to deploy.

After exact authorization-union alignment and per-field/intersection/Pending-retry coverage, the parent reran all 52 frontend files: **1313 passed, 0 failed**. The earlier 1273/103/108 results are history, not added to this count.

Commands from repository root:

```powershell
dotnet test WES.slnx -c Release --no-restore
dotnet test tests\Persistence -c Release --no-build --no-restore --filter "FullyQualifiedName~AlarmConsoleTests|FullyQualifiedName~AlarmOccurrenceTests"
dotnet test tests\Modules -c Release --no-build --no-restore --filter "FullyQualifiedName~NotificationsHttpTests"
dotnet test tests\Architecture -c Release --no-build --no-restore
```

Frontend commands from `clients\ops-web`: `npm test -- --maxWorkers=1`, then `npm run build`. Tests/artifacts stay outside the repository; credentials are not logged.

## Pitfalls and remaining boundaries

Updated entries: **INF-004, INF-009, GEN-003, GEN-004, GEN-006** for generated-file gate completeness, actual schema/version/receipt capacity, all consumers' exact field unions, batched current authority intersection and real response-loss/baseline evidence. No new entry ID. The intermittent baseline login500 remains a hypothesis-free recorded failure, not attributed to resource contention without evidence.

Unavailable: email adapter, SignalR hint, live source authentication and authoritative device recovery. Production/other-provider/physical verification is unrun. Dense table/filter/column-preference/map and recipient-preference depth remains follow-up UI scope, without closing parent DTC-NTF-004/005 or the other T14 capabilities.
