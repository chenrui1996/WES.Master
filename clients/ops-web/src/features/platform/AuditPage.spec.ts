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

it("keeps the last same-scope audit page visible when an explicit refresh fails", async () => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v", requiresPasswordChange: false,
    grants: [{ permission: "platform.audit.read", scope: 0, departments: [], fields: ["Id", "OccurredAt", "CorrelationId"] }] };
  let calls = 0;
  vi.stubGlobal("fetch", vi.fn(async () => {
    calls++;
    return calls === 1
      ? new Response(JSON.stringify({ authorized: true, rows: [
        { id: "audit-current", occurredAt: "2026-10-24T04:00:00Z", correlationId: "safe-correlation",
          actorId: null, targetId: null, action: null, beforeVersion: null, afterVersion: null },
      ] }))
      : new Response(JSON.stringify({ code: "DatabaseUnavailable" }), { status: 503 });
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.text()).toContain("audit-current");
  await wrapper.get("form").trigger("submit");
  await flushPromises();
  expect(wrapper.get('[role="status"]').text()).toContain("刷新失败");
  expect(wrapper.text()).toContain("audit-current");
  expect(wrapper.text()).toContain("重新加载当前权限下的证据");
  wrapper.unmount();
});

function authorizeAudit() {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v", requiresPasswordChange: false,
    grants: [{ permission: "platform.audit.read", scope: 2, departments: [], fields: ["Id", "OccurredAt", "Action", "CorrelationId"] }] };
}
function auditRows(count: number) {
  return Array.from({ length: count }, (_, index) => ({
    id: `audit-${String(index + 1).padStart(3, "0")}`, occurredAt: "2026-10-24T04:00:00Z",
    actorId: null, targetId: null, correlationId: null, action: 0, beforeVersion: null, afterVersion: null,
  }));
}
function evidenceResponse(rows = auditRows(1)) {
  return new Response(JSON.stringify({ authorized: true, rows }));
}
function deferredResponse() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(complete => { resolve = complete; });
  return { promise, resolve };
}

