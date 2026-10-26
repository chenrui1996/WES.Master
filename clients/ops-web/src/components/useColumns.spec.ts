import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, expect, it, vi } from "vitest";
import { defineComponent, ref } from "vue";
import { forgetIdentity, sessionState } from "../features/identity/identity-api";
import ColumnSettings from "./ColumnSettings.vue";
import { calculateFixedColumnLayout, columnPreferenceKey, useColumns } from "./useColumns";

afterEach(() => { vi.restoreAllMocks(); forgetIdentity(); });

it("includes environment, tenant, identity, page, and table in the preference scope", () => {
  const key = columnPreferenceKey("test", "tenant-a", "user-a", "users", "directory");
  expect(key).not.toBe(columnPreferenceKey("production", "tenant-a", "user-a", "users", "directory"));
  expect(key).not.toBe(columnPreferenceKey("test", "tenant-b", "user-a", "users", "directory"));
  expect(key).not.toBe(columnPreferenceKey("test", "tenant-a", "user-b", "users", "directory"));
  expect(key).not.toBe(columnPreferenceKey("test", "tenant-a", "user-a", "roles", "directory"));
  expect(key).not.toBe(columnPreferenceKey("test", "tenant-a", "user-a", "users", "audit"));
});

it("keeps requested sticky columns within half the frame while preserving the identifier and action column", () => {
  const result = calculateFixedColumnLayout({
    columns: [
      { key: "id", label: "ID", required: true, widthClass: "identity" },
      { key: "state", label: "State", widthClass: "status" },
    ],
    selected: ["id", "state"],
    widths: {},
    fixedFirstColumn: true,
    fixedPositions: { state: "right" },
    containerWidth: 900,
    actionWidth: 160,
  });

  expect(result.positions).toEqual({ id: "left" });
  expect(result.offsets).toEqual({ id: 0 });
  expect(result.actionOffset).toBe(0);
  expect(result.warning).toBe(true);
});

it("calculates offsets for multiple left, right, and action columns within the width budget", () => {
  const result = calculateFixedColumnLayout({
    columns: [
      { key: "id", label: "ID", required: true, widthClass: "identity" },
      { key: "state", label: "State", widthClass: "status" },
      { key: "time", label: "Time", widthClass: "time" },
    ],
    selected: ["state", "id", "time"],
    widths: {},
    fixedFirstColumn: true,
    fixedPositions: { state: "left", time: "right" },
    containerWidth: 1400,
    actionWidth: 160,
  });

  expect(result.positions).toEqual({ id: "left", state: "left", time: "right" });
  expect(result.offsets).toEqual({ id: 0, state: 200, time: 160 });
  expect(result.actionOffset).toBe(0);
  expect(result.warning).toBe(false);
});

it("keeps required pins when they exceed the budget and reports the detail-view warning", () => {
  const result = calculateFixedColumnLayout({
    columns: [{ key: "id", label: "ID", required: true, widthClass: "identity" }],
    selected: ["id"],
    widths: {},
    fixedFirstColumn: true,
    fixedPositions: {},
    containerWidth: 600,
    actionWidth: 160,
  });

  expect(result.positions).toEqual({ id: "left" });
  expect(result.warning).toBe(true);
  expect(result.mandatoryOverflow).toBe(true);
});

it("exposes computed sticky offsets and the width-budget warning to table consumers", async () => {
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true, widthClass: "identity" },
        { key: "state", label: "State", widthClass: "status" },
      ], { environment: "test", pageId: "users", tableId: "directory", fixedActionWidth: 160 });
      columns.fixedPositions.value = { state: "right" };
      const frame = document.createElement("div");
      Object.defineProperty(frame, "clientWidth", { value: 900 });
      columns.setTableFrame(frame);
      return { ...columns };
    },
    template: `<output data-test="id-class">{{ fixedColumnClass('id') }}</output>
      <output data-test="state-class">{{ fixedColumnClass('state') ?? 'none' }}</output>
      <output data-test="state-offset">{{ fixedColumnStyle('state')?.['--table-pin-offset'] ?? 'none' }}</output>
      <output data-test="warning">{{ pinningWarning }}</output>`,
  });
  const wrapper = mount(host);
  await flushPromises();

  expect(wrapper.get('[data-test="id-class"]').text()).toBe("table-pinned-left");
  expect(wrapper.get('[data-test="state-class"]').text()).toBe("none");
  expect(wrapper.get('[data-test="state-offset"]').text()).toBe("none");
  expect(wrapper.get('[data-test="warning"]').text()).toContain("预算");
  wrapper.unmount();
});

