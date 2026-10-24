import { mount, flushPromises } from "@vue/test-utils";
import { afterEach, expect, it, vi } from "vitest";
import AuditPage from "./AuditPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
afterEach(() => { forgetIdentity(); vi.unstubAllGlobals(); });
it("queries a bounded increasing interval and exposes no historical mutation action", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v", requiresPasswordChange: false,
    grants: [{ permission: "platform.audit.read", scope: 0, departments: [], fields: ["Id", "OccurredAt", "Action", "CorrelationId"] }] };
  const calls: { url: string; method: string }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    calls.push({ url, method: options.method ?? "GET" });
    return new Response(JSON.stringify({ authorized: true, rows: [{ id: "audit-fixture", occurredAt: "2026-10-24T04:00:00Z",
      actorId: null, targetId: null, correlationId: "correlation-fixture", action: 1, beforeVersion: null, afterVersion: null }] }));
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.text()).toContain("audit-fixture");
  expect(wrapper.text()).toContain("correlation-fixture");
  expect(calls.every(call => call.method === "GET")).toBe(true);
  const url = new URL(calls[0]!.url, "https://localhost");
  expect(Number(url.searchParams.get("limit"))).toBeLessThanOrEqual(100);
  expect(Date.parse(url.searchParams.get("from")!)).toBeLessThan(Date.parse(url.searchParams.get("to")!));
  expect(wrapper.find('[data-test="edit-audit"]').exists()).toBe(false);
  await wrapper.get("#audit-action").setValue("0");
  await wrapper.get("form").trigger("submit");
  await flushPromises();
  expect(new URL(calls.at(-1)!.url, "https://localhost").searchParams.get("action")).toBe("0");
  wrapper.unmount();
});

it("does not resurrect an old audit projection when a revoked field is later granted again", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v", requiresPasswordChange: false,
    grants: [{ permission: "platform.audit.read", scope: 2, departments: [], fields: ["Id", "OccurredAt", "CorrelationId"] }] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ authorized: true, rows: [
    { id: "old-audit", occurredAt: "2026-10-24T04:00:00Z", correlationId: "old-authorized-correlation",
      actorId: null, targetId: null, action: null, beforeVersion: null, afterVersion: null },
  ] }))));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.text()).toContain("old-authorized-correlation");
  sessionState.subject.grants[0]!.fields = ["Id", "OccurredAt"];
  await flushPromises();
  sessionState.subject.grants[0]!.fields.push("CorrelationId");
  await flushPromises();
  expect(wrapper.text()).not.toContain("old-authorized-correlation");
  expect(wrapper.text()).not.toContain("old-audit");
  wrapper.unmount();
});
