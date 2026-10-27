import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import UsersPage from "./UsersPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

const user = { id: "u1", account: "ALICE", name: "Alice", departmentId: "d1", enabled: true,
  version: "user-v1", credentialVersion: null as string | null, roleIds: ["r1"] };
const departments = [{ id: "d1", name: "收货", code: "IN", enabled: true, version: "d-v1" },
  { id: "d2", name: "停用部门", code: "OLD", enabled: false, version: "d-v2" }];
it("disables create until the current initial user query and candidates have finished", async () => {
  let finish!: (value: Response) => void;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/users?")) return new Promise<Response>(resolve => { finish = resolve; });
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? departments : [] }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  expect(wrapper.get('[data-test="new-user"]').attributes("disabled")).toBeDefined();
  finish(new Response(JSON.stringify({ authorized: true, rows: [user] }))); await flushPromises();
  expect(wrapper.get('[data-test="new-user"]').attributes("disabled")).toBeUndefined();
  await wrapper.get('[data-test="new-user"]').trigger("click"); expect(wrapper.find('[data-test="user-form"]').exists()).toBe(true);
  wrapper.unmount();
});
it("keeps create disabled after current query failure and restores it only after explicit successful reread", async () => {
  let reads = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/users?") && ++reads === 2) return new Response('{"code":"Unavailable"}', { status: 503 });
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? departments : url.includes("/roles") ? [] : [user] }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.get('form[aria-label="用户筛选"]').trigger("submit"); await flushPromises();
  expect(wrapper.get('[data-test="new-user"]').attributes("disabled")).toBeDefined();
  await wrapper.get('form[aria-label="用户筛选"]').trigger("submit"); await flushPromises();
  expect(wrapper.get('[data-test="new-user"]').attributes("disabled")).toBeUndefined();
  expect(reads).toBe(3); wrapper.unmount();
});
it.each([403, 503, "denied"] as const)("discards the actual user role editor and protected labels after candidate %s", async status => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/roles?") && url.includes("offset=25"))
      return status === "denied" ? new Response('{"authorized":false,"rows":[]}')
        : new Response('{"code":"RoleCandidatesRevoked"}', { status });
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/roles/references") ? []
      : url.includes("/roles?") ? Array.from({ length: 25 }, (_, index) => ({
        id: `r${index + 1}`, code: `PROTECTED-${index}`, enabled: true,
      })) : url.includes("/departments") ? departments : [user] }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.get('[data-test="roles-user"]').trigger("click"); await flushPromises();
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("PROTECTED-0");
  await wrapper.get('[data-test="more-roles"]').trigger("click"); await flushPromises();
  expect(wrapper.find('[data-test="user-form"]').exists()).toBe(false);
  expect(wrapper.text()).not.toContain("PROTECTED-0");
  expect(wrapper.find('[data-test="more-roles"]').exists()).toBe(false);
  expect(wrapper.findAll('[role="alert"]').some(alert => alert.text().includes("候选读取失败"))).toBe(true);
  wrapper.unmount();
});
it("reports a current initial candidate failure rather than silently showing an empty selector", async () => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => url.includes("/roles?")
    ? new Response('{"code":"CandidatesUnavailable"}', { status: 503 })
    : new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? departments : [user] }))));
  const wrapper = mount(UsersPage); await flushPromises();
  expect(wrapper.findAll('[role="alert"]').some(alert => alert.text().includes("候选读取失败"))).toBe(true);
  wrapper.unmount();
});
beforeEach(() => {
  setAccessToken("opaque", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "me", sessionId: "s1", requiresPasswordChange: false, credentialVersion: null,
    grants: ["users", "departments", "roles"].flatMap(name => ["read", "write"].map(action => ({
      permission: `platform.${name}.${action}`, scope: 2, departments: [], fields: ["Id", "Name", "Account", "DepartmentId", "Enabled", "Version", "Roles", "Credential", "Code", "ParentId"],
    }))) };
});
it("shows the loaded department hierarchy and submits a separate exact-version parent change", async () => {
  const writes: { url: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method === "PUT") {
      writes.push({ url, body: JSON.parse(String(options.body)) });
      return new Response('{"decision":0}');
    }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? [
      { ...departments[0], parentId: null, parentName: null },
      { id: "d3", code: "CHILD", name: "下级部门", enabled: true, version: "child-version", parentId: "d1", parentName: "收货" },
    ] : url.includes("/roles") ? [] : [user] }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  expect(wrapper.get('[aria-label="当前页部门树"]').text()).toContain("下级部门");
  expect(wrapper.get('[data-department="d3"]').attributes("data-depth")).toBe("1");
  await wrapper.get('[data-test="move-department"][data-id="d3"]').trigger("click");
  expect(wrapper.get("#department-parent").findAll("option").map(option => option.attributes("value"))).not.toContain("d3");
  await wrapper.get("#department-parent").setValue("");
  await wrapper.get('[data-test="department-parent-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ url: "/api/platform/departments/d3/parent", body: { expectedVersion: "child-version", parentId: null } }]);
  wrapper.unmount();
});

