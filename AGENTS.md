# WES.Master Agent Rules

Read [README.md](README.md) and the relevant canonical documents before changing this project. This file is an index and enforcement summary; the linked specifications are authoritative.

## Current phase

- Controlled implementation is approved. The project initiator explicitly approved implementation on main and the existing module designs, REQ-ROUTE-001..027 and WES-RULES-1.0 as the software baseline. Follow the dependency-ordered development plan; document each slice before coding and testing.
- MySQL is the first implementation database; SQL Server and PostgreSQL compatibility is deferred, not removed. Follow [ADR-0002](docs/adr/ADR-0002-mysql-first-implementation.md).
- Read the [readiness register](docs/implementation/readiness-register.md) before starting a dependent slice. Software approval does not approve site safety, production connections, deployments, commits or pushes. Missing evidence remains blocked.
- 2026-10-23 follow-up authorization: the initiator explicitly approved committing and pushing each completed, verified work package to the existing GitHub remote. Do not infer production/database/physical safety sign-off from this authorization; do not force-push or mark incomplete packages complete.

## Mandatory rules

- Preserve all Seven.Master business capabilities unless the [scope matrix](docs/01-business-scope-and-status.md) explicitly marks them removed; distinguish implemented, partial, reserved, and unverified behavior.
- Follow the [modular-monolith architecture](docs/02-system-architecture.md). Never reference another module's Infrastructure, DbContext, private entity, or internal table.
- WMS is the only inventory authority. Device/protocol details stay behind adapters. Stacker, FourWay, and External WCS remain separate execution boundaries.
- Follow the [engineering governance](docs/03-engineering-governance.md) for code, comments, docs, tests, security, and change review.
- Every behavior change needs a requirement, detailed design, test case, and traceability update. Do not silently change an approved state machine, API contract, provider, offline policy, Route assumption, or safety boundary.
- Do not add Builder/source-code generation or code that requires a generator to maintain.
- For Route/scheduling changes, read all three [Route documents](docs/route/01-as-is-and-research-review.md), [target design](docs/route/02-target-routing-and-scheduling.md), and [test catalog](docs/route/03-route-test-catalog.md). Never claim physical collision freedom or bounded completion from simulation or paper results alone.
- Run the narrowest relevant tests plus required provider/contract checks. Report unrun tests and unresolved decisions explicitly; never turn an unavailable capability into a success-shaped fallback.
