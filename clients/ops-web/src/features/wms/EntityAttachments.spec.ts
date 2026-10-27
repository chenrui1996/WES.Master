import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import EntityAttachments from "./EntityAttachments.vue";
import StagingFileCatalog from "../platform/StagingFileCatalog.vue";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { readFields } from "../platform/staging-file";
import { warehouseFields, locationFields } from "./master-data";

const entityId = "77966ccd-f1dd-412d-8e45-adb475608842";
const fileId = "a029ce52-d810-405c-b349-c0ac9ebad74b";
const version = "66966ccd-f1dd-412d-8e45-adb475608842";
const row = { id: fileId, fileName: "current-note.txt", contentType: "text/plain", byteCount: 4, state: 1,
  stored: true, version, createdAt: "2026-10-27T00:00:00Z", processingProblem: false };
let wrapper: VueWrapper | undefined;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: "t1", userId: "u1", sessionId: "s1", credentialVersion: null, requiresPasswordChange: false,
    grants: [
      { permission: "platform.files.read", scope: 0, departments: [], fields: readFields },
      { permission: "wms.warehouses.read", scope: 0, departments: [], fields: [...warehouseFields] },
      { permission: "wms.locations.read", scope: 0, departments: [], fields: [...locationFields] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async (url: string) => response(url.includes("?")
    ? { decision: 0, rows: [row], offset: 0, limit: 25 } : { decision: 0, file: row })));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); forgetIdentity(); });
function open() { wrapper = mount(EntityAttachments, { props: { kind: "warehouses", entityId, active: true } }); return wrapper; }
async function list() { await wrapper!.get('[data-test="attachments-read"]').trigger("click"); await flushPromises(); }

