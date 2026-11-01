import { api, isRecord, sessionState } from "../identity/identity-api";

export const alarmFields = ["Id", "SourceOwner", "SourceKind", "ResourceId", "Code", "Severity",
  "FirstOccurredAt", "LastOccurredAt", "OccurrenceCount", "State", "Version", "AcknowledgedBy", "AcknowledgedAt", "SilenceUntil"] as const;
export const deliveryFields = ["Id", "AlarmId", "Channel", "State", "OccurrenceCount", "DueAt",
  "AttemptCount", "NextAttemptAt", "ErrorCode", "Version"] as const;
export const alarmReadFields = [...alarmFields, "SourceEventId", "OccurredAt", "ReceivedAt", "CorrelationId", "Summary"] as const;
export const deliveryReadFields = [...deliveryFields, "AttemptedAt"] as const;
export const alarmActionFields = [...alarmReadFields, "OperationId", "ExpectedVersion", "Reason"] as const;
export const deliveryActionFields = [...deliveryReadFields, "OperationId", "ExpectedVersion", "Reason"] as const;
export const manualAlarmFields = ["OperationId", "ResourceId", "Code", "Severity", "Summary"] as const;
export const severityNames = ["Information", "Warning", "High", "Critical"] as const;
export type AlarmSeverity = typeof severityNames[number];
export type AlarmAction = "acknowledge" | "clear" | "silence";
export interface AlarmRow {
  id: string; sourceOwner: string; sourceKind: "Device" | "Manual"; resourceId: string; code: string;
  severity: AlarmSeverity; firstOccurredAt: string; lastOccurredAt: string; occurrenceCount: number;
  state: "Active" | "Acknowledged" | "Cleared"; version: number; acknowledgedBy: string | null;
  acknowledgedAt: string | null; silenceUntil: string | null;
}
export interface OccurrenceRow {
  id: string; sourceEventId: string; occurredAt: string; receivedAt: string; correlationId: string; summary: string;
}
export interface AlarmDetail extends AlarmRow { occurrences: OccurrenceRow[] }
export interface DeliveryRow {
  id: string; alarmId: string; channel: "InApp"; state: "Pending" | "Sent" | "Failed" | "Retrying" | "Unavailable";
  occurrenceCount: number; dueAt: string; attemptCount: number; nextAttemptAt: string | null; errorCode: string | null; version: number;
}
export interface AttemptRow { id: string; attemptedAt: string; state: DeliveryRow["state"]; errorCode: string | null }
export interface DeliveryDetail extends DeliveryRow { attempts: AttemptRow[] }
export interface AlarmCommand { operationId: string; expectedVersion: number; reason: string; seconds?: number }
export interface ManualAlarmCommand { operationId: string; resourceId: string; code: string; severity: AlarmSeverity; summary: string }
export class AlarmResponseError extends Error {
  constructor() { super("告警或投递响应无效，已丢弃；请重新读取。"); }
}
export function hasNotificationFields(permission: string, required: readonly string[]): boolean {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === permission)
    .flatMap(grant => grant.fields) ?? []);
  return sessionState.subject !== null && !sessionState.subject.requiresPasswordChange && required.every(field => fields.has(field));
}
export function alarmActionPermission(action: AlarmAction): string {
  return `notifications.alarms.${action === "clear" ? "manual.clear" : action}`;
}
export function validNotificationId(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function integer(value: unknown, minimum = 0): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= minimum;
}
function utc(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)) return false;
  const time = Date.parse(value);
  return Number.isFinite(time) && new Date(time).toISOString().slice(0, 19) === value.slice(0, 19);
}
function safeCode(value: unknown): value is string {
  return typeof value === "string" && value.length <= 100 && /^[A-Za-z][A-Za-z0-9_.-]*$/.test(value);
}
function shape(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!isRecord(value) || Object.keys(value).length !== keys.length || !keys.every(key => key in value))
    throw new AlarmResponseError();
  return value;
}
function parseAlarm(raw: unknown): AlarmRow {
  const value = shape(raw, alarmFields.map(field => field[0]!.toLowerCase() + field.slice(1)));
  if (!validNotificationId(value.id) || !validNotificationId(value.resourceId)
    || typeof value.sourceOwner !== "string" || value.sourceOwner.length > 64 || !/^[A-Za-z][A-Za-z0-9_]*$/.test(value.sourceOwner)
    || value.sourceKind !== "Device" && value.sourceKind !== "Manual" || !safeCode(value.code) || value.code.length > 64
    || !severityNames.some(name => name === value.severity) || !utc(value.firstOccurredAt) || !utc(value.lastOccurredAt)
    || Date.parse(value.firstOccurredAt) > Date.parse(value.lastOccurredAt)
    || !integer(value.occurrenceCount, 1) || !integer(value.version, 1)
    || value.state !== "Active" && value.state !== "Acknowledged" && value.state !== "Cleared"
    || value.acknowledgedBy !== null && !validNotificationId(value.acknowledgedBy)
    || value.acknowledgedAt !== null && !utc(value.acknowledgedAt)
    || value.silenceUntil !== null && !utc(value.silenceUntil)) throw new AlarmResponseError();
  return { id: value.id, resourceId: value.resourceId, sourceOwner: value.sourceOwner, sourceKind: value.sourceKind,
    code: value.code, severity: value.severity as AlarmSeverity, firstOccurredAt: value.firstOccurredAt,
    lastOccurredAt: value.lastOccurredAt, occurrenceCount: value.occurrenceCount, state: value.state, version: value.version,
    acknowledgedBy: value.acknowledgedBy, acknowledgedAt: value.acknowledgedAt, silenceUntil: value.silenceUntil };
}
function deliveryState(value: unknown): value is DeliveryRow["state"] {
  return ["Pending", "Sent", "Failed", "Retrying", "Unavailable"].some(state => state === value);
}
function parseDelivery(raw: unknown): DeliveryRow {
  const value = shape(raw, deliveryFields.map(field => field[0]!.toLowerCase() + field.slice(1)));
  if (!validNotificationId(value.id) || !validNotificationId(value.alarmId) || value.channel !== "InApp"
    || !deliveryState(value.state) || !integer(value.occurrenceCount, 1) || !integer(value.attemptCount)
    || !integer(value.version, 1) || !utc(value.dueAt)
    || value.nextAttemptAt !== null && !utc(value.nextAttemptAt)
    || value.errorCode !== null && !safeCode(value.errorCode)) throw new AlarmResponseError();
  return { id: value.id, alarmId: value.alarmId, channel: value.channel, state: value.state,
    occurrenceCount: value.occurrenceCount, dueAt: value.dueAt, attemptCount: value.attemptCount,
    nextAttemptAt: value.nextAttemptAt, errorCode: value.errorCode, version: value.version };
}
async function request(path: string, options: RequestInit): Promise<unknown> {
  try { return await api(path, options); }
  catch (error) { if (error instanceof SyntaxError) throw new AlarmResponseError(); throw error; }
}
async function queryRows<T extends { id: string }>(kind: "alarms" | "deliveries", offset: number, limit: number,
  signal: AbortSignal, parse: (value: unknown) => T): Promise<T[]> {
  if (!Number.isSafeInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("告警页范围无效，请从第一页重新读取。");
  const page = shape(await request(`/api/notifications/${kind}?offset=${offset}&limit=${limit}`, { signal }), ["rows", "offset", "limit"]);
  if (page.offset !== offset || page.limit !== limit || !Array.isArray(page.rows) || page.rows.length > limit)
    throw new AlarmResponseError();
  const rows = page.rows.map(parse);
  if (new Set(rows.map(row => row.id.toLowerCase())).size !== rows.length) throw new AlarmResponseError();
  return rows;
}
export const queryAlarms = (offset: number, limit: number, signal: AbortSignal) => queryRows("alarms", offset, limit, signal, parseAlarm);
export const queryDeliveries = (offset: number, limit: number, signal: AbortSignal) => queryRows("deliveries", offset, limit, signal, parseDelivery);
export async function queryAlarm(id: string, signal: AbortSignal): Promise<AlarmDetail> {
  if (!validNotificationId(id)) throw new Error("请选择有效告警 UUID。");
  const raw = await request(`/api/notifications/alarms/${id}`, { signal });
  if (!isRecord(raw) || !Array.isArray(raw.occurrences) || raw.occurrences.length > 100) throw new AlarmResponseError();
  const { occurrences, ...base } = raw;
  const row = parseAlarm(base);
  if (row.id.toLowerCase() !== id.toLowerCase()) throw new AlarmResponseError();
  const facts = occurrences.map(rawFact => {
    const fact = shape(rawFact, ["id", "sourceEventId", "occurredAt", "receivedAt", "correlationId", "summary"]);
    if (!validNotificationId(fact.id) || !validNotificationId(fact.sourceEventId) || !validNotificationId(fact.correlationId)
      || !utc(fact.occurredAt) || !utc(fact.receivedAt) || typeof fact.summary !== "string"
      || !fact.summary.trim() || fact.summary.length > 512) throw new AlarmResponseError();
    return { id: fact.id, sourceEventId: fact.sourceEventId, occurredAt: fact.occurredAt,
      receivedAt: fact.receivedAt, correlationId: fact.correlationId, summary: fact.summary };
  });
  if (new Set(facts.map(fact => fact.id.toLowerCase())).size !== facts.length || facts.length > row.occurrenceCount) throw new AlarmResponseError();
  return { ...row, occurrences: facts };
}
export async function queryDelivery(id: string, signal: AbortSignal): Promise<DeliveryDetail> {
  if (!validNotificationId(id)) throw new Error("请选择有效投递 UUID。");
  const raw = await request(`/api/notifications/deliveries/${id}`, { signal });
  if (!isRecord(raw) || !Array.isArray(raw.attempts) || raw.attempts.length > 100) throw new AlarmResponseError();
  const { attempts, ...base } = raw;
  const row = parseDelivery(base);
  if (row.id.toLowerCase() !== id.toLowerCase()) throw new AlarmResponseError();
  const history = attempts.map(rawAttempt => {
    const attempt = shape(rawAttempt, ["id", "attemptedAt", "state", "errorCode"]);
    if (!validNotificationId(attempt.id) || !utc(attempt.attemptedAt) || !deliveryState(attempt.state)
      || attempt.errorCode !== null && !safeCode(attempt.errorCode)) throw new AlarmResponseError();
    return { id: attempt.id, attemptedAt: attempt.attemptedAt, state: attempt.state, errorCode: attempt.errorCode };
  });
  if (new Set(history.map(attempt => attempt.id.toLowerCase())).size !== history.length || history.length > row.attemptCount)
    throw new AlarmResponseError();
  return { ...row, attempts: history };
}
async function mutation<T extends { id: string }>(path: string, body: object, signal: AbortSignal,
  parse: (value: unknown) => T, expectedId?: string): Promise<T> {
  const raw = shape(await request(path, { method: "POST", body: JSON.stringify(body), signal }), ["decision", "value"]);
  if (raw.decision !== "Applied" && raw.decision !== "Replayed") throw new AlarmResponseError();
  const value = parse(raw.value);
  if (expectedId && value.id.toLowerCase() !== expectedId.toLowerCase()) throw new AlarmResponseError();
  return value;
}
export function mutateAlarm(id: string, action: AlarmAction, body: AlarmCommand, signal: AbortSignal): Promise<AlarmRow> {
  if (!validNotificationId(id) || !validNotificationId(body.operationId) || !integer(body.expectedVersion, 1)
    || !body.reason.trim() || body.reason.length > 256 || body.seconds !== undefined &&
    (!integer(body.seconds, 1) || body.seconds > 3600 || action !== "silence")) throw new Error("告警操作输入无效。");
  return mutation(`/api/notifications/alarms/${id}/${action}`, body, signal, parseAlarm, id);
}
export function createManualAlarm(body: ManualAlarmCommand, signal: AbortSignal): Promise<AlarmRow> {
  if (!validNotificationId(body.operationId) || !validNotificationId(body.resourceId) || !safeCode(body.code) || body.code.length > 64
    || !severityNames.includes(body.severity) || !body.summary.trim() || body.summary.length > 512) throw new Error("人工告警输入无效。");
  return mutation("/api/notifications/alarms/manual", body, signal, parseAlarm);
}
export function mutateDelivery(id: string, body: AlarmCommand, signal: AbortSignal): Promise<DeliveryRow> {
  if (!validNotificationId(id) || !validNotificationId(body.operationId) || !integer(body.expectedVersion, 1)
    || !body.reason.trim() || body.reason.length > 256 || body.seconds !== undefined) throw new Error("投递重试输入无效。");
  return mutation(`/api/notifications/deliveries/${id}/retry`, body, signal, parseDelivery, id);
}
