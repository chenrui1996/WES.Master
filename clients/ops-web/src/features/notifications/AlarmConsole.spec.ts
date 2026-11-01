import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import AlarmsPage from "./AlarmsPage.vue";
import DeliveriesPage from "./DeliveriesPage.vue";
import NotificationActionPanel from "./NotificationActionPanel.vue";
import { alarmReadFields, deliveryReadFields, alarmActionFields, deliveryActionFields, manualAlarmFields, type AlarmRow } from "./alarms";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";

const id = "77966ccd-f1dd-412d-8e45-adb475608842";
const row: AlarmRow = { id, resourceId: id, sourceOwner: "Manual", sourceKind: "Manual", code: "Blocked",
  severity: "Warning", firstOccurredAt: "2026-11-01T12:00:00Z", lastOccurredAt: "2026-11-01T12:00:00Z",
  occurrenceCount: 1, state: "Active", version: 1, acknowledgedAt: null, acknowledgedBy: null, silenceUntil: null };
const delivery = { id, alarmId: id, channel: "InApp", state: "Sent", occurrenceCount: 1, dueAt: "2026-11-01T12:00:30Z",
  attemptCount: 1, nextAttemptAt: null, errorCode: null, version: 2 };
let wrapper: VueWrapper | undefined;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: id, userId: id, sessionId: id, requiresPasswordChange: false, credentialVersion: null,
    grants: [
      { permission: "notifications.alarms.read", scope: 0, departments: [], fields: [...alarmReadFields] },
      { permission: "notifications.alarms.acknowledge", scope: 0, departments: [], fields: [...alarmActionFields] },
      { permission: "notifications.alarms.manual.clear", scope: 0, departments: [], fields: [...alarmActionFields] },
      { permission: "notifications.alarms.silence", scope: 0, departments: [], fields: [...alarmActionFields, "Seconds"] },
      { permission: "notifications.alarms.manual.create", scope: 0, departments: [], fields: [...manualAlarmFields] },
      { permission: "notifications.deliveries.read", scope: 0, departments: [], fields: [...deliveryReadFields] },
      { permission: "notifications.deliveries.retry", scope: 0, departments: [], fields: [...deliveryActionFields] },
    ] };
  vi.stubGlobal("fetch", vi.fn(async () => response({ rows: [row], offset: 0, limit: 25 })));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); forgetIdentity(); });