it("loads scoped column preferences, enforces required fields, and persists only column IDs", async () => {
  const values = new Map<string, string>();
  const storage = {
    getItem: vi.fn((key: string) => values.get(key) ?? null),
    setItem: vi.fn((key: string, value: string) => { values.set(key, value); }),
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const scope = { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage };
  const initial = new Map<string, string>();
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  initial.set(preferenceKey, JSON.stringify({ version: 1, selected: ["name"] }));
  for (const [key, value] of initial) values.set(key, value);
  const host = defineComponent({
    setup() {
      const schema = ref([{ key: "id", label: "ID", required: true }, { key: "name", label: "Name" }]);
      const columns = useColumns(() => schema.value, scope);
      return { ...columns };
    },
    template: `<output>{{ selected.join(',') }}</output><output data-test="fixed">{{ fixedFirstColumn }}</output>
      <button @click="selected = ['id']">ID only</button>`,
  });

  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("name,id");
  expect(wrapper.get('[data-test="fixed"]').text()).toBe("true");
  await wrapper.get("button").trigger("click");
  await flushPromises();

  expect(JSON.parse(values.get(preferenceKey) ?? "{}")).toEqual({
    version: 1, selected: ["id"], knownColumns: ["id", "name"], fixedFirstColumn: true,
  });
  expect([...values.keys()]).toEqual([preferenceKey]);
  wrapper.unmount();

  const restored = mount(host);
  await flushPromises();
  expect(restored.get("output").text()).toBe("id");
  restored.unmount();
});

it("persists fixed positions only for currently authorized columns", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const showName = ref(true);
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        ...(showName.value ? [{ key: "name", label: "Name" }] : []),
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return {
        ...columns,
        pinName: () => { columns.fixedPositions.value = { name: "right", revoked: "left" }; },
        revokeName: () => { showName.value = false; },
      };
    },
    template: `<button @click="pinName">Pin</button><button @click="revokeName">Revoke</button>
      <output data-test="positions">{{ JSON.stringify(fixedPositions) }}</output>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  await wrapper.get("button:first-of-type").trigger("click");
  await flushPromises();
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").fixedPositions).toEqual({ name: "right" });

  await wrapper.get("button:last-of-type").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-test="positions"]').text()).toBe("{}");
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").fixedPositions).toBeUndefined();
  wrapper.unmount();
});

it("persists the user's explicit choice to unpin the primary identifier", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [{ key: "id", label: "ID", required: true }],
        { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return { ...columns, unpin: () => { columns.fixedFirstColumn.value = false; } };
    },
    template: `<button @click="unpin">Unpin</button><output>{{ fixedFirstColumn }}</output>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  await wrapper.get("button").trigger("click");
  await flushPromises();
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").fixedFirstColumn).toBe(false);
  wrapper.unmount();
});

