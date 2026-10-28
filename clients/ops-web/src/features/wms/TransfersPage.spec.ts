import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import TransfersPage from "./TransfersPage.vue";
import TransferConfirmation from "./TransferConfirmation.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { transferFields, type TransferCommand, type TransferDetail } from "./transfers";
import { warehouseFields, locationFields } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const target = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const next = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(): TransferDetail {
  return { id, code: "TR-1", warehouseId: id, state: 1, version: id, createdAt: "2026-10-29T00:00:00Z",
    lines: [{ id, executionId: next, sourceBalanceId: id, targetBalanceId: target, itemId: id, unitId: id, batch: "", precision: 0,
      containerId: id, sourceLocationId: id, targetLocationId: target, branch: 0, plannedQuantity: "3.000000",
      reservedQuantity: "3.000000", movedQuantity: "0.000000", closedQuantity: "0.000000", executionStatus: 0,
      postingStatus: 0, sourceVersion: id, targetVersion: target }] };
}
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  const permissions: [string, readonly string[]][] = [
    ["wms.transfers.read", transferFields], ["wms.transfers.create", ["Code", "WarehouseId", "Lines"]],
    ["wms.transfers.approve", ["State"]], ["wms.transfers.dispatch", ["State"]], ["wms.transfers.sync", ["State"]],
    ["wms.transfers.arrive", ["LineId", "SourceLocationId", "TargetLocationId", "ContainerId", "Quantity", "Reason"]],
    ["wms.transfers.unknown", ["State", "Reason"]], ["wms.transfers.close", ["State", "Reason"]],
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
async function load(record = detail()) {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  wrapper = mount(TransfersPage);
  await wrapper.get('input[name="transferId"]').setValue(id);
  await wrapper.get('[data-test="transfer-query"]').trigger("submit"); await flushPromises();
}
function confirmation() {
  const command: TransferCommand = { kind: "dispatch", operation: next, id, input: { expectedVersion: id, lineId: id } };
  wrapper = mount(TransferConfirmation, { props: { command, summary: "TR-1" } });
}
async function confirm() { await wrapper!.get('[data-test="confirm-transfer"]').trigger("click"); await flushPromises(); }
it("previews all draft lines without posting, keeps decimal spelling and explicit branch", async () => {
  wrapper = mount(TransfersPage);
  await wrapper.get('[data-test="prepare-transfer-create"]').trigger("click");
  await wrapper.get('input[name="code"]').setValue("TR-NEW");
  await wrapper.get('input[name="warehouseId"]').setValue(id);
  await wrapper.get('input[name="sourceBalanceId"]').setValue(id);
  await wrapper.get('input[name="targetBalanceId"]').setValue(target);
  await wrapper.get('input[name="quantity"]').setValue("3.000");
  await wrapper.get('[data-test="transfer-create-form"]').trigger("submit");
  expect(fetch).not.toHaveBeenCalled(); await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/transfers");
  expect(JSON.parse(String(options?.body))).toEqual({ code: "TR-NEW", warehouseId: id,
    lines: [{ id: expect.any(String), sourceBalanceId: id, targetBalanceId: target, quantity: "3.000", branch: 0 }] });
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("freezes approval parent and every source/target version, then requires explicit GET", async () => {
  const record = detail(); record.state = 0; record.lines[0]!.reservedQuantity = "0.000000"; await load(record);
  await wrapper!.get('[data-test="prepare-transfer-approval"]').trigger("click");
  await wrapper!.get('[data-test="transfer-action-form"]').trigger("submit"); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[1]![1]?.body))).toEqual({ expectedVersion: id,
    balances: [{ balanceId: id, expectedVersion: id }, { balanceId: target, expectedVersion: target }] });
  expect(fetch).toHaveBeenCalledTimes(2);
  await wrapper!.get('[data-test="read-transfer-after-write"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(3);
});
it("starts a fresh single-row draft after a previous multi-row creation has been confirmed", async () => {
  wrapper = mount(TransfersPage);
  await wrapper.get('[data-test="prepare-transfer-create"]').trigger("click");
  await wrapper.get('input[name="code"]').setValue("TR-FIRST");
  await wrapper.get('input[name="warehouseId"]').setValue(id);
  await wrapper.get('input[name="sourceBalanceId"]').setValue(id);
  await wrapper.get('input[name="targetBalanceId"]').setValue(target);
  await wrapper.get('input[name="quantity"]').setValue("3");
  await wrapper.get('[data-test="transfer-add-line"]').trigger("click");
  await wrapper.findAll('input[name="sourceBalanceId"]')[1]!.setValue(crypto.randomUUID());
  await wrapper.findAll('input[name="targetBalanceId"]')[1]!.setValue(crypto.randomUUID());
  await wrapper.findAll('input[name="quantity"]')[1]!.setValue("1");
  const firstLine = wrapper.findAll("legend")[0]!.text();
  await wrapper.get('[data-test="transfer-create-form"]').trigger("submit"); await confirm();
  await wrapper.get('[data-test="read-transfer-after-write"]').trigger("click"); await flushPromises();
  await wrapper.get('[data-test="prepare-transfer-create"]').trigger("click");
  expect(wrapper.findAll("fieldset")).toHaveLength(1);
  expect(wrapper.get('input[name="code"]').element).toHaveProperty("value", "");
  expect(wrapper.get('input[name="sourceBalanceId"]').element).toHaveProperty("value", "");
  expect(wrapper.get("legend").text()).not.toBe(firstLine);
  await wrapper.get('[data-test="transfer-add-line"]').trigger("click");
  expect(wrapper.findAll("fieldset")).toHaveLength(2);
});
it("parent stale transition preserves pending original confirmation and same key/body recovery", async () => {
  await load();
  await wrapper!.get('[data-test="prepare-transfer-dispatch"]').trigger("click");
  await wrapper!.get('[data-test="transfer-action-form"]').trigger("submit");
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 })); await confirm();
  expect(wrapper!.find('[data-test="retry-transfer"]').exists()).toBe(true);
  const original = vi.mocked(fetch).mock.calls[1]!;
  await wrapper!.get('[data-test="retry-transfer"]').trigger("click"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[2]![0]).toBe(original[0]);
  expect(vi.mocked(fetch).mock.calls[2]![1]?.body).toBe(original[1]?.body);
  expect(new Headers(vi.mocked(fetch).mock.calls[2]![1]?.headers).get("Idempotency-Key"))
    .toBe(new Headers(original[1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain("不是当前版本、同步成功");
});
it("shows durable failed arrival without offering closure or another dispatch", async () => {
  const record = detail(); record.state = 2; record.lines[0]!.executionStatus = 2; record.lines[0]!.postingStatus = 3; await load(record);
  expect(wrapper!.text()).toContain("到达补账失败");
  expect(wrapper!.find('[data-test="prepare-transfer-synchronizations"]').exists()).toBe(true);
  expect(wrapper!.find('[data-test="prepare-transfer-closure"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="prepare-transfer-dispatch"]').exists()).toBe(false);
});
it("requires explicit scan tuple and exact unit quantity before arrival preview", async () => {
  const record = detail(); record.state = 2; record.lines[0]!.executionStatus = 1; await load(record);
  await wrapper!.get('[data-test="prepare-transfer-arrivals"]').trigger("click");
  await wrapper!.get('input[name="scanSourceLocationId"]').setValue(id);
  await wrapper!.get('input[name="scanTargetLocationId"]').setValue(next);
  await wrapper!.get('input[name="scanContainerId"]').setValue(id);
  await wrapper!.get('input[name="quantity"]').setValue("3");
  await wrapper!.get('textarea[name="reason"]').setValue("到达");
  await wrapper!.get('[data-test="transfer-action-form"]').trigger("submit");
  expect(wrapper!.text()).toContain("扫码"); expect(wrapper!.find('[data-test="confirm-transfer"]').exists()).toBe(false);
  await wrapper!.get('input[name="scanTargetLocationId"]').setValue(target);
  await wrapper!.get('[data-test="transfer-action-form"]').trigger("submit"); await confirm();
  expect(JSON.parse(String(vi.mocked(fetch).mock.calls[1]![1]?.body))).toEqual({ expectedVersion: id, lineId: id,
    sourceLocationId: id, targetLocationId: target, containerId: id, quantity: "3", reason: "到达" });
});
it("transport line is explicitly pending dependency and cannot use human completion", async () => {
  const record = detail(); record.lines[0]!.branch = 1; await load(record);
  expect(wrapper!.text()).toContain("可信运输"); expect(wrapper!.find('[data-test="prepare-transfer-dispatch"]').exists()).toBe(false);
});
it("drops private detail on identity change and invalidates frozen operation", async () => {
  await load(); await wrapper!.get('[data-test="prepare-transfer-dispatch"]').trigger("click");
  await wrapper!.get('[data-test="transfer-action-form"]').trigger("submit");
  sessionState.subject = { ...sessionState.subject!, userId: target }; await flushPromises();
  expect(wrapper!.find('[data-test="transfer-detail"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="confirm-transfer"]').exists()).toBe(false);
});
it("ignores late old-route reads", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockReturnValueOnce(new Promise<Response>(done => { resolve = done; }));
  wrapper = mount(TransfersPage, { props: { id } });
  await wrapper.get('[data-test="transfer-query"]').trigger("submit");
  await wrapper.setProps({ id: target });
  resolve(new Response(JSON.stringify({ decision: 0, record: detail() }))); await flushPromises();
  expect(wrapper.find('[data-test="transfer-detail"]').exists()).toBe(false);
});
it("10-second deadline ignores late success and only manual original recovery is possible", async () => {
  vi.useFakeTimers(); confirmation();
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockReturnValueOnce(new Promise<Response>(done => { resolve = done; }));
  await wrapper!.get('[data-test="confirm-transfer"]').trigger("click");
  await vi.advanceTimersByTimeAsync(10000); await flushPromises();
  expect(wrapper!.text()).toContain("10 秒"); expect(fetch).toHaveBeenCalledTimes(1);
  resolve(new Response(JSON.stringify({ decision: 0, id, version: next }))); await flushPromises();
  expect(wrapper!.find('[data-test="read-transfer-after-write"]').exists()).toBe(false);
  await wrapper!.get('[data-test="retry-transfer"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("offline abort becomes Unknown, reconnect never auto-retries", async () => {
  confirmation(); vi.mocked(fetch).mockReturnValueOnce(new Promise<Response>(() => {}));
  await wrapper!.get('[data-test="confirm-transfer"]').trigger("click");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.text()).toContain("结果不明"); expect(wrapper!.get('[data-test="retry-transfer"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("malformed 201 is Unknown, definitive rejection is not automatically retryable", async () => {
  confirmation(); vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, id, version: next, synced: true }), { status: 201 }));
  await confirm(); expect(wrapper!.text()).toContain("结果不明");
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 409 }));
  await wrapper!.get('[data-test="retry-transfer"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
  expect(wrapper!.find('[data-test="retry-transfer"]').exists()).toBe(true);
});
it("previews two complete lines and removes by stable identity without silently discarding another line", async () => {
  wrapper = mount(TransfersPage);
  await wrapper.get('[data-test="prepare-transfer-create"]').trigger("click");
  await wrapper.get('input[name="code"]').setValue("TR-TWO");
  await wrapper.get('input[name="warehouseId"]').setValue(id);
  await wrapper.get('[data-test="transfer-add-line"]').trigger("click");
  const secondSource = crypto.randomUUID(); const secondTarget = crypto.randomUUID();
  const inputs = wrapper.findAll('input[name="sourceBalanceId"]');
  await inputs[0]!.setValue(id); await inputs[1]!.setValue(secondSource);
  const targets = wrapper.findAll('input[name="targetBalanceId"]');
  await targets[0]!.setValue(target); await targets[1]!.setValue(secondTarget);
  const quantities = wrapper.findAll('input[name="quantity"]');
  await quantities[0]!.setValue("3.000"); await quantities[1]!.setValue("1");
  await wrapper.get('[data-test="transfer-create-form"]').trigger("submit");
  const preview = JSON.parse(wrapper.get('[data-test="transfer-preview-body"]').text());
  expect(preview.lines).toHaveLength(2); expect(preview.lines[1]).toMatchObject({ sourceBalanceId: secondSource, quantity: "1" });
  expect(preview.lines[0].id).not.toBe(preview.lines[1].id);
  expect(fetch).not.toHaveBeenCalled();
});
it("caps the actual creation UI at100 rows and exposes no local write queue", async () => {
  wrapper = mount(TransfersPage);
  await wrapper.get('[data-test="prepare-transfer-create"]').trigger("click");
  for (let count = 1; count < 100; ++count) await wrapper.get('[data-test="transfer-add-line"]').trigger("click");
  expect(wrapper.findAll("fieldset")).toHaveLength(100);
  expect(wrapper.get('[data-test="transfer-add-line"]').attributes("disabled")).toBeDefined();
  expect(fetch).not.toHaveBeenCalled(); expect(localStorage.length).toBe(0);
});
it("malformed or forbidden current detail clears old projection; transient failure keeps stale but unwritable data", async () => {
  await load();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  await wrapper!.get('[data-test="transfer-query"]').trigger("submit"); await flushPromises();
  expect(wrapper!.find('[data-test="transfer-detail"]').exists()).toBe(true);
  expect(wrapper!.get('[data-test="prepare-transfer-dispatch"]').attributes("disabled")).toBeDefined();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 403 }));
  await wrapper!.get('[data-test="transfer-query"]').trigger("submit"); await flushPromises();
  expect(wrapper!.find('[data-test="transfer-detail"]').exists()).toBe(false);
});
it("field revocation invalidates confirmed preview before any write", async () => {
  await load(); await wrapper!.get('[data-test="prepare-transfer-dispatch"]').trigger("click");
  await wrapper!.get('[data-test="transfer-action-form"]').trigger("submit");
  sessionState.subject!.grants = sessionState.subject!.grants.filter(grant => grant.permission !== "wms.transfers.dispatch");
  await flushPromises();
  expect(wrapper!.find('[data-test="confirm-transfer"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("缺少完整字段权限");
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("independent definitive rejection allows close but not a new blind retry", async () => {
  confirmation(); vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 409 })); await confirm();
  expect(wrapper!.find('[data-test="retry-transfer"]').exists()).toBe(false);
  expect(wrapper!.text()).toContain("本次请求被拒绝");
});
