import type { Component } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import UsersPage from "./platform/UsersPage.vue";
import RolesPage from "./platform/RolesPage.vue";
import TenantsPage from "./platform/TenantsPage.vue";
import SettingsPage from "./platform/SettingsPage.vue";
import SessionPage from "./identity/SessionPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "./identity/identity-api";

interface PagingCase { label: string; component: Component; endpoint: string; row: Record<string, unknown>; submit?: string }
const cases: PagingCase[] = [
  { label: "用户分页", component: UsersPage, endpoint: "/api/platform/users", row: {
    account: "operator", name: "Operator", departmentId: null, enabled: true, version: "v", credentialVersion: null, roleIds: [],
  } },
  { label: "角色分页", component: RolesPage, endpoint: "/api/platform/roles", row: {
    code: "OPERATOR", enabled: true, version: "v", rules: [], affectedSubjects: null,
  } },
  { label: "租户分页", component: TenantsPage, endpoint: "/api/platform/tenants", row: {
    name: "Tenant", enabled: true, defaultLanguage: "zh-CN", version: "v",
  } },
  { label: "配置分页", component: SettingsPage, endpoint: "/api/platform/settings", row: {
    key: "query.snapshotTtlSeconds", value: "600", source: "ApprovedDefault", version: "v", kind: "P", state: "Configured",
  } },
  { label: "字典分页", component: SettingsPage, endpoint: "/api/platform/dictionary", submit: '[data-test="dictionary-filters"]', row: {
    category: "task", code: "READY", languageKey: "task.ready", label: "准备", resolvedLanguage: "zh-CN", missingTranslation: false, version: "v",
  } },
  { label: "会话分页", component: SessionPage, endpoint: "/api/identity/sessions", row: {
    version: "v", client: 0, current: false, createdAt: "2026-10-24T00:00:00Z", lastActivityAt: "2026-10-24T00:00:00Z",
    expiresAt: "2026-10-25T00:00:00Z", revokedAt: null, requiresPasswordChange: false, state: 0,
  } },
];
const wrappers: ReturnType<typeof mount>[] = [];
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v", requiresPasswordChange: false,
    grants: ["users", "roles", "tenants", "settings", "dictionary", "departments"].map(owner => ({
      permission: `platform.${owner}.read`, scope: 2, departments: [],
      fields: ["Id", "Account", "Name", "Code", "Enabled", "Version", "Key", "Value", "Source", "Label", "LanguageKey"],
    })) };
});
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); forgetIdentity(); vi.unstubAllGlobals(); });

it.each(cases)("$label uses live 25-row paging and resets scope when switching 50/100", async entry => {
  const records = Array.from({ length: 60 }, (_, index) => ({ ...entry.row, id: `item-${index}`, tenantId: `item-${index}` }));
  const requests: URLSearchParams[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const parsed = new URL(url, "https://localhost");
    if (parsed.pathname !== entry.endpoint) return new Response('{"authorized":true,"rows":[]}');
    requests.push(parsed.searchParams);
    const offset = Number(parsed.searchParams.get("offset")); const limit = Number(parsed.searchParams.get("limit"));
    const rows = records.slice(offset, offset + limit);
    return new Response(JSON.stringify(entry.endpoint.startsWith("/api/identity/") ? rows : { authorized: true, rows }));
  }));
  const wrapper = mount(entry.component); wrappers.push(wrapper); await flushPromises();
  if (entry.submit) { await wrapper.get(entry.submit).trigger("submit"); await flushPromises(); }
  expect(requests.at(-1)!.get("limit")).toBe("25");
  const pager = wrapper.get(`nav[aria-label="${entry.label}"]`);
  expect(pager.text()).toContain("本页 25 条");
  await pager.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("25");
  for (const limit of ["50", "100"]) {
    await pager.get("select").setValue(limit); await flushPromises();
    expect(requests.at(-1)!.get("offset")).toBe("0");
    expect(requests.at(-1)!.get("limit")).toBe(limit);
    expect(pager.text()).toContain(`本页 ${limit === "50" ? "50" : "60"} 条`);
  }
  expect(pager.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
});

it.each(cases)("$label does not keep old-limit rows as stale data when a size-change read fails", async entry => {
  const records = Array.from({ length: 25 }, (_, index) => ({ ...entry.row, id: `old-${index}`, tenantId: `old-${index}` }));
  let reads = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const parsed = new URL(url, "https://localhost");
    if (parsed.pathname !== entry.endpoint) return new Response('{"authorized":true,"rows":[]}');
    if (++reads > 1) return new Response("{}", { status: 503 });
    return new Response(JSON.stringify(entry.endpoint.startsWith("/api/identity/") ? records : { authorized: true, rows: records }));
  }));
  const wrapper = mount(entry.component); wrappers.push(wrapper); await flushPromises();
  if (entry.submit) { await wrapper.get(entry.submit).trigger("submit"); await flushPromises(); }
  const pager = wrapper.get(`nav[aria-label="${entry.label}"]`);
  await pager.get("select").setValue("50"); await flushPromises();
  expect(pager.text()).toContain("本页 0 条");
  expect(wrapper.findAll(".query-state tbody tr")).toHaveLength(0);
  expect(pager.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
  expect(wrapper.findAll('[role="alert"]').some(alert => alert.text().includes("503"))).toBe(true);
});

