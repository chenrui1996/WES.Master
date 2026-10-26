import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import AuditPage from "./AuditPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

const snapshotId = "5240a797-1205-4ff1-9bd6-32ad57a9e300";
const row = { id: "27b96ac1-6d5f-4093-a552-f58c91c587b1", occurredAt: "2026-10-24T04:00:00Z",
  action: 0, actorId: null, targetId: null, correlationId: null, beforeVersion: null, afterVersion: null };
const requests: RequestInit[] = [];
let wrapper: VueWrapper | undefined;
let response: () => Promise<Response>;
let create: ReturnType<typeof vi.fn>;
let revoke: ReturnType<typeof vi.fn>;

beforeEach(() => {
  requests.length = 0;
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", credentialVersion: "v",
    requiresPasswordChange: false, grants: ["platform.audit.read", "platform.audit.export"].map(permission =>
      ({ permission, scope: 2, departments: [], fields: ["Id", "OccurredAt", "Action"] })) };
  response = async () => new Response('"Id","OccurredAt"\r\n', { headers: { "Content-Type": "text/csv; charset=utf-8" } });
  vi.stubGlobal("fetch", vi.fn(async (path: string, options: RequestInit) => {
    if (path.endsWith("/export")) { requests.push(options); return response(); }
    return new Response(JSON.stringify(options.method === "POST"
      ? { snapshotId, expiresAt: "2099-01-01T00:00:00Z", count: 1, rows: [row] }
      : { authorized: true, rows: [row] }));
  }));
  create = vi.fn(() => "blob:audit-fixture"); revoke = vi.fn();
  Object.defineProperty(URL, "createObjectURL", { configurable: true, value: create });
  Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: revoke });
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; forgetIdentity(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });
async function snapshot() {
  wrapper = mount(AuditPage); await flushPromises();
  expect(wrapper.find('[data-test="export-audit"]').exists()).toBe(false);
  await wrapper.get('[data-test="create-audit-snapshot"]').trigger("click"); await flushPromises();
}
async function submit() {
  await wrapper!.get('form[aria-label="导出固定审计快照"]').trigger("submit"); await flushPromises();
}

it("exports only an explicitly created snapshot and checked columns, then revokes its temporary URL", async () => {
  await snapshot();
  await wrapper!.get('input[name="audit-export-column"][value="Action"]').setValue(true);
  await submit();
  expect(JSON.parse(String(requests[0]!.body))).toEqual({ columns: ["Id", "OccurredAt", "Action"] });
  expect(new Headers(requests[0]!.headers).has("Idempotency-Key")).toBe(true);
  expect(create).toHaveBeenCalledOnce();
  expect(revoke).toHaveBeenCalledWith("blob:audit-fixture");
  expect(wrapper!.text()).toContain("不代表客户端已收齐");
});

it("never offers export to an audit reader without the separate export permission", async () => {
  sessionState.subject!.grants.pop();
  await snapshot();
  expect(wrapper!.find('[data-test="export-audit"]').exists()).toBe(false);
});

it("retains the original operation after a lost confirmation only for explicit same-selection retry", async () => {
  await snapshot();
  response = async () => new Response('{"code":"DependencyUnavailable","correlationId":"export-uncertain"}', { status: 503 });
  await submit();
  expect(wrapper!.text()).toContain("export-uncertain");
  expect(requests).toHaveLength(1);
  response = async () => new Response('"Id","OccurredAt"\r\n', { headers: { "Content-Type": "text/csv" } });
  await submit();
  expect(new Headers(requests[1]!.headers).get("Idempotency-Key"))
    .toBe(new Headers(requests[0]!.headers).get("Idempotency-Key"));
  expect(create).toHaveBeenCalledOnce();
});

it("drops late binary bytes when permission is withdrawn rather than creating a reusable URL", async () => {
  await snapshot();
  let finish: ((value: Response) => void) | undefined;
  response = () => new Promise(resolve => { finish = resolve; });
  await submit();
  sessionState.subject!.grants.pop();
  await flushPromises();
  finish?.(new Response("must-not-download", { headers: { "Content-Type": "text/csv" } }));
  await flushPromises();
  expect(create).not.toHaveBeenCalled();
  expect(wrapper!.text()).not.toContain(snapshotId);
});

it("blocks a successful-looking response of the wrong media type and does not create an object URL", async () => {
  await snapshot();
  response = async () => new Response("{}", { headers: { "Content-Type": "application/json" } });
  await submit();
  expect(create).not.toHaveBeenCalled();
  expect(wrapper!.text()).toContain("导出响应无效");
});

it("changing the query while exporting aborts the request and discards its eventual bytes", async () => {
  await snapshot();
  let finish: ((value: Response) => void) | undefined;
  response = () => new Promise(resolve => { finish = resolve; });
  await submit();
  await wrapper!.get("#audit-from").setValue("2026-10-20T00:00");
  expect(requests[0]!.signal?.aborted).toBe(true);
  finish?.(new Response("not-the-current-query", { headers: { "Content-Type": "text/csv" } }));
  await flushPromises();
  expect(create).not.toHaveBeenCalled();
  expect(wrapper!.find('[data-test="export-audit"]').exists()).toBe(false);
});

it("changing the explicit selection starts a new export identity instead of reusing a different payload", async () => {
  await snapshot(); await submit();
  await wrapper!.get('input[name="audit-export-column"][value="Action"]').setValue(true);
  await submit();
  expect(new Headers(requests[1]!.headers).get("Idempotency-Key"))
    .not.toBe(new Headers(requests[0]!.headers).get("Idempotency-Key"));
  expect(JSON.parse(String(requests[1]!.body))).toEqual({ columns: ["Id", "OccurredAt", "Action"] });
});

it("offline export is rejected visibly without queuing a download on reconnect", async () => {
  await snapshot();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  await submit();
  expect(wrapper!.text()).toContain("离线");
  expect(requests).toHaveLength(0);
  window.dispatchEvent(new Event("online"));
  await flushPromises();
  expect(requests).toHaveLength(0);
  expect(create).not.toHaveBeenCalled();
});
