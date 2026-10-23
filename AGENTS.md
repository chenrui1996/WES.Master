# WES.Master Agent Rules

Read [README.md](README.md) and the relevant canonical documents before changing this project. This file is an index and enforcement summary; the linked specifications are authoritative.

## Current phase

- The approved startup deliverable is documentation only. Do not create application code, solution/project files, migrations, generated code, or scaffolding during this phase.
- Before any later implementation phase, obtain explicit phase approval and update the current design/test traceability and acceptance documents first.

## Mandatory rules

- Preserve all Seven.Master business capabilities unless the [scope matrix](docs/01-business-scope-and-status.md) explicitly marks them removed; distinguish implemented, partial, reserved, and unverified behavior.
- Follow the [modular-monolith architecture](docs/02-system-architecture.md). Never reference another module's Infrastructure, DbContext, private entity, or internal table.
- WMS is the only inventory authority. Device/protocol details stay behind adapters. Stacker, FourWay, and External WCS remain separate execution boundaries.
- Follow the [engineering governance](docs/03-engineering-governance.md) for code, comments, docs, tests, security, and change review.
- Every behavior change needs a requirement, detailed design, test case, and traceability update. Do not silently change an approved state machine, API contract, provider, offline policy, Route assumption, or safety boundary.
- Do not add Builder/source-code generation or code that requires a generator to maintain.
- For Route/scheduling changes, read all three [Route documents](docs/route/01-as-is-and-research-review.md), [target design](docs/route/02-target-routing-and-scheduling.md), and [test catalog](docs/route/03-route-test-catalog.md). Never claim physical collision freedom or bounded completion from simulation or paper results alone.
- Run the narrowest relevant tests plus required provider/contract checks. Report unrun tests and unresolved decisions explicitly; never turn an unavailable capability into a success-shaped fallback.
