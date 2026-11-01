import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { forgetIdentity, sessionState, setAccessToken } from "../identity/identity-api";
import { alarmFields, deliveryFields, queryAlarms, queryDeliveries, queryAlarm, mutateAlarm, mutateDelivery } from "./alarms";

export const id = "77966ccd-f1dd-412d-8e45-adb475608842";
export const alarm = { id, sourceOwner: "Manual", sourceKind: "Manual", resourceId: id, code: "Path.Blocked",
  severity: "Warning", firstOccurredAt: "2026-11-01T12:00:00Z", lastOccurredAt: "2026-11-01T12:00:00Z",
  occurrenceCount: 1, state: "Active", version: 1, acknowledgedBy: null, acknowledgedAt: null, silenceUntil: null };
export const delivery = { id, alarmId: id, channel: "InApp", state: "Pending", occurrenceCount: 1,
  dueAt: "2026-11-01T12:00:30Z", attemptCount: 0, nextAttemptAt: null, errorCode: null, version: 1 };
beforeEach(() => {
  setAccessToken("fixture", "2099-01-01T00:00:00Z");
  sessionState.subject = { tenantId: id, userId: id, sessionId: id, requiresPasswordChange: false,
    credentialVersion: null, grants: [
      { permission: "notifications.alarms.read", scope: 0, departments: [], fields: [...alarmFields] },
      { permission: "notifications.deliveries.read", scope: 0, departments: [], fields: [...deliveryFields] },
    ] };
  vi.stubGlobal("fetch", vi.fn());
});
afterEach(() => { vi.unstubAllGlobals(); forgetIdentity(); });
const response = (value: unknown) => new Response(JSON.stringify(value));
it("reads owner-filtered alarm page without trusting client scope or source claims", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ rows: [alarm], offset: 0, limit: 25 }));
  expect(await queryAlarms(0, 25, new AbortController().signal)).toEqual([alarm]);
  expect(fetch).toHaveBeenCalledWith("/api/notifications/alarms?offset=0&limit=25",
    expect.objectContaining({ cache: "no-store" }));
});
it.each([[], [alarm, alarm], [{ ...alarm, version: 9007199254740992 }], [{ ...alarm, severity: "Unknown" }],
  [{ ...alarm, sourceKind: "Fake" }], [{ ...alarm, state: "RecoveredEquipment" }],
  [{ ...alarm, code: "Blocked\n" }], [{ ...alarm, sourceOwner: "Manual\n" }], [{ ...alarm, id: `${id}\n` }],
  [{ ...alarm, lastOccurredAt: "2026-02-30T00:00:00Z" }], [{ ...alarm, extra: true }],
].map(rows => ({ rows })))("rejects invalid alarm projections rather than displaying success-shaped data %#", async ({ rows }) => {
  vi.mocked(fetch).mockResolvedValue(response({ rows, offset: rows.length === 0 ? 9 : 0, limit: 25 }));
  await expect(queryAlarms(0, 25, new AbortController().signal)).rejects.toThrow();
});
it("keeps a valid empty result distinct from failed reads", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(response({ rows: [], offset: 0, limit: 25 }));
  expect(await queryAlarms(0, 25, new AbortController().signal)).toEqual([]);
  vi.mocked(fetch).mockResolvedValueOnce(new Response("{}", { status: 503 }));
  await expect(queryAlarms(0, 25, new AbortController().signal)).rejects.toThrow();
});
it.each([-1, 10001, 0.5])("rejects unbounded offsets %s before HTTP", async offset => {
  await expect(queryAlarms(offset, 25, new AbortController().signal)).rejects.toThrow();
  expect(fetch).not.toHaveBeenCalled();
});
it("reads independent delivery without converting Sent to read", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ rows: [{ ...delivery, state: "Sent", attemptCount: 1 }], offset: 0, limit: 25 }));
  const rows = await queryDeliveries(0, 25, new AbortController().signal);
  expect(rows[0]!.state).toBe("Sent");
  expect(rows[0]).not.toHaveProperty("read");
});
it.each(["Read", "Recovered", "Success"])("rejects invented delivery state %s", async state => {
  vi.mocked(fetch).mockResolvedValue(response({ rows: [{ ...delivery, state }], offset: 0, limit: 25 }));
  await expect(queryDeliveries(0, 25, new AbortController().signal)).rejects.toThrow();
});
it("rejects wrong selected alarm and duplicate occurrence IDs", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ ...alarm, id: "67966ccd-f1dd-412d-8e45-adb475608842", occurrences: [] }));
  await expect(queryAlarm(id, new AbortController().signal)).rejects.toThrow();
});
it("rejects duplicate original occurrence identity in detail", async () => {
  const fact = { id, sourceEventId: id, occurredAt: alarm.firstOccurredAt, receivedAt: alarm.lastOccurredAt,
    correlationId: id, summary: "Controlled fact" };
  vi.mocked(fetch).mockResolvedValue(response({ ...alarm, occurrenceCount: 2, occurrences: [fact, fact] }));
  await expect(queryAlarm(id, new AbortController().signal)).rejects.toThrow();
});
it("freezes operationId and exact body in mutation API without implicit new key", async () => {
  const request = { operationId: id, expectedVersion: 1, reason: "Verified" };
  vi.mocked(fetch).mockResolvedValue(response({ decision: "Applied", value: { ...alarm, state: "Acknowledged", version: 2 } }));
  await mutateAlarm(id, "acknowledge", request, new AbortController().signal);
  const [url, options] = vi.mocked(fetch).mock.calls[0]!;
  expect(url).toBe(`/api/notifications/alarms/${id}/acknowledge`);
  expect(options!.body).toBe(JSON.stringify(request));
  expect(new Headers(options!.headers).has("Idempotency-Key")).toBe(false);
});
it("does not accept a mismatched delivery receipt", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ decision: "Applied",
    value: { ...delivery, id: "67966ccd-f1dd-412d-8e45-adb475608842" } }));
  await expect(mutateDelivery(id, { operationId: id, expectedVersion: 1, reason: "Verified" },
    new AbortController().signal)).rejects.toThrow();
});
