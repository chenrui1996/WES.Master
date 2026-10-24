# Ops Web Industrial Visual Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish and apply one accessible, compact industrial visual baseline across every current Ops Web route and document it for future pages.

**Architecture:** Keep Vue 3 and Element Plus; add a small identity-scoped visual-preference module and a density control, let `App.vue` own shell navigation and density state, and use shared CSS tokens/classes across the existing pages. Preserve every API, owner projection, permission check, business workflow, and safety gate.

**Tech Stack:** Vue 3.5, TypeScript 5.9, Element Plus 2.14, Vitest 5, Vue Test Utils, Playwright 1.64, Vite 8, .NET 10 HTTPS browser fixture.

**Spec:** `docs/superpowers/specs/2026-10-24-ops-web-industrial-visual-baseline-design.md`

## Global Constraints

- Use a light steel-gray work surface, graphite navigation, and safety amber for primary actions/current navigation.
- Red, amber, and green remain semantic error, warning, and confirmed-success colors; status is never communicated by color alone.
- Base body and form text: approximately 14px at 1.45–1.5 line height.
- Dense table text: approximately 13px; retain legibility at 200% browser zoom.
- Use a 4px-based spacing scale (4, 8, 12, 16, 24, 32px).
- Header baseline: approximately 56px.
- Desktop navigation: narrow 56–64px rail by default; expandable to approximately 224px.
- Compact data rows target 36–40px; standard rows 44–48px; comfortable rows 52–56px.
- Keep all interactive controls/touch targets at least 44px high.
- The local density preference key includes application environment, tenant ID, and authenticated user ID; the stored value is only a density enum.
- Reuse the existing Vue/Element Plus stack; do not introduce a new grid, design system dependency, or icon package.
- The server remains authoritative for authorization and field projection; hidden columns are not a security boundary.
- Do not change backend APIs, data models, routes, business capabilities, workflow, or physical-control gates.
- Keep the UI usable if browser storage is disabled; surface preference persistence errors explicitly.

---

## File map

| File | Responsibility |
|---|---|
| `clients/ops-web/src/features/visual-preferences.ts` | Density enum, identity/environment storage key, safe read/write, reactive preference scoped to current identity |
| `clients/ops-web/src/features/visual-preferences.spec.ts` | Key isolation, valid/invalid preference, identity switch, and storage failure tests |
| `clients/ops-web/src/components/DensityControl.vue` | Accessible compact/standard/comfortable selector and non-blocking persistence warning |
| `clients/ops-web/src/components/DensityControl.spec.ts` | Selector label, emitted update, and persistence warning behavior |
| `clients/ops-web/src/App.vue` | Collapsible desktop navigation, narrow-screen navigation, current density root attribute, and shell control |
| `clients/ops-web/src/style.css` | Design tokens, shared shell, control, page, table-density, focus, and breakpoint styles |
| `clients/ops-web/src/components/ColumnSettings.vue` | Shared density, focus, borders, and spacing for column preferences |
| `clients/ops-web/src/features/identity/LoginPage.vue` | Authenticated-shell-independent baseline styling |
| `clients/ops-web/src/features/identity/SessionPage.vue` | Shared page/form/table styling without changing session behavior |
| `clients/ops-web/src/features/platform/UsersPage.vue` | Shared page, filter, table, editor, and status styling |
| `clients/ops-web/src/features/platform/RolesPage.vue` | Shared page, filter, table, editor, and status styling |
| `clients/ops-web/src/features/platform/TenantsPage.vue` | Shared page, filter, table, editor, and status styling |
| `clients/ops-web/src/features/platform/SettingsPage.vue` | Shared page, filter, table, editor, and status styling |
| `clients/ops-web/src/features/platform/AuditPage.vue` | Shared read-only evidence and table styling |
| `clients/ops-web/tests/platform.spec.ts` | Real Chromium assertions for shell, density persistence, all routes, viewport overflow, and existing business flow |
| `docs/detailed-design/06-ops-web-visual-style-baseline.md` | Normative implementation-ready style rules for current and future Ops Web pages |
| `docs/detailed-design/00-shared-page-and-test-standard.md` | Link to the visual baseline and clarify its relationship with the existing page contract |
| `docs/detailed-design/README.md` | Add the visual-baseline document to the design reading order |
| `docs/implementation/20-t03-t04-operations-client.md` | Record the shipped T03/T04 client styling baseline and its verified evidence |

