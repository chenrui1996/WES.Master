# T14 alarm aggregation, authorization and independent delivery

- Status: basic software slice implemented and verified; 2026-11-01. Owner: Notifications. Evidence and unrelated baseline failures: [194](194-t14-alarm-console-tests.md).
- User continuation explicitly requests implementing aggregation, authorization, independent delivery and pages after the proposed design; no production or physical permission is implied.
- Baseline: [RULE-14/PAR-05](../detailed-design/05-approved-rules-and-defaults.md), [module](../modules/notifications-and-jobs.md), [pages/tests](../detailed-design/notifications-and-jobs.md), [immutable ingress](191-t14-durable-alarm-occurrences.md).
- Scope: FUNC-NTF-003, PAGE-NTF-001/002, DTC-NTF-004/005 and VAL-06 alarm subset.

## Contract and boundaries

Keep every immutable occurrence. Notification aggregation uses source owner, resource, code, severity, trusted tenant and creator. Ordinary notifications use a fixed 30-second received-time window; High/Critical do not wait. Late source timestamps never replace current equipment state. Aggregate count is the count of distinct persisted memberships, never incoming requests. Acknowledged/Cleared are operator alarm state only, not equipment recovery, reservation release or issue closure. A new occurrence must not silently revive a cleared alarm or overwrite a later source recovery.

All new writes are atomic, versioned, audited and permanently idempotent by operationId and exact body. A source occurrence, membership and pending notification must not be split across commits. Existing raw ingress remains compatible: its facts can be processed through a bounded internal projection path with trusted creator metadata, without faking source authentication. Public manual ingress derives tenant and creator from the current authenticated subject. No public device-source ingress or manual device clear.

Current Platform authority is accessed only through owner application APIs. Tenant, real creator and current directory department are owner metadata, not request scope claims. Permissions:

- `notifications.alarms.read`: complete explicit alarm and occurrence fields.
- `notifications.alarms.acknowledge`, `notifications.alarms.manual.clear`, `notifications.alarms.silence`: action fields and complete read intersection.
- `notifications.alarms.manual.create`: explicit manual input fields; creator is current actor.
- `notifications.deliveries.read`, `notifications.deliveries.retry`: complete delivery/attempt fields and current alarm scope.

No automatic grant or permission reuse. Missing fields fail closed; read and precommit/write authorization are freshly re-evaluated, including replay. Manual clear only applies to Manual sources. High/Critical cannot be silenced. Ordinary silence defaults to 900 seconds, maximum 3600, requires actor/reason/scope/expiry audit. If silence/ack/clear require later lifecycle work, they remain disabled rather than appearing successful.

The exact alarm read union is the 14 alarm row fields plus `SourceEventId,OccurredAt,ReceivedAt,CorrelationId,Summary` (19); the delivery read union is the 10 delivery row fields plus `AttemptedAt` (11). Lists and details both require these full unions; delivery reads additionally require the alarm read intersection. There are no synthetic `Occurrences` or `Attempts` permission fields. Alarm actions require all 19 fields under their action permission plus `OperationId,ExpectedVersion,Reason` (and `Seconds` for silence), intersected with alarm read. Retry requires all 11 delivery fields plus those three action fields, intersected with both reads. Manual create requires `OperationId,ResourceId,Code,Severity,Summary` and alarm read.

Independent durable delivery carries an alarm reference, due time, channel, state and immutable attempts. The basic channel is a durable in-app notification recoverable through current-authorized queries; delivery Sent does not mean read. SignalR may be an optional online hint only, never persistence or replay authority. Email is explicitly Unavailable until a real configured adapter is supplied; no test adapter in production composition. Do not claim device authentication, actual email delivery or online push when they are not wired.

The implemented channel is InApp only; no fabricated Email row is created. High/Critical records are immediately durable Sent with an attempt. Ordinary records are Pending until due, then a bounded delivery query processes at most 100 currently authorized records transactionally, rechecking authority after save before commit. No worker or online push is implied; without a query ordinary records remain Pending. Explicit retry accepts Pending/Failed/Unavailable InApp records with CAS and audit, never duplicates Sent.

## HTTP and client agreement

All endpoints under `/api/notifications`, authenticated, no-store and independent default-false startup setting. Strict JSON and bounded pagination follow existing Host patterns. Expected endpoints:

