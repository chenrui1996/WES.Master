import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import InboundDraftCreation from "./InboundDraftCreation.vue";
import InboundPage from "./InboundPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { inboundDraftError, type InboundDraftBody } from "./inbound-drafts";
import { inboundFields } from "./inbound";
import { warehouseFields } from "./master-data";

const id = "017966cc-f1dd-412d-8e45-adb475608842";
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [
      { permission: "wms.inbound.write", scope: 0, departments: [], fields: ["Code", "WarehouseId", "Lines"] },
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: [...warehouseFields] },
      { permission: "wms.inbound.read", scope: 0, departments: [], fields: [...inboundFields] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ decision: 0, id }), { status: 201 })));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
function open() { wrapper = mount(InboundDraftCreation); }
async function prepare() {
  await wrapper!.get('input[name="draftCode"]').setValue("new-in");
  await wrapper!.get('input[name="draftWarehouse"]').setValue(id);
  for (const field of ["draftItem", "draftUnit"]) await wrapper!.get(`input[name="${field}"]`).setValue(id);
  await wrapper!.get('input[name="draftPlanned"]').setValue("5");
}
async function preview() { await prepare(); await wrapper!.get("form").trigger("submit"); await flushPromises(); }
async function confirm() { await wrapper!.get('[data-test="confirm-inbound-draft"]').trigger("click"); await flushPromises(); }
function body(planned: string, precision = 6): InboundDraftBody {
  return { code: "A", warehouseId: id, lines: [{ id, itemId: id, unitId: id, batch: "", planned, precision }] };
}
it("previews all stable planned identities and sends only the frozen explicit operation after confirmation", async () => {
  open(); await preview(); expect(fetch).not.toHaveBeenCalled();
  expect(wrapper!.get('[data-test="inbound-draft-preview"]').text()).toContain("NEW-IN");
  await confirm();
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe("/api/wms/inbound");
  const sent = JSON.parse(String(options?.body));
  expect(sent).toEqual({ code: "NEW-IN", warehouseId: id, lines: [{ id: expect.any(String), itemId: id, unitId: id, batch: "", precision: 0, planned: "5" }] });
  expect(new Headers(options?.headers).get("Idempotency-Key")).toMatch(/^[a-f0-9-]{36}$/);
  expect(wrapper!.text()).toContain("已确认入库草稿身份");
  expect(wrapper!.text()).toContain("不是当前版本");
  await wrapper!.get('[data-test="read-created-inbound"]').trigger("click");
  expect(wrapper!.emitted("read")).toEqual([[id]]);
  expect(wrapper!.emitted("attempted")).toHaveLength(1);
});
it.each(["", "0", "-1", "+1", "01", ".1", "1.", "1e2", " 1", "1,000", "1.0000001", "10000000000000000000000"])(
  "rejects invalid exact planned quantity %s", quantity => { expect(inboundDraftError(body(quantity))).not.toBe(""); });
it("accepts the exact maximum without Number rounding and rejects unit overprecision", () => {
  expect(inboundDraftError(body("9999999999999999999999.999999"))).toBe("");
  expect(inboundDraftError(body("1.1", 0))).not.toBe("");
  expect(inboundDraftError(body("0.000001", 6))).toBe("");
});
it("supports multiple explicit lines with stable identities and refuses removing the final line", async () => {
  open(); await prepare();
  expect(wrapper!.get('button[aria-label="移除计划行 1"]').attributes("disabled")).toBeDefined();
  await wrapper!.findAll("button").find(button => button.text() === "增加计划行")!.trigger("click");
  expect(wrapper!.findAll("fieldset")).toHaveLength(2);
  await wrapper!.get('button[aria-label="移除计划行 1"]').trigger("click");
  expect(wrapper!.findAll("fieldset")).toHaveLength(1);
});
it.each(["Code", "WarehouseId", "Lines", ...warehouseFields.map(field => `parent:${field}`)])(
  "requires current complete field %s", async missing => {
    const parent = missing.startsWith("parent:");
    const grant = sessionState.subject!.grants[parent ? 1 : 0]!;
    grant.fields = grant.fields.filter(field => field !== missing.replace("parent:", ""));
    open(); expect(wrapper!.find("form").exists()).toBe(false); expect(fetch).not.toHaveBeenCalled();
  });