All edits in this plan are frontend, browser-test, or directly related documentation changes. Do not stage unrelated dirty T03/T04 files.

## Task 1: Add identity-scoped density preferences

**Files:**
- Create: `clients/ops-web/src/features/visual-preferences.ts`
- Create: `clients/ops-web/src/features/visual-preferences.spec.ts`

**Interfaces:**
- Produces `DensityMode = "compact" | "standard" | "comfortable"`.
- Produces `visualPreferenceKey(environment: string, tenantId: string, userId: string): string`.
- Produces `readDensityPreference(storage: Pick<Storage, "getItem">, key: string): { density: DensityMode; warning: string | null }`.
- Produces `writeDensityPreference(storage: Pick<Storage, "setItem">, key: string, density: DensityMode): string | null`.
- Produces `useVisualPreferences(environment: string, storageProvider?: () => Pick<Storage, "getItem" | "setItem">)` returning readonly `density`, readonly `storageWarning`, and `setDensity(mode)`.
- Reads identity from `sessionState.subject`; while no subject exists, uses the list default `"compact"` and does not access identity-scoped storage.
- Keys must include the application environment, tenant UUID, and user UUID. Stored values are only the density enum strings.

- [ ] **Step 1: Write failing tests for preference key isolation and valid reads**

Add these tests to `visual-preferences.spec.ts`:

```ts
import { beforeEach, describe, expect, it } from "vitest";
import { forgetIdentity, sessionState } from "./identity/identity-api";
import {
  readDensityPreference,
  visualPreferenceKey,
} from "./visual-preferences";

describe("visual density preferences", () => {
  beforeEach(() => {
    forgetIdentity();
    localStorage.clear();
  });

  it("isolates preferences by environment, tenant, and user", () => {
    const first = visualPreferenceKey("production", "tenant-a", "user-a");
    expect(visualPreferenceKey("test", "tenant-a", "user-a")).not.toBe(first);
    expect(visualPreferenceKey("production", "tenant-b", "user-a")).not.toBe(first);
    expect(visualPreferenceKey("production", "tenant-a", "user-b")).not.toBe(first);
  });

  it("reads only a valid density enum from the matching key", () => {
    const key = visualPreferenceKey("test", "tenant-a", "user-a");
    localStorage.setItem(key, "comfortable");
    expect(readDensityPreference(localStorage, key)).toEqual({
      density: "comfortable",
      warning: null,
    });
  });
});
```

- [ ] **Step 2: Run the new preference spec and verify it fails because the module is absent**

Run from `clients/ops-web`:

```powershell
npm test -- src/features/visual-preferences.spec.ts --reporter=dot
```

Expected: module-resolution failure for `visual-preferences.ts`; existing tests remain untouched.

- [ ] **Step 3: Add malformed-value, identity-change, and storage-failure tests**

Extend the imports with `effectScope` from `"vue"`, `Subject` from `./identity/identity-api`, and `useVisualPreferences`/`writeDensityPreference` from `./visual-preferences`. Add this typed subject factory:

```ts
const subject = (tenantId: string, userId: string): Subject => ({
  tenantId,
  userId,
  sessionId: "session",
  requiresPasswordChange: false,
  credentialVersion: null,
  grants: [],
});
```

Then add these complete tests:

```ts
it("falls back to compact and exposes a warning for malformed stored values", () => {
  const key = visualPreferenceKey("test", "tenant-a", "user-a");
  localStorage.setItem(key, "unknown-density-value");
  const result = readDensityPreference(localStorage, key);
  expect(result.density).toBe("compact");
  expect(result.warning).toBeTruthy();
});

it("falls back to compact and exposes a warning when storage reads fail", () => {
  const storage = {
    getItem: (_key: string) => {
      throw new DOMException("blocked", "SecurityError");
    },
  };
  const key = visualPreferenceKey("test", "tenant-a", "user-a");
  expect(readDensityPreference(storage, key)).toMatchObject({
    density: "compact",
    warning: expect.any(String),
  });
});

it("loads a different value after the authenticated tenant or user changes", () => {
  const keyA = visualPreferenceKey("test", "tenant-a", "user-a");
  const keyB = visualPreferenceKey("test", "tenant-b", "user-b");
  localStorage.setItem(keyA, "comfortable");
  localStorage.setItem(keyB, "standard");
  const scope = effectScope();
  const preferences = scope.run(() => useVisualPreferences("test"))!;
  sessionState.subject = subject("tenant-a", "user-a");
  expect(preferences.density.value).toBe("comfortable");
  sessionState.subject = subject("tenant-b", "user-b");
  expect(preferences.density.value).toBe("standard");
  expect(localStorage.getItem(keyA)).toBe("comfortable");
  expect(localStorage.getItem(keyB)).toBe("standard");
  scope.stop();
});

it("keeps the selected density in memory and exposes a warning when storage writes fail", () => {
  const storage = {
    getItem: (key: string) => localStorage.getItem(key),
    setItem: (_key: string, _value: string) => {
      throw new DOMException("blocked", "SecurityError");
    },
  };
  const key = visualPreferenceKey("test", "tenant-a", "user-a");
  expect(writeDensityPreference(storage, key, "standard")).toBeTruthy();
  const scope = effectScope();
  const preferences = scope.run(() => useVisualPreferences("test", () => storage))!;
  sessionState.subject = subject("tenant-a", "user-a");
  preferences.setDensity("comfortable");
  expect(preferences.density.value).toBe("comfortable");
  expect(preferences.storageWarning.value).toBeTruthy();
  scope.stop();
});
```

- [ ] **Step 4: Run the expanded spec and observe the intended failures**

Run:

```powershell
npm test -- src/features/visual-preferences.spec.ts --reporter=dot
```

Expected: failures name the missing exports or behaviors; no production code has been written yet.

- [ ] **Step 5: Implement the preference module**

Use `sessionState.subject?.tenantId` and `sessionState.subject?.userId` as the identity scope. Watch the identity key with synchronous invalidation; set density to compact and clear the old warning before reading the next key. Catch only storage access/read/write operations, return a visible warning for malformed/unavailable storage, and keep the current in-memory selection usable. Do not serialize JSON, tokens, grants, rows, or form state.

- [ ] **Step 6: Run the preference spec and verify all isolation/fallback tests pass**

Run:

```powershell
npm test -- src/features/visual-preferences.spec.ts --reporter=dot
```

Expected: all preference tests pass, including tenant/user/environment separation and storage failure.

- [ ] **Step 7: Commit the independently tested preference module**

```powershell
git add -- clients/ops-web/src/features/visual-preferences.ts clients/ops-web/src/features/visual-preferences.spec.ts
git commit -m "feat(ops-web): scope density preferences by identity" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 2: Add the accessible density control

**Files:**
- Create: `clients/ops-web/src/components/DensityControl.vue`
- Create: `clients/ops-web/src/components/DensityControl.spec.ts`

**Interfaces:**
- Props: `{ modelValue: DensityMode; storageWarning: string | null }`.
- Emits: `update:modelValue` with a `DensityMode`.
- The control exposes a visible label and native select/input semantics; persistence errors render as a non-blocking `role="status"` message.

- [ ] **Step 1: Write a failing component test**

Test that an accessible label such as `"显示密度"` is associated with the select; selecting `"standard"` emits `update:modelValue` with `"standard"`; and a non-null storage warning is rendered with `role="status"`.

- [ ] **Step 2: Run the component spec and verify it fails for the missing component**

Run:

```powershell
npm test -- src/components/DensityControl.spec.ts --reporter=dot
```

Expected: module-resolution failure for `DensityControl.vue`.

- [ ] **Step 3: Implement the minimal native control**

Use a labelled `<select>` with the three exact modes and localized labels. Render the warning without blocking the select. Do not add a dependency, custom popup, or decorative icon.

- [ ] **Step 4: Run the component spec and verify its behavior**

Run:

```powershell
npm test -- src/components/DensityControl.spec.ts --reporter=dot
```

Expected: the label, update event, and status message assertions pass.

- [ ] **Step 5: Commit the independently tested control**

```powershell
git add -- clients/ops-web/src/components/DensityControl.vue clients/ops-web/src/components/DensityControl.spec.ts
git commit -m "feat(ops-web): add accessible density control" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 3: Tokenize the shell and add responsive navigation controls