const filters = [
  { entry: cases[0]!, selector: '[name="filterAccount"]', form: '[aria-label="用户筛选"]', value: "OPERATOR", key: "account" },
  { entry: cases[1]!, selector: '[name="filterRoleCode"]', form: '[data-test="role-filters"]', value: "OPERATOR", key: "code" },
  { entry: cases[2]!, selector: '[name="filterTenantId"]', form: '[data-test="tenant-filters"]', value: "f6cc1213-7de4-41bf-b3a8-4645677395e1", key: "tenantId" },
];
it.each(filters)("$entry.label cancels a pending later page on filter changes and starts the next read at zero", async testCase => {
  const entry = testCase.entry;
  const records = Array.from({ length: 25 }, (_, index) => ({ ...entry.row, id: `item-${index}`, tenantId: `item-${index}` }));
  let resolve!: (response: Response) => void;
  const pending = new Promise<Response>(done => { resolve = done; });
  const requests: URLSearchParams[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const parsed = new URL(url, "https://localhost");
    if (parsed.pathname !== entry.endpoint) return new Response('{"authorized":true,"rows":[]}');
    requests.push(parsed.searchParams);
    return requests.length === 2 ? pending : new Response(JSON.stringify({ authorized: true, rows: records }));
  }));
  const wrapper = mount(entry.component); wrappers.push(wrapper); await flushPromises();
  const pager = wrapper.get(`nav[aria-label="${entry.label}"]`);
  await pager.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("25");
  await wrapper.get(testCase.selector).setValue(testCase.value);
  expect(pager.get("select").attributes("disabled")).toBeUndefined();
  resolve(new Response(JSON.stringify({ authorized: true, rows: [{ ...records[0], id: "obsolete", tenantId: "obsolete" }] })));
  await flushPromises();
  expect(wrapper.findAll(".query-state tbody tr")).toHaveLength(0);
  await wrapper.get(testCase.form).trigger("submit"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("0");
  expect(requests.at(-1)!.get(testCase.key)).toBe(testCase.value);
  expect(pager.text()).toContain("本页 25 条");
});

it("keeps dictionary paging available after reading an empty later page", async () => {
  const records = Array.from({ length: 25 }, (_, index) => ({ ...cases[4]!.row, id: `entry-${index}` }));
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const parsed = new URL(url, "https://localhost");
    const rows = parsed.pathname === "/api/platform/dictionary" && parsed.searchParams.get("offset") === "0" ? records : [];
    return new Response(JSON.stringify({ authorized: true, rows }));
  }));
  const wrapper = mount(SettingsPage); wrappers.push(wrapper); await flushPromises();
  await wrapper.get('[data-test="dictionary-filters"]').trigger("submit"); await flushPromises();
  const pager = wrapper.get('nav[aria-label="字典分页"]');
  await pager.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(pager.text()).toContain("本页 0 条");
  expect(pager.get('[data-test="previous-page"]').attributes("disabled")).toBeUndefined();
  await pager.get('[data-test="previous-page"]').trigger("click"); await flushPromises();
  expect(pager.text()).toContain("本页 25 条");
});

it("keeps settings and dictionary page sizes independent within the same page", async () => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const parsed = new URL(url, "https://localhost");
    const template = parsed.pathname === "/api/platform/settings" ? cases[3]!.row : cases[4]!.row;
    const records = Array.from({ length: 60 }, (_, index) => ({ ...template, id: `item-${index}` }));
    const offset = Number(parsed.searchParams.get("offset")); const limit = Number(parsed.searchParams.get("limit"));
    return new Response(JSON.stringify({ authorized: true, rows: records.slice(offset, offset + limit) }));
  }));
  const wrapper = mount(SettingsPage); wrappers.push(wrapper); await flushPromises();
  const settings = wrapper.get('nav[aria-label="配置分页"]'); const dictionary = wrapper.get('nav[aria-label="字典分页"]');
  await settings.get("select").setValue("50"); await flushPromises();
  await wrapper.get('[data-test="dictionary-filters"]').trigger("submit"); await flushPromises();
  expect(dictionary.text()).toContain("最多 25 条");
  await dictionary.get("select").setValue("100"); await flushPromises();
  expect(dictionary.text()).toContain("本页 60 条");
  expect(settings.text()).toContain("本页 50 条");
  expect(settings.get("select").element.value).toBe("50");
});

it("releases the dictionary busy state on a category change and ignores old evidence", async () => {
  let resolve!: (response: Response) => void;
  const pending = new Promise<Response>(done => { resolve = done; });
  let reads = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.startsWith("/api/platform/dictionary") && ++reads === 1) return pending;
    return new Response('{"authorized":true,"rows":[]}');
  }));
  const wrapper = mount(SettingsPage); wrappers.push(wrapper); await flushPromises();
  await wrapper.get('[data-test="dictionary-filters"]').trigger("submit"); await flushPromises();
  const pager = wrapper.get('nav[aria-label="字典分页"]');
  expect(pager.get("select").attributes("disabled")).toBeDefined();
  await wrapper.get("#dictionary-category").setValue("other");
  expect(pager.get("select").attributes("disabled")).toBeUndefined();
  resolve(new Response(JSON.stringify({ authorized: true, rows: [{ ...cases[4]!.row, id: "obsolete-entry" }] })));
  await flushPromises();
  expect(wrapper.text()).not.toContain("obsolete-entry");
  expect(pager.text()).toContain("本页 0 条");
});
