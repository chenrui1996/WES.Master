import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import WmsCreationEvents from "./WmsCreationEvents.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { creationEventFields, queryCreationEvents } from "./wms-creation-events";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const row = { eventId: id, owner: "Wms", contractName: "wms.master-data.created", schemaVersion: 1,
  aggregateId: id, aggregateVersion: id, correlationId: id, occurredAt: "2026-10-27T00:00:00Z", status: 0 };
const page = (rows: unknown[] = [row], offset = 0, limit = 25) => new Response(JSON.stringify({ rows, offset, limit }));
let wrapper: VueWrapper | undefined;
async function read() { await wrapper!.get('button[data-test="read-wms-events"]').trigger("click"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "wms.messages.read", scope: 0, departments: [], fields: [...creationEventFields] }] };
  vi.stubGlobal("fetch", vi.fn(async () => page()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
it("reads only explicit WMS pending metadata with Own scope and no fictitious delivery statistics", async () => {
  wrapper = mount(WmsCreationEvents); expect(fetch).not.toHaveBeenCalled(); await read();
  expect(fetch).toHaveBeenCalledWith("/api/wms/master-data-events?offset=0&limit=25", expect.objectContaining({ cache: "no-store" }));
  expect(wrapper.get('[data-test="wms-events"]').text()).toContain(id);
  expect(wrapper.text()).toContain("尚未接入");
  expect(wrapper.text()).not.toContain("Inbox 收据数");
  expect(wrapper.find('[data-test="replay"]').exists()).toBe(false);
});
it.each(creationEventFields)("requires current WMS field %s", async field => {
  sessionState.subject!.grants[0]!.fields = creationEventFields.filter(value => value !== field);
  wrapper = mount(WmsCreationEvents); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([
  [0, "待发布（Pending）"], [1, "投递中（Leased）"], [2, "已确认投递（Published）"], [3, "已隔离（Quarantined）"],
])("renders the actual durable delivery state %i without business-completion claims", async (status, label) => {
  vi.mocked(fetch).mockResolvedValueOnce(page([{ ...row, status }]));
  wrapper = mount(WmsCreationEvents); await read();
  expect(wrapper.get('[data-test="wms-events"] h2').text()).toBe(label);
  expect(wrapper.text()).toContain("不表示业务完成");
});
it.each(["platform.messages.read", "wms.warehouses.read"])("cannot borrow %s permission", async permission => {
  sessionState.subject!.grants[0]!.permission = permission;
  wrapper = mount(WmsCreationEvents); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...row, owner: "Platform" }, { ...row, contractName: "other" }, { ...row, schemaVersion: 2 },
  { ...row, status: 4 }, { ...row, eventId: "bad" }, { ...row, aggregateId: "bad" },
  { ...row, aggregateVersion: "00000000-0000-0000-0000-000000000000" }, { ...row, correlationId: "" },
  { ...row, occurredAt: "2026-02-30T00:00:00Z" }, { ...row, occurredAt: "2026-10-27T00:00:00+08:00" },
  { ...row, payload: "secret" }, { ...row, attempts: 0 },
])("clears old metadata on malformed or expanded row %#", async invalid => {
  wrapper = mount(WmsCreationEvents); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([invalid])); await read();
  expect(wrapper.find('[data-test="wms-events"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([
  () => page([row, row]), () => page(Array.from({ length: 26 }, () => row)),
  () => page([], 25), () => page([], 0, 50), () => new Response("{"),
  () => new Response(JSON.stringify({ rows: [], offset: 0, limit: 25, total: 0 })),
])("rejects malformed or mismatched page %#", async response => {
  vi.mocked(fetch).mockResolvedValueOnce(response()); wrapper = mount(WmsCreationEvents); await read();
  expect(wrapper.text()).toContain("无效");
});
it.each([[-1, 25], [10001, 25], [0.5, 25], [0, 1]])("rejects bounds %s/%s before HTTP", async (offset, limit) => {
  await expect(queryCreationEvents(offset, limit, new AbortController().signal)).rejects.toThrow("范围");
  expect(fetch).not.toHaveBeenCalled();
});
it.each([401, 403, 404])("clears old rows on HTTP %i", async status => {
  wrapper = mount(WmsCreationEvents); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status }));
  await read(); expect(wrapper.find('[data-test="wms-events"]').exists()).toBe(false);
});
it("keeps an identical failed query stale, never pretending an empty success", async () => {
  wrapper = mount(WmsCreationEvents); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "safe-id" }), { status: 503 }));
  await read(); expect(wrapper.get('[data-test="wms-events"]').text()).toContain(id);
  expect(wrapper.text()).toContain("陈旧"); expect(wrapper.text()).toContain("safe-id");
});
it("shows a real empty result without inferring other owners or broker health", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page([])); wrapper = mount(WmsCreationEvents); await read();
  expect(wrapper.text()).toContain("当前授权范围没有创建事件");
});
it.each(["grants", "identity", "offline", "unmount"])("cancels %s and discards late results", async change => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.mocked(fetch).mockImplementationOnce((_, options) => {
    signal = options?.signal ?? undefined; return new Promise(resolve => { finish = resolve; });
  });
  wrapper = mount(WmsCreationEvents); await wrapper.get('[data-test="read-wms-events"]').trigger("click");
  if (change === "grants") sessionState.subject!.grants = [];
  if (change === "identity") sessionState.subject!.sessionId = "s2";
  if (change === "offline") { vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); }
  if (change === "unmount") { wrapper.unmount(); wrapper = undefined; }
  await flushPromises(); expect(signal?.aborted).toBe(true);
  finish(page()); await flushPromises();
  expect(wrapper?.find('[data-test="wms-events"]').exists() ?? false).toBe(false);
});
it("does not automatically reread when connectivity returns", async () => {
  wrapper = mount(WmsCreationEvents); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  await read(); expect(fetch).toHaveBeenCalledTimes(1); expect(wrapper.text()).toContain("陈旧");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("uses realtime paging and clears rows before moving or resizing", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page(Array.from({ length: 25 }, (_, index) => ({
    ...row, eventId: `77966ccd-f1dd-412d-8e45-${String(index).padStart(12, "0")}`,
  }))));
  wrapper = mount(WmsCreationEvents); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([], 25));
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith("/api/wms/master-data-events?offset=25&limit=25", expect.anything());
  expect(wrapper.find('[data-test="wms-events"]').exists()).toBe(false);
  vi.mocked(fetch).mockResolvedValueOnce(page([], 0, 50));
  await wrapper.get("select").setValue("50"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith("/api/wms/master-data-events?offset=0&limit=50", expect.anything());
});
