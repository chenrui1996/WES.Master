import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import TenantsPage from "./TenantsPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

beforeEach(() => {
  setAccessToken("opaque", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "me", sessionId: "s1", requiresPasswordChange: false, credentialVersion: null,
    grants: ["read", "write", "switch"].map(action => ({ permission: `platform.tenants.${action}`,
      scope: 2, departments: [], fields: ["Id", "Name", "Enabled", "Version", "DefaultLanguage"] })) };
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
const tenants = [
  { tenantId: "t1", name: "当前租户", enabled: true, defaultLanguage: "zh-CN", version: "v1" },
  { tenantId: "t2", name: "目标租户", enabled: true, defaultLanguage: null, version: null },
];
it("filters only the eligible catalog by tenant UUID and enabled state", async () => {
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url); return new Response('{"authorized":true,"rows":[]}');
  }));
  const wrapper = mount(TenantsPage); await flushPromises();
  await wrapper.get('[name="filterTenantId"]').setValue("bde6df13-f184-4b0e-abf4-33140fbba522");
  await wrapper.get('[name="filterTenantEnabled"]').setValue("true");
  await wrapper.get('[data-test="tenant-filters"]').trigger("submit"); await flushPromises();
  expect(reads).toContain("/api/platform/tenants?offset=0&limit=50&tenantId=bde6df13-f184-4b0e-abf4-33140fbba522&enabled=true");
  wrapper.unmount();
});
it("abandons the old identity and emits a fresh-authentication request only after a successful check", async () => {
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
    if (options.method === "POST") { writes.push(JSON.parse(String(options.body))); return new Response('{"authorized":true,"tenantId":"t2"}'); }
    return new Response(JSON.stringify({ authorized: true, rows: tenants }));
  }));
  const wrapper = mount(TenantsPage);
  await flushPromises();
  await wrapper.get('[data-test="switch-tenant"]').trigger("click");
  expect(writes).toHaveLength(0);
  await wrapper.get('[data-test="switch-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ targetTenantId: "t2" }]);
  expect(sessionState.token).toBeNull();
  expect(sessionState.subject).toBeNull();
  expect(wrapper.emitted("reauthenticate")).toEqual([["t2"]]);
  expect(wrapper.text()).not.toContain("当前租户");
  wrapper.unmount();
});
it("sends the exact current version and never writes a different tenant", async () => {
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method === "PUT") { writes.push([url, JSON.parse(String(options.body))]); return new Response('{"decision":0,"version":"v2"}'); }
    return new Response(JSON.stringify({ authorized: true, rows: tenants }));
  }));
  const wrapper = mount(TenantsPage);
  await flushPromises();
  expect(wrapper.findAll('[data-test="edit-tenant"]')).toHaveLength(1);
  await wrapper.get('[data-test="edit-tenant"]').trigger("click");
  await wrapper.get('[name="tenantName"]').setValue("更新名称");
  await wrapper.get('[data-test="tenant-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([["/api/platform/tenants/current", { expectedVersion: "v1", name: "更新名称", enabled: true, defaultLanguage: "zh-CN" }]]);
  wrapper.unmount();
});
it("retains identity on denied switch and never emits reauthentication or displays old rows", async () => {
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
    if (options.method === "POST") return new Response('{"authorized":false,"tenantId":null}');
    return new Response(JSON.stringify({ authorized: true, rows: tenants }));
  }));
  const wrapper = mount(TenantsPage);
  await flushPromises();
  await wrapper.get('[data-test="switch-tenant"]').trigger("click");
  await wrapper.get('[data-test="switch-form"]').trigger("submit");
  await flushPromises();
  expect(sessionState.token).toBe("opaque");
  expect(wrapper.emitted("reauthenticate")).toBeUndefined();
  expect(wrapper.text()).not.toContain("目标租户");
  expect(wrapper.get('[role="alert"]').text()).toContain("TenantSwitchDenied");
  wrapper.unmount();
});
it("rejects a mismatched successful switch response instead of authenticating a different target", async () => {
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) =>
    new Response(JSON.stringify(options.method === "POST" ? { authorized: true, tenantId: "unexpected-tenant" } : { authorized: true, rows: tenants }))));
  const wrapper = mount(TenantsPage);
  await flushPromises();
  await wrapper.get('[data-test="switch-tenant"]').trigger("click");
  await wrapper.get('[data-test="switch-form"]').trigger("submit");
  await flushPromises();
  expect(sessionState.token).toBe("opaque");
  expect(wrapper.emitted("reauthenticate")).toBeUndefined();
  expect(wrapper.get('[role="alert"]').text()).toContain("TenantSwitchDenied");
  wrapper.unmount();
});
it("discards an outstanding switch check after the subject changes", async () => {
  let finish: ((response: Response) => void) | undefined;
  vi.stubGlobal("fetch", vi.fn((_url: string, options: RequestInit) => options.method === "POST"
    ? new Promise<Response>(resolve => { finish = resolve; })
    : Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: tenants })))));
  const wrapper = mount(TenantsPage);
  await flushPromises();
  await wrapper.get('[data-test="switch-tenant"]').trigger("click");
  await wrapper.get('[data-test="switch-form"]').trigger("submit");
  forgetIdentity();
  setAccessToken("replacement", "2099-01-01T00:00:00Z");
  finish?.(new Response('{"authorized":true,"tenantId":"t2"}'));
  await flushPromises();
  expect(sessionState.token).toBe("replacement");
  expect(wrapper.emitted("reauthenticate")).toBeUndefined();
  expect(wrapper.find('[data-test="switch-form"]').exists()).toBe(false);
  wrapper.unmount();
});
