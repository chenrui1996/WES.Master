import { mount, flushPromises } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import SessionPage from "./SessionPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "./identity-api";

const family = {
  id: "other-family", version: "opaque-version+/=", client: 0, current: false, state: 0,
  createdAt: "2026-10-24T00:00:00Z", lastActivityAt: "2026-10-24T01:00:00Z",
  expiresAt: "2000-10-24T08:00:00Z", revokedAt: null, requiresPasswordChange: false,
};
const wrappers: ReturnType<typeof mount>[] = [];
function render() { const wrapper = mount(SessionPage); wrappers.push(wrapper); return wrapper; }
function response(value: unknown) { return new Response(JSON.stringify(value), { status: 200 }); }
function deferred() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; });
  return { promise, resolve };
}
function admin(fields = ["State", "UserId", "Version", "Client"]) {
  sessionState.subject!.grants = [{ permission: "platform.sessions.read", scope: 2, departments: [], fields }];
}
beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, "showModal", { configurable: true, value() { this.open = true; } });
  Object.defineProperty(HTMLDialogElement.prototype, "close", { configurable: true, value() { this.open = false; } });
  setAccessToken("opaque", "2099-01-01T00:00:00Z");
  sessionState.subject = {
    tenantId: "tenant", userId: "user", sessionId: "session",
    requiresPasswordChange: false, credentialVersion: "credential-version", grants: [],
  };
});
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.unstubAllGlobals(); forgetIdentity(); });

it("shows session metadata, confirms scope and sends selected version without refresh secrets", async () => {
  const posted: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
    if (options.method === "POST") { posted.push(String(options.body)); return new Response(null, { status: 204 }); }
    return response([family]);
  }));
  const wrapper = render(); await flushPromises();
  expect(wrapper.text()).toContain("other-family");
  await wrapper.get('[data-test="revoke"]').trigger("click");
  expect(posted).toHaveLength(0);
  await wrapper.get('[data-test="confirm-revoke"]').trigger("click"); await flushPromises();
  expect(posted).toEqual([JSON.stringify({ expectedVersion: family.version })]);
  expect(wrapper.text()).not.toContain("refreshToken");
});

it("renders authoritative active and idle states without consulting expiry clock", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => response([family, { ...family, id: "idle-family", state: 1 }])));
  const wrapper = render(); await flushPromises();
  expect(wrapper.get("tbody").text()).toContain("活跃");
  expect(wrapper.get("tbody").text()).toContain("闲置");
  expect(wrapper.get("tbody").text()).not.toContain("已过期");
});

it("exposes stale rows without actions during same-scope refresh failure and allows explicit retry", async () => {
  const next = deferred();
  const fetcher = vi.fn().mockResolvedValueOnce(response([family])).mockReturnValueOnce(next.promise)
    .mockResolvedValueOnce(response([]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="revoke"]').trigger("click");
  await wrapper.get('[data-test="reload"]').trigger("click");
  expect(wrapper.text()).toContain("正在读取");
  expect(wrapper.find("tbody tr").exists()).toBe(true);
  expect(wrapper.get('[data-test="revoke"]').attributes("disabled")).toBeDefined();
  expect((wrapper.get("dialog").element as HTMLDialogElement).open).toBe(false);
  next.resolve(new Response(null, { status: 503 })); await flushPromises();
  expect(wrapper.get('[aria-label="会话列表"] [role="status"]').text()).toContain("503");
  expect(wrapper.find("tbody tr").exists()).toBe(true);
  expect(wrapper.get('[data-test="revoke"]').attributes("disabled")).toBeDefined();
  await wrapper.get('[data-test="reload"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("当前没有可展示");
});

it("invalidates old responses and confirmation immediately when filters change", async () => {
  const late = deferred();
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(response([family])).mockReturnValueOnce(late.promise));
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="revoke"]').trigger("click");
  await wrapper.get('[data-test="reload"]').trigger("click");
  await wrapper.get("#session-state").setValue("idle");
  late.resolve(response([family])); await flushPromises();
  expect(wrapper.find("tbody tr").exists()).toBe(false);
  expect((wrapper.get("dialog").element as HTMLDialogElement).open).toBe(false);
});

