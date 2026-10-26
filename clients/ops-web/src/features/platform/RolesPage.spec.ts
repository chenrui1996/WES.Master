import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import RolesPage from "./RolesPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

function deferredResponse() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; });
  return { promise, resolve };
}

beforeEach(() => {
  setAccessToken("opaque", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "me", sessionId: "s1", requiresPasswordChange: false, credentialVersion: null,
    grants: [
      { permission: "platform.roles.read", scope: 2, departments: [], fields: ["Id", "Code", "Enabled", "Version", "Rules"] },
      { permission: "platform.roles.write", scope: 2, departments: [], fields: ["Code", "Enabled", "Rules"] },
      { permission: "platform.users.read", scope: 1, departments: ["d1"], fields: ["Id", "Name"] },
      { permission: "platform.departments.read", scope: 2, departments: [], fields: ["Id", "Name", "Enabled"] },
    ] };
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
it("queries exact role code and permission before returning to the first page", async () => {
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url); return new Response('{"authorized":true,"rows":[]}');
  }));
  const wrapper = mount(RolesPage); await flushPromises();
  await wrapper.get('[name="filterRoleCode"]').setValue("OPS");
  await wrapper.get('[name="filterRolePermission"]').setValue("platform.users.read");
  await wrapper.get('[data-test="role-filters"]').trigger("submit"); await flushPromises();
  expect(reads).toContain("/api/platform/roles?offset=0&limit=25&code=OPS&permission=platform.users.read");
  wrapper.unmount();
});
it("does not expose department code lookup without the Code field grant", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response('{"authorized":true,"rows":[]}')));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  await wrapper.get('[name="scope"]').setValue("1");
  expect(wrapper.find('[name="departmentCode"]').exists()).toBe(false);
  wrapper.unmount();
});
it("previews the selected version and authorized current membership count without assuming a global effect", async () => {
  sessionState.subject!.grants[0]!.fields.push("AffectedSubjects");
  vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify({ authorized: true,
    rows: url.includes("departments") ? [] : [
      { id: "r1", code: "OPS", enabled: true, version: "impact-version", rules: [], affectedSubjects: 7 },
    ] }))));
  const wrapper = mount(RolesPage); await flushPromises();
  await wrapper.get('[data-test="edit-role"]').trigger("click");
  expect(wrapper.get('[data-test="role-impact"]').text()).toContain("7");
  expect(wrapper.get('[data-test="role-impact"]').text()).toContain("impact-version");
  sessionState.subject!.grants[0]!.fields = ["Id"];
  await flushPromises();
  expect(wrapper.find('[data-test="role-impact"]').exists()).toBe(false);
  wrapper.unmount();
});
it("resolves saved department references independently when editing a role", async () => {
  const firstId = "12345678-1234-1234-1234-123456789001";
  const hiddenId = "12345678-1234-1234-1234-123456789002";
  const disabledId = "12345678-1234-1234-1234-123456789003";
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url);
    if (url.includes("/departments/references?")) return new Response(JSON.stringify({ authorized: true, rows: [
      { id: firstId, code: "READ-01", name: "收货部门", enabled: true },
      { id: disabledId, code: "OLD-03", name: "停用部门", enabled: false },
    ] }));
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/api/platform/roles?")
      ? [{ id: "r1", code: "OPS", enabled: true, version: "v1", rules: [
        { permission: "platform.users.read", scope: 1, departments: [firstId, hiddenId, disabledId], fields: ["Name"] },
      ] }]
      : [] }));
  }));
  sessionState.subject!.grants[3]!.fields.push("Code");
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="edit-role"]').trigger("click");
  await flushPromises();

  expect(reads.some(url => url === `/api/platform/departments/references?ids=${firstId}&ids=${hiddenId}&ids=${disabledId}`)).toBe(true);
  expect(wrapper.get('[data-test="role-form"]').text()).toContain("READ-01 · 收货部门");
  expect(wrapper.get('[data-test="role-form"]').text()).toContain("OLD-03 · 停用部门（已停用）");
  expect(wrapper.get('[data-test="role-form"]').text()).toContain("关联不可用或当前无权查看");
  expect(wrapper.get('[data-test="role-form"]').text()).not.toContain(hiddenId);
  wrapper.unmount();
});
it("does not let a late department reference response overwrite a different edited role", async () => {
  const oldDepartment = "12345678-1234-1234-1234-123456789011";
  const newDepartment = "12345678-1234-1234-1234-123456789012";
  const pendingOld = deferredResponse();
  vi.stubGlobal("fetch", vi.fn((url: string) => {
    if (url.includes("/departments/references?") && url.includes(oldDepartment)) return pendingOld.promise;
    if (url.includes("/departments/references?")) return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [
      { id: newDepartment, code: "NEW", name: "新部门", enabled: true },
    ] })));
    if (url.includes("/api/platform/roles?")) return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [
      { id: "r-old", code: "OLD", enabled: true, version: "v1", rules: [
        { permission: "platform.users.read", scope: 1, departments: [oldDepartment], fields: ["Name"] },
      ] },
      { id: "r-new", code: "NEW", enabled: true, version: "v2", rules: [
        { permission: "platform.users.read", scope: 1, departments: [newDepartment], fields: ["Name"] },
      ] },
    ] })));
    return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: [] })));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.findAll('[data-test="edit-role"]')[0]!.trigger("click");
  await wrapper.findAll('[data-test="role-form"] button').find(button => button.text() === "取消")!.trigger("click");
  await wrapper.findAll('[data-test="edit-role"]')[1]!.trigger("click");
  await flushPromises();
  pendingOld.resolve(new Response(JSON.stringify({ authorized: true, rows: [
    { id: oldDepartment, code: "OLD", name: "旧部门", enabled: true },
  ] })));
  await flushPromises();

  expect(wrapper.get('[data-test="role-form"]').text()).toContain("NEW · 新部门");
  expect(wrapper.get('[data-test="role-form"]').text()).not.toContain("OLD · 旧部门");
  wrapper.unmount();
});
it("shows reference read denial without revealing unavailable department IDs", async () => {
  const restrictedId = "12345678-1234-1234-1234-123456789021";
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/departments/references?"))
      return new Response('{"code":"Forbidden","detail":"restricted"}', { status: 403 });
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/api/platform/roles?")
      ? [{ id: "r1", code: "OPS", enabled: true, version: "v1", rules: [
        { permission: "platform.users.read", scope: 1, departments: [restrictedId], fields: ["Name"] },
      ] }]
      : [] }));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="edit-role"]').trigger("click");
  await flushPromises();

  expect(wrapper.get('[data-test="department-reference-error"]').text()).toContain("当前无权查看");
  expect(wrapper.get('[data-test="role-form"]').text()).toContain("关联不可用或当前无权查看");
  expect(wrapper.get('[data-test="role-form"]').text()).not.toContain(restrictedId);
  wrapper.unmount();
});
it("resolves more than 100 saved department references in bounded owner batches", async () => {
  const ids = Array.from({ length: 101 }, (_, index) =>
    `00000000-0000-0000-0000-${String(index + 1).padStart(12, "0")}`);
  const batches: string[][] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/departments/references?")) {
      const requested = new URL(url, "https://test.local").searchParams.getAll("ids");
      batches.push(requested);
      return new Response(JSON.stringify({ authorized: true, rows: requested.map((id, index) => ({
        id, code: `D-${batches.length}-${index}`, name: `部门-${id.slice(-3)}`, enabled: true,
      })) }));
    }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("/api/platform/roles?")
      ? [{ id: "r1", code: "OPS", enabled: true, version: "v1", rules: [
        { permission: "platform.users.read", scope: 1, departments: ids, fields: ["Name"] },
      ] }]
      : [] }));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="edit-role"]').trigger("click");
  await flushPromises();

  expect(batches.map(batch => batch.length)).toEqual([100, 1]);
  expect(wrapper.get('[data-test="role-form"]').text()).toContain(`部门-${ids[0]!.slice(-3)}`);
  expect(wrapper.get('[data-test="role-form"]').text()).toContain(`部门-${ids[100]!.slice(-3)}`);
  wrapper.unmount();
});
it("loads authorized department candidates in 25-row pages without dropping earlier options", async () => {
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url);
    return new Response(JSON.stringify({ authorized: true, rows:
      !url.includes("departments") ? [] : url.includes("offset=25") ? [{ id: "d-later", name: "后页部门", enabled: true }]
        : Array.from({ length: 25 }, (_, index) => ({ id: `d-${index}`, name: `部门${index}`, enabled: true })),
    }));
  }));
  sessionState.subject!.grants[2]!.scope = 2;
  const wrapper = mount(RolesPage); await flushPromises();
  expect(reads).toContain("/api/platform/departments?offset=0&limit=25");
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  await wrapper.get('[name="scope"]').setValue("1");
  await wrapper.get('[data-test="more-departments"]').trigger("click"); await flushPromises();
  expect(reads).toContain("/api/platform/departments?offset=25&limit=25");
  expect(wrapper.find('input[name="grantDepartment"][value="d-later"]').exists()).toBe(true);
  expect(wrapper.find('input[name="grantDepartment"][value="d-0"]').exists()).toBe(true);
  expect(wrapper.find('[data-test="more-departments"]').exists()).toBe(false);
  wrapper.unmount();
});
it("debounces department code lookup and retains an explicitly selected department summary", async () => {
  vi.useFakeTimers();
  sessionState.subject!.grants[3]!.fields.push("Code");
  sessionState.subject!.grants[2]!.departments.push("d2");
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    reads.push(url);
    const code = new URL(url, "https://test.local").searchParams.get("code");
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("departments")
      ? code === "AB" ? [{ id: "d2", name: "部门AB", enabled: true }]
        : [{ id: "d1", name: "部门A", enabled: true }]
      : [] }));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  await wrapper.get('[name="scope"]').setValue("1");
  await wrapper.get('input[name="grantDepartment"][value="d1"]').setValue(true);

  await wrapper.get('[name="departmentCode"]').setValue("AB");
  await vi.advanceTimersByTimeAsync(249);
  expect(reads.some(url => url.includes("code=AB"))).toBe(false);
  await vi.advanceTimersByTimeAsync(1);
  await flushPromises();

  expect(reads).toContain("/api/platform/departments?offset=0&limit=25&code=AB");
  expect(wrapper.find('input[name="grantDepartment"][value="d1"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="selected-departments"]').text()).toContain("部门A");
  expect(wrapper.find('input[name="grantDepartment"][value="d2"]').exists()).toBe(true);
  await wrapper.get('input[name="grantField"][value="Name"]').setValue(true);
  await wrapper.get('[data-test="add-rule"]').trigger("click");
  expect(wrapper.get('[data-test="role-form"]').text()).toContain("部门A");
  wrapper.unmount();
  vi.useRealTimers();
});
it("keeps the AB department results when an earlier A response arrives last", async () => {
  vi.useFakeTimers();
  sessionState.subject!.grants[3]!.fields.push("Code");
  sessionState.subject!.grants[2]!.departments.push("d2", "d3");
  const first = deferredResponse();
  const second = deferredResponse();
  const reads: string[] = [];
  vi.stubGlobal("fetch", vi.fn((url: string) => {
    reads.push(url);
    const code = new URL(url, "https://test.local").searchParams.get("code");
    if (url.includes("departments") && code === "A") return first.promise;
    if (url.includes("departments") && code === "AB") return second.promise;
    return Promise.resolve(new Response(JSON.stringify({ authorized: true, rows: url.includes("departments")
      ? [{ id: "d1", code: "BASE", name: "默认部门", enabled: true }]
      : [] })));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  await wrapper.get('[name="scope"]').setValue("1");
  await wrapper.get('input[name="grantDepartment"][value="d1"]').setValue(true);

  await wrapper.get('[name="departmentCode"]').setValue("A");
  await vi.advanceTimersByTimeAsync(250);
  expect(reads.some(url => url.endsWith("&code=A"))).toBe(true);
  await wrapper.get('[name="departmentCode"]').setValue("AB");
  await vi.advanceTimersByTimeAsync(250);
  expect(reads.some(url => url.endsWith("&code=AB"))).toBe(true);

  second.resolve(new Response(JSON.stringify({ authorized: true, rows: [
    { id: "d2", code: "AB", name: "AB部门", enabled: true },
  ] })));
  await flushPromises();
  first.resolve(new Response(JSON.stringify({ authorized: true, rows: [
    { id: "d3", code: "A", name: "A部门", enabled: true },
  ] })));
  await flushPromises();
  expect(wrapper.find('input[name="grantDepartment"][value="d2"]').exists()).toBe(true);
  expect(wrapper.find('input[name="grantDepartment"][value="d3"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="selected-departments"]').text()).toContain("默认部门");
  wrapper.unmount();
  vi.useRealTimers();
});
it("uses validated grant choices and explicit numeric department scope", async () => {
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method === "POST") { writes.push(JSON.parse(String(options.body))); return new Response('{"decision":0,"version":"v2"}'); }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("departments") ? [
      { id: "d1", name: "收货", enabled: true }, { id: "d2", name: "已停用", enabled: false },
    ] : [] }));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="roleCode"]').setValue("READERS");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  expect(wrapper.find('[name="scope"] option[value="2"]').exists()).toBe(false);
  await wrapper.get('[name="scope"]').setValue("1");
  await wrapper.get('input[name="grantDepartment"][value="d1"]').setValue(true);
  await wrapper.get('input[name="grantField"][value="Name"]').setValue(true);
  await wrapper.get('[data-test="add-rule"]').trigger("click");
  await wrapper.get('[data-test="role-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([{ code: "READERS", rules: [{ permission: "platform.users.read", scope: 1, departments: ["d1"], fields: ["Name"] }] }]);
  expect(wrapper.text()).not.toContain("已停用");
  wrapper.unmount();
});
it("does not offer projected-null rules as an editable empty policy", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ authorized: true, rows: [
    { id: "r1", code: "HIDDEN", enabled: true, version: "v1", rules: null },
  ] }))));
  const wrapper = mount(RolesPage);
  await flushPromises();
  expect(wrapper.text()).toContain("HIDDEN");
  expect(wrapper.find('[data-test="edit-role"]').exists()).toBe(false);
  wrapper.unmount();
});
it("clears builder field and department choices when permission changes", async () => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => new Response(JSON.stringify({ authorized: true, rows:
    url.includes("departments") ? [{ id: "d1", name: "收货", enabled: true }] : [],
  }))));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  await wrapper.get('input[name="grantDepartment"][value="d1"]').setValue(true);
  await wrapper.get('input[name="grantField"][value="Name"]').setValue(true);
  await wrapper.get('[name="permission"]').setValue("platform.roles.write");
  expect(wrapper.find('input[name="grantField"][value="Name"]').exists()).toBe(false);
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  expect((wrapper.get('input[name="grantField"][value="Name"]').element as HTMLInputElement).checked).toBe(false);
  expect((wrapper.get('input[name="grantDepartment"][value="d1"]').element as HTMLInputElement).checked).toBe(false);
  wrapper.unmount();
});
it("validates a permission rule before it can be submitted", async () => {
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
    if (options.method === "POST") writes.push(options.body);
    return new Response('{"authorized":true,"rows":[]}');
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="new-role"]').trigger("click");
  await wrapper.get('[name="roleCode"]').setValue("INVALID");
  await wrapper.get('[name="permission"]').setValue("platform.users.read");
  await wrapper.get('[data-test="add-rule"]').trigger("click");
  expect(wrapper.get('[role="alert"]').text()).toContain("规则无效");
  await wrapper.get('[data-test="role-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toHaveLength(0);
  wrapper.unmount();
});
it("sends the exact role version once and discards stale rules after a conflict", async () => {
  const writes: unknown[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (options.method === "PUT") {
      writes.push([url, JSON.parse(String(options.body))]);
      return new Response('{"code":"RoleVersionConflict","correlationId":"role-conflict"}', { status: 412 });
    }
    return new Response(JSON.stringify({ authorized: true, rows: url.includes("departments") ? [] : [
      { id: "r1", code: "OPS", enabled: true, version: "role-v1", rules: [] },
    ] }));
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  await wrapper.get('[data-test="edit-role"]').trigger("click");
  await wrapper.get('[data-test="role-form"]').trigger("submit");
  await flushPromises();
  expect(writes).toEqual([["/api/platform/roles/r1", { expectedVersion: "role-v1", enabled: true, rules: [] }]]);
  expect(wrapper.find('[data-test="role-form"]').exists()).toBe(false);
  expect(wrapper.text()).not.toContain("OPS");
  expect(wrapper.get('[role="alert"]').text()).toContain("重新读取");
  wrapper.unmount();
});

it("retains same-scope role results as stale and blocks editing after refresh failure", async () => {
  let roleReads = 0;
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.includes("/api/platform/roles?")) {
      roleReads++;
      return roleReads === 1
        ? new Response(JSON.stringify({ authorized: true, rows: [
          { id: "r1", code: "OPS", enabled: true, version: "role-v1", rules: [] },
        ] }))
        : new Response('{"code":"Unavailable","correlationId":"role-refresh"}', { status: 503 });
    }
    return new Response('{"authorized":true,"rows":[]}');
  }));
  const wrapper = mount(RolesPage);
  await flushPromises();
  expect(wrapper.text()).toContain("OPS");

  await wrapper.get('[data-test="role-filters"]').trigger("submit");
  await flushPromises();

  expect(wrapper.text()).toContain("OPS");
  expect(wrapper.get('[aria-label="角色目录"] [role="status"]').text()).toContain("role-refresh");
  expect(wrapper.get('[data-test="edit-role"]').attributes("disabled")).toBeDefined();
  wrapper.unmount();
});
