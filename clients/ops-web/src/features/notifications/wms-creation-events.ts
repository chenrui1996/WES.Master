import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, validUtc } from "../wms/master-data";

export const creationEventFields = ["EventId", "Owner", "ContractName", "SchemaVersion", "AggregateId",
  "AggregateVersion", "CorrelationId", "OccurredAt", "Status"] as const;
export interface CreationEvent {
  eventId: string; owner: "Wms"; contractName: "wms.master-data.created"; schemaVersion: 1;
  aggregateId: string; aggregateVersion: string; correlationId: string; occurredAt: string; status: 0;
}
export class CreationEventResponseError extends Error {
  constructor() { super("WMS 创建事件响应无效，已丢弃；请重新读取。"); }
}
export function mayReadCreationEvents(): boolean {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === "wms.messages.read").flatMap(grant => grant.fields));
  return !!sessionState.subject && creationEventFields.every(field => fields.has(field));
}
function parseEvent(value: unknown): CreationEvent {
  const keys = creationEventFields.map(field => field[0]!.toLowerCase() + field.slice(1));
  if (!isRecord(value) || Object.keys(value).length !== keys.length || !keys.every(key => key in value)
    || !validIdentity(value.eventId) || !validIdentity(value.aggregateId) || !validIdentity(value.aggregateVersion)
    || !validIdentity(value.correlationId) || !validUtc(value.occurredAt)
    || value.owner !== "Wms" || value.contractName !== "wms.master-data.created" || value.schemaVersion !== 1 || value.status !== 0)
    throw new CreationEventResponseError();
  return { eventId: value.eventId, owner: value.owner, contractName: value.contractName, schemaVersion: value.schemaVersion,
    aggregateId: value.aggregateId, aggregateVersion: value.aggregateVersion, correlationId: value.correlationId,
    occurredAt: value.occurredAt, status: value.status };
}
export async function queryCreationEvents(offset: number, limit: number, signal: AbortSignal): Promise<CreationEvent[]> {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("创建事件页范围无效，请从第一页重新读取。");
  let value: unknown;
  try { value = await api(`/api/wms/master-data-events?${new URLSearchParams({ offset: String(offset), limit: String(limit) })}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new CreationEventResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !Array.isArray(value.rows)
    || value.rows.length > limit || value.offset !== offset || value.limit !== limit) throw new CreationEventResponseError();
  const rows = value.rows.map(parseEvent);
  if (new Set(rows.map(row => row.eventId.toLowerCase())).size !== rows.length) throw new CreationEventResponseError();
  return rows;
}
