import { mount, flushPromises } from "@vue/test-utils";
import { afterEach, expect, it, vi } from "vitest";
import SettingsPage from "./SettingsPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

afterEach(() => { forgetIdentity(); vi.unstubAllGlobals(); });
it("sends P/H and fully missing-translation filters to their owners before pagination", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false, credentialVersion: null,
    grants: [{ permission: "platform.settings.read", scope: 2, departments: [], fields: ["Id", "Key"] },
      { permission: "platform.dictionary.read", scope: 2, departments: [], fields: ["Id", "Category", "Label"] }] };
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url); return new Response('{"authorized":true,"rows":[]}');
  }));
  const wrapper = mount(SettingsPage); await flushPromises();
  await wrapper.get('[name="filterSettingKind"]').setValue("H");
  await wrapper.get('[data-test="setting-filters"]').trigger("submit"); await flushPromises();
  expect(reads).toContain("/api/platform/settings?offset=0&limit=50&kind=H");
  await wrapper.get('[name="filterMissingTranslation"]').setValue("true");
  await wrapper.get('[data-test="dictionary-filters"]').trigger("submit"); await flushPromises();
  expect(reads).toContain("/api/platform/dictionary?category=task&language=zh-CN&offset=0&limit=50&missingTranslation=true");
  wrapper.unmount();
});
it("shows explicit H evidence absence and changes a P scalar only with its selected version", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false, credentialVersion: "v",
    grants: [ { permission: "platform.settings.read", scope: 0, departments: [], fields: ["Id", "Key", "Value", "Source", "Version"] },
      { permission: "platform.settings.write", scope: 0, departments: [], fields: ["Value"] } ] };
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
    if (options.method === "PUT") { writes.push(JSON.parse(String(options.body))); return new Response(JSON.stringify({ version: "next" })); }
    return new Response(JSON.stringify({ authorized: true, rows: [
      { id: "p", key: "query.snapshotTtlSeconds", value: "600", source: "ApprovedBaseline", version: "selected-version", kind: "P", state: "Configured" },
      { id: "h", key: "device.feedbackFreshnessMilliseconds", value: null, source: "Unverified", version: "hardware-version", kind: "H", state: "Unverified" },
    ] }));
  }));
  const wrapper = mount(SettingsPage);
  await flushPromises();
  expect(wrapper.text()).toContain("Unverified");
  expect(wrapper.findAll('[data-test="edit-setting"]')).toHaveLength(1);
  await wrapper.get('[data-test="edit-setting"]').trigger("click");
  await wrapper.get("#setting-value").setValue("300");
  await wrapper.get('[data-test="setting-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ expectedVersion: "selected-version", value: "300" }]);
  wrapper.unmount();
});

it("discards old rows and editor values on field revocation instead of restoring them when a grant returns", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false, credentialVersion: "v",
    grants: [
      { permission: "platform.settings.read", scope: 2, departments: [], fields: ["Id", "Key", "Value", "Source", "Version"] },
      { permission: "platform.settings.write", scope: 2, departments: [], fields: ["Value"] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ authorized: true, rows: [
    { id: "p", key: "query.snapshotTtlSeconds", value: "613", source: "ApprovedBaseline", version: "old-version", kind: "P", state: "Configured" },
  ] }))));
  const wrapper = mount(SettingsPage); await flushPromises();
  await wrapper.get('[data-test="edit-setting"]').trigger("click");
  expect(wrapper.text()).toContain("613");
  sessionState.subject.grants[0]!.fields = ["Id"];
  await flushPromises();
  expect(wrapper.find('[data-test="setting-form"]').exists()).toBe(false);
  expect(wrapper.text()).not.toContain("613");
  sessionState.subject.grants[0]!.fields = ["Id", "Key", "Value", "Source", "Version"];
  await flushPromises();
  expect(wrapper.text()).not.toContain("613");
  wrapper.unmount();
});

it("preserves configuration rows as stale when the same-scope refresh fails", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false, credentialVersion: "v",
    grants: [{ permission: "platform.settings.read", scope: 0, departments: [], fields: ["Id", "Key", "Value", "Source", "Version"] }] };
  let failRefresh = false;
  vi.stubGlobal("fetch", vi.fn(async () => failRefresh
    ? new Response("unavailable", { status: 503 })
    : new Response(JSON.stringify({ authorized: true, rows: [
      { id: "p", key: "query.snapshotTtlSeconds", value: "600", source: "ApprovedBaseline", version: "v1", kind: "P", state: "Configured" },
    ] }))));
  const wrapper = mount(SettingsPage); await flushPromises();
  expect(wrapper.text()).toContain("query.snapshotTtlSeconds");
  failRefresh = true;
  await wrapper.get("section[aria-labelledby='catalog-title'] > button").trigger("click");
  await flushPromises();
  expect(wrapper.find('[aria-label="强类型配置目录"] [role="status"]').text()).toContain("最近一次已加载");
  expect(wrapper.text()).toContain("query.snapshotTtlSeconds");
  wrapper.unmount();
});

it("clears configuration rows immediately when its filter changes", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false, credentialVersion: "v",
    grants: [{ permission: "platform.settings.read", scope: 0, departments: [], fields: ["Id", "Key", "Value", "Source", "Version"] }] };
  let resolveRefresh: ((response: Response) => void) | undefined;
  vi.stubGlobal("fetch", vi.fn(async () => new Promise<Response>(resolve => { resolveRefresh = resolve; })));
  const wrapper = mount(SettingsPage); await flushPromises();
  // Resolve the mount-time request before starting a separate same-page filter query.
  resolveRefresh?.(new Response(JSON.stringify({ authorized: true, rows: [
    { id: "p", key: "query.snapshotTtlSeconds", value: "600", source: "ApprovedBaseline", version: "v1", kind: "P", state: "Configured" },
  ] })));
  await flushPromises();
  await wrapper.get('[name="filterSettingKind"]').setValue("H");
  await flushPromises();
  expect(wrapper.text()).not.toContain("query.snapshotTtlSeconds");
  wrapper.unmount();
});

it("discards settings rows when a refresh is forbidden instead of preserving stale data", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false, credentialVersion: "v",
    grants: [{ permission: "platform.settings.read", scope: 0, departments: [], fields: ["Id", "Key", "Value", "Source", "Version"] }] };
  let forbidden = false;
  vi.stubGlobal("fetch", vi.fn(async () => forbidden
    ? new Response('{"code":"Forbidden"}', { status: 403 })
    : new Response(JSON.stringify({ authorized: true, rows: [
      { id: "p", key: "query.snapshotTtlSeconds", value: "600", source: "ApprovedBaseline", version: "v1", kind: "P", state: "Configured" },
    ] }))));
  const wrapper = mount(SettingsPage); await flushPromises();
  forbidden = true;
  await wrapper.get("section[aria-labelledby='catalog-title'] > button").trigger("click");
  await flushPromises();
  expect(wrapper.text()).not.toContain("query.snapshotTtlSeconds");
  expect(wrapper.find('[aria-label="强类型配置目录"] [role="alert"]').exists()).toBe(true);
  wrapper.unmount();
});