it("sends explicit state, ISO time range and bounded pagination filters", async () => {
  const fetcher = vi.fn(async (_url: string) => response([family]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get("#session-state").setValue("idle");
  await wrapper.get("#session-from").setValue("2026-10-01T00:00");
  await wrapper.get("#session-to").setValue("2026-11-01T00:00");
  await wrapper.get("#session-limit").setValue("1");
  await wrapper.get('[data-test="session-filters"]').trigger("submit"); await flushPromises();
  const query = new URL(String(fetcher.mock.calls.at(-1)?.[0]), "https://example.test").searchParams;
  expect(query.get("state")).toBe("idle");
  expect(query.get("from")).toBe(new Date("2026-10-01T00:00").toISOString());
  expect(query.get("to")).toBe(new Date("2026-11-01T00:00").toISOString());
  expect(query.get("limit")).toBe("1");
  await wrapper.get('[data-test="next-page"]').trigger("click"); await flushPromises();
  expect(String(fetcher.mock.calls.at(-1)?.[0])).toContain("offset=1");
});

it("rejects invalid ranges without sending requests", async () => {
  const fetcher = vi.fn(async () => response([])); vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get("#session-from").setValue("2026-11-01T00:00");
  await wrapper.get("#session-to").setValue("2026-10-01T00:00");
  await wrapper.get('[data-test="session-filters"]').trigger("submit"); await flushPromises();
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(wrapper.get('[role="alert"]').text()).toContain("时间");
});

it("uses admin envelope and only projected fields in columns and details", async () => {
  admin();
  const fetcher = vi.fn(async (url: string) => url.includes("admin-sessions")
    ? response({ authorized: true, rows: [{ id: "admin-family", state: 1, version: "admin-version", client: null, userId: null }] })
    : response([family]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="admin-tab"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("admin-family");
  expect(wrapper.find('[data-test="column-createdAt"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="column-userId"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="revoke"]').exists()).toBe(false);
  expect(wrapper.get("tbody").text()).toContain("闲置");
  expect(wrapper.text()).not.toContain("admin-version");
});

it("requires admin revoke permission and preserves the exact selected opaque version", async () => {
  admin();
  sessionState.subject!.grants.push({ permission: "platform.sessions.revoke", fields: [], departments: [], scope: 2 });
  const fetcher = vi.fn(async (url: string, options: RequestInit) => options.method === "POST"
    ? new Response(null, { status: 204 })
    : url.includes("admin-sessions") ? response({ authorized: true, rows: [family] }) : response([]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="admin-tab"]').trigger("click"); await flushPromises();
  await wrapper.get('[data-test="revoke"]').trigger("click");
  await wrapper.get('[data-test="confirm-revoke"]').trigger("click"); await flushPromises();
  expect(fetcher.mock.calls.find(([, options]) => options.method === "POST")?.[1].body)
    .toBe(JSON.stringify({ expectedVersion: family.version }));
});

it("does not offer admin state or user filtering without their field grants", async () => {
  admin(["Version"]);
  vi.stubGlobal("fetch", vi.fn(async (url: string) => url.includes("admin-sessions")
    ? response({ authorized: true, rows: [{ id: "admin-family" }] }) : response([])));
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="admin-tab"]').trigger("click"); await flushPromises();
  expect(wrapper.find("#session-state").exists()).toBe(false);
  expect(wrapper.find("#session-user").exists()).toBe(false);
  expect(wrapper.find('[data-test="revoke"]').exists()).toBe(false);
});

it.each([{}, [{ ...family, state: 9 }], [{ ...family, version: 12 }], [{ ...family, id: "" }]])(
  "rejects malformed self response %j", async value => {
    vi.stubGlobal("fetch", vi.fn(async () => response(value)));
    const wrapper = render(); await flushPromises();
    expect(wrapper.find("tbody tr").exists()).toBe(false);
    expect(wrapper.get('[role="alert"]').text()).toContain("格式");
  },
);

it("clears sensitive rows and pending requests immediately on permission or subject changes", async () => {
  admin();
  const late = deferred();
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(response([family])).mockReturnValueOnce(late.promise));
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="admin-tab"]').trigger("click");
  sessionState.subject!.grants = []; await flushPromises();
  late.resolve(response({ authorized: true, rows: [family] })); await flushPromises();
  expect(wrapper.find('[data-test="admin-tab"]').exists()).toBe(false);
  expect(wrapper.find("tbody tr").exists()).toBe(false);
  sessionState.subject!.userId = "another-user"; await flushPromises();
  expect(wrapper.find("tbody tr").exists()).toBe(false);
});

it("reuses column visibility and keyboard ordering and never restores hidden values in details", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => response([family])));
  const wrapper = render(); await flushPromises();
  expect(wrapper.get("table").classes()).toContain("table-fixed-first-column");
  expect(wrapper.get("table").classes()).toContain("table-fixed-actions");
  await wrapper.get('[data-test="column-client"] input').setValue(false);
  expect(wrapper.get("tbody").text()).toContain("Web");
  await wrapper.get('[data-test="column-state"]').trigger("keydown", { key: "ArrowUp" });
  expect(wrapper.get("tbody").text()).toContain("Web");
  await wrapper.get("details.column-settings form").trigger("submit");
  expect(wrapper.get("tbody").text()).not.toContain("Web");
  const headers = wrapper.findAll("thead th").map(header => header.text());
  expect(headers.indexOf("状态")).toBeLessThan(headers.indexOf("是否当前"));
  expect(wrapper.get('[data-test="session-id"]').attributes("title")).toBe(family.id);
});

