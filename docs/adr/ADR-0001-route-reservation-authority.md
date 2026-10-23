# ADR-0001: Route Reservation Authority

- Status: accepted
- Date: 2026-10-23
- Decision owner/reviewer: Project owner (decision confirmed for this design); Route and equipment-safety owners review operational details before implementation
- Related scope/requirements/risks: `SCOPE-OPS-02`, `REQ-OPS-007`, `REQ-ROUTE-028`, `REQ-ROUTE-029`; stale writer, duplicate grant, unknown physical occupancy, provider-specific transaction semantics

## Background and problem

Stacker and FourWay must not grant conflicting resource reservations or dispatch movement commands from stale scheduler instances. The existing Route target design requires durable recovery and owner-scoped grants, but leaves the reservation authority and failover mechanism open. HotStore/Redis availability and database-provider locking differences must not change which movement commands are authorized.

## Decision

Each WCS Pack uses a durable database reservation ledger as the authority for resource grants, ownership, and lifecycle state. The same durable authority stores one active writer lease per Pack and a monotonically increasing fencing epoch. Lease acquisition or takeover advances the epoch atomically.

HotStore/Redis may be used for rebuildable indexes, snapshots, and read acceleration only. A cache hit, cache miss, expiry, or stale value cannot grant, release, or prove physical occupancy.

Each command envelope includes the Pack's writer epoch, reservation token, task/vehicle identity, map version, and command ID. The durable dispatch gate validates the active epoch and reservation before transitioning a command to dispatching. A lease takeover fences future durable mutations and dispatch authorization by the old epoch. Commands already in flight at takeover are treated as outcome-unknown; the new writer reconciles their result and current physical state before issuing conflicting work.

## Constraints and invariants

1. A resource grant is committed durably before a command that depends on it may enter dispatch.
2. Every grant/release/change is scoped to Pack, resource, route/task, vehicle, MapVersion, writer epoch, and request/owner identity.
3. A stale fencing epoch cannot grant, extend, release, or authorize a new command dispatch.
4. A unique reservation identity and provider-tested atomic transaction semantics prevent two conflicting grants from both becoming authoritative.
5. Grant and release retries are idempotent for the same request/owner identity; a different owner cannot release another owner's grant.
6. Expired lease time alone never frees a resource when physical state is unknown. The resource remains protected until authoritative feedback/position reconciliation supports release.
7. On restart or takeover, the new writer reconciles durable grants, commands in flight, feedback offsets, and physical device/vehicle state before dispatch resumes.
8. Loss of HotStore cannot affect reservation correctness; loss of the durable authority disables control writes.
9. This software protocol is not a physical collision-avoidance or safety certification; safety PLC and site-specific protective systems remain authoritative for physical safety.

## Alternatives and trade-offs

### Database multi-writer locking as the primary authority — not selected

Database transactions and locks could permit several schedulers to compete directly. Their isolation, lock hints, and uniqueness behavior differ among SQL Server, PostgreSQL, and MySQL; establishing identical physical-control semantics would require provider-specific implementation and extensive conformance evidence. Reconsider only if measured availability or throughput needs justify it and every supported provider passes the same reservation contract.

### Redis/HotStore as grant authority — rejected

A fast cache authority would couple movement authorization to cache persistence, failover, replication, and stale-key behavior. It cannot be the only recoverable source for physical occupancy or grants; it is retained only as a derived acceleration layer.

## Safety, data, compatibility, and operations impact

- Each Pack needs a durable lease/epoch record, reservation ledger, and a readiness gate for control writes.
- Lease acquisition/takeover must atomically increment an epoch in the durable database. Provider implementations may differ internally but must satisfy the same logical fencing contract.
- The dispatcher checks the epoch immediately before the durable dispatch transition. If a command may already have crossed the adapter boundary during failover, its status is `Unknown` until queried/reconciled; the new writer must not assume it was not executed.
- Read-only queries may remain available without a valid writer lease only where an explicit deployment policy allows them. All control writes fail closed if the database, lease, or fencing state is unavailable.
- Database provider support is conditional on passing concurrency, fencing, takeover, crash-recovery, and restore tests. SQLite or in-memory substitutes are not evidence of production provider behavior.
- Existing Route tasks retain their MapVersion and reservation history through recovery. Manual cleanup cannot delete an unresolved physical occupancy record.

## Verification and evidence

Requirements are defined in [Route target design](../route/02-target-routing-and-scheduling.md) and linked in the [traceability matrix](../04-requirement-design-test-traceability.md):

- `REQ-ROUTE-028`: durable ledger grant authority — `ROUTE-TC-040`, `ROUTE-TC-043..045`.
- `REQ-ROUTE-029`: fencing, failover, and unknown occupancy — `ROUTE-TC-041..042`, `ROUTE-TC-046`.

The detailed tests are designed, not executed. Run them against SQL Server, PostgreSQL, and MySQL with concurrency and crash fault injection before claiming support. Record provider version, transaction/isolation configuration, writer epochs, durable rows, command state, physical reconciliation result, and test output.

## Consequences and recovery

The design favors explicit recovery and correctness over cache-dependent availability. A Pack without a valid writer epoch cannot accept new control work. Failover may pause scheduling while the new writer reconciles active reservations and uncertain commands. If reconciliation cannot establish physical state, the resource remains protected and the issue is escalated to operations rather than being automatically released.

Rollback of a software deployment does not erase ledger history or free active physical occupancy. Recovery restores the durable authority, obtains a newer writer epoch, reconciles active work, and only then resumes dispatch.

## Review conditions

Review before implementation if a provider cannot implement atomic epoch takeover, if site requirements demand active writes during database unavailability, if equipment accepts commands without a queryable outcome, or if multi-writer operation is needed for measured throughput. Any such change requires a superseding ADR and updated Route tests.
