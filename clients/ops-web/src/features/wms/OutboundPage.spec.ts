import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import OutboundPage from "./OutboundPage.vue";
import OutboundConfirmation from "./OutboundConfirmation.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { outboundFields, type OutboundCommand, type OutboundDetail } from "./outbound";
import { warehouseFields, locationFields } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const next = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(state: OutboundDetail["state"] = 0): OutboundDetail {
  return { id, code: "OUT-EXEC", warehouseId: id, state, version, createdAt: "2026-10-28T00:00:00Z",
    lines: [{ id, executionId: id, balanceId: id, itemId: id, unitId: id, batch: "BATCH-1", precision: 0, containerId: id, locationId: id,
      branch: 0, plannedQuantity: "7.000000", reservedQuantity: state === 0 ? "0.000000" : "7.000000", pickedQuantity: "0.000000",
      shortageQuantity: "0.000000", releasedQuantity: "0.000000", shippedQuantity: "0.000000" }] };
}
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  const permissions: [string, readonly string[]][] = [
    ["wms.outbound.read", outboundFields], ["wms.outbound.create", ["Code", "WarehouseId", "Lines"]],
    ["wms.outbound.approve", ["State"]], ["wms.outbound.pick", ["LineId", "PickedQuantity", "ShortageQuantity", "Reason"]],
    ["wms.outbound.release", ["LineId", "Quantity", "Reason"]], ["wms.outbound.ship", ["LineId", "Quantity"]],
    ["wms.outbound.cancel", ["State", "Reason"]], ["wms.outbound.close", ["State", "Reason"]],
    ["wms.warehouses.read", warehouseFields], ["wms.locations.read", locationFields],
    ["wms.items.read", ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"]],
    ["wms.units.read", ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"]],
    ["wms.containers.read", ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"]],
  ];
  sessionState.subject = { tenantId: id, userId: id, sessionId: id, requiresPasswordChange: false, credentialVersion: null,
    grants: permissions.map(([permission, fields]) => ({ permission, fields: [...fields], scope: 0, departments: [] })) };
  vi.stubGlobal("fetch", vi.fn(async (_url, options) => new Response(JSON.stringify(options?.method === "POST"
    ? { decision: 0, id, version: next } : { decision: 0, record: detail() }))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
function confirmation(command: OutboundCommand = { kind: "approval", operation: next, id,
  input: { expectedVersion: version, balances: [{ balanceId: id, expectedVersion: version }] } }) {
  wrapper = mount(OutboundConfirmation, { props: { command, summary: "OUT-EXEC" } });
}
async function load(value = detail()) {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record: value })));
  wrapper = mount(OutboundPage);
  await wrapper.get('input[name="orderId"]').setValue(id);
  await wrapper.get('[data-test="outbound-query"]').trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-outbound"]').trigger("click"); await flushPromises(); }
it("previews all balances and original versions before approval; read is explicit", async () => {
  await load();
  await wrapper!.get('[data-test="prepare-outbound-approval"]').trigger("click");
  await wrapper!.get('input[name="expectedBalanceVersion"]').setValue(version);
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(wrapper!.get('[data-test="outbound-preview-body"]').text()).toContain(version);
  await confirm();
  expect(fetch).toHaveBeenCalledTimes(2);
  const [url, options] = vi.mocked(fetch).mock.calls[1]!;
  expect(url).toBe(`/api/wms/outbound/${id}/approval`);
  expect(JSON.parse(String(options?.body))).toEqual({ expectedVersion: version, balances: [{ balanceId: id, expectedVersion: version }] });
  expect(wrapper!.text()).toContain(`原执行版本：${next}`);
  await wrapper!.get('[data-test="read-outbound-after-write"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(3);
  expect(wrapper!.get('[data-test="outbound-detail"]').text()).toContain("OUT-EXEC");
});
it("creates full multi-row source-specific draft with one frozen exact body, not a reservation", async () => {
  wrapper = mount(OutboundPage);
  await wrapper.get('[data-test="prepare-outbound-create"]').trigger("click");
  await wrapper.get('input[name="code"]').setValue("OUT-NEW");
  await wrapper.get('input[name="warehouseId"]').setValue(id);
  await wrapper.get('input[name="balanceId"]').setValue(id);
  await wrapper.get('input[name="plannedQuantity"]').setValue("7.000");
  await wrapper.findAll("button").find(button => button.text() === "添加计划行")!.trigger("click");
  await wrapper.findAll('input[name="balanceId"]')[1]!.setValue(next);
  await wrapper.findAll('input[name="plannedQuantity"]')[1]!.setValue("2");
  await wrapper.findAll('select[name="branch"]')[1]!.setValue(1);
  await wrapper.get('[data-test="outbound-create-form"]').trigger("submit"); await flushPromises();
  expect(fetch).not.toHaveBeenCalled();
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/outbound");
  const body = JSON.parse(String(options?.body));
  expect(body).toEqual({ code: "OUT-NEW", warehouseId: id, lines: [
    { id: expect.any(String), balanceId: id, quantity: "7.000", branch: 0 },
    { id: expect.any(String), balanceId: next, quantity: "2", branch: 1 }] });
  expect(new Set(body.lines.map((line: { id: string }) => line.id)).size).toBe(2);
});
it("clears dependent source dimensions after changing warehouse", async () => {
  wrapper = mount(OutboundPage); await wrapper.get('[data-test="prepare-outbound-create"]').trigger("click");
  await wrapper.get('input[name="warehouseId"]').setValue(id);
  await wrapper.get('input[name="balanceId"]').setValue(id); await wrapper.get('input[name="plannedQuantity"]').setValue("7");
  await wrapper.get('input[name="warehouseId"]').setValue(next);
  expect(wrapper.get<HTMLInputElement>('input[name="balanceId"]').element.value).toBe("");
  expect(wrapper.get<HTMLInputElement>('input[name="plannedQuantity"]').element.value).toBe("");
});
it("records incremental pick5 short2 with explanation and no implicit shipment", async () => {
  await load(detail(1));
  await wrapper!.get('[data-test="prepare-outbound-picking"]').trigger("click");
  await wrapper!.get('input[name="pickedQuantity"]').setValue("5"); await wrapper!.get('input[name="shortageQuantity"]').setValue("2");
  await wrapper!.get('textarea[name="reason"]').setValue("实拣5缺2");
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises(); await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[1]!;
  expect(url).toBe(`/api/wms/outbound/${id}/picking`);
  expect(JSON.parse(String(options?.body))).toEqual({ expectedVersion: version, lineId: id,
    pickedQuantity: "5", shortageQuantity: "2", reason: "实拣5缺2" });
});
it("never offers a manual shipment or unexecuted cancellation for picked transport", async () => {
  const value = detail(2); Object.assign(value.lines[0]!, { branch: 1, pickedQuantity: "5.000000", shortageQuantity: "2.000000" });
  await load(value);
  expect(wrapper!.text()).toContain("等待运输，不能手工发运");
  expect(wrapper!.find('[data-test="prepare-outbound-shipment"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="prepare-outbound-cancellation"]').exists()).toBe(false);
});
it("preflights exact unit precision and does not send excessive or fractional countable picks", async () => {
  await load(detail(1)); await wrapper!.get('[data-test="prepare-outbound-picking"]').trigger("click");
  await wrapper!.get('input[name="pickedQuantity"]').setValue("0.001"); await wrapper!.get('textarea[name="reason"]').setValue("核对");
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit");
  expect(wrapper!.text()).toContain("单位精度"); expect(fetch).toHaveBeenCalledTimes(1);
});
it.each(["wms.outbound.read", "wms.outbound.approve", "wms.warehouses.read", "wms.locations.read", "wms.items.read", "wms.units.read", "wms.containers.read"])(
  "requires all fields in %s before confirmation", async permission => {
    sessionState.subject!.grants.find(grant => grant.permission === permission)!.fields.pop();
    confirmation(); await flushPromises();
    expect(wrapper!.find('[data-test="confirm-outbound"]').exists()).toBe(false);
    expect(fetch).not.toHaveBeenCalled(); expect(wrapper!.text()).toContain("缺少完整字段权限");
  });
it.each(["shortage-release", "shipment", "close-remainder"] as const)("previews and executes named %s, never rewrites original plan", async kind => {
  const value = detail(2); Object.assign(value.lines[0]!, { pickedQuantity: "5.000000", shortageQuantity: "2.000000",
    releasedQuantity: kind === "shortage-release" ? "0.000000" : "2.000000",
    reservedQuantity: kind === "shortage-release" ? "7.000000" : kind === "shipment" ? "5.000000" : "0.000000",
    shippedQuantity: kind === "close-remainder" ? "5.000000" : "0.000000" });
  await load(value); await wrapper!.get(`[data-test="prepare-outbound-${kind}"]`).trigger("click");
  if (kind !== "close-remainder") await wrapper!.get('input[name="quantity"]').setValue(kind === "shipment" ? "5" : "2");
  if (kind !== "shipment") await wrapper!.get('textarea[name="reason"]').setValue("已核实原事实");
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1); await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[1]!;
  expect(url).toBe(`/api/wms/outbound/${id}/${kind}`);
  const input = JSON.parse(String(options?.body));
  expect(input.expectedVersion).toBe(version); expect(input).not.toHaveProperty("plannedQuantity");
  expect(input).not.toHaveProperty("actorId"); expect(input).not.toHaveProperty("transportId");
});
it("cancels only an unpicked plan using explicit reason and no fabricated execution", async () => {
  await load(detail(1)); await wrapper!.get('[data-test="prepare-outbound-cancellation"]').trigger("click");
  await wrapper!.get('textarea[name="reason"]').setValue("确认全部未执行");
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises(); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[1]![1]?.body))).toEqual({ expectedVersion: version, reason: "确认全部未执行" });
});
it("allows picked5 direct shipment while unpicked2 remains reserved for explicit partial closure", async () => {
  const value = detail(2); value.lines[0]!.pickedQuantity = "5.000000";
  await load(value); await wrapper!.get('[data-test="prepare-outbound-shipment"]').trigger("click");
  await wrapper!.get('input[name="quantity"]').setValue("5");
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises();
  expect(wrapper!.find('[data-test="confirm-outbound"]').exists()).toBe(true);
  expect(fetch).toHaveBeenCalledTimes(1); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[1]![1]?.body))).toEqual({ expectedVersion: version, lineId: id, quantity: "5" });
});
it("retains frozen key/body across Unknown, 403 rejection and manual recovery; never automatic", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 })).mockResolvedValueOnce(new Response("{}", { status: 403 }));
  confirmation(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  await wrapper!.get('[data-test="retry-outbound"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
  await wrapper!.get('[data-test="retry-outbound"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(calls).toHaveLength(3); expect(new Set(calls.map(call => call[1]?.body)).size).toBe(1);
  expect(new Set(calls.map(call => new Headers(call[1]?.headers).get("Idempotency-Key"))).size).toBe(1);
  expect(wrapper!.text()).toContain(`原执行版本：${next}`);
});
it("keeps the original confirmation alive after attempted invalidates detail freshness", async () => {
  await load(); await wrapper!.get('[data-test="prepare-outbound-approval"]').trigger("click");
  await wrapper!.get('input[name="expectedBalanceVersion"]').setValue(version);
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  await confirm();
  expect(wrapper!.find('[data-test="retry-outbound"]').exists()).toBe(true);
  expect(wrapper!.find('[data-test="outbound-query"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="prepare-outbound-approval"]').exists()).toBe(false);
});
it("uses exactly 10 seconds, discards late success, blocks offline and never queues", async () => {
  vi.useFakeTimers(); let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  confirmation(); await confirm();
  await vi.advanceTimersByTimeAsync(9999); expect(wrapper!.find('[data-test="retry-outbound"]').exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(1); expect(wrapper!.find('[data-test="retry-outbound"]').exists()).toBe(true);
  resolve(new Response(JSON.stringify({ decision: 0, id, version: next }))); await flushPromises();
  expect(wrapper!.find('[data-test="read-outbound-after-write"]').exists()).toBe(false);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.get('[data-test="retry-outbound"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("invalidates old execution and hides original body immediately on subject change", async () => {
  confirmation(); await confirm(); sessionState.subject!.userId = next; await flushPromises();
  expect(wrapper!.text()).not.toContain("OUT-EXEC");
  expect(wrapper!.find('[data-test="outbound-preview-body"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="read-outbound-after-write"]').exists()).toBe(false);
});
it("clears old current detail on 403 or malformed response, not a success-shaped empty order", async () => {
  await load(); vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 403 }));
  await wrapper!.get('[data-test="outbound-query"]').trigger("submit"); await flushPromises();
  expect(wrapper!.find('[data-test="outbound-detail"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("RequestFailed403");
});
it("discards old late detail response after switching to another UUID", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  wrapper = mount(OutboundPage);
  await wrapper.get('input[name="orderId"]').setValue(id);
  await wrapper.get('[data-test="outbound-query"]').trigger("submit");
  await wrapper.get('input[name="orderId"]').setValue(next);
  resolve(new Response(JSON.stringify({ decision: 0, record: detail() }))); await flushPromises();
  expect(wrapper.find('[data-test="outbound-detail"]').exists()).toBe(false);
  expect(wrapper.get<HTMLInputElement>('input[name="orderId"]').element.value).toBe(next);
});
it("route UUID change drops confirmation and aborts its old pending execution", async () => {
  await load(); await wrapper!.get('[data-test="prepare-outbound-approval"]').trigger("click");
  await wrapper!.get('input[name="expectedBalanceVersion"]').setValue(version);
  await wrapper!.get('[data-test="outbound-action-form"]').trigger("submit"); await flushPromises();
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  await confirm(); const signal = vi.mocked(fetch).mock.calls[1]![1]?.signal;
  await wrapper!.setProps({ id: next });
  expect(signal?.aborted).toBe(true);
  resolve(new Response(JSON.stringify({ decision: 0, id, version: next }))); await flushPromises();
  expect(wrapper!.find('[data-test="read-outbound-after-write"]').exists()).toBe(false);
  expect(wrapper!.get<HTMLInputElement>('input[name="orderId"]').element.value).toBe(next);
});
it("keeps old detail visibly stale after connection failure and disables actions", async () => {
  await load(); vi.mocked(fetch).mockRejectedValueOnce(new TypeError("连接失败"));
  await wrapper!.get('[data-test="outbound-query"]').trigger("submit"); await flushPromises();
  expect(wrapper!.get('[data-test="outbound-detail"]').text()).toContain("OUT-EXEC");
  expect(wrapper!.get('[data-test="prepare-outbound-approval"]').attributes("disabled")).toBeDefined();
  expect(wrapper!.text()).toContain("陈旧详情");
});
