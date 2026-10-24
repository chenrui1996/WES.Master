# Ops Web Industrial Visual Baseline

- Status: Written specification approved by the user; implementation is in place, with native 200% desktop-zoom inspection still outstanding.
- Date: 2026-10-24
- Scope: All current Ops Web routes and future pages built on the same operations shell.
- Design authority: This document defines the visual baseline and must remain subordinate to approved business, authorization, safety, and page-interaction contracts.

## 1. Problem and goals

The current operations client has a functional Element Plus-based shell, but global spacing, page headers, filters, dense tables, and navigation do not yet form a consistent industrial operations visual system. The client needs a practical, calm, high-information layout for warehouse and operations users that makes frequent scanning efficient without reducing clarity or hiding consequential actions.

Goals:

- Establish one reusable visual language for the existing Ops Web pages and future pages.
- Increase useful records and controls visible in the available viewport while preserving readable Chinese text and accessible interaction targets.
- Make navigation, page hierarchy, filters, tables, forms, and system states visually consistent.
- Retain the existing Vue 3, Element Plus, semantic HTML, owner-provided data, and authorization boundaries.
- Validate the baseline at desktop, narrow, zoomed, keyboard-only, and relevant loading/error/empty states.

## 2. Approved design decisions

These decisions were confirmed during design review:

1. Apply the baseline to every current Ops Web route, including login, sessions, users/departments, roles, tenants, configuration/dictionary, and audit.
2. Use a light steel-gray work surface, graphite navigation, and safety amber for primary actions/current navigation. Red, amber, and green remain semantic error, warning, and confirmed-success colors; status is never communicated by color alone.
3. Default dense lists and queues to compact density; keep forms and high-consequence confirmations at standard density. Offer compact, standard, and comfortable table density options.
4. Keep all interactive controls/touch targets at least 44px high even when data rows and filter spacing are compact.
5. Make the desktop navigation a narrow rail by default, with an explicit expand/collapse control and full accessible route names. On narrower screens, use a horizontal, locally scrollable navigation.
6. Apply density as a whole-client preference stored locally and partitioned by the current environment, tenant, and user. It stores only a density enum, never identity tokens, permissions, row data, or form content.
7. Reuse the existing Vue/Element Plus stack; do not introduce a new grid, design system dependency, or icon package for this visual pass.

## 3. Visual language

### 3.1 Palette and semantic use

Use neutral, low-glare surfaces and clear separators:

- Work area: cool light gray.
- Page and editor surfaces: white.
- Primary shell/navigation: dark graphite.
- Primary action and current navigation accent: safety amber.
- Default text: dark slate; secondary text: muted slate.
- Borders: neutral gray with sufficient contrast to distinguish controls and table rows.
- Status colors: success green, warning amber, danger red; each status includes text or an accessible label.

Initial palette values should be recorded as CSS custom properties in the implementation. They are candidates rather than final compliance claims; verify text and control contrast against WCAG 2.2 AA before acceptance. Do not use gradients, decorative shadows, flashing, or color-only status encoding.

### 3.2 Typography and spacing

- Use a system font stack appropriate for Windows and Chinese text; do not add a remote font dependency.
- Base body and form text: approximately 14px at 1.45–1.5 line height.
- Dense table text: approximately 13px; retain legibility at 200% browser zoom.
- Page titles: approximately 20px; section headings: approximately 16px; use consistent weight and spacing rather than oversized display typography.
- Use a 4px-based spacing scale (4, 8, 12, 16, 24, 32px) and a restrained industrial control radius around 4px.
- Long identifiers and translated text must wrap or be available in a non-destructive detail affordance; they must not overlap adjacent content.

## 4. Application shell and responsive layout

- Header baseline: approximately 56px; retain environment and connection context without competing with page work.
- Desktop navigation: narrow 56–64px rail by default; expandable to approximately 224px. Collapsed entries retain accessible names and keyboard-accessible text labels/tooltips. The current route uses both a semantic current-page marker and the amber accent.
- At narrower widths, move navigation to a horizontal, locally scrollable region; do not create page-level horizontal overflow.
- Content uses a compact 16px default inset with more room at wide viewports; all work regions must allow `min-width: 0`.
- Keep page title, current scope, and primary action in a single compact header row where space permits.
- Filters remain an explicit form with clear submit/reset affordances. They may wrap into multiple rows rather than shrink controls below their usable size.
- Tables own their horizontal/vertical overflow and sticky header. Detail/edit regions use a separate scroll context when needed.
- Do not hide existing authorized actions to make a viewport appear cleaner. At narrow widths, use a documented wrap, local scroll, or detail layout.
- Preserve the existing page-specific risk confirmations and not-applicable/read-only behavior. This baseline is not a mobile-equivalence claim for workflows the detailed design marks desktop-oriented.

## 5. Density and page patterns

