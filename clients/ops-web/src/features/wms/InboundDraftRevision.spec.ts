import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import InboundDraftCreation from "./InboundDraftCreation.vue";
import InboundPage from "./InboundPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { inboundFields, type InboundDetail } from "./inbound";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
const version = "6a3ea5df-8407-4af1-8017-5e00d31e4d3e";
const after = "c682cb8c-94c9-44f4-9c64-86db33361e82";
function detail(): InboundDetail {
  return { id, code: "IN-REV", warehouseId: id, state: 0, version, createdAt: "2026-10-28T00:00:00Z",
    lines: [{ id, itemId: id, unitId: id, batch: "", precision: 0, planned: "5.000000", received: "0.000000", remaining: "5.000000" }], receipts: [] };
}
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [
      { permission: "wms.inbound.write", scope: 0, departments: [], fields: ["Code", "WarehouseId", "Lines"] },
      { permission: "wms.inbound.read", scope: 0, departments: [], fields: [...inboundFields] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, id, version: after }))));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
function open() { wrapper = mount(InboundDraftCreation, { props: { draft: detail() } }); }
async function preview() {
  await wrapper!.get('input[name="draftPlanned"]').setValue("7");
  await wrapper!.get("form").trigger("submit"); await flushPromises();
}
async function confirm() { await wrapper!.get('[data-test="confirm-inbound-draft"]').trigger("click"); await flushPromises(); }
it("reuses explicit plan preview with stable line IDs, immutable headers and exact original version", async () => {
  open();
  expect(wrapper!.get('input[name="draftCode"]').attributes("disabled")).toBeDefined();
  expect(wrapper!.get('input[name="draftWarehouse"]').attributes("disabled")).toBeDefined();
  await preview(); expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.get('[data-test="inbound-draft-preview"]').text()).toContain(version);
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/wms/inbound/${id}/draft`); expect(options?.method).toBe("PUT");
  expect(JSON.parse(String(options?.body))).toEqual({ expectedVersion: version,
    lines: [{ id, itemId: id, unitId: id, batch: "", precision: 0, planned: "7" }] });
  expect(wrapper!.text()).toContain(`原修订版本：${after}`);
  expect(wrapper!.text()).toContain("后续可能已变化");
  await wrapper!.get('[data-test="read-created-inbound"]').trigger("click");
  expect(wrapper!.emitted("read")).toEqual([[id]]);
});
it.each(["Code", "WarehouseId", "Lines", ...inboundFields.map(field => `read:${field}`)])(
  "requires all current fields %s without borrowing parent warehouse authority", missing => {
    const read = missing.startsWith("read:");
    const grant = sessionState.subject!.grants[read ? 1 : 0]!;
    grant.fields = grant.fields.filter(field => field !== missing.replace("read:", ""));
    open(); expect(wrapper!.find("form").exists()).toBe(false); expect(fetch).not.toHaveBeenCalled();
  });
it.each([1, 2, 3] as const)("never edits state %i", state => {
  wrapper = mount(InboundDraftCreation, { props: { draft: { ...detail(), state } } });
  expect(wrapper.find("form").exists()).toBe(false);
});
it.each([
  { decision: 0, id, version: "bad" }, { decision: 0, id }, { decision: 2, id, version: after },
  { decision: 0, id, version },
  { decision: 0, id: after, version: after }, { decision: 0, id, version: after, extra: true },
])("treats malformed or wrong-document success as Unknown %#", async value => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(value)));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.text()).not.toContain("已确认入库草稿身份");
});
it("keeps the original id, version, key and body frozen through explicit Unknown recovery", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  open(); await preview(); await confirm();
  expect(wrapper!.find("form").exists()).toBe(false);
  await wrapper!.get('[data-test="retry-inbound-draft"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(calls[0]![0]).toBe(calls[1]![0]); expect(calls[0]![1]?.body).toBe(calls[1]![1]?.body);
  expect(new Headers(calls[0]![1]?.headers).get("Idempotency-Key")).toBe(new Headers(calls[1]![1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain("已确认入库草稿身份");
});
it("invalidates old versions and ignores late replies if the source draft is replaced", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await preview(); await confirm();
  await wrapper!.setProps({ draft: { ...detail(), version: after } });
  resolve(new Response(JSON.stringify({ decision: 0, id, version: after }))); await flushPromises();
  expect(wrapper!.text()).toContain("主体或字段权限已改变");
  expect(wrapper!.find('[data-test="inbound-draft-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain("已确认入库草稿身份");
});
it("keeps uncertain revision mounted after the real parent's attempted handler clears detail", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(detail())));
  wrapper = mount(InboundPage, { props: { id } });
  await wrapper.get("form").trigger("submit"); await flushPromises();
  await wrapper.findAll("button").find(button => button.text() === "准备修订入库草稿")!.trigger("click");
  await preview();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  await confirm();
  expect(wrapper.find('[data-test="inbound-detail"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="retry-inbound-draft"]').exists()).toBe(true);
  await wrapper.get('[data-test="retry-inbound-draft"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("已确认入库草稿身份");
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ ...detail(), version: after,
    lines: [{ ...detail().lines[0], planned: "7.000000", remaining: "7.000000" }] })));
  await wrapper.get('[data-test="read-created-inbound"]').trigger("click"); await flushPromises();
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain("7.000000");
  expect(vi.mocked(fetch).mock.calls.map(call => call[1]?.method ?? "GET")).toEqual(["GET", "PUT", "PUT", "GET"]);
});
it("requires a fresh online detail and cancels editing when the selected order changes", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(detail())));
  wrapper = mount(InboundPage, { props: { id } });
  expect(wrapper.text()).not.toContain("准备修订入库草稿");
  await wrapper.get("form").trigger("submit"); await flushPromises();
  await wrapper.findAll("button").find(button => button.text() === "准备修订入库草稿")!.trigger("click");
  await preview();
  await wrapper.get('input[name="orderId"]').setValue(after);
  expect(wrapper.find('[data-test="inbound-draft-preview"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