it("preserves same-scope query results and reports a failed refresh as stale", async () => {
  let userReads = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/api/platform/users?")) {
      userReads++;
      return userReads === 1
        ? new Response(JSON.stringify({ authorized: true, rows: [user] }))
        : new Response(JSON.stringify({ code: "Unavailable", correlationId: "refresh-fixture" }), { status: 503 });
    }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? departments : [] }));
  }));
  const wrapper = mount(UsersPage);
  await flushPromises();
  expect(wrapper.text()).toContain("ALICE");

  await wrapper.get('[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();

  expect(wrapper.text()).toContain("ALICE");
  expect(wrapper.get('[role="status"]').text()).toContain("refresh-fixture");
  expect(wrapper.get('[data-test="edit-user"]').attributes("disabled")).toBeDefined();
  wrapper.unmount();
});

it("distinguishes the first empty directory, a submitted empty filter, and a failed query", async () => {
  let failReads = false;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (failReads && url.includes("/api/platform/users?"))
      return new Response('{"code":"DirectoryUnavailable","correlationId":"empty-state-fixture"}', { status: 503 });
    return new Response(JSON.stringify({ authorized: true, rows:
      url.includes("/api/platform/users?") ? [] : url.includes("/departments") ? departments : [] }));
  }));

  const wrapper = mount(UsersPage);
  await flushPromises();
  const emptyState = wrapper.get('[aria-label="用户目录"]');
  expect(emptyState.text()).toContain("尚无可读用户");
  expect(emptyState.text()).toContain("新建用户档案");
  expect(wrapper.find('[data-test="new-user"]').exists()).toBe(true);

  await wrapper.get('[name="filterAccount"]').setValue("MISSING");
  expect(emptyState.text()).toContain("筛选条件已更改");
  await wrapper.get('[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();
  expect(emptyState.text()).toContain("无匹配用户");
  expect(emptyState.text()).toContain("清除筛选");

  await wrapper.get('[name="filterAccount"]').setValue("");
  expect(emptyState.text()).toContain("筛选条件已更改");
  await wrapper.get('[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();
  expect(emptyState.text()).toContain("尚无可读用户");

  failReads = true;
  await wrapper.get('[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();
  expect(emptyState.find('[role="alert"]').text()).toContain("DirectoryUnavailable");
  expect(emptyState.text()).not.toContain("尚无可读用户");
  expect(emptyState.text()).not.toContain("无匹配用户");
  wrapper.unmount();
});

it("clears prior query rows as soon as the filter scope changes", async () => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify({
    authorized: true,
    rows: url.includes("/api/platform/users?") ? [user] : url.includes("/departments") ? departments : [],
  }))));
  const wrapper = mount(UsersPage);
  await flushPromises();
  expect(wrapper.text()).toContain("ALICE");

  await wrapper.get('[name="filterAccount"]').setValue("BOB");

  expect(wrapper.text()).not.toContain("ALICE");
  wrapper.unmount();
});

afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
it("makes later authorized department and role candidates selectable without dropping the first page", async () => {
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url);
    return new Response(JSON.stringify({ authorized: true, rows:
    url.includes("/departments") ? url.includes("offset=25") ? [{ id: "d-later", name: "后页部门", enabled: true }]
      : Array.from({ length: 25 }, (_, index) => ({ id: `d-${index}`, name: `部门${index}`, enabled: true }))
    : url.includes("/roles") ? url.includes("offset=25") ? [{ id: "r-later", code: "LATER", enabled: true }]
      : Array.from({ length: 25 }, (_, index) => ({ id: `r-${index}`, code: `ROLE${index}`, enabled: true })) : [user],
    }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  expect(reads).toContain("/api/platform/departments?offset=0&limit=25");
  expect(reads).toContain("/api/platform/roles?offset=0&limit=25");
  await wrapper.get('[data-test="more-departments"]').trigger("click"); await flushPromises();
  await wrapper.get('[data-test="more-roles"]').trigger("click"); await flushPromises();
  expect(reads).toContain("/api/platform/departments?offset=25&limit=25");
  expect(reads).toContain("/api/platform/roles?offset=25&limit=25");
  await wrapper.get('[data-test="new-user"]').trigger("click");
  expect(wrapper.find('[name="departmentId"] option[value="d-later"]').exists()).toBe(true);
  expect(wrapper.find('[name="departmentId"] option[value="d-0"]').exists()).toBe(true);
  await wrapper.get('[data-test="roles-user"]').trigger("click");
  expect(wrapper.find('input[value="r-later"]').exists()).toBe(true);
  expect(wrapper.find('[data-test="more-departments"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="more-roles"]').exists()).toBe(false);
  wrapper.unmount();
});
function respond(writes: { url: string; body: unknown }[], status = 200) {
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method && options.method !== "GET") {
      writes.push({ url, body: JSON.parse(String(options.body)) });
      return new Response(JSON.stringify(status === 200 ? { decision: 0, id: "u1", version: "v2" } : { code: "StaleVersion" }), { status });
    }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? departments
      : url.includes("/roles") ? [{ id: "r1", code: "OPS", enabled: true, version: "r-v1", rules: [] }] : [user] }));
  }));
}
it("creates a profile without credential and filters department candidates", async () => {
  const writes: { url: string; body: unknown }[] = [];
  respond(writes);
  const wrapper = mount(UsersPage);
  await flushPromises();
  expect(wrapper.findAll("select option").map(option => option.text())).not.toContain("停用部门");
  await wrapper.get('[data-test="new-user"]').trigger("click");
  await wrapper.get('[name="account"]').setValue("bob");
  await wrapper.get('[name="userName"]').setValue("Bob");
  await wrapper.get('[name="departmentId"]').setValue("d1");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ url: "/api/platform/users", body: { account: "bob", name: "Bob", departmentId: "d1" } }]);
  wrapper.unmount();
});
it("fences profile writes with the displayed version and requires reread after conflict", async () => {
  const writes: { url: string; body: unknown }[] = [];
  respond(writes, 412);
  const wrapper = mount(UsersPage);
  await flushPromises();
  await wrapper.get('[data-test="edit-user"]').trigger("click");
  await wrapper.get('[name="userName"]').setValue("Changed");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes[0]).toEqual({ url: "/api/platform/users/u1", body: { expectedVersion: "user-v1", name: "Changed", departmentId: "d1", enabled: true } });
  expect(wrapper.find('[data-test="user-form"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("重新读取");
  expect(writes).toHaveLength(1);
  wrapper.unmount();
});
it("requires explicit confirmation and submits only the authorized credential version", async () => {
  const writes: { url: string; body: unknown }[] = [];
  const target = { ...user, credentialVersion: "credential-v1" };
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method === "POST") {
      writes.push({ url, body: JSON.parse(String(options.body)) });
      return new Response(null, { status: 204 });
    }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/departments") ? departments
      : url.includes("/roles") ? [] : [target] }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.get('[data-test="disable-credential"]').trigger("click");
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("登录凭据");
  expect(writes).toEqual([]);
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{
    url: "/api/platform/users/u1/disable",
    body: { expectedVersion: "credential-v1" },
  }]);
  expect(wrapper.text()).toContain("登录凭据已停用");
  wrapper.unmount();
});
it("does not expose the credential-disable action without the current Enabled write grant", async () => {
  sessionState.subject!.grants = sessionState.subject!.grants.map(grant => grant.permission === "platform.users.write"
    ? { ...grant, fields: grant.fields.filter(field => field !== "Enabled") } : grant);
  vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify({ authorized: true, rows:
    url.includes("/departments") ? departments : url.includes("/roles") ? [] : [{ ...user, credentialVersion: "credential-v1" }],
  }))));
  const wrapper = mount(UsersPage); await flushPromises();
  expect(wrapper.find('[data-test="disable-credential"]').exists()).toBe(false);
  wrapper.unmount();
});
it("never restores a late user response after identity changes", async () => {
  let resolve: ((response: Response) => void) | undefined;
  vi.stubGlobal("fetch", vi.fn((url: string) => url.includes("/users")
    ? new Promise<Response>(r => { resolve = r; })
    : Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [] })))));
  const wrapper = mount(UsersPage);
  await flushPromises();
  forgetIdentity();
  await flushPromises();
  resolve?.(new Response(JSON.stringify({ authorized: true, rows: [user] })));
  await flushPromises();
  expect(wrapper.text()).not.toContain("ALICE");
  expect(wrapper.find('[data-test="edit-user"]').exists()).toBe(false);
  wrapper.unmount();
});
it("preserves existing associations until explicitly removed and never silently strips an unavailable role", async () => {
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method === "PUT") { writes.push(JSON.parse(String(options.body))); return new Response('{"decision":0}'); }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("departments") ? departments
      : url.includes("roles") ? [{ id: "r1", code: "OLD", enabled: false }] : [user] }));
  }));
  const wrapper = mount(UsersPage);
  await flushPromises();
  await wrapper.get('[data-test="roles-user"]').trigger("click");
  expect(wrapper.text()).toContain("当前关联不在已加载的新增候选中");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([]);
  await wrapper.get('[data-test="remove-unavailable-role"]').trigger("click");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ expectedVersion: "user-v1", roleIds: [] }]);
  wrapper.unmount();
});
it("resolves saved roles independently and hides unresolved role IDs", async () => {
  const selectedRole = "12345678-1234-1234-1234-123456789001";
  const disabledRole = "12345678-1234-1234-1234-123456789002";
  const hiddenRole = "12345678-1234-1234-1234-123456789003";
  const reads: string[] = [];
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    reads.push(url);
    if (options.method && options.method !== "GET") {
      writes.push(JSON.parse(String(options.body)));
      return new Response('{"decision":0}');
    }
    if (url.includes("/roles/references?")) return new Response(JSON.stringify({ authorized: true, rows: [
      { id: selectedRole, code: "PICKER", enabled: true },
      { id: disabledRole, code: "RETIRED", enabled: false },
    ] }));
    if (url.includes("/api/platform/roles?")) return new Response(JSON.stringify({ authorized: true, rows: [
      { id: "r1", code: "FIRST-PAGE", enabled: true },
    ] }));
    if (url.includes("/api/platform/users?")) return new Response(JSON.stringify({ authorized: true, rows: [
      { ...user, roleIds: [selectedRole, disabledRole, hiddenRole] },
    ] }));
    return new Response(JSON.stringify({ authorized: true, rows: departments }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.get('[data-test="roles-user"]').trigger("click");
  await flushPromises();

  expect(reads).toContain(`/api/platform/roles/references?ids=${selectedRole}&ids=${disabledRole}&ids=${hiddenRole}`);
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("PICKER");
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("RETIRED（已停用）");
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("关联不可用或当前无权查看");
  expect(wrapper.get('[data-test="user-form"]').text()).not.toContain(hiddenRole);
  expect(wrapper.find(`input[value="${selectedRole}"]`).exists()).toBe(false);
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([]);
  await wrapper.get(`[data-test="remove-unavailable-role"][data-role-id="${disabledRole}"]`).trigger("click");
  await wrapper.get(`[data-test="remove-unavailable-role"][data-role-id="${hiddenRole}"]`).trigger("click");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ expectedVersion: "user-v1", roleIds: [selectedRole] }]);
  wrapper.unmount();
});
it("does not apply a late role reference response after closing the selected user's editor", async () => {
  const oldRole = "12345678-1234-1234-1234-123456789011";
  const newRole = "12345678-1234-1234-1234-123456789012";
  let resolveOld!: (response: Response) => void;
  const oldResponse = new Promise<Response>(resolve => { resolveOld = resolve; });
  vi.stubGlobal("fetch", vi.fn((url: string) => {
    if (url.includes("/roles/references?") && url.includes(oldRole)) return oldResponse;
    if (url.includes("/roles/references?")) return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [
      { id: newRole, code: "NEW-ROLE", enabled: true },
    ] })));
    if (url.includes("/api/platform/roles?")) return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [] })));
    if (url.includes("/api/platform/users?")) return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [
      { ...user, id: "u-old", account: "OLD", roleIds: [oldRole] },
      { ...user, id: "u-new", account: "NEW", roleIds: [newRole] },
    ] })));
    return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: departments })));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.findAll('[data-test="roles-user"]')[0]!.trigger("click");
  await wrapper.findAll('[data-test="user-form"] button').find(button => button.text() === "取消")!.trigger("click");
  await wrapper.findAll('[data-test="roles-user"]')[1]!.trigger("click");
  await flushPromises();
  resolveOld(new Response(JSON.stringify({ authorized: true, rows: [
    { id: oldRole, code: "OLD-ROLE", enabled: true },
  ] })));
  await flushPromises();

  expect(wrapper.get('[data-test="user-form"]').text()).toContain("NEW-ROLE");
  expect(wrapper.get('[data-test="user-form"]').text()).not.toContain("OLD-ROLE");
  wrapper.unmount();
});
it("reports a role reference authorization failure without revealing the saved role ID", async () => {
  const restrictedRole = "12345678-1234-1234-1234-123456789021";
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/roles/references?"))
      return new Response('{"code":"Forbidden","detail":"restricted"}', { status: 403 });
    if (url.includes("/api/platform/roles?")) return new Response(JSON.stringify({ authorized: true, rows: [] }));
    if (url.includes("/api/platform/users?")) return new Response(JSON.stringify({ authorized: true, rows: [
      { ...user, roleIds: [restrictedRole] },
    ] }));
    return new Response(JSON.stringify({ authorized: true, rows: departments }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.get('[data-test="roles-user"]').trigger("click");
  await flushPromises();

  expect(wrapper.get('[data-test="role-reference-error"]').text()).toContain("当前无权查看");
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("关联不可用或当前无权查看");
  expect(wrapper.get('[data-test="user-form"]').text()).not.toContain(restrictedRole);
  wrapper.unmount();
});
it("resolves more than 100 saved role references in bounded batches", async () => {
  const roleIds = Array.from({ length: 101 }, (_, index) =>
    `00000000-0000-0000-0000-${String(index + 1).padStart(12, "0")}`);
  const batches: string[][] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/roles/references?")) {
      const requested = new URL(url, "https://test.local").searchParams.getAll("ids");
      batches.push(requested);
      return new Response(JSON.stringify({ authorized: true, rows: requested.map((id, index) => ({
        id, code: `ROLE-${batches.length}-${index}`, enabled: true,
      })) }));
    }
    if (url.includes("/api/platform/roles?")) return new Response(JSON.stringify({ authorized: true, rows: [] }));
    if (url.includes("/api/platform/users?")) return new Response(JSON.stringify({ authorized: true, rows: [
      { ...user, roleIds },
    ] }));
    return new Response(JSON.stringify({ authorized: true, rows: departments }));
  }));
  const wrapper = mount(UsersPage); await flushPromises();
  await wrapper.get('[data-test="roles-user"]').trigger("click");
  await flushPromises();

  expect(batches.map(batch => batch.length)).toEqual([100, 1]);
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("ROLE-1-0");
  expect(wrapper.get('[data-test="user-form"]').text()).toContain("ROLE-2-0");
  wrapper.unmount();
});
it("clears a revoked field and its column control on a new projection", async () => {
  let hidden = false;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify({ authorized: true,
    rows: url.includes("departments") || url.includes("roles") ? [] : [{ ...user, name: hidden ? null : "Private name" }],
  }))));
  const wrapper = mount(UsersPage);
  await flushPromises();
  expect(wrapper.text()).toContain("Private name");
  hidden = true;
  await wrapper.get('form[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();
  expect(wrapper.text()).not.toContain("Private name");
  expect(wrapper.find('[data-test="column-name"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="edit-user"]').exists()).toBe(false);
  wrapper.unmount();
});
it("provisions credentials separately with the exact user version and clears the password after rejection", async () => {
  const writes: { url: string; body: unknown }[] = [];
  respond(writes, 400);
  const wrapper = mount(UsersPage);
  await flushPromises();
  await wrapper.get('[data-test="credential-user"]').trigger("click");
  await wrapper.get('[name="password"]').setValue("new password example");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ url: "/api/platform/users/u1/credential",
    body: { expectedVersion: "user-v1", password: "new password example" } }]);
  expect((wrapper.get('[name="password"]').element as HTMLInputElement).value).toBe("");
  expect(wrapper.text()).not.toContain("new password example");
  expect(wrapper.get('[role="alert"]').text()).toContain("StaleVersion");
  wrapper.unmount();
});
it("combines matching field grants without widening department scope for user creation", async () => {
  const subject = sessionState.subject!;
  subject.grants = [
    ...subject.grants.filter(grant => grant.permission !== "platform.users.write"),
    { permission: "platform.users.write", scope: 1, departments: ["d1"], fields: ["Account", "Name"] },
    { permission: "platform.users.write", scope: 1, departments: ["d1"], fields: ["DepartmentId", "Enabled"] },
  ];
  const writes: { url: string; body: unknown }[] = [];
  respond(writes);
  const wrapper = mount(UsersPage);
  await flushPromises();
  await wrapper.get('[data-test="new-user"]').trigger("click");
  await wrapper.get('[name="account"]').setValue("scoped");
  await wrapper.get('[name="userName"]').setValue("Scoped user");
  await wrapper.get('[name="departmentId"]').setValue("d1");
  await wrapper.get('[data-test="user-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toHaveLength(1);
  expect(writes[0]?.body).toEqual({ account: "scoped", name: "Scoped user", departmentId: "d1" });
  wrapper.unmount();
});
it("does not let an older search overwrite a newer query under the same identity", async () => {
  let first: ((response: Response) => void) | undefined;
  let userRequests = 0;
  vi.stubGlobal("fetch", vi.fn((url: string) => {
    if (url.includes("/users")) {
      userRequests++;
      if (userRequests === 1) return new Promise<Response>(resolve => { first = resolve; });
      expect(url).toContain("name=Recent");
      return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [{ ...user, name: "Recent" }] })));
    }
    return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [] })));
  }));
  const wrapper = mount(UsersPage);
  await flushPromises();
  await wrapper.get('[name="filterName"]').setValue("Recent");
  await wrapper.get('form[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();
  first?.(new Response(JSON.stringify({ authorized: true, rows: [{ ...user, name: "Obsolete" }] })));
  await flushPromises();
  expect(wrapper.text()).toContain("Recent");
  expect(wrapper.text()).not.toContain("Obsolete");
  wrapper.unmount();
});
it("never treats a forbidden directory as empty or retains old rows", async () => {
  let forbidden = false;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (forbidden && url.includes("/users")) return new Response('{"code":"DirectoryDenied","correlationId":"test-correlation"}', { status: 403 });
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/users") ? [user] : [] }));
  }));
  const wrapper = mount(UsersPage);
  await flushPromises();
  expect(wrapper.text()).toContain("ALICE");
  forbidden = true;
  await wrapper.get('form[aria-label="用户筛选"]').trigger("submit");
  await flushPromises();
  expect(wrapper.text()).not.toContain("ALICE");
  expect(wrapper.get('[aria-label="用户目录"] [role="alert"]').text()).toContain("DirectoryDenied");
  expect(wrapper.text()).not.toContain("尚无可读用户");
  expect(wrapper.find('[data-test="column-account"]').exists()).toBe(false);
  wrapper.unmount();
});
it("clears pending editor state on offline and does not send a queued write on reconnect", async () => {
  const writes: { url: string; body: unknown }[] = [];
  respond(writes);
  const wrapper = mount(UsersPage);
  await flushPromises();
  await wrapper.get('[data-test="edit-user"]').trigger("click");
  await wrapper.get('[name="userName"]').setValue("Unsent");
  vi.stubGlobal("navigator", { onLine: false });
  window.dispatchEvent(new Event("offline"));
  await flushPromises();
  expect(wrapper.find('[data-test="user-form"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("当前离线");
  vi.stubGlobal("navigator", { onLine: true });
  window.dispatchEvent(new Event("online"));
  await flushPromises();
  expect(writes).toHaveLength(0);
  expect(wrapper.text()).not.toContain("Unsent");
  wrapper.unmount();
});