**Files:**
- Modify: `clients/ops-web/src/App.vue`
- Modify: `clients/ops-web/src/style.css`
- Modify: `clients/ops-web/tests/platform.spec.ts`

**Interfaces:**
- `App.vue` consumes `useVisualPreferences(import.meta.env.MODE)` and `DensityControl`.
- Desktop navigation has a button with `data-test="navigation-toggle"`, `aria-expanded`, and `aria-controls` referencing the navigation element.
- The authenticated layout carries `data-density` with the current `DensityMode`.
- The shell remains permission-filtered as it is today; collapsing navigation must not change route permission or page operations.

- [ ] **Step 1: Add browser assertions before implementation**

Extend the existing `"real HTTPS identity, authorized named pages and responsive frame"` Playwright test in `tests/platform.spec.ts` immediately after login:

```ts
const nav = page.getByRole("navigation", { name: "管理导航" });
const toggle = page.getByTestId("navigation-toggle");
await expect(toggle).toHaveAttribute("aria-expanded", "false");
const collapsedWidth = await page.locator(".sidebar").evaluate(element => element.getBoundingClientRect().width);
await toggle.click();
await expect(toggle).toHaveAttribute("aria-expanded", "true");
const expandedWidth = await page.locator(".sidebar").evaluate(element => element.getBoundingClientRect().width);
expect(expandedWidth).toBeGreaterThan(collapsedWidth + 100);
await expect(nav.getByRole("link", { name: "用户与部门", exact: true })).toBeVisible();
await page.getByLabel("显示密度").selectOption("comfortable");
await expect(page.locator(".app-layout")).toHaveAttribute("data-density", "comfortable");
const densityEntries = await page.evaluate(() => Object.entries(localStorage));
expect(densityEntries).toHaveLength(1);
expect(densityEntries[0]?.[0]).toContain("production");
expect(densityEntries[0]?.[1]).toBe("comfortable");
expect(JSON.stringify(densityEntries)).not.toContain("opaque");
```

Capture the one density entry after selection. After logout, assert that `Object.entries(localStorage)` still equals the captured entry and `sessionStorage.length` is zero. The key intentionally includes environment/tenant/user identifiers; assert that no token, grants, session state, rows, or form values were stored. Assert that a second user/tenant preference is not loaded using the unit coverage from Task 1; do not add identity API credentials to browser storage.

- [ ] **Step 2: Run the real-browser test to verify the missing shell behavior**

Build the current reviewed bundle, then run the controlled fixture:

```powershell
Set-Location D:\Junheinrich\Junheinrich.Master\WES.Master\clients\ops-web
npm run build
Set-Location D:\Junheinrich\Junheinrich.Master\WES.Master
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages" --logger "console;verbosity=quiet"
```

Expected: the browser test fails because the navigation toggle and density control do not yet exist. The fixture must remain the existing HTTPS Kestrel + isolated `wes_test` path; never point Playwright at a production host.

- [ ] **Step 3: Add semantic preference and navigation state to the shell**

In `App.vue`, add a `ref(true)` for collapsed desktop navigation, pass the composable's `density` and `storageWarning` to `DensityControl`, bind the density value at `.app-layout`, and add the toggle's `aria-expanded`, `aria-controls`, visible focus, and accessible name. Keep tenant context and session/environment status visible. On logout or subject change, the composable must switch preference scope; navigation state does not persist or affect permissions.

- [ ] **Step 4: Add global foundation tokens and shell CSS**

