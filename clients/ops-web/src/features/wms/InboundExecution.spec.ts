import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import InboundExecution from "./InboundExecution.vue";
import InboundPage from "./InboundPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { inboundFields, type InboundDetail } from "./inbound";
import { warehouseFields, locationFields } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const after = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(state: InboundDetail["state"] = 0): InboundDetail {
  return { id, code: "IN-EXEC", warehouseId: id, state, version, createdAt: "2026-10-28T00:00:00Z",
    lines: [{ id, itemId: id, unitId: id, batch: "BATCH-1", precision: 3, planned: "5.000000", received: "0.000000", remaining: "5.000000" }], receipts: [] };
}
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  const permissions: [string, readonly string[]][] = [
    ["wms.inbound.read", inboundFields], ["wms.inbound.approve", ["State"]],
    ["wms.inbound.receive", ["LineId", "ExecutionId", "Quantity", "ContainerId", "ReceiptLocationId", "TargetLocationId", "BalanceId"]],
    ["wms.warehouses.read", warehouseFields], ["wms.locations.read", locationFields],
    ["wms.items.read", ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"]],
    ["wms.units.read", ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"]],
    ["wms.containers.read", ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"]],
  ];
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: permissions.map(([permission, fields]) => ({ permission, fields: [...fields], scope: 0, departments: [] })) };
  vi.stubGlobal("fetch", vi.fn(async (_url, options) => {
    const body = JSON.parse(String(options?.body));
    return new Response(JSON.stringify({ decision: 0, id: body.executionId ?? id, version: after }));
  }));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
function open(kind: "approval" | "receipts" = "approval") {
  wrapper = mount(InboundExecution, { props: { order: detail(kind === "approval" ? 0 : 1), kind } });
}
async function previewReceipt(quantity = "5.000") {
  for (const name of ["containerId", "receiptLocationId", "targetLocationId", "balanceId", "expectedBalanceVersion"])
    await wrapper!.get(`input[name="${name}"]`).setValue(id);
  await wrapper!.get('input[name="quantity"]').setValue(quantity);
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-inbound-execution"]').trigger("click"); await flushPromises(); }
it.each(["approval", "receipts"] as const)("previews all identity, plan, exact input and versions before %s; reads only explicitly", async kind => {
  open(kind);
  if (kind === "receipts") await previewReceipt();
  else await wrapper!.get("form").trigger("submit");
  const preview = wrapper!.get('[data-test="inbound-execution-preview"]');
  for (const text of [id, version, "IN-EXEC", "BATCH-1", "5.000000"]) expect(preview.text()).toContain(text);
  expect(fetch).not.toHaveBeenCalled();
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/inbound/${id}/${kind}`); expect(options?.method).toBe("POST");
  const input = JSON.parse(String(options?.body));
  expect(input.expectedVersion).toBe(version);
  expect(new Headers(options?.headers).get("Idempotency-Key")).toMatch(/^[a-f0-9-]{36}$/);
  if (kind === "receipts") {
    expect(input).toEqual({ expectedVersion: version, expectedBalanceVersion: id, lineId: id, executionId: expect.any(String),
      quantity: "5.000", containerId: id, receiptLocationId: id, targetLocationId: id, balanceId: id });
  } else expect(input).toEqual({ expectedVersion: version });
  expect(wrapper!.text()).toContain(`原执行版本：${after}`);
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="read-executed-inbound"]').trigger("click");
  expect(wrapper!.emitted("read")).toEqual([[id]]);
});
it.each(["0", "5.001", "0.0001", "1e0", " 1", "1 ", "1.0000001", "NaN", "-1"])(
  "rejects quantity %s without rounding, exceeding remaining or writing", async quantity => {
    open("receipts"); await previewReceipt(quantity);
    expect(wrapper!.find('[data-test="inbound-execution-preview"]').exists()).toBe(false);
    expect(wrapper!.get('[role="alert"]').text()).not.toBe(""); expect(fetch).not.toHaveBeenCalled();
  });
it.each(["wms.inbound.read", "wms.inbound.approve", "wms.warehouses.read", "wms.items.read", "wms.units.read"])(
  "requires complete current approval permission %s", permission => {
    sessionState.subject!.grants.find(grant => grant.permission === permission)!.fields.pop();
    open(); expect(wrapper!.find("form").exists()).toBe(false); expect(fetch).not.toHaveBeenCalled();
  });
it.each(["wms.inbound.receive", "wms.locations.read", "wms.containers.read"])(
  "requires receipt resource permission %s", permission => {
    sessionState.subject!.grants.find(grant => grant.permission === permission)!.fields.pop();
    open("receipts"); expect(wrapper!.find("form").exists()).toBe(false);
  });
it.each([
  { decision: 0, id, version: "bad" }, { decision: 0, id }, { decision: 2, id, version: after },
  { decision: 0, id, version }, { decision: 0, id: after, version: after },
  { decision: 0, id, version: after, extra: true },
])("keeps malformed or wrong-identity success Unknown %#", async value => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(value)));
  open(); await wrapper!.get("form").trigger("submit"); await confirm();
  expect(wrapper!.text()).toContain("结果不明"); expect(wrapper!.find('[data-test="read-executed-inbound"]').exists()).toBe(false);
});
it("retains original key and full receipt body through Unknown, rejection and manual recovery", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }))
    .mockResolvedValueOnce(new Response("{}", { status: 403 }));
  open("receipts"); await previewReceipt(); await confirm();
  await wrapper!.get('[data-test="retry-inbound-execution"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
  expect(wrapper!.find("form").exists()).toBe(false);
  await wrapper!.get('[data-test="retry-inbound-execution"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(new Set(calls.map(call => call[1]?.body)).size).toBe(1);
  expect(new Set(calls.map(call => new Headers(call[1]?.headers).get("Idempotency-Key"))).size).toBe(1);
  expect(wrapper!.text()).toContain(`原执行版本：${after}`);
});
it("marks exactly 10 seconds Unknown, ignores late response and never retries online automatically", async () => {
  vi.useFakeTimers(); let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await wrapper!.get("form").trigger("submit"); await confirm();
  await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.find('[data-test="retry-inbound-execution"]').exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(1); expect(wrapper!.find('[data-test="retry-inbound-execution"]').exists()).toBe(true);
  resolve(new Response(JSON.stringify({ decision: 0, id, version: after }))); await flushPromises();
  expect(wrapper!.find('[data-test="read-executed-inbound"]').exists()).toBe(false);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.get('[data-test="retry-inbound-execution"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("clears all old identities and ignores late POST after current subject changes", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await wrapper!.get("form").trigger("submit"); await confirm();
  sessionState.subject = { ...sessionState.subject!, sessionId: "other" }; await flushPromises();
  resolve(new Response(JSON.stringify({ decision: 0, id, version: after }))); await flushPromises();
  expect(wrapper!.text()).toContain("主体或原单已改变");
  expect(wrapper!.text()).not.toContain(id); expect(wrapper!.text()).not.toContain(version);
});
it.each([404, 405, 422])("reports definite endpoint/schema rejection %i without inventing success or retry", async status => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status }));
  open("receipts"); await previewReceipt(); await confirm();
  expect(wrapper!.text()).toContain("本次请求被拒绝");
  expect(wrapper!.find('[data-test="read-executed-inbound"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="retry-inbound-execution"]').exists()).toBe(false);
});
it("does not accept an unrelated execution identity as receipt confirmation", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, id, version: after })));
  open("receipts"); await previewReceipt(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.find('[data-test="read-executed-inbound"]').exists()).toBe(false);
});
it("cancels a disconnected pending POST and ignores its late success even after reconnect", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await wrapper!.get("form").trigger("submit"); await confirm();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.text()).toContain("连接中断，结果不明");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  resolve(new Response(JSON.stringify({ decision: 0, id, version: after }))); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1); expect(wrapper!.find('[data-test="read-executed-inbound"]').exists()).toBe(false);
});
it("invalidates the original plan version and clears its frozen request on source replacement", async () => {
  open(); await wrapper!.get("form").trigger("submit");
  await wrapper!.setProps({ order: { ...detail(), version: after } });
  expect(wrapper!.text()).toContain("主体或原单已改变");
  expect(wrapper!.find('[data-test="inbound-execution-preview"]').exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});
it("keeps Unknown in the real parent while queries are blocked, then explicitly reads current detail", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(detail())));
  wrapper = mount(InboundPage, { props: { id } });
  await wrapper.get("form").trigger("submit"); await flushPromises();
  await wrapper.get('[data-test="prepare-inbound-approval"]').trigger("click");
  await wrapper.get("form").trigger("submit");
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 })); await confirm();
  expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false);
  expect(wrapper.find('input[name="orderId"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="retry-inbound-execution"]').exists()).toBe(true);
  await wrapper.get('[data-test="retry-inbound-execution"]').trigger("click"); await flushPromises();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ ...detail(1), version: after })));
  await wrapper.get('[data-test="read-executed-inbound"]').trigger("click"); await flushPromises();
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain(after);
  expect(vi.mocked(fetch).mock.calls.map(call => call[1]?.method ?? "GET")).toEqual(["GET", "POST", "POST", "GET"]);
});