it("does not send an invalid local plan", async () => {
  open(); await wrapper!.get("form").trigger("submit"); await flushPromises();
  expect(fetch).not.toHaveBeenCalled(); expect(wrapper!.find('[role="alert"]').exists()).toBe(true);
});
it("manual uncertain recovery preserves the exact original body and operation", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain("结果不明");
  expect(wrapper!.find("form").exists()).toBe(false);
  await wrapper!.get('[data-test="retry-inbound-draft"]').trigger("click"); await flushPromises();
  const calls = vi.mocked(fetch).mock.calls;
  expect(calls[0]![1]?.body).toBe(calls[1]![1]?.body);
  expect(new Headers(calls[0]![1]?.headers).get("Idempotency-Key")).toBe(new Headers(calls[1]![1]?.headers).get("Idempotency-Key"));
  expect(wrapper!.text()).toContain("已确认入库草稿身份");
});
it.each([{ decision: 0, id, version: id }, { decision: 2, id }, { decision: 0, id: "bad" }, { id }])(
  "never confirms malformed success %#", async value => {
    vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify(value)));
    open(); await preview(); await confirm();
    expect(wrapper!.text()).toContain("结果不明");
    expect(wrapper!.text()).not.toContain("已确认入库草稿身份");
  });
it.each([400, 401, 403, 404, 405, 409])("keeps explicit HTTP %i refusal distinct from confirmation", async status => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status }));
  open(); await preview(); await confirm();
  expect(wrapper!.text()).toContain(status === 401 ? "当前主体或字段权限已改变" : "本次请求被拒绝");
  expect(wrapper!.find('[data-test="retry-inbound-draft"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("does not erase a preceding Unknown when the same original operation is later refused", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 403 }));
  open(); await preview(); await confirm();
  await wrapper!.get('[data-test="retry-inbound-draft"]').trigger("click"); await flushPromises();
  expect(wrapper!.text()).toContain("先前不明结果仍需查证");
});
it("invalidates frozen business data and discards late replies after subject change", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await preview(); await confirm();
  sessionState.subject!.sessionId = "changed"; await flushPromises();
  resolve(new Response(JSON.stringify({ decision: 0, id }))); await flushPromises();
  expect(wrapper!.text()).toContain("主体或字段权限已改变");
  expect(wrapper!.find('[data-test="inbound-draft-preview"]').exists()).toBe(false);
  expect(wrapper!.text()).not.toContain("已确认入库草稿身份");
});
it("times out without replacing the original operation and ignores the late confirmation", async () => {
  vi.useFakeTimers();
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await preview(); await confirm();
  await vi.advanceTimersByTimeAsync(10000); await flushPromises();
  expect(wrapper!.text()).toContain("10 秒");
  resolve(new Response(JSON.stringify({ decision: 0, id }))); await flushPromises();
  expect(wrapper!.text()).not.toContain("已确认入库草稿身份");
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("goes Unknown on offline during submission and does not automatically retry on reconnect", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  open(); await preview(); await confirm();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper!.text()).toContain("结果不明");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
  resolve(new Response(JSON.stringify({ decision: 0, id }))); await flushPromises();
  expect(wrapper!.text()).not.toContain("已确认入库草稿身份");
});
it("actual parent keeps the panel mounted after attempted creation and wires explicit result read to a fresh GET", async () => {
  wrapper = mount(InboundPage);
  await wrapper.findAll("button").find(button => button.text() === "准备入库草稿")!.trigger("click");
  await prepare();
  await wrapper.getComponent(InboundDraftCreation).get("form").trigger("submit"); await flushPromises();
  await confirm();
  expect(wrapper.text()).toContain("已确认入库草稿身份");
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ id, code: "NEW-IN", warehouseId: id, state: 0,
    version: id, createdAt: "2026-10-28T00:00:00Z", lines: [{ id, itemId: id, unitId: id, batch: "", precision: 0,
      planned: "5.000000", received: "0.000000", remaining: "5.000000" }], receipts: [] })));
  await wrapper.get('[data-test="read-created-inbound"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith(`/api/wms/inbound/${id}`, expect.objectContaining({ cache: "no-store" }));
  expect(wrapper.get('[data-test="inbound-detail"]').text()).toContain("NEW-IN");
});