it("does not fetch or expose reference mutations on mount with metadata read only", () => {
  const view = open();
  expect(fetch).not.toHaveBeenCalled();
  expect(view.find('[data-test="attachment-prepare"]').exists()).toBe(false);
  expect(view.find('[data-test="attachment-recovery"]').exists()).toBe(false);
});
it("rereads an explicitly selected exact attachment and does not label it unbound", async () => {
  const view = open(); await list();
  expect(view.find('[data-test="attachment-details"]').exists()).toBe(false);
  await view.get('[data-test="attachment-select"]').trigger("click"); await flushPromises();
  expect(view.get('[data-test="attachment-details"]').text()).toContain(version);
  expect(view.get('[data-test="attachment-details"]').text()).not.toContain("未绑定");
  expect(vi.mocked(fetch).mock.calls.map(call => call[0])).toEqual([
    `/api/wms/warehouses/${entityId}/files?offset=0&limit=25`, `/api/wms/warehouses/${entityId}/files/${fileId}`,
  ]);
});
it("clears attachment data on current subject change and never automatically queries the new subject", async () => {
  const view = open(); await list();
  sessionState.subject = { ...sessionState.subject!, userId: "u2" }; await flushPromises();
  expect(view.text()).not.toContain(row.fileName);
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("leaves a failed same-query list explicitly stale and unselectable", async () => {
  const view = open(); await list();
  vi.mocked(fetch).mockResolvedValueOnce(response({ code: "DependencyUnavailable", correlationId: "attachment-dependency" }, 503));
  await list();
  expect(view.text()).toContain(row.fileName);
  expect(view.text()).toContain("attachment-dependency");
  expect(view.get('[data-test="attachment-select"]').attributes("disabled")).toBeDefined();
});

function grantWrites() {
  sessionState.subject!.grants.push(
    { permission: "platform.files.write", scope: 0, departments: [], fields: ["Attach", "Detach"] },
    { permission: "wms.warehouses.write", scope: 0, departments: [], fields: ["Attach", "Detach"] });
}
async function previewDetach() {
  grantWrites(); const view = open(); await list();
  await view.get('[data-test="attachment-select"]').trigger("click"); await flushPromises();
  await view.get('[data-test="attachment-prepare"]').trigger("click");
  return view;
}
const writes = () => vi.mocked(fetch).mock.calls.filter(call => call[1]?.method === "PUT" || call[1]?.method === "DELETE");

it("freezes an explicit detach preview and cancelling it sends no write", async () => {
  const view = await previewDetach();
  expect(writes()).toHaveLength(0);
  expect(view.get('[data-test="attachment-command"]').text()).toContain(version);
  expect(view.emitted("locked")?.at(-1)).toEqual([true]);
  expect(view.get('[data-test="attachments-read"]').attributes("disabled")).toBeDefined();
  await view.get('[data-test="attachment-cancel-preview"]').trigger("click");
  expect(writes()).toHaveLength(0);
  expect(view.find('[data-test="attachment-command"]').exists()).toBe(false);
  expect(view.emitted("locked")?.at(-1)).toEqual([false]);
});
it("confirms detach only on the second action, then treats the receipt as original fact rather than a list", async () => {
  const view = await previewDetach();
  vi.mocked(fetch).mockResolvedValueOnce(response({ id: fileId, version, attached: false }));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  expect(writes()).toHaveLength(1);
  expect(writes()[0]?.[1]?.method).toBe("DELETE");
  expect(writes()[0]?.[1]?.body).toBe(JSON.stringify({ expectedVersion: version }));
  expect(view.get('[data-test="attachment-receipt"]').text()).toContain("原回执");
  expect(view.find('[data-test="attachment-details"]').exists()).toBe(false);
  expect(view.find('[data-test="attachment-select"]').exists()).toBe(false);
  expect(view.emitted("locked")?.at(-1)).toEqual([false]);
});
it("recovers a lost response using exactly the original key, version, action and target without rereading", async () => {
  const view = await previewDetach();
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("response lost"));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  expect(view.text()).toContain("原操作结果不明");
  const original = writes()[0]!;
  vi.mocked(fetch).mockResolvedValueOnce(response({ id: fileId, version, attached: false }));
  await view.get('[data-test="attachment-retry"]').trigger("click"); await flushPromises();
  expect(writes()).toHaveLength(2);
  expect(writes()[1]?.[0]).toBe(original[0]);
  expect(writes()[1]?.[1]?.method).toBe(original[1]?.method);
  expect(writes()[1]?.[1]?.body).toBe(original[1]?.body);
  expect(new Headers(writes()[1]?.[1]?.headers).get("Idempotency-Key")).toBe(new Headers(original[1]?.headers).get("Idempotency-Key"));
  expect(view.find('[data-test="attachment-receipt"]').exists()).toBe(true);
});
it("keeps an unknown operation locked even when the subsequent original-key recovery is forbidden", async () => {
  const view = await previewDetach();
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("response lost"));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  vi.mocked(fetch).mockResolvedValueOnce(response({ code: "Forbidden", correlationId: "attachment-revoked" }, 403));
  await view.get('[data-test="attachment-retry"]').trigger("click"); await flushPromises();
  expect(writes()).toHaveLength(2);
  expect(view.text()).toContain("原操作结果仍不明");
  expect(view.text()).toContain("attachment-revoked");
  expect(view.emitted("locked")?.at(-1)).toEqual([true]);
  expect(view.find('[data-test="attachment-details"]').exists()).toBe(false);
  expect(view.get('[data-test="attachment-exit"]').attributes("disabled")).toBeDefined();
  await view.get('[name="recordedOriginal"]').setValue(true);
  await view.get('[data-test="attachment-exit"]').trigger("click");
  expect(view.text()).toContain("不会撤销");
  expect(view.emitted("locked")?.at(-1)).toEqual([false]);
});
it("uses the ten-second deadline without accepting a late receipt or rebuilding the frozen request", async () => {
  const view = await previewDetach(); vi.useFakeTimers();
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>(done => { resolve = done; }));
  await view.get('[data-test="attachment-confirm"]').trigger("click");
  const signal = writes()[0]?.[1]?.signal;
  await vi.advanceTimersByTimeAsync(10000);
  expect(signal?.aborted).toBe(true);
  expect(view.text()).toContain("原操作结果不明");
  resolve(response({ id: fileId, version, attached: false })); await flushPromises();
  expect(view.find('[data-test="attachment-receipt"]').exists()).toBe(false);
  expect(view.find('[data-test="attachment-retry"]').exists()).toBe(true);
});
it("manually recovers the recorded version and key without generating a replacement or reading a new version", async () => {
  grantWrites(); const view = open(); const random = vi.spyOn(crypto, "randomUUID");
  await view.get('[data-test="attachment-recovery"] summary').trigger("click");
  const originalVersion = "55966ccd-f1dd-412d-8e45-adb475608842";
  const originalOperation = "44966ccd-f1dd-412d-8e45-adb475608842";
  await view.get('[name="originalFile"]').setValue(fileId);
  await view.get('[name="originalVersion"]').setValue(originalVersion);
  await view.get('[name="originalOperation"]').setValue(originalOperation);
  await view.get('[name="originalAttached"]').setValue("false");
  await view.get('[data-test="attachment-recovery-form"]').trigger("submit");
  expect(fetch).not.toHaveBeenCalled(); expect(random).not.toHaveBeenCalled();
  vi.mocked(fetch).mockResolvedValueOnce(response({ id: fileId, version, attached: false }));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  expect(writes()).toHaveLength(1);
  expect(writes()[0]?.[1]?.body).toBe(JSON.stringify({ expectedVersion: originalVersion }));
  expect(new Headers(writes()[0]?.[1]?.headers).get("Idempotency-Key")).toBe(originalOperation);
});
it("can recover the frozen operation online even when disconnection made the parent detail stale", async () => {
  const view = await previewDetach();
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("disconnected"));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  await view.setProps({ active: false });
  vi.mocked(fetch).mockResolvedValueOnce(response({ id: fileId, version, attached: false }));
  expect(view.get('[data-test="attachments-read"]').attributes("disabled")).toBeDefined();
  expect(view.get('[data-test="attachment-retry"]').attributes("disabled")).toBeUndefined();
  await view.get('[data-test="attachment-retry"]').trigger("click"); await flushPromises();
  expect(writes()).toHaveLength(2);
  expect(view.find('[data-test="attachment-receipt"]').exists()).toBe(true);
});