async function read() { await wrapper!.get('[data-test="read"]').trigger("click"); await flushPromises(); }
it("requires explicit read and shows alarm facts without equipment-clear promises", async () => {
  wrapper = mount(AlarmsPage);
  expect(fetch).not.toHaveBeenCalled(); await read();
  expect(wrapper.get('[data-test="alarm-rows"]').text()).toContain("Blocked");
  expect(wrapper.get('[data-test="alarm-rows"]').text()).toContain("人工源");
  expect(wrapper.get('[data-test="alarm-rows"]').text()).toContain("确认人 / 确认 UTC");
  expect(wrapper.text()).toContain("不改变设备联锁");
});
it("displays authorized zero work separately from no prior query", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ rows: [], offset: 0, limit: 25 }));
  wrapper = mount(AlarmsPage); await read();
  expect(wrapper.text()).toContain("当前授权范围没有告警");
});
it.each(alarmReadFields)("does not read incomplete projection missing %s", async field => {
  sessionState.subject!.grants[0]!.fields = alarmReadFields.filter(value => value !== field);
  wrapper = mount(AlarmsPage); await read(); expect(fetch).not.toHaveBeenCalled();
});
it("clears private alarm data on permission revocation and ignores late fetch", async () => {
  wrapper = mount(AlarmsPage); await read();
  let finish!: (value: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  await wrapper.get('[data-test="read"]').trigger("click");
  sessionState.subject!.grants = []; await flushPromises();
  finish(response({ rows: [row], offset: 0, limit: 25 })); await flushPromises();
  expect(wrapper.find('[data-test="alarm-rows"]').exists()).toBe(false);
});
it("keeps previously read rows explicitly stale on provider failure, not empty success", async () => {
  wrapper = mount(AlarmsPage); await read();
  vi.mocked(fetch).mockResolvedValue(response({ code: "Unavailable", correlationId: id }, 503));
  await read(); expect(wrapper.text()).toContain("陈旧"); expect(wrapper.text()).toContain("Unavailable");
});
it("disables new writes and query while offline without scheduling reconnect requests", async () => {
  wrapper = mount(AlarmsPage); await read();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false); dispatchEvent(new Event("offline")); await flushPromises();
  expect(wrapper.get('[data-test="read"]').attributes("disabled")).toBeDefined();
  expect(wrapper.get('[data-test="manual"]').attributes("disabled")).toBeDefined();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true); dispatchEvent(new Event("online")); await flushPromises();
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("does not allow device manual clear or high severity silence", async () => {
  wrapper = mount(NotificationActionPanel, { props: { target: { kind: "alarm", row: { ...row, sourceKind: "Device", severity: "Critical" }, action: "clear" } } });
  expect(wrapper.text()).toContain("不能执行"); expect(wrapper.find('form').exists()).toBe(false);
});
it("requires preview and preserves exact original operation across unknown delivery", async () => {
  wrapper = mount(NotificationActionPanel, { props: { target: { kind: "alarm", row, action: "acknowledge" } } });
  await wrapper.get('textarea').setValue("Verified locally");
  await wrapper.get('form').trigger("submit"); expect(fetch).not.toHaveBeenCalled();
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("Connection lost"));
  await wrapper.get('[data-test="confirm"]').trigger("click"); await flushPromises();
  const original = vi.mocked(fetch).mock.calls[0]![1]!.body;
  expect(wrapper.text()).toContain("结果不明");
  vi.mocked(fetch).mockResolvedValueOnce(response({ decision: "Replayed", value: { ...row, state: "Acknowledged", version: 2 } }));
  await wrapper.get('[data-test="recover"]').trigger("click"); await flushPromises();
  expect(vi.mocked(fetch).mock.calls[1]![1]!.body).toBe(original);
  expect(wrapper.emitted("changed")).toHaveLength(1);
});
it("invalidates prepared mutation when subject changes and does not send stale permission", async () => {
  wrapper = mount(NotificationActionPanel, { props: { target: { kind: "alarm", row, action: "acknowledge" } } });
  await wrapper.get('textarea').setValue("Verified"); await wrapper.get('form').trigger("submit");
  sessionState.subject!.grants = []; await flushPromises();
  expect(wrapper.find('[data-test="confirm"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("已改变"); expect(fetch).not.toHaveBeenCalled();
});
it.each(alarmActionFields)("rejects acknowledgement missing current action field %s", field => {
  sessionState.subject!.grants[1]!.fields = alarmActionFields.filter(value => value !== field);
  wrapper = mount(NotificationActionPanel, { props: { target: { kind: "alarm", row, action: "acknowledge" } } });
  expect(wrapper.find("form").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});
it.each(deliveryReadFields)("does not read delivery missing current field %s", async field => {
  sessionState.subject!.grants[5]!.fields = deliveryReadFields.filter(value => value !== field);
  wrapper = mount(DeliveriesPage); await read();
  expect(fetch).not.toHaveBeenCalled();
});
it("requires alarm read intersection before reading deliveries", async () => {
  sessionState.subject!.grants = sessionState.subject!.grants.filter(grant => grant.permission !== "notifications.alarms.read");
  wrapper = mount(DeliveriesPage); await read();
  expect(fetch).not.toHaveBeenCalled();
});
it("allows explicitly authorized Pending InApp retry without treating Sent as retryable", async () => {
  wrapper = mount(NotificationActionPanel, { props: { target: { kind: "delivery", row: { ...delivery, channel: "InApp", state: "Pending" } } } });
  expect(wrapper.find("form").exists()).toBe(true);
  await wrapper.setProps({ target: { kind: "delivery", row: { ...delivery, channel: "InApp", state: "Sent" } } });
  expect(wrapper.find("form").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
});
it("opens detail and retains original action panel when parent clears stale detail after mutation", async () => {
  wrapper = mount(AlarmsPage); await read();
  vi.mocked(fetch).mockResolvedValueOnce(response({ ...row, occurrences: [] }));
  await wrapper.get('[data-test="alarm-detail"]').trigger("click"); await flushPromises();
  await wrapper.get('[data-test="acknowledge"]').trigger("click");
  await wrapper.get('textarea').setValue("Verified"); await wrapper.get('form').trigger("submit");
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("Connection lost"));
  await wrapper.get('[data-test="confirm"]').trigger("click"); await flushPromises();
  expect(wrapper.find('[data-test="recover"]').exists()).toBe(true);
});
it("shows independent delivery history and Sent does not imply already read", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(response({ rows: [delivery], offset: 0, limit: 25 }));
  wrapper = mount(DeliveriesPage); await read();
  expect(wrapper.text()).toContain("不表示已读"); expect(wrapper.text()).toContain("站内");
  vi.mocked(fetch).mockResolvedValueOnce(response({ ...delivery, attempts: [{ id, attemptedAt: delivery.dueAt, state: "Sent", errorCode: null }] }));
  await wrapper.get('[data-test="delivery-detail"]').trigger("click"); await flushPromises();
  expect(wrapper.get('[data-test="attempts"]').text()).toContain(delivery.dueAt);
});
