import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MessageCounts from "./MessageCounts.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

const fields = ["Owner", "ObservedAt", "PendingCount", "LeasedCount", "PublishedCount", "QuarantinedCount", "InboxReceiptCount"];
const summary = { owner: "Platform", observedAt: "2026-10-27T00:00:00Z",
  pendingCount: 1, leasedCount: 2, publishedCount: 3, quarantinedCount: 4, inboxReceiptCount: 5 };
const response = (value: unknown = { authorized: true, summary }) => new Response(JSON.stringify(value));
let wrapper: VueWrapper | undefined;
async function read() { await wrapper!.get('[data-test="read-counts"]').trigger("click"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "platform.messages.read", scope: 2, departments: [], fields: [...fields] }] };
  vi.stubGlobal("fetch", vi.fn(async () => response()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
it("reads explicit unfiltered owner counts and distinguishes retained Inbox receipts from business effects", async () => {
  wrapper = mount(MessageCounts); expect(fetch).not.toHaveBeenCalled(); await read();
  expect(fetch).toHaveBeenCalledWith("/api/platform/messages/summary", expect.objectContaining({ cache: "no-store" }));
  expect(wrapper.get('[data-test="message-counts"]').text()).toContain("5");
  expect(wrapper.text()).toContain("不受消息列表筛选影响");
  expect(wrapper.text()).toContain("不是唯一业务效果数");
});
it.each(fields)("requires current count field %s instead of trusting row grants", async missing => {
  sessionState.subject!.grants[0]!.fields = fields.filter(field => field !== missing);
  wrapper = mount(MessageCounts); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([0, 1])("requires All scope rather than %i", async scope => {
  sessionState.subject!.grants[0]!.scope = scope;
  wrapper = mount(MessageCounts); await read(); expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...summary, pendingCount: -1 }, { ...summary, leasedCount: 1.5 },
  { ...summary, publishedCount: Number.MAX_SAFE_INTEGER + 1 },
  { ...summary, inboxReceiptCount: "5" }, { ...summary, owner: "Wms" },
  { ...summary, observedAt: "2026-02-30T00:00:00Z" }, { ...summary, payload: "bad" },
])("rejects invalid counts without replacing them with zero %#", async invalid => {
  wrapper = mount(MessageCounts); await read();
  vi.mocked(fetch).mockResolvedValueOnce(response({ authorized: true, summary: invalid })); await read();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([401, 403, 404])("clears retained counts on status %i", async status => {
  wrapper = mount(MessageCounts); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "c1" }), { status }));
  await read(); expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
});
it("retains explicit stale counts on a temporary failure", async () => {
  wrapper = mount(MessageCounts); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "c1" }), { status: 503 }));
  await read();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(true);
  expect(wrapper.text()).toContain("陈旧");
  expect(wrapper.text()).toContain("c1");
});
it("clears on current authority change and discards late counts", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  wrapper = mount(MessageCounts); await wrapper.get('[data-test="read-counts"]').trigger("click"); await flushPromises();
  sessionState.subject!.grants = []; await flushPromises();
  finish(response()); await flushPromises();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
});
it("marks counts stale on a replay revision without automatic rereading", async () => {
  wrapper = mount(MessageCounts, { props: { revision: 0 } }); await read();
  await wrapper.setProps({ revision: 1 });
  expect(wrapper.text()).toContain("陈旧");
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("does not automatically refresh on offline recovery", async () => {
  wrapper = mount(MessageCounts); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  await read(); expect(fetch).toHaveBeenCalledTimes(1);
  expect(wrapper.text()).toContain("陈旧");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it.each([
  { authorized: false, summary }, { authorized: true, summary: null },
  { authorized: true, summary, total: 0 }, { summary },
])("rejects malformed or denied success-shaped summary envelopes %#", async value => {
  vi.mocked(fetch).mockResolvedValueOnce(response(value));
  wrapper = mount(MessageCounts); await read();
  expect(wrapper.text()).toContain("无效");
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
});
it("discards in-flight summary counts when a replay revision changes", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  wrapper = mount(MessageCounts, { props: { revision: 0 } });
  await wrapper.get('[data-test="read-counts"]').trigger("click"); await flushPromises();
  await wrapper.setProps({ revision: 1 });
  finish(response()); await flushPromises();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
});
it("represents a genuine authorized zero without suggesting unavailable dependencies succeeded", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(response({ authorized: true, summary: {
    ...summary, pendingCount: 0, leasedCount: 0, publishedCount: 0, quarantinedCount: 0, inboxReceiptCount: 0,
  } }));
  wrapper = mount(MessageCounts); await read();
  expect(wrapper.get('[data-test="message-counts"]').text()).toContain("待发布0");
  expect(wrapper.text()).toContain("不是统一快照或 Broker 健康检查");
});
it("rejects invalid JSON and clears prior observations", async () => {
  wrapper = mount(MessageCounts); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{")); await read();
  expect(wrapper.text()).toContain("无效");
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
});

const wmsSummary = { owner: "Wms", observedAt: "2026-10-27T00:00:00Z", pendingCount: 27 };
function wmsGrants(scope = 0, selected = ["Owner", "ObservedAt", "PendingCount"]) {
  sessionState.subject!.grants = [{ permission: "wms.messages.read", scope, departments: [], fields: selected }];
}
it.each([0, 1, 2])("reads WMS creator-scoped statistics with explicit fields and scope %i", async scope => {
  wmsGrants(scope); vi.mocked(fetch).mockResolvedValueOnce(response(wmsSummary));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } });
  expect(fetch).not.toHaveBeenCalled(); await read();
  expect(fetch).toHaveBeenCalledWith("/api/wms/master-data-events/summary", expect.objectContaining({ cache: "no-store" }));
  expect(wrapper.get('[data-test="message-counts"]').text()).toContain("待发布27");
  expect(wrapper.text()).toContain("creator");
  expect(wrapper.find('[data-test="message-counts"]').text()).not.toContain("Inbox");
  expect(wrapper.find('[data-test="message-counts"]').text()).not.toContain("Broker");
});
it.each(["Owner", "ObservedAt", "PendingCount"])("requires independent WMS statistics field %s", async missing => {
  wmsGrants(0, ["Owner", "ObservedAt", "PendingCount"].filter(field => field !== missing));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  expect(fetch).not.toHaveBeenCalled();
});
it("does not borrow Platform statistics authority for WMS", async () => {
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  expect(fetch).not.toHaveBeenCalled();
});
it.each([
  { ...wmsSummary, pendingCount: -1 }, { ...wmsSummary, pendingCount: 1.1 },
  { ...wmsSummary, pendingCount: Number.MAX_SAFE_INTEGER + 1 }, { ...wmsSummary, pendingCount: "27" },
  { ...wmsSummary, observedAt: "2026-02-30T00:00:00Z" }, { ...wmsSummary, owner: "Platform" },
  { ...wmsSummary, inboxReceiptCount: 0 }, { authorized: true, summary: wmsSummary }, { ...wmsSummary, observedAt: null },
])("rejects invalid WMS counters without showing a successful zero %#", async invalid => {
  wmsGrants(); vi.mocked(fetch).mockResolvedValueOnce(response(wmsSummary));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(response(invalid)); await read();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([401, 403, 404])("clears WMS statistics on live HTTP status %i", async status => {
  wmsGrants(); vi.mocked(fetch).mockResolvedValueOnce(response(wmsSummary));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "c1" }), { status }));
  await read(); expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
});
it("keeps WMS observations explicitly stale on 503 without inventing transport counts", async () => {
  wmsGrants(); vi.mocked(fetch).mockResolvedValueOnce(response(wmsSummary));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "c1" }), { status: 503 }));
  await read(); expect(wrapper.get('[data-test="message-counts"]').text()).toContain("待发布27");
  expect(wrapper.text()).toContain("陈旧");
});
it("clears and cancels statistics on owner change and discards a late result without auto-fetching", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  wrapper = mount(MessageCounts, { props: { owner: "Platform" } });
  await wrapper.get('[data-test="read-counts"]').trigger("click"); await flushPromises();
  const signal = vi.mocked(fetch).mock.calls[0]![1]!.signal!;
  await wrapper.setProps({ owner: "Wms" });
  expect(signal.aborted).toBe(true);
  finish(response()); await flushPromises();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("clears WMS observations immediately on statistics field revocation", async () => {
  wmsGrants(); vi.mocked(fetch).mockResolvedValueOnce(response(wmsSummary));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  sessionState.subject!.grants[0]!.fields = ["Owner", "ObservedAt"]; await flushPromises();
  expect(wrapper.find('[data-test="message-counts"]').exists()).toBe(false);
  await read(); expect(fetch).toHaveBeenCalledTimes(1);
});
it("keeps WMS offline observations stale and reconnects without a request", async () => {
  wmsGrants(); vi.mocked(fetch).mockResolvedValueOnce(response(wmsSummary));
  wrapper = mount(MessageCounts, { props: { owner: "Wms" } }); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises(); await read();
  expect(wrapper.text()).toContain("陈旧");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