- GET `/alarms?offset=0&limit=50`, returns `{ rows, offset, limit }`.
- GET `/alarms/{id}`, returns alarm plus `occurrences`.
- POST `/alarms/manual`, `{ operationId, resourceId, code, severity, summary }`.
- POST `/alarms/{id}/acknowledge`, `/clear`, `/silence`: `{ operationId, expectedVersion, reason, seconds? }`.
- GET `/deliveries?offset=0&limit=50`, returns `{ rows, offset, limit }`.
- GET `/deliveries/{id}`, returns delivery plus `attempts`.
- POST `/deliveries/{id}/retry`, `{ operationId, expectedVersion, reason }`.

Alarm row: `id, sourceOwner, sourceKind, resourceId, code, severity, firstOccurredAt, lastOccurredAt, occurrenceCount, state, version, acknowledgedBy, acknowledgedAt, silenceUntil`.
Occurrence: `id, sourceEventId, occurredAt, receivedAt, correlationId, summary`.
Delivery row: `id, alarmId, channel, state, occurrenceCount, dueAt, attemptCount, nextAttemptAt, errorCode, version`.
Attempt: `id, attemptedAt, state, errorCode`.
States serialized as stable names; sourceKind Device/Manual and severity Information/Warning/High/Critical. Mutations return a stable decision and current projection. Forbidden 403, invalid 400, missing 404, concurrent/idempotency conflict 409, adapter/provider failure safe 503/500. Never return empty success for failed query.

Vue pages use existing session/permission/navigation and API clients; strict parsers, semantic per-record lists/details, query states, bounded explicit pagination, detail selection and dangerous action preview. Stale/offline forbids new writes; Unknown preserves exact original operation separately from detail reload, manual same-key recovery only. Subject/permission changes clear private state; late results cannot repopulate old state. No local write queue, no raw secret/address display.

The current six-entry deployment UI catalogue has no Notifications code. These two pages use the existing Platform UI availability flag plus their own Notifications permission, without extending or mislabelling the six-entry catalogue contract. This UI-only availability does not imply Notifications backend readiness: its independent default-false setting still controls endpoint exposure, and a disabled endpoint is an explicit unavailable response, never an authorized empty list.

The basic pages show a complete fixed authorized projection with stable per-record list/detail sections, matching the existing message console; all alarm identity/severity/state/time/count and delivery identity/channel/state/due/attempt-count fields are mandatory. There are no optional hidden sensitive fields or client-supplied resource scope. Default count/severity/state remain visible without collapse. Extended grouped dense tables/column preferences and source-resource candidate chains remain follow-up interaction depth, not a grant or source authentication mechanism.

## Verification and stop

| Case | Required evidence |
|---|---|
| TC-T14-CONSOLE-01 | 29/30/31 received-time buckets, distinct memberships, late source facts and concurrent replay; original occurrences remain immutable |
| TC-T14-CONSOLE-02 | High/Critical immediate durable InApp; ordinary Pending until due, exactly one persisted attempt after authorized query |
| TC-T14-CONSOLE-03 | Complete 19/11 field unions, tenant/real creator/current department, all Platform field batches intersected, revoke after save/read/replay |
| TC-T14-CONSOLE-04 | Permanent exact-body operation receipts and safe numeric CAS; confirmation/manual-only clear, default900/max3600/reject3601 silence and high denial |
| TC-T14-CONSOLE-05 | Independent attempt failure rolls back, explicit retry version/audit/identity, Sent cannot duplicate, source facts never erased |
| TC-T14-CONSOLE-06 | Default-disabled strict/auth/no-store HTTP, safe non-success provider errors and bounded exact public projection |
| TC-T14-CONSOLE-07 | Two named pages, zero-work/error/stale/offline/permission/session switch, frozen Unknown outside query lifecycle and actual committed response-loss replay |

Real authorized `wes_test` only. Test 29/30/31-second aggregation boundaries, High/Critical immediate due, same-source replay, concurrent projection, save/commit failure rollback, late observations, tenant/creator/field and revoke-before-commit/replay/read, manual/device clear distinction, silence 900/3600/3601 and high severity denial, independent delivery/attempt failure and retry identities. UI/HTTP tests cover disabled configuration, strict parsing, query failures, paging, permission/offline, preview/Unknown and session switch. Whole Release, architecture, EF/actual SQL binary collations, changed-file formatting and document traceability. No production connection, real equipment, Jobs/announcements/cache, T12 gate change or claim of full T14 completion.