In `style.css`, define named `--wes-*` color, typography, spacing, header, expanded-nav, collapsed-nav, and density tokens; map relevant Element Plus color/border/text variables to them. Use an initial amber token with foreground color chosen to meet WCAG contrast; do not use white text on an amber background unless the measured ratio passes. Set desktop navigation to 56–64px by default and approximately 224px expanded; at the 1024px breakpoint move it to a horizontal local-scrolling region. Keep all controls at least 44px, visible focus, `min-width: 0`, and reduced-motion support.

- [ ] **Step 5: Run component tests and strict build**

Run:

```powershell
Set-Location D:\Junheinrich\Junheinrich.Master\WES.Master\clients\ops-web
npm test -- src/features/visual-preferences.spec.ts src/components/DensityControl.spec.ts --reporter=dot
npm run build
```

Expected: both specs and `vue-tsc --noEmit`/Vite build pass.

- [ ] **Step 6: Commit only the shell, tokens, and browser assertion changes**

```powershell
git add -- clients/ops-web/src/App.vue clients/ops-web/src/style.css clients/ops-web/tests/platform.spec.ts
git commit -m "feat(ops-web): add compact responsive operations shell" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 4: Apply the baseline to every current route and column settings

**Files:**
- Modify: `clients/ops-web/src/components/ColumnSettings.vue`
- Modify: `clients/ops-web/src/features/identity/LoginPage.vue`
- Modify: `clients/ops-web/src/features/identity/SessionPage.vue`
- Modify: `clients/ops-web/src/features/platform/UsersPage.vue`
- Modify: `clients/ops-web/src/features/platform/RolesPage.vue`
- Modify: `clients/ops-web/src/features/platform/TenantsPage.vue`
- Modify: `clients/ops-web/src/features/platform/SettingsPage.vue`
- Modify: `clients/ops-web/src/features/platform/AuditPage.vue`
- Modify: `clients/ops-web/tests/platform.spec.ts`

**Interfaces:**
- Every authenticated route marks its page root with a shared `wes-page` class; the login form uses the same tokens but remains outside the authenticated shell.
- Density classes/data attributes affect table row height and permitted spacing only; they do not shrink buttons, labels, inputs, confirmation targets, or focus indicators.
- Existing `table-scroll`, `toolbar`, `actions`, and `editor` semantics remain intact.

- [ ] **Step 1: Add route-level browser assertions before page styling**

In the existing loop over the five authorized platform routes in `tests/platform.spec.ts`, assert the page root:

```ts
await expect(page.locator(".workspace .wes-page")).toHaveCount(1);
```

After the session route is reached, assert its `.wes-page` marker. On each of the five platform routes, assert its table scroll region is visible. On `/login`, assert the login panel is visible and `.app-layout`/authenticated navigation is absent.

- [ ] **Step 2: Run the controlled browser test and verify the page marker assertion fails**

Run the frontend build, then the exact isolated HTTPS browser fixture command from Task 3 Step 2.

Expected: the new `.wes-page` assertion fails on the first existing page. Do not weaken the assertion to pass without marking all in-scope routes.

- [ ] **Step 3: Normalize login and session pages without changing behavior**

Add `wes-page` to the session route and apply shared heading, filter, status, form, and table tokens. Keep the login panel outside the authenticated shell while aligning its typography, controls, validation, and spacing. Preserve all login/session/API/security behavior.

- [ ] **Step 4: Normalize users and roles pages without changing behavior**

Add `wes-page` and shared page-header/filter/editor/table classes to the users and roles roots. Preserve route-specific filters, permission checks, affected-subject projections, role/delegation behavior, confirmations, error handling, paging, and authorized fields exactly.

- [ ] **Step 5: Normalize tenants and settings pages without changing behavior**

Add `wes-page` and shared page-header/filter/editor/table classes to the tenants and settings roots. Preserve tenant reauthentication, setting/dictionary filtering, write confirmations, translation behavior, error handling, paging, and field-level authorization.

- [ ] **Step 6: Normalize audit and column-settings styles without changing behavior**

Add `wes-page` to the audit root; retain read-only evidence semantics and its separate time/filter toolbar. Update `ColumnSettings.vue` borders, focus ring, labels, buttons, and spacing to use shared tokens without changing its authorized-column whitelist or keyboard ordering.

- [ ] **Step 7: Add density styles and verify all interactive targets remain 44px**

Use `.app-layout[data-density="compact"]`, `"standard"`, and `"comfortable"` only for table rows and non-critical layout spacing. Target 36–40px, 44–48px, and 52–56px respectively. Keep `button`, `input`, `select`, and other interactive targets at least 44px regardless of density. Forms and confirmations always use standard spacing.

- [ ] **Step 8: Run all page component specs and strict build**

Run:

```powershell
Set-Location D:\Junheinrich\Junheinrich.Master\WES.Master\clients\ops-web
npm test -- src/features/identity/LoginPage.spec.ts src/features/identity/SessionPage.spec.ts src/features/platform/UsersPage.spec.ts src/features/platform/RolesPage.spec.ts src/features/platform/TenantsPage.spec.ts src/features/platform/SettingsPage.spec.ts src/features/platform/AuditPage.spec.ts src/components/ColumnSettings.spec.ts --reporter=dot
npm run build
```

Expected: the named route/component specs pass and strict TypeScript/Vite build succeeds.

- [ ] **Step 9: Commit the route styling as one coherent visual slice**

```powershell
git add -- clients/ops-web/src/components/ColumnSettings.vue clients/ops-web/src/features/identity/LoginPage.vue clients/ops-web/src/features/identity/SessionPage.vue clients/ops-web/src/features/platform/UsersPage.vue clients/ops-web/src/features/platform/RolesPage.vue clients/ops-web/src/features/platform/TenantsPage.vue clients/ops-web/src/features/platform/SettingsPage.vue clients/ops-web/src/features/platform/AuditPage.vue clients/ops-web/tests/platform.spec.ts
git commit -m "style(ops-web): unify current operations pages" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 5: Document the baseline and link future-page guidance

