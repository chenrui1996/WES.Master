import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MessagesPage from "./MessagesPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { queryMessages } from "./messages";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const row = { eventId: id, contractName: "platform.audit", schemaVersion: 1, owner: "Platform",
  status: 0, attempts: 0, createdAt: "2026-10-27T00:00:00Z", nextAttemptAt: "2026-10-27T00:00:00Z",
  publishedAt: null, lastErrorCategory: null };
const required = ["EventId", "Owner", "Status", "CreatedAt"];
let wrapper: VueWrapper | undefined;
const page = (rows: unknown[] = [row]) => new Response(JSON.stringify({ authorized: true, rows }));
async function read() { await wrapper!.get('form').trigger("submit"); await flushPromises(); }
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [{ permission: "platform.messages.read", scope: 2, departments: [],
      fields: [...required, "ContractName", "SchemaVersion", "Attempts", "NextAttemptAt", "PublishedAt", "LastErrorCategory"] }] };
  vi.stubGlobal("fetch", vi.fn(async () => page()));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });

it("reads only explicit Platform metadata and never presents business completion or replay", async () => {
  wrapper = mount(MessagesPage);
  expect(fetch).not.toHaveBeenCalled();
  await read();
  expect(fetch).toHaveBeenCalledWith("/api/platform/messages?offset=0&limit=25", expect.objectContaining({ cache: "no-store" }));
  expect(wrapper.get('[data-test="messages"]').text()).toContain(id);
  expect(wrapper.text()).toContain("不表示消费成功或业务完成");
  expect(wrapper.find('[data-test="replay"]').exists()).toBe(false);
});

