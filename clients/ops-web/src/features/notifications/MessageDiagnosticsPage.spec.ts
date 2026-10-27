import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import MessageDiagnosticsPage from "./MessageDiagnosticsPage.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { moduleCatalog } from "../module-catalog";
import { creationEventFields } from "./wms-creation-events";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const platform = { eventId: id, owner: "Platform", contractName: "platform.audit", schemaVersion: 1, status: 3, attempts: 2,
  createdAt: "2026-10-27T00:00:00Z", nextAttemptAt: null, publishedAt: null, lastErrorCategory: "InvalidContract" };
const wms = { eventId: id, owner: "Wms", contractName: "wms.master-data.created", schemaVersion: 1, status: 0,
  aggregateId: id, aggregateVersion: id, correlationId: id, occurredAt: "2026-10-27T00:00:00Z" };
let wrapper: VueWrapper | undefined;
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  moduleCatalog.state.status = "ready"; moduleCatalog.state.enabled = new Set(["Platform", "Wms"]);
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", requiresPasswordChange: false,
    credentialVersion: null, grants: [
      { permission: "platform.messages.read", scope: 2, departments: [], fields: ["EventId", "Owner", "Status", "CreatedAt", "ContractName", "SchemaVersion", "Attempts"] },
      { permission: "wms.messages.read", scope: 0, departments: [], fields: [...creationEventFields] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async url => String(url).startsWith("/api/wms/")
    ? new Response(JSON.stringify({ rows: [wms], offset: 0, limit: 25 }))
    : new Response(JSON.stringify({ authorized: true, rows: [platform] }))));
});
afterEach(() => {
  wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity();
  moduleCatalog.state.status = "unloaded"; moduleCatalog.state.enabled = null;
});
it("defaults to Platform when allowed and switches to independently authorized WMS without automatic reads", async () => {
  wrapper = mount(MessageDiagnosticsPage);
  expect(wrapper.get("label").text()).toBe("消息 owner");
  expect(wrapper.get("select").attributes("id")).toBe(wrapper.get("label").attributes("for"));
  expect(wrapper.get("h1").text()).toContain("Platform");
  await wrapper.get("form").trigger("submit"); await flushPromises();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(true);
  await wrapper.get("select").setValue("Wms");
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
  await wrapper.get('[data-test="read-wms-events"]').trigger("click"); await flushPromises();
  expect(wrapper.get('[data-test="wms-events"]').text()).toContain("wms.master-data.created");
  await wrapper.get("select").setValue("Platform");
  expect(wrapper.find('[data-test="wms-events"]').exists()).toBe(false);
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("selects only WMS when Platform is disabled, without borrowing its permission", () => {
  moduleCatalog.state.enabled = new Set(["Wms"]);
  wrapper = mount(MessageDiagnosticsPage);
  expect(wrapper.get("select").element.value).toBe("Wms");
  expect(wrapper.get("select").findAll("option")).toHaveLength(1);
  expect(wrapper.get("h1").text()).toContain("WMS");
});
it.each(["module", "permission", "identity"])("clears the old owner immediately on %s changes", async change => {
  wrapper = mount(MessageDiagnosticsPage); await wrapper.get("form").trigger("submit"); await flushPromises();
  if (change === "module") moduleCatalog.state.enabled = new Set(["Wms"]);
  if (change === "permission") sessionState.subject!.grants = sessionState.subject!.grants.filter(grant => grant.permission === "wms.messages.read");
  if (change === "identity") sessionState.subject!.sessionId = "s2";
  await flushPromises(); expect(wrapper.find('[data-test="messages"]').exists()).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("cannot display an owner using another enabled module", async () => {
  moduleCatalog.state.enabled = new Set(["Platform"]);
  sessionState.subject!.grants = sessionState.subject!.grants.filter(grant => grant.permission === "wms.messages.read");
  wrapper = mount(MessageDiagnosticsPage); await flushPromises();
  expect(wrapper.find("h1").exists()).toBe(false);
  expect(wrapper.get('[role="alert"]').text()).toContain("不允许");
  expect(fetch).not.toHaveBeenCalled();
});
it("cancels the departed owner's request and ignores its late response", async () => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.mocked(fetch).mockImplementationOnce((_, options) => {
    signal = options?.signal ?? undefined; return new Promise(resolve => { finish = resolve; });
  });
  wrapper = mount(MessageDiagnosticsPage); await wrapper.get("form").trigger("submit");
  await wrapper.get("select").setValue("Wms"); await flushPromises();
  expect(signal?.aborted).toBe(true);
  finish(new Response(JSON.stringify({ authorized: true, rows: [platform] }))); await flushPromises();
  expect(wrapper.find('[data-test="messages"]').exists()).toBe(false); expect(fetch).toHaveBeenCalledTimes(1);
});
it("locks owner selection through the real Platform replay preview until explicitly closed", async () => {
  sessionState.subject!.grants.push({ permission: "platform.messages.replay", scope: 2, departments: [], fields: ["EventId", "Status", "Attempts", "Reason"] });
  wrapper = mount(MessageDiagnosticsPage); await wrapper.get("form").trigger("submit"); await flushPromises();
  await wrapper.get('[data-test="prepare-replay"]').trigger("click"); await flushPromises();
  expect(wrapper.get("select").attributes("disabled")).toBeDefined();
  expect(wrapper.text()).toContain("不能切换 owner");
  moduleCatalog.state.enabled = new Set(["Platform"]); await flushPromises();
  moduleCatalog.state.enabled = new Set(["Platform", "Wms"]); await flushPromises();
  expect(wrapper.get("select").attributes("disabled")).toBeDefined();
  await wrapper.get('.replay-panel button[type="button"]').trigger("click"); await flushPromises();
  expect(wrapper.get("select").attributes("disabled")).toBeUndefined();
});