it("keeps preference layouts isolated across tenant, identity, and environment changes", async () => {
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  const setSubject = (tenantId: string, userId: string) => {
    sessionState.subject = {
      tenantId, userId, sessionId: `session-${userId}`,
      requiresPasswordChange: false, credentialVersion: null, grants: [],
    };
  };
  const createHost = (environment: string) => defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        { key: "name", label: "Name" },
      ], { environment, pageId: "users", tableId: "directory", storageProvider: () => storage });
      return {
        ...columns,
        selectIdOnly: () => { columns.selected.value = ["id"]; },
        setNameWidth: () => { columns.widths.value = { name: 240 }; },
      };
    },
    template: `<output data-test="selected">{{ selected.join(',') }}</output>
      <output data-test="name-width">{{ widthStyle('name')?.width ?? 'auto' }}</output>
      <button data-test="id-only" @click="selectIdOnly">ID only</button>
      <button data-test="set-width" @click="setNameWidth">Set name width</button>`,
  });
  const tenantAUserAKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const tenantBUserAKey = "wes-ops-web:columns:v1:test:tenant-b:user-a:users:directory";
  const tenantAUserBKey = "wes-ops-web:columns:v1:test:tenant-a:user-b:users:directory";
  const productionUserAKey = "wes-ops-web:columns:v1:production:tenant-a:user-a:users:directory";
  setSubject("tenant-a", "user-a");
  const host = createHost("test");
  const first = mount(host);
  await flushPromises();
  expect(first.get('[data-test="selected"]').text()).toBe("id,name");
  await first.get('[data-test="id-only"]').trigger("click");
  await flushPromises();
  expect(JSON.parse(values.get(tenantAUserAKey) ?? "{}")).toEqual({
    version: 1, selected: ["id"], knownColumns: ["id", "name"], fixedFirstColumn: true,
  });

  setSubject("tenant-b", "user-a");
  await flushPromises();
  expect(first.get('[data-test="selected"]').text()).toBe("id,name");
  await first.get('[data-test="set-width"]').trigger("click");
  await flushPromises();
  expect(JSON.parse(values.get(tenantBUserAKey) ?? "{}").widths).toEqual({ name: 240 });
  expect(first.get('[data-test="name-width"]').text()).toBe("240px");

  setSubject("tenant-a", "user-a");
  await flushPromises();
  expect(first.get('[data-test="selected"]').text()).toBe("id");
  expect(first.get('[data-test="name-width"]').text()).toBe("200px");

  setSubject("tenant-a", "user-b");
  await flushPromises();
  expect(first.get('[data-test="selected"]').text()).toBe("id,name");
  expect(first.get('[data-test="name-width"]').text()).toBe("200px");
  await first.get('[data-test="id-only"]').trigger("click");
  await flushPromises();
  expect(values.has(tenantAUserBKey)).toBe(true);

  setSubject("tenant-a", "user-a");
  await flushPromises();
  expect(first.get('[data-test="selected"]').text()).toBe("id");
  first.unmount();

  const production = mount(createHost("production"));
  await flushPromises();
  expect(production.get('[data-test="selected"]').text()).toBe("id,name");
  await production.get('[data-test="id-only"]').trigger("click");
  await flushPromises();
  expect(values.has(productionUserAKey)).toBe(true);
  expect([...values.keys()]).toEqual([
    tenantAUserAKey, tenantBUserAKey, tenantAUserBKey, productionUserAKey,
  ]);
  production.unmount();

  const restoredTestEnvironment = mount(host);
  await flushPromises();
  expect(restoredTestEnvironment.get('[data-test="selected"]').text()).toBe("id");
  expect(restoredTestEnvironment.get('[data-test="name-width"]').text()).toBe("200px");
  restoredTestEnvironment.unmount();
});