it.each(required)("requires the full mandatory All projection including %s", async field => {
  sessionState.subject!.grants[0]!.fields = required.filter(value => value !== field);
  wrapper = mount(MessagesPage); await read();
  expect(fetch).not.toHaveBeenCalled();
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined();
});
it.each([0, 1])("does not mistake scope %i for owner-wide diagnostics", async scope => {
  sessionState.subject!.grants[0]!.scope = scope;
  wrapper = mount(MessagesPage); await read(); expect(fetch).not.toHaveBeenCalled();
});
it("does not display optional fields without their own current grant", async () => {
  sessionState.subject!.grants[0]!.fields = required;
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.get('[data-test="messages"]').text()).not.toContain("platform.audit");
  expect(wrapper.get('[data-test="messages"]').text()).toContain(id);
});
it("combines All grants but never borrows optional fields from Own scope", async () => {
  sessionState.subject!.grants = [
    { permission: "platform.messages.read", scope: 2, departments: [], fields: ["EventId", "Owner"] },
    { permission: "platform.messages.read", scope: 2, departments: [], fields: ["Status", "CreatedAt"] },
    { permission: "platform.messages.read", scope: 0, departments: [], fields: ["ContractName"] },
  ];
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.get('[data-test="messages"]').text()).toContain(id);
  expect(wrapper.get('[data-test="messages"]').text()).not.toContain("platform.audit");
});
it("shows an authorized empty result without inferring other owner or broker health", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page([]));
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.text()).toContain("当前查询没有消息行");
  expect(wrapper.text()).toContain("不表示其他 owner 无积压或 Broker 健康");
});
it.each([0, 1, 2, 3])("labels finite delivery lifecycle %i without inventing a business result", async status => {
  vi.mocked(fetch).mockResolvedValueOnce(page([{ ...row, status }]));
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.get('[data-test="messages"] h2').text()).toBe(["待发布", "发布租约中", "Broker 已确认", "已隔离"][status]);
});
it("clears rows immediately on revocation and discards a late response", async () => {
  wrapper = mount(MessagesPage); await read();
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  await wrapper.get('form').trigger("submit");
  sessionState.subject!.grants = []; await flushPromises();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  finish(page()); await flushPromises();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
});
it.each([401, 403, 404])("clears old metadata on HTTP %i", async status => {
  wrapper = mount(MessagesPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Denied", correlationId: "safe-id" }), { status }));
  await read();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
});
it("keeps a failed identical query explicitly stale with a safe correlation", async () => {
  wrapper = mount(MessagesPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable", correlationId: "safe-id" }), { status: 503 }));
  await read();
  expect(wrapper.get('[data-test="messages"]').text()).toContain(id);
  expect(wrapper.text()).toContain("陈旧");
  expect(wrapper.text()).toContain("safe-id");
});
it.each([
  { ...row, eventId: "bad" }, { ...row, owner: "Wms" }, { ...row, status: 4 },
  { ...row, attempts: -1 }, { ...row, schemaVersion: 0 },
  { ...row, createdAt: "2026-02-30T00:00:00Z" },
  { ...row, nextAttemptAt: "2026-10-27T00:00:00+08:00" },
  { ...row, lastErrorCategory: "raw secret" }, { ...row, payload: "secret" },
])("rejects malformed or expanded metadata %#", async invalid => {
  wrapper = mount(MessagesPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([invalid])); await read();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([
  () => page([row, row]),
  () => page(Array.from({ length: 26 }, () => row)),
  () => new Response("{"),
  () => new Response(JSON.stringify({ authorized: false, rows: [] })),
  () => new Response(JSON.stringify({ authorized: true, rows: [], total: 0 })),
])("rejects malformed whole pages %#", async response => {
  vi.mocked(fetch).mockResolvedValueOnce(response());
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.text()).toContain("无效");
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
});
it("does not send offline or automatically reread when connectivity returns", async () => {
  wrapper = mount(MessagesPage); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper.text()).toContain("陈旧");
  await read(); expect(fetch).toHaveBeenCalledTimes(1);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("discards an in-flight response arriving after offline cancellation", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  wrapper = mount(MessagesPage); await wrapper.get('form').trigger("submit"); await flushPromises();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  dispatchEvent(new Event("offline")); await flushPromises();
  finish(page()); await flushPromises();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(wrapper.get('button[type="submit"]').attributes("disabled")).toBeDefined();
});
it("discards a late failure after switching the current subject", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  wrapper = mount(MessagesPage); await wrapper.get('form').trigger("submit"); await flushPromises();
  sessionState.subject = { ...sessionState.subject!, userId: "another-user" }; await flushPromises();
  finish(new Response(JSON.stringify({ code: "OldFailure", correlationId: "old-subject" }), { status: 503 })); await flushPromises();
  expect(wrapper.text()).not.toContain("old-subject");
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
});
it("uses realtime paging and resets page size without retaining previous rows", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(page(Array.from({ length: 25 }, (_, index) =>
    ({ ...row, eventId: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}` }))));
  wrapper = mount(MessagesPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(page([]));
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith("/api/platform/messages?offset=25&limit=25", expect.anything());
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  vi.mocked(fetch).mockResolvedValueOnce(page([]));
  await wrapper.get('[aria-label="Platform 消息实时分页"] select').setValue("50"); await flushPromises();
  expect(fetch).toHaveBeenLastCalledWith("/api/platform/messages?offset=0&limit=50", expect.anything());
});

function grantReplay() {
  sessionState.subject!.grants.push({ permission: "platform.messages.replay", scope: 2, departments: [],
    fields: ["EventId", "Status", "Attempts", "Reason"] });
}
const quarantined = { ...row, status: 3, attempts: 2, lastErrorCategory: "InvalidContract" };
it("rereads the selected event and freezes its fresh attempt expectation before preview", async () => {
  grantReplay();
  vi.mocked(fetch).mockResolvedValueOnce(page([quarantined])).mockResolvedValueOnce(page([{ ...quarantined, attempts: 3 }]));
  wrapper = mount(MessagesPage); await read();
  await wrapper.get('[data-test="prepare-replay"]').trigger("click"); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(wrapper.findComponent({ name: "MessageReplayPanel" }).exists()).toBe(true);
  await wrapper.get('textarea').setValue("reviewed and repaired");
  await wrapper.get('.replay-panel form').trigger("submit"); await flushPromises();
  expect(wrapper.get('[data-test="replay-preview"]').text()).toContain("Expected attempts3");
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(wrapper.get('.messages-page > form button').attributes("disabled")).toBeDefined();
});
it.each([{ rows: [] }, { rows: [{ ...quarantined, status: 0 }] }, { rows: [{ ...quarantined, schemaVersion: 2 }] }])(
  "does not prepare a vanished or no-longer-replayable event %#", async ({ rows }) => {
    grantReplay();
    vi.mocked(fetch).mockResolvedValueOnce(page([quarantined])).mockResolvedValueOnce(page(rows));
    wrapper = mount(MessagesPage); await read();
    await wrapper.get('[data-test="prepare-replay"]').trigger("click"); await flushPromises();
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(wrapper.text()).toContain("不能准备重放");
    expect(fetch).toHaveBeenCalledTimes(2);
  });
it.each(["EventId", "Status", "Attempts", "Reason"])("requires current replay field %s", async field => {
  grantReplay();
  sessionState.subject!.grants[1]!.fields = sessionState.subject!.grants[1]!.fields.filter(value => value !== field);
  vi.mocked(fetch).mockResolvedValueOnce(page([quarantined]));
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.find('[data-test="prepare-replay"]').exists()).toBe(false);
});
it.each(["ContractName", "SchemaVersion", "Attempts"])("requires current read field %s before replay preparation", async field => {
  grantReplay();
  sessionState.subject!.grants[0]!.fields = sessionState.subject!.grants[0]!.fields.filter(value => value !== field);
  vi.mocked(fetch).mockResolvedValueOnce(page([quarantined]));
  wrapper = mount(MessagesPage); await read();
  expect(wrapper.find('[data-test="prepare-replay"]').exists()).toBe(false);
});
it("never prepares from a stale row following a transient read failure", async () => {
  grantReplay();
  vi.mocked(fetch).mockResolvedValueOnce(page([quarantined]));
  wrapper = mount(MessagesPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(new Response(JSON.stringify({ code: "Unavailable" }), { status: 503 }));
  await read();
  expect(wrapper.get('[data-test="prepare-replay"]').attributes("disabled")).toBeDefined();
});
it("clears rows on filter changes and uses the same server filters when preparing replay", async () => {
  grantReplay(); wrapper = mount(MessagesPage); await read();
  await wrapper.get('[name="messageStatus"]').setValue("3");
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  await wrapper.get('[name="messageFrom"]').setValue("2026-10-27T00:00");
  await wrapper.get('[name="messageBefore"]').setValue("2026-10-28T00:00");
  vi.mocked(fetch).mockResolvedValueOnce(page([quarantined])).mockResolvedValueOnce(page([quarantined]));
  await read();
  await wrapper.get('[data-test="prepare-replay"]').trigger("click"); await flushPromises();
  const urls = vi.mocked(fetch).mock.calls.slice(-2).map(([url]) => new URL(String(url), "https://localhost"));
  for (const url of urls) {
    expect(url.searchParams.get("status")).toBe("3");
    expect(url.searchParams.get("createdFrom")).toBe("2026-10-27T00:00:00Z");
    expect(url.searchParams.get("createdBefore")).toBe("2026-10-28T00:00:00Z");
  }
});
it("rejects a response outside the requested status instead of displaying it", async () => {
  wrapper = mount(MessagesPage);
  await wrapper.get('[name="messageStatus"]').setValue("3");
  await read();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("无效");
});
it.each([
  { createdAt: "2026-10-27T00:00:00Z", accepted: true },
  { createdAt: "2026-10-27T00:00:59.9999999Z", accepted: true },
  { createdAt: "2026-10-27T00:01:00Z", accepted: false },
  { createdAt: "2026-10-26T23:59:59Z", accepted: false },
])("validates the exact half-open response time boundary $createdAt", async ({ createdAt, accepted }) => {
  wrapper = mount(MessagesPage);
  await wrapper.get('[name="messageFrom"]').setValue("2026-10-27T00:00");
  await wrapper.get('[name="messageBefore"]').setValue("2026-10-27T00:01");
  vi.mocked(fetch).mockResolvedValueOnce(page([{ ...row, createdAt }])); await read();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(accepted);
});
it.each([
  { status: 4 }, { status: -1 }, { status: 0.5 }, { createdFrom: "bad" },
  { createdBefore: "0001-01-01T00:00:00Z" }, { createdFrom: "2026-02-30T00:00:00Z" },
  { createdFrom: row.createdAt, createdBefore: row.createdAt },
])("rejects invalid client filters before networking %#", async filters => {
  await expect(queryMessages(0, 25, new AbortController().signal, filters)).rejects.toThrow();
  expect(fetch).not.toHaveBeenCalled();
});
it("discards a late unfiltered response after the filter changes", async () => {
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  wrapper = mount(MessagesPage); await wrapper.get('form').trigger("submit"); await flushPromises();
  await wrapper.get('[name="messageStatus"]').setValue("3");
  finish(page()); await flushPromises();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(wrapper.text()).not.toContain(id);
});
it.each([
  { createdAt: "2026-10-27T00:00:00.0000000Z", accepted: false },
  { createdAt: "2026-10-27T00:00:00.0000001Z", accepted: true },
  { createdAt: "2026-10-27T00:00:00.0000002Z", accepted: false },
])("preserves sub-millisecond request precision and exact response comparisons $createdAt", async ({ createdAt, accepted }) => {
  const filters = { createdFrom: "2026-10-27T00:00:00.0000001Z", createdBefore: "2026-10-27T00:00:00.0000002Z" };
  vi.mocked(fetch).mockResolvedValueOnce(page([{ ...row, createdAt }]));
  const result = queryMessages(0, 25, new AbortController().signal, filters);
  if (accepted) expect(await result).toHaveLength(1);
  else await expect(result).rejects.toThrow("无效");
  const query = new URL(String(vi.mocked(fetch).mock.calls[0]![0]), "https://localhost").searchParams;
  expect(query.get("createdFrom")).toBe(filters.createdFrom);
  expect(query.get("createdBefore")).toBe(filters.createdBefore);
});