it("rereads a staging candidate and binds only after explicit preview and confirmation", async () => {
  grantWrites(); const view = open();
  view.getComponent(StagingFileCatalog).vm.$emit("select", fileId); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(`/api/platform/files/staging/${fileId}`);
  expect(writes()).toHaveLength(0);
  await view.get('[data-test="attachment-prepare"]').trigger("click");
  expect(writes()).toHaveLength(0);
  vi.mocked(fetch).mockResolvedValueOnce(response({ id: fileId, version, attached: true }));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  expect(writes()).toHaveLength(1);
  expect(writes()[0]?.[1]?.method).toBe("PUT");
  expect(writes()[0]?.[1]?.body).toBe(JSON.stringify({ expectedVersion: version }));
  expect(view.find('[data-test="attachment-receipt"]').exists()).toBe(true);
});

it.each([{ state: 0, stored: true }, { state: 1, stored: false }, { state: 2, stored: true }])(
  "never previews binding an unavailable or unstored candidate %#", async invalid => {
    grantWrites(); const view = open();
    vi.mocked(fetch).mockResolvedValueOnce(response({ decision: 0, file: { ...row, ...invalid } }));
    view.getComponent(StagingFileCatalog).vm.$emit("select", fileId); await flushPromises();
    expect(view.get('[data-test="attachment-prepare"]').attributes("disabled")).toBeDefined();
    await view.get('[data-test="attachment-prepare"]').trigger("click");
    expect(view.find('[data-test="attachment-command"]').exists()).toBe(false);
    expect(writes()).toHaveLength(0);
  });

it("retains the original operation when a successful response has a malformed receipt", async () => {
  const view = await previewDetach();
  vi.mocked(fetch).mockResolvedValueOnce(response({ id: fileId, version, attached: true }));
  await view.get('[data-test="attachment-confirm"]').trigger("click"); await flushPromises();
  expect(view.text()).toContain("原操作结果不明");
  expect(view.find('[data-test="attachment-receipt"]').exists()).toBe(false);
  expect(view.emitted("locked")?.at(-1)).toEqual([true]);
});