it("clears the active column layout on logout and restores it only after the same identity returns", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  const setUserA = () => {
    sessionState.subject = {
      tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
      requiresPasswordChange: false, credentialVersion: null, grants: [],
    };
  };
  setUserA();
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        { key: "name", label: "Name" },
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return { ...columns, selectIdOnly: () => { columns.selected.value = ["id"]; } };
    },
    template: `<output>{{ selected.join(',') }}</output><button @click="selectIdOnly">ID only</button>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("id,name");

  await wrapper.get("button").trigger("click");
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("id");
  const savedPreference = values.get(preferenceKey);
  expect(savedPreference).toContain('"selected":["id"]');

  forgetIdentity();
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("");
  expect(values.get(preferenceKey)).toBe(savedPreference);
  expect(savedPreference).not.toContain("session-a");

  setUserA();
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("id");
  wrapper.unmount();
});

it("reports corrupt preferences and excludes fields that are no longer authorized", async () => {
  const values = new Map<string, string>([
    ["wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory", "{broken"],
  ]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      return useColumns(() => [{ key: "id", label: "ID", required: true }, { key: "name", label: "Name" }],
        { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
    },
    template: `<output>{{ selected.join(',') }}</output><p role="status">{{ preferenceWarning }}</p>`,
  });

  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("id,name");
  expect(wrapper.get('[role="status"]').text()).toContain("格式无效");
  expect(values.get("wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory")).toBe("{broken");
  wrapper.unmount();
});

it("migrates saved column choices by stable ID when the authorized schema changes", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>([[preferenceKey, JSON.stringify({
    version: 1,
    selected: ["name", "id", "secret"],
    knownColumns: ["id", "name", "secret"],
    widths: { name: 220, secret: 360 },
    fixedFirstColumn: true,
  })]]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      return useColumns(() => [
        { key: "id", label: "ID", required: true },
        { key: "name", label: "User name" },
        { key: "email", label: "Email" },
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
    },
    template: `<output data-test="selected">{{ selected.join(',') }}</output>
      <output data-test="name-width">{{ widthStyle('name')?.width ?? 'auto' }}</output>`,
  });

  const wrapper = mount(host);
  await flushPromises();

  expect(wrapper.get('[data-test="selected"]').text()).toBe("name,id,email");
  expect(wrapper.get('[data-test="name-width"]').text()).toBe("220px");
  expect(JSON.parse(values.get(preferenceKey) ?? "{}")).toEqual({
    version: 1,
    selected: ["name", "id", "email"],
    knownColumns: ["id", "name", "email"],
    fixedFirstColumn: true,
    widths: { name: 220 },
  });
  wrapper.unmount();
});

it("keeps the current layout usable and reports when browser storage is disabled", async () => {
  const failure = new DOMException("Storage is disabled", "SecurityError");
  const storage = {
    getItem: vi.fn(() => { throw failure; }),
    setItem: vi.fn(() => { throw failure; }),
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        { key: "name", label: "Name" },
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return {
        ...columns,
        selectIdOnly: () => { columns.selected.value = ["id"]; },
      };
    },
    template: `<output data-test="selected">{{ selected.join(',') }}</output>
      <p role="status">{{ preferenceWarning }}</p><button @click="selectIdOnly">ID only</button>`,
  });

  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id,name");
  expect(wrapper.get('[role="status"]').text()).toContain("无法读取或保存");

  await wrapper.get("button").trigger("click");
  await flushPromises();

  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");
  expect(wrapper.get('[role="status"]').text()).toContain("无法读取或保存");
  expect(storage.setItem).toHaveBeenCalled();
  wrapper.unmount();
});

it("keeps applied columns in the page and the old preference when storage quota is exceeded", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>([[preferenceKey, JSON.stringify({
    version: 1, selected: ["id", "name"], knownColumns: ["id", "name"],
  })]]);
  let rejectWrites = false;
  const storage = {
    getItem: vi.fn((key: string) => values.get(key) ?? null),
    setItem: vi.fn((key: string, value: string) => {
      if (rejectWrites) throw new DOMException("Storage quota exceeded", "QuotaExceededError");
      values.set(key, value);
    }),
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        { key: "name", label: "Name" },
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return {
        ...columns,
        selectIdOnly: () => { columns.selected.value = ["id"]; },
      };
    },
    template: `<output data-test="selected">{{ selected.join(',') }}</output>
      <p role="status">{{ preferenceWarning }}</p><button @click="selectIdOnly">ID only</button>`,
  });

  const wrapper = mount(host);
  await flushPromises();
  const savedBeforeFailure = values.get(preferenceKey);
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id,name");
  expect(wrapper.get('[role="status"]').text()).toBe("");
  rejectWrites = true;

  await wrapper.get("button").trigger("click");
  await flushPromises();

  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");
  expect(wrapper.get('[role="status"]').text()).toContain("无法读取或保存");
  expect(values.get(preferenceKey)).toBe(savedBeforeFailure);
  wrapper.unmount();
});

it("drops revoked columns from live preferences when the authorized schema changes", async () => {
  const values = new Map<string, string>([
    ["wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory",
      JSON.stringify({ version: 1, selected: ["id", "secret"] })],
  ]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const showSecret = ref(true);
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        ...(showSecret.value ? [{ key: "secret", label: "Secret" }] : []),
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return { ...columns, revoke: () => { showSecret.value = false; } };
    },
    template: `<output>{{ selected.join(',') }}</output><button @click="revoke">撤权</button>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("id,secret");

  await wrapper.get("button").trigger("click");
  await flushPromises();

  expect(wrapper.get("output").text()).toBe("id");
  expect(JSON.parse(values.get("wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory") ?? "{}"))
    .toEqual({ version: 1, selected: ["id"], knownColumns: ["id"], fixedFirstColumn: true });
  wrapper.unmount();
});

