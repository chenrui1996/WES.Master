import { api, isRecord, sessionState } from "../identity/identity-api";

export const requiredMessageFields = ["EventId", "Owner", "Status", "CreatedAt"] as const;
export const replayMessageFields = ["EventId", "Status", "Attempts", "Reason"] as const;
export const messageCountFields = ["Owner", "ObservedAt", "PendingCount", "LeasedCount", "PublishedCount", "QuarantinedCount", "InboxReceiptCount"] as const;
export interface MessageSummary {
  owner: "Platform"; observedAt: string; pendingCount: number; leasedCount: number;
  publishedCount: number; quarantinedCount: number; inboxReceiptCount: number;
}
export async function queryMessageSummary(signal: AbortSignal): Promise<MessageSummary> {
  let value: unknown;
  try { value = await api("/api/platform/messages/summary", { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new MessageResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 2 || value.authorized !== true || !isRecord(value.summary))
    throw new MessageResponseError();
  const row = value.summary;
  if (Object.keys(row).length !== 7 || row.owner !== "Platform" || !utc(row.observedAt)
    || !integer(row.pendingCount, 0) || !integer(row.leasedCount, 0) || !integer(row.publishedCount, 0)
    || !integer(row.quarantinedCount, 0) || !integer(row.inboxReceiptCount, 0)) throw new MessageResponseError();
  return { owner: row.owner, observedAt: row.observedAt, pendingCount: row.pendingCount, leasedCount: row.leasedCount,
    publishedCount: row.publishedCount, quarantinedCount: row.quarantinedCount, inboxReceiptCount: row.inboxReceiptCount };
}
export function allMessageFields(permission: "read" | "replay"): Set<string> {
  return new Set(sessionState.subject?.grants
    .filter(grant => grant.permission === `platform.messages.${permission}` && grant.scope === 2)
    .flatMap(grant => grant.fields) ?? []);
}
export const messageStatuses = ["待发布", "发布租约中", "Broker 已确认", "已隔离"] as const;
const errors = ["BrokerUnavailable", "BrokerTimeout", "InvalidContract", "AuthorizationDenied"];
const keys = ["eventId", "contractName", "schemaVersion", "owner", "status", "attempts", "createdAt",
  "nextAttemptAt", "publishedAt", "lastErrorCategory"];
export interface MessageRow {
  eventId: string; contractName: string | null; schemaVersion: number | null; owner: "Platform";
  status: 0 | 1 | 2 | 3; attempts: number | null; createdAt: string;
  nextAttemptAt: string | null; publishedAt: string | null; lastErrorCategory: string | null;
}
export class MessageResponseError extends Error {
  constructor() { super("消息诊断响应无效，已丢弃；请重新读取。"); }
}
export function replayableMessage(row: MessageRow): boolean {
  return row.owner === "Platform" && row.contractName === "platform.audit" && row.schemaVersion === 1
    && row.status === 3 && row.attempts !== null && Number.isSafeInteger(row.attempts) && row.attempts >= 1;
}
export interface MessageReplayRequest { eventId: string; expectedAttempts: number; reason: string; operation: string }
export async function replayMessage(request: MessageReplayRequest, signal: AbortSignal): Promise<void> {
  const value: unknown = await api(`/api/platform/messages/${request.eventId}/replay`, {
    method: "POST", headers: { "Idempotency-Key": request.operation },
    body: JSON.stringify({ expectedAttempts: request.expectedAttempts, reason: request.reason }), signal,
  });
  if (!isRecord(value) || Object.keys(value).length !== 2 || value.decision !== 5
    || value.attempts !== null && value.attempts !== 0) throw new MessageResponseError();
}
function utc(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)) return false;
  const time = Date.parse(value);
  return Number.isFinite(time) && new Date(time).toISOString().slice(0, 19) === value.slice(0, 19);
}
function utcKey(value: string): string {
  return value.slice(0, 19) + (value.match(/\.(\d{1,7})/)?.[1] ?? "").padEnd(7, "0");
}
function integer(value: unknown, minimum: number): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= minimum;
}
function parseRow(value: unknown): MessageRow {
  if (!isRecord(value) || Object.keys(value).length !== keys.length || !keys.every(key => key in value)
    || typeof value.eventId !== "string" || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value.eventId)
    || value.eventId === "00000000-0000-0000-0000-000000000000"
    || value.owner !== "Platform" || !utc(value.createdAt)
    || value.status !== 0 && value.status !== 1 && value.status !== 2 && value.status !== 3
    || value.contractName !== null && value.contractName !== "platform.audit"
    || value.schemaVersion !== null && !integer(value.schemaVersion, 1)
    || value.attempts !== null && !integer(value.attempts, 0)
    || value.nextAttemptAt !== null && !utc(value.nextAttemptAt)
    || value.publishedAt !== null && !utc(value.publishedAt)
    || value.lastErrorCategory !== null && (typeof value.lastErrorCategory !== "string" || !errors.includes(value.lastErrorCategory)))
    throw new MessageResponseError();
  return { eventId: value.eventId, owner: value.owner, status: value.status, createdAt: value.createdAt,
    contractName: value.contractName, schemaVersion: value.schemaVersion, attempts: value.attempts,
    nextAttemptAt: value.nextAttemptAt, publishedAt: value.publishedAt, lastErrorCategory: value.lastErrorCategory };
}
export interface MessageFilters { status?: number; createdFrom?: string; createdBefore?: string }
export async function queryMessages(offset: number, limit: number, signal: AbortSignal, filters: MessageFilters = {}): Promise<MessageRow[]> {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("消息页范围无效，请从第一页重新读取。");
  if (filters.status !== undefined && (!Number.isInteger(filters.status) || filters.status < 0 || filters.status > 3))
    throw new Error("请选择有效的消息状态。");
  for (const time of [filters.createdFrom, filters.createdBefore]) {
    if (time !== undefined && (!utc(time) || Number(time.slice(0, 4)) < 1000))
      throw new Error("请输入公元 1000 年起的有效 UTC 创建时间。");
  }
  if (filters.createdFrom !== undefined && filters.createdBefore !== undefined
    && utcKey(filters.createdFrom) >= utcKey(filters.createdBefore)) throw new Error("创建时间结束必须晚于开始。");
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });
  if (filters.status !== undefined) query.set("status", String(filters.status));
  if (filters.createdFrom !== undefined) query.set("createdFrom", filters.createdFrom);
  if (filters.createdBefore !== undefined) query.set("createdBefore", filters.createdBefore);
  let value: unknown;
  try { value = await api(`/api/platform/messages?${query}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new MessageResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 2 || value.authorized !== true
    || !Array.isArray(value.rows) || value.rows.length > limit) throw new MessageResponseError();
  const rows = value.rows.map(parseRow);
  if (new Set(rows.map(row => row.eventId.toLowerCase())).size !== rows.length) throw new MessageResponseError();
  if (rows.some(row => filters.status !== undefined && row.status !== filters.status
    || filters.createdFrom !== undefined && utcKey(row.createdAt) < utcKey(filters.createdFrom)
    || filters.createdBefore !== undefined && utcKey(row.createdAt) >= utcKey(filters.createdBefore))) throw new MessageResponseError();
  return rows;
}