it("does not retry failed writes and invalidates their confirmation", async () => {
  const fetcher = vi.fn(async (_url: string, options: RequestInit) => options.method === "POST"
    ? new Response(null, { status: 412 }) : response([family]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="revoke"]').trigger("click");
  await wrapper.get('[data-test="confirm-revoke"]').trigger("click"); await flushPromises();
  expect(fetcher.mock.calls.filter(([, options]) => options.method === "POST")).toHaveLength(1);
  expect(wrapper.find("tbody tr").exists()).toBe(false);
  expect((wrapper.get("dialog").element as HTMLDialogElement).open).toBe(false);
});

it("drops a projected admin page on denied refresh instead of retaining sensitive rows", async () => {
  admin();
  const fetcher = vi.fn().mockResolvedValueOnce(response([]))
    .mockResolvedValueOnce(response({ authorized: true, rows: [family] }))
    .mockResolvedValueOnce(new Response(null, { status: 403 }));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="admin-tab"]').trigger("click"); await flushPromises();
  expect(wrapper.find("tbody tr").exists()).toBe(true);
  await wrapper.get('[data-test="reload"]').trigger("click"); await flushPromises();
  expect(wrapper.find("tbody tr").exists()).toBe(false);
  expect(wrapper.get('[role="alert"]').text()).toContain("403");
  expect(wrapper.find('[data-test="column-state"]').exists()).toBe(false);
});

it("invalidates a selected confirmation on a filter edit without issuing a write", async () => {
  const fetcher = vi.fn(async (_url: string, _options: RequestInit) => response([family]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  await wrapper.get('[data-test="revoke"]').trigger("click");
  await wrapper.get("#session-state").setValue("revoked");
  await wrapper.get('[data-test="confirm-revoke"]').trigger("click"); await flushPromises();
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(wrapper.find("tbody tr").exists()).toBe(false);
  expect((wrapper.get("dialog").element as HTMLDialogElement).open).toBe(false);
});

it("preserves restricted password flow, validates confirmation and submits only explicitly", async () => {
  sessionState.subject!.requiresPasswordChange = true;
  const fetcher = vi.fn(async (_url: string, options: RequestInit) => options.method === "POST"
    ? new Response(null, { status: 204 }) : response([]));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = render(); await flushPromises();
  expect(wrapper.text()).toContain("只有受限会话");
  await wrapper.get("#old-password").setValue("old synthetic password");
  await wrapper.get("#new-password").setValue("new synthetic password");
  await wrapper.get("#repeat-password").setValue("not matching");
  await wrapper.get("fieldset form").trigger("submit");
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(wrapper.get('[role="alert"]').text()).toContain("不一致");
  await wrapper.get("#repeat-password").setValue("new synthetic password");
  await wrapper.get("fieldset form").trigger("submit"); await flushPromises();
  expect(fetcher.mock.calls.at(-1)?.[1].body).toBe(JSON.stringify({
    expectedVersion: "credential-version", oldPassword: "old synthetic password", newPassword: "new synthetic password",
  }));
  expect(sessionState.subject).toBeNull();
});