it("loads and persists bounded widths only for currently authorized columns", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>([[preferenceKey, JSON.stringify({
    version: 1, selected: ["id", "name"], widths: { name: 220, revoked: 400 },
  })]]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const showName = ref(true);
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true },
        ...(showName.value ? [{ key: "name", label: "Name" }] : []),
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return { ...columns, revoke: () => { showName.value = false; }, pin: () => { columns.fixedFirstColumn.value = true; } };
    },
    template: `<output>{{ JSON.stringify(widths) }}</output><output data-test="fixed">{{ fixedFirstColumn }}</output>
      <button @click="revoke">撤权</button><button @click="pin">固定首列</button>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe('{"name":220}');
  await wrapper.get("button:last-of-type").trigger("click");
  await wrapper.get("button").trigger("click");
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("{}");
  expect(wrapper.get('[data-test="fixed"]').text()).toBe("true");
  expect(JSON.parse(values.get(preferenceKey) ?? "{}")).toEqual({
    version: 1, selected: ["id"], knownColumns: ["id"], fixedFirstColumn: true,
  });
  wrapper.unmount();

  const restored = mount(host);
  await flushPromises();
  expect(restored.get('[data-test="fixed"]').text()).toBe("true");
  restored.unmount();
});

it("drops widths above 600px while preserving the rest of the preference", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>([[preferenceKey, JSON.stringify({
    version: 1, selected: ["name"], widths: { name: 601 },
  })]]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() { return useColumns(() => [
      { key: "id", label: "ID", required: true }, { key: "name", label: "Name" },
    ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage }); },
    template: `<output>{{ JSON.stringify(widths) }}</output><p role="status">{{ preferenceWarning }}</p>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("{}");
  expect(wrapper.get('[role="status"]').text()).toContain("字段范围");
  expect(values.get(preferenceKey)).toContain('"name":601');
  wrapper.unmount();
});

it("drops saved widths below the authorized column category minimum", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>([[preferenceKey, JSON.stringify({
    version: 1, selected: ["id", "name", "occurredAt"], widths: { occurredAt: 160, name: 700 },
  })]]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true, widthClass: "identity" },
        { key: "name", label: "名称", widthClass: "identity" },
        { key: "occurredAt", label: "时间", widthClass: "time" },
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return { ...columns };
    },
    template: `<output>{{ JSON.stringify(widths) }}</output><output data-test="selected">{{ selected.join(',') }}</output>
      <output data-test="width">{{ widthStyle('occurredAt')?.width }}</output>
      <p role="status">{{ preferenceWarning }}</p>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  expect(wrapper.get("output").text()).toBe("{}");
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id,name,occurredAt");
  expect(wrapper.get('[data-test="width"]').text()).toBe("200px");
  expect(wrapper.get('[role="status"]').text()).toContain("字段范围");
  wrapper.unmount();
});

it("announces cross-tab updates to scoped preferences without applying them to current state", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    components: { ColumnSettings },
    setup() { return useColumns(() => [
      { key: "id", label: "ID", required: true }, { key: "name", label: "Name" },
    ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage }); },
    template: `<ColumnSettings :columns="available" v-model="selected" />
      <output>{{ selected.join(',') }}</output><p role="status">{{ preferenceWarning }}</p>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  await wrapper.get('input[value="name"]').setValue(false);
  expect(wrapper.get('input[value="name"]').element).toHaveProperty("checked", false);
  window.dispatchEvent(new StorageEvent("storage", { key: preferenceKey, newValue: '{"version":1,"selected":["name"]}' }));
  await flushPromises();
  expect(wrapper.get('[role="status"]').text()).toContain("其他标签页");
  expect(wrapper.get('input[value="name"]').element).toHaveProperty("checked", false);
  expect(wrapper.get("output").text()).toBe("id,name");
  await wrapper.get('[data-test="apply-columns"]').trigger("submit");
  expect(wrapper.get("output").text()).toBe("id");
  wrapper.unmount();
});

