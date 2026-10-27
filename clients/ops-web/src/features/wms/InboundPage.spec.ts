import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import InboundPage from "./InboundPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { inboundFields, readInbound } from "./inbound";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const second = "77966ccd-f1dd-412d-8e45-adb475608843";
const line = { id, itemId: id, unitId: id, batch: "", precision: 6,
  planned: "999999999.999999", received: "0.000001", remaining: "999999999.999998" };
const receipt = { id, lineId: id, containerId: id, receiptLocationId: id, targetLocationId: second,
  quantity: "0.000001", requiresTransport: true, arrived: false, transportId: null };
const detail = { id, code: "IN-001", warehouseId: id, state: 2, version: id, createdAt: "2026-10-27T00:00:00.123456Z",
  lines: [line], receipts: [receipt] };
let wrapper: VueWrapper | undefined;
const response = (value: unknown = detail) => new Response(JSON.stringify(value));
async function read() { await wrapper!.get("form").trigger("submit"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "wms.inbound.read", scope: 0, departments: [], fields: [...inboundFields] }] };
  vi.stubGlobal("fetch", vi.fn(async () => response()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
it("reads explicit exact amounts and pending transport without inventing write actions", async () => {
  wrapper = mount(InboundPage, { props: { id } });
  expect(fetch).not.toHaveBeenCalled(); await read();
  expect(fetch).toHaveBeenCalledWith(`/api/wms/inbound/${id}`, expect.objectContaining({ cache: "no-store" }));
  const text = wrapper.get('[data-test="inbound-detail"]').text();
  expect(text).toContain(line.planned); expect(text).toContain(line.received); expect(text).toContain(line.remaining);
  expect(text).toContain("尚未关联，待运输"); expect(text).toContain("不能视为已到位");
  expect(wrapper.text()).toContain("创建、审核与收货入口尚未开放");
  expect(wrapper.find('[type="number"]').exists()).toBe(false);
  expect(wrapper.findAll("button")).toHaveLength(1);
});
it.each(inboundFields)("requires independent current field %s", async missing => {
  sessionState.subject!.grants[0]!.fields = inboundFields.filter(field => field !== missing);
  wrapper = mount(InboundPage, { props: { id } }); await read();
  expect(fetch).not.toHaveBeenCalled();
});
it("does not borrow warehouse or ledger fields", async () => {
  sessionState.subject!.grants[0]!.permission = "wms.ledger.read";
  wrapper = mount(InboundPage, { props: { id } }); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each(["", "bad", "00000000-0000-0000-0000-000000000000", ` ${id}`])("rejects invalid ID %s locally", async input => {
  await expect(readInbound(input, new AbortController().signal)).rejects.toThrow("UUID"); expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...detail, id: second }, { ...detail, code: "lower" }, { ...detail, createdAt: "2026-02-31T00:00:00Z" },
  { ...detail, creator: id }, { ...detail, state: 4 }, { ...detail, state: 3 }, { ...detail, state: 0 },
  { ...detail, lines: [] }, { ...detail, lines: [line, line] }, { ...detail, receipts: [receipt, receipt] },
  { ...detail, lines: [{ ...line, planned: 1 }] }, { ...detail, lines: [{ ...line, precision: 0 }] },
  { ...detail, lines: [{ ...line, planned: "1e10" }] }, { ...detail, lines: [{ ...line, remaining: "999999999.999999" }] },
  { ...detail, receipts: [{ ...receipt, lineId: second }] }, { ...detail, receipts: [{ ...receipt, arrived: true }] },
  { ...detail, receipts: [{ ...receipt, requiresTransport: false }] }, { ...detail, receipts: [] },
  { ...detail, receipts: [{ ...receipt, quantity: "-0.000001" }] },
  { ...detail, receipts: [{ ...receipt, transportId: "bad" }] },
  { ...detail, receipts: [{ ...receipt, receiptLocationId: second, arrived: false, requiresTransport: false }] },
])("rejects malformed or contradictory detail %# and clears previous data", async invalid => {
  wrapper = mount(InboundPage, { props: { id } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(response(invalid)); await read();
  expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false); expect(wrapper.text()).toContain("无效");
});
it("rejects malformed JSON without retaining old content", async () => {
  wrapper = mount(InboundPage, { props: { id } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{")); await read();
  expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false);
});
it.each([401, 403, 404])("clears detail on HTTP %i", async status => {
  wrapper = mount(InboundPage, { props: { id } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status }));
  await read(); expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false);
});
it("retains same-order 503 only as explicit stale detail with error", async () => {
  wrapper = mount(InboundPage, { props: { id } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "safe-id" }), { status: 503 }));
  await read(); expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain(detail.code);
  expect(wrapper.text()).toContain("陈旧详情"); expect(wrapper.text()).toContain("safe-id");
});
it.each(["subject", "id", "offline", "unmount"])("cancels %s and rejects late responses", async change => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.mocked(fetch).mockImplementationOnce((_, options) => {
    signal = options?.signal ?? undefined; return new Promise(resolve => { finish = resolve; });
  });
  wrapper = mount(InboundPage, { props: { id } }); await wrapper.get("form").trigger("submit");
  if (change === "subject") sessionState.subject!.sessionId = "s2";
  if (change === "id") await wrapper.get('[name="orderId"]').setValue(second);
  if (change === "offline") { vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); }
  if (change === "unmount") { wrapper.unmount(); wrapper = undefined; }
  await flushPromises(); expect(signal?.aborted).toBe(true);
  finish(response()); await flushPromises();
  expect(wrapper?.find('[data-test="inbound-detail"]').exists() ?? false).toBe(false);
});
it("clears on field revocation and route ID changes without automatically loading", async () => {
  wrapper = mount(InboundPage, { props: { id } }); await read();
  sessionState.subject!.grants[0]!.fields = ["Id"]; await flushPromises();
  expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false);
  await wrapper.setProps({ id: second });
  expect(wrapper.get<HTMLInputElement>('[name="orderId"]').element.value).toBe(second);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("marks offline stale and does not automatically reload on reconnect", async () => {
  wrapper = mount(InboundPage, { props: { id } }); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper.text()).toContain("陈旧详情"); await read(); expect(fetch).toHaveBeenCalledTimes(1);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("renders an actual draft with no receipts rather than inferring completion", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(response({ ...detail, state: 0, lines: [{ ...line, received: "0.000000", remaining: line.planned }], receipts: [] }));
  wrapper = mount(InboundPage, { props: { id } }); await read();
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain("草稿");
  expect(wrapper.text()).toContain("该单尚无收货事实");
});
it("preserves quantities above JavaScript integer precision with exact BigInt reconciliation", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(response({ ...detail, lines: [{ ...line,
    planned: "9999999999999999999999.999999", remaining: "9999999999999999999999.999998" }] }));
  wrapper = mount(InboundPage, { props: { id } }); await read();
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain("9999999999999999999999.999998");
});
it("renders same-location fully received quantities as complete without inventing transport", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(response({ ...detail, state: 3,
    lines: [{ ...line, planned: "5.000000", received: "5.000000", remaining: "0.000000" }],
    receipts: [{ ...receipt, quantity: "5.000000", targetLocationId: id, requiresTransport: false, arrived: true }] }));
  wrapper = mount(InboundPage, { props: { id } }); await read();
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain("已完成");
  expect(wrapper.text()).toContain("同址收货，无需运输");
});