**Files:**
- Create: `docs/detailed-design/06-ops-web-visual-style-baseline.md`
- Modify: `docs/detailed-design/00-shared-page-and-test-standard.md`
- Modify: `docs/detailed-design/README.md`
- Modify: `docs/implementation/20-t03-t04-operations-client.md`

- [ ] **Step 1: Add the visual baseline document**

Record the approved palette semantics, typography, spacing, shell sizes, breakpoints, density targets, identity-scoped preference rule, accessibility constraints, reusable list/form/audit patterns, and measurable acceptance checks from the approved spec. State explicitly that page contracts, permissions, APIs, and physical-control gates remain authoritative.

- [ ] **Step 2: Link it from the shared page standard and detailed-design index**

Add one cross-reference in the shared standard and include it in the README reading order. Keep the existing approved dense-page interaction contract and DENSITY/DATA_COMPLEXITY/CONSEQUENCE model; the new visual baseline implements presentation, not a replacement for those contracts.

- [ ] **Step 3: Update the operations client implementation record**

Document only verified shipped behavior and refer to the design baseline. Do not state WCAG, responsive, or browser acceptance as passed until Task 6 evidence exists.

- [ ] **Step 4: Review and commit only the four design/implementation documents**

```powershell
git add -- docs/detailed-design/06-ops-web-visual-style-baseline.md docs/detailed-design/00-shared-page-and-test-standard.md docs/detailed-design/README.md docs/implementation/20-t03-t04-operations-client.md
git diff --cached --check
git commit -m "docs(ops-web): define reusable industrial visual baseline" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 6: Verify visual, accessibility, and regression acceptance

**Files:**
- Modify: `clients/ops-web/tests/platform.spec.ts` only if test assertions need to complete requirements from earlier tasks.
- No new dependencies.

- [x] **Step 1: Add measurable browser checks for layout, density, and overflow**

In the existing real Chromium test, for 1440px assert that the default rail is 56–64px and the expanded rail is 216–232px; verify the expanded width exceeds collapsed width by at least 100px. At 375px, 768px, and 1024px assert `document.documentElement.scrollWidth <= document.documentElement.clientWidth`, and allow horizontal overflow only inside `.table-scroll`. Verify unwrapped-row targets of 36–40px, 44–48px, and 52–56px; for rows that grow to preserve long content, assert each density step increases block padding by 8–12px instead of truncating content. Assert buttons, text inputs, and selects remain at least 44px high.

- [x] **Step 2: Add browser checks for preference isolation and safe storage**

Assert that local storage contains only the matching environment/tenant/user preference key and an exact density enum value; it contains no access token, grant, row, password, or form fields. Change density, log out, and verify that the preference remains while session state is cleared. The preference key itself is intentionally identity-scoped and is not treated as session data. Use Task 1 unit tests for distinct tenant/user keys, malformed values, and storage exceptions.

- [x] **Step 3: Add a measured contrast assertion for final tokens**

In `tests/platform.spec.ts`, read the computed foreground/background token values from the page and calculate relative luminance using the WCAG formula. Assert at least 4.5:1 for normal text and 3:1 for meaningful control boundaries/focus indicators. If any pair fails, adjust the token and rerun; do not lower the threshold or exempt the primary action text.

- [ ] **Step 4: Verify keyboard focus, narrow reflow, and 200% zoom**

In Chromium, use keyboard-only navigation to reach the navigation toggle, density selector, and page actions; assert the focused control matches `:focus-visible` and remains operable. Check document-level horizontal overflow at 375px, 768px, and 1024px, and inspect the current routes at actual 200% browser zoom for clipped titles, labels, validation, and actions. Treat the 720px viewport check as supplemental reflow coverage, not a claim that it emulates browser zoom. Record no color-only status and verify `prefers-reduced-motion` suppresses non-essential transitions.

- [x] **Step 5: Run the complete frontend test suite and strict production build**

Run:

```powershell
Set-Location D:\Junheinrich\Junheinrich.Master\WES.Master\clients\ops-web
npm test -- --reporter=dot
npm run build
```

Expected: every Vitest spec passes and `vue-tsc --noEmit && vite build` succeeds.

- [x] **Step 6: Run the actual browser acceptance through the isolated HTTPS fixture**

Run:

```powershell
Set-Location D:\Junheinrich\Junheinrich.Master\WES.Master
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages" --logger "console;verbosity=quiet"
```

Expected: the real Chromium test passes against the existing HTTPS Kestrel fixture and isolated `wes_test`, with no production URL, saved browser trace, video, or screenshot.

Task 6 Steps 1–3, 5, and 6 are complete. Step 4 remains open: Chromium now verifies keyboard traversal to the density selector, navigation toggle, and audit query action, along with 375/768/1024px reflow and reduced-motion behavior; a manual all-route inspection at native 200% desktop browser zoom is still required. Step 7 remains open until the final diff is reviewed and the acceptance evidence is committed.

- [x] **Step 7: Review the final diff and commit the acceptance evidence**

Use `git diff --check`, `git status --short`, and targeted diffs for the files listed in this plan. Confirm no `.superpowers/brainstorm/`, `node_modules`, `dist`, `test-results`, credentials, or unrelated T03/T04 files are staged. Commit the final browser assertions only if they were not included in Task 4:

```powershell
git add -- clients/ops-web/tests/platform.spec.ts
git commit -m "test(ops-web): verify visual baseline in Chromium" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Push only the resulting intended commits to `origin main` after their exact file lists and test evidence are checked.

## Spec coverage self-review

| Spec requirement | Plan task |
|---|---|
| Light industrial palette, semantic states, typography, spacing | Tasks 3–5 |
| All current routes and future-page standard | Tasks 4–5 |
| Collapsible narrow rail and responsive navigation | Tasks 3 and 6 |
| Compact/standard/comfortable table density and ≥44px controls | Tasks 2–4 and 6 |
| Environment/tenant/user-scoped local preference | Task 1; shell wiring in Task 3; browser coverage in Task 6 |
| Element Plus reuse and no new dependency | Tasks 3–4 |
| Permission/API/business behavior unchanged | Tasks 3–5 |
| Storage failure, keyboard, zoom, contrast, reduced motion | Tasks 1, 3, and 6 |
| Current and future design documentation | Task 5 |
| Build, component tests, actual HTTPS browser validation | Tasks 4 and 6 |