it("keeps named views isolated from default layout and persists them only when explicitly saved", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [
        { key: "id", label: "ID", required: true, widthClass: "identity" },
        { key: "name", label: "Name", widthClass: "identity" },
      ], { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return {
        ...columns,
        activeView: columns.columnViews.activeViewId,
        namedViewDirty: columns.columnViews.viewDirty,
        savedViews: columns.columnViews.views,
        hideName: () => { columns.selected.value = ["id"]; },
        makeNamedChange: () => { columns.selected.value = ["id", "name"]; columns.widths.value = { name: 240 }; },
      };
    },
    template: `<output data-test="selected">{{ selected.join(',') }}</output>
      <output data-test="active">{{ activeView }}</output>
      <output data-test="dirty">{{ namedViewDirty }}</output>
      <output data-test="view-count">{{ savedViews.length }}</output>
      <output data-test="width">{{ widths.name ?? 'default' }}</output>
      <button @click="hideName">Hide name</button>
      <button @click="columnViews.createView('Operations')">Create view</button>
      <button @click="makeNamedChange">Edit view</button>
      <button @click="columnViews.saveView">Save view</button>
      <button @click="columnViews.restoreView">Restore view</button>
      <button @click="columnViews.activateView(null)">Default view</button>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  await wrapper.get("button:nth-of-type(1)").trigger("click");
  await flushPromises();
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").selected).toEqual(["id"]);
  await wrapper.get("button:nth-of-type(2)").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");
  expect(wrapper.get('[data-test="dirty"]').text()).toBe("false");
  await wrapper.get("button:nth-of-type(3)").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-test="dirty"]').text()).toBe("true");
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").selected).toEqual(["id"]);
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").views[0].selected).toEqual(["id"]);
  await wrapper.get("button:nth-of-type(5)").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");
  await wrapper.get("button:nth-of-type(3)").trigger("click");
  await wrapper.get("button:nth-of-type(4)").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-test="dirty"]').text()).toBe("false");
  expect(JSON.parse(values.get(preferenceKey) ?? "{}").views[0].widths).toEqual({ name: 240 });
  expect(Object.keys(JSON.parse(values.get(preferenceKey) ?? "{}").views[0]).sort())
    .toEqual(["fixedFirstColumn", "fixedPositions", "id", "knownColumns", "name", "selected", "widths"].sort());
  await wrapper.get("button:nth-of-type(6)").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-test="selected"]').text()).toBe("id");
  expect(wrapper.get('[data-test="width"]').text()).toBe("default");
  wrapper.unmount();

  sessionState.subject!.userId = "user-b";
  const otherIdentity = mount(host);
  await flushPromises();
  expect(otherIdentity.get('[data-test="view-count"]').text()).toBe("0");
  otherIdentity.unmount();

  sessionState.subject!.userId = "user-a";
  const restored = mount(host);
  await flushPromises();
  expect(restored.get('[data-test="selected"]').text()).toBe("id");
  expect(restored.get('[data-test="active"]').text()).toBe("");
  expect(restored.get('[data-test="view-count"]').text()).toBe("1");
  restored.unmount();
});

it("strips unrelated persisted data from loaded personal views before rewriting preferences", async () => {
  const preferenceKey = "wes-ops-web:columns:v1:test:tenant-a:user-a:users:directory";
  const values = new Map([[preferenceKey, JSON.stringify({
    version: 1, selected: ["id"], knownColumns: ["id"], fixedFirstColumn: true,
    views: [{
      id: "view-existing", name: "Existing", selected: ["id"], knownColumns: ["id"], widths: {},
      fixedFirstColumn: true, filters: { email: "private@example.test" }, rows: [{ id: "sensitive" }],
    }],
  })]]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  sessionState.subject = {
    tenantId: "tenant-a", userId: "user-a", sessionId: "session-a",
    requiresPasswordChange: false, credentialVersion: null, grants: [],
  };
  const host = defineComponent({
    setup() {
      const columns = useColumns(() => [{ key: "id", label: "ID", required: true }],
        { environment: "test", pageId: "users", tableId: "directory", storageProvider: () => storage });
      return { renameExisting: () => {
        columns.columnViews.activateView("view-existing");
        columns.columnViews.renameView("Renamed");
      } };
    },
    template: `<button @click="renameExisting">Rename</button>`,
  });
  const wrapper = mount(host);
  await flushPromises();
  await wrapper.get("button").trigger("click");
  await flushPromises();

  const savedView = JSON.parse(values.get(preferenceKey) ?? "{}").views[0];
  expect(savedView.name).toBe("Renamed");
  expect(Object.keys(savedView).sort())
    .toEqual(["fixedFirstColumn", "fixedPositions", "id", "knownColumns", "name", "selected", "widths"].sort());
  wrapper.unmount();
});