Density applies to tabular data presentation and non-critical spacing, not to authorization, workflow, or control semantics.

| Density | Default use | Target data row height | Behavior |
|---|---|---:|---|
| Compact | Lists, queues, audit and high-volume read views | 36–40px | Default for dense data; controls remain at least 44px |
| Standard | Forms, details and general-purpose tables | 44–48px | Used regardless of the user's table preference for forms and confirmations |
| Comfortable | User-selected readability mode | 52–56px | Adds row breathing room without changing columns, sorting, filtering, or page size |

Provide one accessible, named density control in the application shell. The setting is global to the current browser identity context; switching it changes only presentation. Invalid or unavailable local preference data falls back to compact for data lists and standard for forms, and does not prevent page use.

Reusable patterns:

- **List/workbench:** compact page header, inline filters, clearly grouped actions, authorized table columns, local scrolling, and explicit paging.
- **Form/detail:** standard text and form spacing, grouped sections, visible version/scope context where the existing contract requires it, and stable save/cancel placement.
- **Login/session:** clear identity/security hierarchy without the authenticated operations navigation.
- **Audit/read-only:** compact scanning, clear timestamp and status hierarchy, and no visual implication of a write affordance where none exists.
- **Column settings:** visually aligned with the same typography, borders, focus treatment, and spacing as other page controls.

## 6. Shared implementation surface

Implementation should use the existing `src/style.css` as the token and responsive-shell foundation. The application shell in `src/App.vue` owns the navigation toggle and density preference control. Existing components and pages should use shared semantic classes/tokens rather than duplicating new page-specific color and spacing values. `ColumnSettings.vue` and each current route should be brought into the same visual rhythm while retaining their current API interactions, permissions, and named operations.

Page style changes must preserve:

- The server remains authoritative for authorization and field projection.
- A hidden column is not a security boundary; unauthorized fields must not be returned or restored from preferences.
- Existing page route, API, filtering, paging, confirmation, stale/offline, error, and read-only semantics.
- Existing high-consequence behavior and explicit user-confirmed actions.
- Existing keyboard access and browser-native behavior unless a tested accessibility improvement replaces it.

No backend, data model, persistence schema, route, or business capability changes are part of the visual baseline.

## 7. Preference safety and error handling

The local density preference is a presentation-only enum. Its key must include application environment, tenant ID, and authenticated user ID; scope changes must not reuse another identity's preference. Clear the active in-memory density context on logout/identity change before loading the new context. Do not store tokens or any data beyond the density enum.

Malformed or inaccessible local preference state must be handled explicitly and safely: use the default density and surface a non-blocking notice only if project conventions support one. Do not silently claim a preference was saved if persistence failed. The layout itself remains usable if browser storage is disabled.

Navigation collapse state may remain in memory for the current app lifetime. It is not a permission signal and must not affect available routes or operations.

## 8. Accessibility and usability

- Meet WCAG 2.2 AA contrast for text, focus indicators, control boundaries, and semantic status treatments.
- Preserve a visible `:focus-visible` outline and keyboard operation for navigation, filters, column settings, density selection, and all actions.
- Provide an accessible name for each collapsed navigation item and the density control.
- Preserve 200% zoom and reflow without clipping page titles, controls, validation, or high-consequence context.
- Keep action/touch targets at least 44px; compact data rows do not imply undersized controls.
- Do not encode state, authorization, selection, or risk by color alone.
- Respect reduced-motion preferences; the design does not require animation to communicate state.

## 9. Verification and acceptance

Acceptance is based on behavior and rendered layout, not token presence alone:

1. All current Ops Web routes use the same tokenized typography, spacing, control, and state styles; login remains a distinct unauthenticated layout.
2. At 1440px, the default narrow rail leaves measurably more table width than the expanded rail; both states remain fully keyboard-operable.
3. Dense list routes default to compact data rows; standard/comfortable selections change row density without reducing controls below 44px or altering data/permission behavior.
4. A density preference is restored only for the matching environment/tenant/user context; logout, tenant switch, user switch, malformed storage, and disabled storage do not leak a previous preference or break the UI.
5. At 1024px and 768px, navigation and content adapt without document-level horizontal overflow; a wide table may scroll inside its own container.
6. At 200% zoom and with long Chinese/English identifiers, text remains readable and controls remain reachable.
7. Keyboard-only review reaches every control with visible focus; status messages remain semantic and accessible.
8. Contrast ratios are measured for final foreground/background combinations and meet WCAG 2.2 AA.
9. Existing component tests and real-browser flows remain green; add focused tests for density scope/persistence, navigation collapse, and responsive shell behavior.
10. Strict frontend type-check/build succeeds; no new runtime dependency is introduced.

Non-goals for this design: redesigning individual business workflows, changing APIs or data, enabling production/physical-control actions, introducing team-synchronized preferences, or claiming field/terminal acceptance.
