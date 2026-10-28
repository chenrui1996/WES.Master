import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import CountsPage from "./CountsPage.vue";
import CountConfirmation from "./CountConfirmation.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { countFields, countPolicy, type CountCommand, type CountDetail } from "./counts";
import { warehouseFields, locationFields } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const next = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(state: CountDetail["state"] = 1): CountDetail {
  return { id, code: "COUNT-EXEC", warehouseId: id, locationId: id, state, version, createdAt: "2026-10-28T00:00:00Z",
    snapshotAt: state === 0 ? null : "2026-10-28T00:01:00Z", lockOwnerId: state === 1 || state === 2 ? id : null, reminderDue: false,
    policy: countPolicy, lines: state === 0 ? [] : [{ id, itemId: id, unitId: id, batch: "", precision: 0, containerId: id,
      quantity: state === 2 ? "8.000000" : null, countedBy: state === 2 ? id : null,
      countedAt: state === 2 ? "2026-10-28T00:02:00Z" : null }] };
}
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  const permissions: [string, readonly string[]][] = [
    ["wms.counts.read", countFields], ["wms.counts.create", ["Code", "WarehouseId", "LocationId"]],
    ["wms.counts.start", ["State"]], ["wms.counts.record", ["LineId", "Quantity", "LocationId", "ItemId", "ContainerId", "Reason"]],
    ["wms.counts.adjust", ["State", "Reason"]], ["wms.counts.invalidate", ["State", "Reason"]],
    ["wms.counts.review", ["BookQuantity", "Difference"]],
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
  wrapper = mount(CountsPage);
  await wrapper.get('input[name="countId"]').setValue(id);
  await wrapper.get('[data-test="count-query"]').trigger("submit"); await flushPromises();
}
function confirmation() {
  const command: CountCommand = { kind: "invalidation", operation: next, id, input: { expectedVersion: version, reason: "人工解锁" } };
  wrapper = mount(CountConfirmation, { props: { command, summary: "COUNT-EXEC" } });
}
async function confirm() { await wrapper!.get('[data-test="confirm-count"]').trigger("click"); await flushPromises(); }
it("creates a scope-only draft through frozen preview and reads the written plan explicitly", async () => {
  wrapper = mount(CountsPage);
  await wrapper.get('[data-test="prepare-count-create"]').trigger("click");
  await wrapper.get('input[name="code"]').setValue("COUNT-NEW");
  await wrapper.get('input[name="warehouseId"]').setValue(id);
  await wrapper.get('input[name="locationId"]').setValue(next);
  await wrapper.get('[data-test="count-create-form"]').trigger("submit"); await flushPromises();
  expect(fetch).not.toHaveBeenCalled(); await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/counts");
  expect(JSON.parse(String(options?.body))).toEqual({ code: "COUNT-NEW", warehouseId: id, locationId: next });
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper.get('[data-test="read-count-after-write"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("records zero without fetching or exposing book values and does not auto-adjust", async () => {
  await load();
  expect(wrapper!.text()).toContain("未盘不是零"); expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper!.get('[data-test="prepare-count-records"]').trigger("click");
  await wrapper!.get('input[name="scanLocationId"]').setValue(id);
  await wrapper!.get('input[name="scanItemId"]').setValue(id);
  await wrapper!.get('input[name="scanContainerId"]').setValue(id);
  await wrapper!.get('input[name="quantity"]').setValue("0.000");
  await wrapper!.get('textarea[name="reason"]').setValue("无实物");
  await wrapper!.get('[data-test="count-action-form"]').trigger("submit"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1); await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[1]!;
  expect(url).toBe(`/api/wms/counts/${id}/records`);
  expect(JSON.parse(String(options?.body))).toEqual({ expectedVersion: version, lineId: id, quantity: "0.000",
    locationId: id, itemId: id, containerId: id, reason: "无实物" });
  expect(wrapper!.text()).not.toContain("10.000000");
});
it("shows only a reminder at 1800 seconds, still allows record and explicit invalidation", async () => {
  const record = detail(); record.reminderDue = true; await load(record);
  expect(wrapper!.text()).toContain("30分钟提醒"); expect(wrapper!.text()).toContain("不会自动解锁");
  expect(wrapper!.find('[data-test="prepare-count-records"]').exists()).toBe(true);
  expect(wrapper!.find('[data-test="prepare-count-invalidation"]').exists()).toBe(true);
});
it("requires explicit authorized review, blocks nonzero self-approval and clears review on reread", async () => {
  const record = detail(2); await load(record);
  expect(wrapper!.find('[data-test="prepare-count-adjustment"]').exists()).toBe(false);
  const review = { ...record, lines: record.lines.map(line => ({ ...line, bookQuantity: "10.000000", difference: "-2.000000" })) };
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record: review })));
  await wrapper!.get('[data-test="count-review"]').trigger("click"); await flushPromises();
  expect(wrapper!.get('[data-test="count-review-detail"]').text()).toContain("-2.000000");
  expect(wrapper!.text()).toContain("不能自行批准");
  expect(wrapper!.find('[data-test="prepare-count-adjustment"]').exists()).toBe(false);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record })));
  await wrapper!.get('[data-test="count-query"]').trigger("submit"); await flushPromises();
  expect(wrapper!.find('[data-test="count-review-detail"]').exists()).toBe(false);
});
it("allows a different authorized reviewer to preview the complete delta before adjustment", async () => {
  sessionState.subject!.userId = next;
  const record = detail(2); await load(record);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0,
    record: { ...record, lines: record.lines.map(line => ({ ...line, bookQuantity: "10.000000", difference: "-2.000000" })) } })));
  await wrapper!.get('[data-test="count-review"]').trigger("click"); await flushPromises();
  await wrapper!.get('[data-test="prepare-count-adjustment"]').trigger("click");
  await wrapper!.get('textarea[name="reason"]').setValue("复核实盘");
  await wrapper!.get('[data-test="count-action-form"]').trigger("submit"); await flushPromises();
  expect(wrapper!.get('[data-test="count-review-preview"]').text()).toContain("-2.000000");
  await confirm();
  expect(vi.mocked(fetch).mock.calls[2]![0]).toBe(`/api/wms/counts/${id}/adjustment`);
});
it("clears scans when selecting another line and clears private review on subject change", async () => {
  const record = detail(); record.lines.push({ ...record.lines[0]!, id: next, containerId: next }); await load(record);
  await wrapper!.get('[data-test="prepare-count-records"]').trigger("click");
  await wrapper!.get('input[name="scanContainerId"]').setValue(id);
  await wrapper!.get('input[name="quantity"]').setValue("8"); await wrapper!.get('select[name="lineId"]').setValue(next);
  expect(wrapper!.get<HTMLInputElement>('input[name="scanContainerId"]').element.value).toBe("");
  expect(wrapper!.get<HTMLInputElement>('input[name="quantity"]').element.value).toBe("");
  sessionState.subject = { ...sessionState.subject!, userId: next }; await flushPromises();
  expect(wrapper!.find('[data-test="count-detail"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="count-action-form"]').exists()).toBe(false);
});
it("discards a late review after switching the plan and never merges versions", async () => {
  await load(detail(2));
  let resolve!: (value: Response) => void; vi.mocked(fetch).mockReturnValueOnce(new Promise(done => { resolve = done; }));
  await wrapper!.get('[data-test="count-review"]').trigger("click");
  await wrapper!.setProps({ id: next });
  const record = detail(2);
  resolve(new Response(JSON.stringify({ decision: 0, record: { ...record,
    lines: record.lines.map(line => ({ ...line, bookQuantity: "10.000000", difference: "-2.000000" })) } }))); await flushPromises();
  expect(wrapper!.find('[data-test="count-review-detail"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="prepare-count-adjustment"]').exists()).toBe(false);
});
it("mismatched review version requires a fresh blind read, not an automatic overwrite", async () => {
  const record = detail(2); await load(record); sessionState.subject!.userId = next; await flushPromises();
  await wrapper!.get('[data-test="count-query"]').trigger("submit"); await flushPromises();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record: { ...record, version: next,
    lines: record.lines.map(line => ({ ...line, bookQuantity: "10.000000", difference: "-2.000000" })) } })));
  await wrapper!.get('[data-test="count-review"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("复核快照版本与盲盘详情不同");
  expect(wrapper!.find('[data-test="prepare-count-adjustment"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="count-review-detail"]').exists()).toBe(false);
});
it("hides private review immediately after its field authority is revoked", async () => {
  const record = detail(2); await load(record);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 0, record: { ...record,
    lines: record.lines.map(line => ({ ...line, bookQuantity: "10.000000", difference: "-2.000000" })) } })));
  await wrapper!.get('[data-test="count-review"]').trigger("click"); await flushPromises();
  expect(wrapper!.get('[data-test="count-review-detail"]').text()).toContain("10.000000");
  sessionState.subject!.grants.find(grant => grant.permission === "wms.counts.review")!.fields.pop(); await flushPromises();
  expect(wrapper!.find('[data-test="count-review-detail"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="count-review"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain("10.000000");
});
it("invalidation and new start previews never reuse prior measured rows", async () => {
  await load(detail(4));
  await wrapper!.get('[data-test="prepare-count-start"]').trigger("click");
  await wrapper!.get('[data-test="count-action-form"]').trigger("submit"); await flushPromises();
  expect(wrapper!.get('[data-test="count-preview-body"]').text()).toBe(JSON.stringify({ expectedVersion: version }, null, 2));
  await confirm(); expect(vi.mocked(fetch).mock.calls[1]![0]).toBe(`/api/wms/counts/${id}/start`);
});
it("invalidated counts can only restart, not record or adjust stale lines", async () => {
  await load(detail(4));
  expect(wrapper!.find('[data-test="prepare-count-start"]').exists()).toBe(true);
  expect(wrapper!.find('[data-test="prepare-count-records"]').exists()).toBe(false);
  expect(wrapper!.find('[data-test="prepare-count-adjustment"]').exists()).toBe(false);
});
it("missing record field authority hides recording and makes zero requests", async () => {
  sessionState.subject!.grants.find(grant => grant.permission === "wms.counts.record")!.fields.pop();
  await load(); expect(wrapper!.find('[data-test="prepare-count-records"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("retains frozen original operation across response loss, offline and manual recovery", async () => {
  confirmation(); vi.mocked(fetch).mockResolvedValueOnce(new Response("lost", { status: 503 })); await confirm();
  const first = vi.mocked(fetch).mock.calls[0]!;
  expect(wrapper!.text()).toContain("结果不明");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.get<HTMLButtonElement>('[data-test="retry-count"]').element.disabled).toBe(true);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ decision: 1, id, version: next })));
  await wrapper!.get('[data-test="retry-count"]').trigger("click"); await flushPromises();
  const second = vi.mocked(fetch).mock.calls[1]!;
  expect(second[0]).toBe(first[0]); expect(second[1]?.body).toBe(first[1]?.body);
  expect(new Headers(second[1]?.headers).get("Idempotency-Key")).toBe(new Headers(first[1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain(`原执行版本：${next}`);
});
it("keeps the confirmation mounted when parent detail becomes stale after a failed POST", async () => {
  await load(detail(0));
  await wrapper!.get('[data-test="prepare-count-start"]').trigger("click");
  await wrapper!.get('[data-test="count-action-form"]').trigger("submit"); await flushPromises();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("lost", { status: 503 })); await confirm();
  expect(wrapper!.find('[data-test="retry-count"]').exists()).toBe(true);
  expect(wrapper!.get<HTMLButtonElement>('[data-test="count-query-button"]').element.disabled).toBe(true);
});
it("times out without retrying and ignores late success", async () => {
  vi.useFakeTimers(); confirmation();
  let resolve!: (value: Response) => void; vi.mocked(fetch).mockReturnValueOnce(new Promise(done => { resolve = done; }));
  await wrapper!.get('[data-test="confirm-count"]').trigger("click");
  await vi.advanceTimersByTimeAsync(10000); await flushPromises();
  expect(wrapper!.text()).toContain("10 秒"); expect(fetch).toHaveBeenCalledTimes(1);
  resolve(new Response(JSON.stringify({ decision: 0, id, version: next }))); await flushPromises();
  expect(wrapper!.find('[data-test="read-count-after-write"]').exists()).toBe(false);
});
it("route and subject changes invalidate old confirmations and discard late reads", async () => {
  let resolve!: (value: Response) => void; vi.mocked(fetch).mockReturnValueOnce(new Promise(done => { resolve = done; }));
  wrapper = mount(CountsPage, { props: { id } });
  await wrapper.get('[data-test="count-query"]').trigger("submit");
  await wrapper.setProps({ id: next });
  resolve(new Response(JSON.stringify({ decision: 0, record: detail() }))); await flushPromises();
  expect(wrapper.find('[data-test="count-detail"]').exists()).toBe(false);
  wrapper.unmount(); confirmation();
  sessionState.subject = { ...sessionState.subject!, userId: next }; await flushPromises();
  expect(wrapper!.text()).toContain("主体或原输入已改变");
  expect(wrapper!.find('[data-test="confirm-count"]').exists()).toBe(false);
});
