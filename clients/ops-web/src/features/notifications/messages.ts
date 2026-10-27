import { api, isRecord } from "../identity/identity-api";

export const requiredMessageFields = ["EventId", "Owner", "Status", "CreatedAt"] as const;
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
function utc(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)) return false;
  const time = Date.parse(value);
  return Number.isFinite(time) && new Date(time).toISOString().slice(0, 19) === value.slice(0, 19);
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
export async function queryMessages(offset: number, limit: number, signal: AbortSignal): Promise<MessageRow[]> {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("消息页范围无效，请从第一页重新读取。");
  let value: unknown;
  try { value = await api(`/api/platform/messages?offset=${offset}&limit=${limit}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new MessageResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 2 || value.authorized !== true
    || !Array.isArray(value.rows) || value.rows.length > limit) throw new MessageResponseError();
  const rows = value.rows.map(parseRow);
  if (new Set(rows.map(row => row.eventId.toLowerCase())).size !== rows.length) throw new MessageResponseError();
  return rows;
}