it("pages sixty live rows with a 25 default and explicit 25/50/100 choices without inventing a total", async () => {
  authorizeAudit();
  const rows = auditRows(60);
  const requests: URLSearchParams[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const query = new URL(url, "https://localhost").searchParams;
    requests.push(query);
    const offset = Number(query.get("offset")); const limit = Number(query.get("limit"));
    return evidenceResponse(rows.slice(offset, offset + limit));
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  const pager = wrapper.get('nav[aria-label="证据分页"]');
  expect(requests[0]!.get("limit")).toBe("25");
  expect(wrapper.findAll("tbody tr")).toHaveLength(25);
  expect(pager.text()).toContain("实时");
  expect(wrapper.text()).toContain("不保证跨请求无丢重");
  expect(wrapper.get("#audit-limit").findAll("option").map(option => option.element.value)).toEqual(["25", "50", "100"]);
  await pager.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("25");
  expect(wrapper.findAll("tbody tr")).toHaveLength(25);
  expect(wrapper.findAll("tbody tr")[0]!.text()).toContain("audit-026");
  await pager.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("50");
  expect(wrapper.findAll("tbody tr")).toHaveLength(10);
  expect(pager.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
  await wrapper.get("#audit-limit").setValue("50"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("0");
  expect(requests.at(-1)!.get("limit")).toBe("50");
  expect(wrapper.findAll("tbody tr")).toHaveLength(50);
  await wrapper.get("#audit-limit").setValue("100"); await flushPromises();
  expect(requests.at(-1)!.get("offset")).toBe("0");
  expect(requests.at(-1)!.get("limit")).toBe("100");
  expect(wrapper.findAll("tbody tr")).toHaveLength(60);
  expect(pager.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
  wrapper.unmount();
});

it("reads live offsets after an insertion and makes no fixed-snapshot claim", async () => {
  authorizeAudit();
  const rows = auditRows(60);
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const query = new URL(url, "https://localhost").searchParams;
    const offset = Number(query.get("offset")); const limit = Number(query.get("limit"));
    return evidenceResponse(rows.slice(offset, offset + limit));
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.findAll("tbody tr").at(-1)!.text()).toContain("audit-025");
  rows.unshift({ ...rows[0]!, id: "new-live-audit" });
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(wrapper.findAll("tbody tr")[0]!.text()).toContain("audit-025");
  expect(wrapper.text()).toContain("不保证跨请求无丢重");
  expect(wrapper.text()).not.toContain("new-live-audit");
  wrapper.unmount();
});

it("clears the previous page before a failed next-page read and retries the requested offset", async () => {
  authorizeAudit();
  const offsets: string[] = []; let calls = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls++; offsets.push(new URL(url, "https://localhost").searchParams.get("offset")!);
    if (calls === 2) return new Response("{}", { status: 503 });
    return evidenceResponse(calls === 1 ? auditRows(25) : [{ ...auditRows(1)[0]!, id: "next-page-evidence" }]);
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).not.toContain("audit-001");
  expect(wrapper.findAll("tbody tr")).toHaveLength(0);
  expect(wrapper.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
  await wrapper.get('[role="alert"] button').trigger("click"); await flushPromises();
  expect(offsets).toEqual(["0", "25", "25"]);
  expect(wrapper.text()).toContain("next-page-evidence");
  wrapper.unmount();
});

it.each(["http", "projection"])("discards previously visible evidence when authorization is denied by %s", async variant => {
  authorizeAudit();
  let calls = 0;
  vi.stubGlobal("fetch", vi.fn(async () => {
    if (++calls === 1) return evidenceResponse();
    return variant === "http" ? new Response("{}", { status: 403 })
      : new Response(JSON.stringify({ authorized: false, rows: [] }));
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.text()).toContain("audit-001");
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.text()).not.toContain("audit-001");
  expect(wrapper.get('[role="alert"]').text()).toContain(variant === "http" ? "RequestFailed403" : "AuditReadForbidden");
  wrapper.unmount();
});

it.each([200, 503])("invalidates pending work immediately on filter changes and ignores its late %s result", async status => {
  authorizeAudit();
  const pending = deferredResponse(); let calls = 0;
  vi.stubGlobal("fetch", vi.fn(() => ++calls === 1 ? pending.promise : Promise.resolve(evidenceResponse())));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined();
  await wrapper.get("#audit-action").setValue("0");
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeUndefined();
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.text()).toContain("audit-001");
  pending.resolve(status === 200 ? evidenceResponse([{ ...auditRows(1)[0]!, id: "obsolete-evidence" }])
    : new Response("{}", { status }));
  await flushPromises();
  expect(wrapper.text()).not.toContain("obsolete-evidence");
  expect(wrapper.text()).toContain("audit-001");
  expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  wrapper.unmount();
});

it("does not relabel page-two evidence as first-page stale data after a failed search", async () => {
  authorizeAudit();
  let calls = 0;
  vi.stubGlobal("fetch", vi.fn(async () => {
    calls++;
    if (calls === 3) return new Response("{}", { status: 503 });
    return evidenceResponse(auditRows(25).map(row => ({ ...row, id: `page-${calls}-${row.id}` })));
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("page-2-audit-001");
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.text()).not.toContain("page-2-audit-001");
  expect(wrapper.findAll("tbody tr")).toHaveLength(0);
  wrapper.unmount();
});

it("clears both data and pending work on an identity change without accepting late evidence", async () => {
  authorizeAudit();
  const pending = deferredResponse(); let calls = 0;
  vi.stubGlobal("fetch", vi.fn(() => ++calls === 1 ? Promise.resolve(evidenceResponse()) : pending.promise));
  const wrapper = mount(AuditPage); await flushPromises();
  await wrapper.get("form").trigger("submit"); await flushPromises();
  sessionState.subject!.grants = [];
  await flushPromises();
  expect(wrapper.text()).not.toContain("audit-001");
  expect(wrapper.get('[role="alert"]').text()).toContain("身份或权限已变更");
  pending.resolve(evidenceResponse([{ ...auditRows(1)[0]!, id: "revoked-evidence" }]));
  await flushPromises();
  expect(wrapper.text()).not.toContain("revoked-evidence");
  expect(wrapper.get('[role="alert"]').text()).toContain("身份或权限已变更");
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeUndefined();
  wrapper.unmount();
});

it("rejects an oversized response rather than presenting it as a bounded page", async () => {
  authorizeAudit();
  vi.stubGlobal("fetch", vi.fn(async () => evidenceResponse(auditRows(26))));
  const wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.findAll("tbody tr")).toHaveLength(0);
  expect(wrapper.get('[role="alert"]').text()).toContain("审计响应无效");
  wrapper.unmount();
});

it("pages a fixed audit snapshot and clears its evidence when the server expires it", async () => {
  authorizeAudit();
  const calls: { url: string; method: string; body?: string }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit = {}) => {
    const method = options.method ?? "GET";
    calls.push({ url, method, body: typeof options.body === "string" ? options.body : undefined });
    if (url.includes("/api/platform/audit/snapshots/")) return new Response("{}", { status: 410 });
    if (url.endsWith("/api/platform/audit/snapshots")) return new Response(JSON.stringify({
      snapshotId: "8b708c1d-f484-43e2-93c2-5f03096e5974", expiresAt: "2026-10-24T04:10:00Z", count: 60, rows: auditRows(25),
    }));
    return evidenceResponse(auditRows(25));
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  await wrapper.get('[data-test="create-audit-snapshot"]').trigger("click"); await flushPromises();
  expect(calls.at(-1)!.method).toBe("POST");
  expect(calls.at(-1)!.url).toContain("/api/platform/audit/snapshots");
  expect(wrapper.text()).toContain("固定核对快照");
  expect(wrapper.text()).toContain("60 条");
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  const continuation = new URL(calls.at(-1)!.url, "https://localhost");
  expect(continuation.pathname).toContain("/api/platform/audit/snapshots/8b708c1d-f484-43e2-93c2-5f03096e5974");
  expect(continuation.searchParams.get("offset")).toBe("25");
  expect(wrapper.findAll("tbody tr")).toHaveLength(0);
  expect(wrapper.get('[role="alert"]').text()).toContain("快照已过期");
  wrapper.unmount();
});

it("invalidates an audit snapshot when its query filters change", async () => {
  authorizeAudit();
  const paths: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    paths.push(url);
    return url.endsWith("/api/platform/audit/snapshots")
      ? new Response(JSON.stringify({ snapshotId: "8b708c1d-f484-43e2-93c2-5f03096e5974",
        expiresAt: "2026-10-24T04:10:00Z", count: 1, rows: auditRows(1) }))
      : evidenceResponse([{ ...auditRows(1)[0]!, id: "live-after-filter-change" }]);
  }));
  const wrapper = mount(AuditPage); await flushPromises();
  await wrapper.get('[data-test="create-audit-snapshot"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("固定核对快照：1 条");
  await wrapper.get("#audit-action").setValue("0");
  expect(wrapper.text()).not.toContain("固定核对快照：1 条");
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(new URL(paths.at(-1)!, "https://localhost").pathname).toBe("/api/platform/audit");
  expect(wrapper.text()).toContain("live-after-filter-change");
  wrapper.unmount();
});
