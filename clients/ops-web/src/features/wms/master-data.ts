import { api, isRecord, sessionState } from "../identity/identity-api";

export type ResourceKind = "warehouses" | "locations";
export const warehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"] as const;
export const locationFields = [...warehouseFields, "WarehouseId", "PackId", "SchemaVersion"] as const;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const code = /^[a-z0-9_.-]{1,64}$/i;
function validName(value: unknown): value is string {
  return typeof value === "string" && value.length >= 1 && value.length <= 128 && value.trim() === value
    && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
export function validIdentity(value: unknown): value is string {
  return typeof value === "string" && uuid.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}
interface MasterDefinition {
  id: string; code: string; name: string; enabled: boolean; version: string; createdAt: string;
}
export type MasterData = { kind: "warehouses"; definition: MasterDefinition }
  | { kind: "locations"; definition: MasterDefinition & { warehouseId: string; packId: string; schemaVersion: string } };
export class MasterDataResponseError extends Error {
  constructor() { super("主数据响应无效或身份不符，已丢弃；请重新读取。"); }
}
export function validUtc(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value))
    return false;
  const time = Date.parse(value);
  return Number.isFinite(time) && new Date(time).toISOString().slice(0, 19) === value.slice(0, 19);
}
function parseDefinition(value: unknown, kind: ResourceKind, id: string): MasterData {
  const expected = (kind === "warehouses" ? warehouseFields : locationFields)
    .map(field => field[0]!.toLowerCase() + field.slice(1));
  if (!isRecord(value) || Object.keys(value).length !== expected.length || !expected.every(field => field in value)
    || !validIdentity(value.id) || value.id.toLowerCase() !== id.toLowerCase()
    || typeof value.code !== "string" || !code.test(value.code) || value.code !== value.code.toUpperCase()
    || !validName(value.name) || typeof value.enabled !== "boolean"
    || !validIdentity(value.version) || !validUtc(value.createdAt))
    throw new MasterDataResponseError();
  const definition: MasterDefinition = {
    id: value.id, code: value.code, name: value.name, enabled: value.enabled, version: value.version, createdAt: value.createdAt,
  };
  if (kind === "warehouses") return { kind, definition };
  if (!validIdentity(value.warehouseId) || !validIdentity(value.packId)
    || typeof value.schemaVersion !== "string" || !code.test(value.schemaVersion)) throw new MasterDataResponseError();
  return { kind, definition: { ...definition, warehouseId: value.warehouseId, packId: value.packId, schemaVersion: value.schemaVersion } };
}
export async function readMasterData(kind: ResourceKind, id: string, signal: AbortSignal): Promise<MasterData> {
  if (!validIdentity(id)) throw new Error("请输入有效的非空 UUID。");
  try {
    return parseDefinition(await api(`/api/wms/${kind}/${id}`, { signal }), kind, id);
  } catch (error) {
    if (error instanceof SyntaxError) throw new MasterDataResponseError();
    throw error;
  }
}

export interface MasterDataFilters { code?: string; enabled?: boolean; warehouseId?: string }
export interface MasterDataPage { rows: MasterData[]; offset: number; limit: number }
export async function queryMasterData(kind: ResourceKind, offset: number, limit: number, filters: MasterDataFilters,
  signal: AbortSignal): Promise<MasterDataPage> {
  if (!Number.isInteger(offset) || offset < 0 || offset > 10000 || ![25, 50, 100].includes(limit))
    throw new Error("目录页范围无效，请从第一页重新读取。");
  if (filters.code !== undefined && !code.test(filters.code)) throw new Error("代码须为 1 至 64 个 ASCII 字母、数字、-、_ 或 .。");
  if (filters.warehouseId !== undefined && (kind !== "locations" || !validIdentity(filters.warehouseId)))
    throw new Error("仓库筛选须为有效的非空 UUID，且只适用于库位目录。");
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) });
  if (filters.code !== undefined) query.set("code", filters.code.toUpperCase());
  if (filters.enabled !== undefined) query.set("enabled", String(filters.enabled));
  if (filters.warehouseId !== undefined) query.set("warehouseId", filters.warehouseId);
  let value: unknown;
  try { value = await api(`/api/wms/${kind}?${query}`, { signal }); }
  catch (error) { if (error instanceof SyntaxError) throw new MasterDataResponseError(); throw error; }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !Array.isArray(value.rows)
    || value.offset !== offset || value.limit !== limit || value.rows.length > limit) throw new MasterDataResponseError();
  const ids = new Set<string>();
  const rows = value.rows.map((row: unknown) => {
    if (!isRecord(row) || !validIdentity(row.id)) throw new MasterDataResponseError();
    const parsed = parseDefinition(row, kind, row.id);
    if (ids.has(parsed.definition.id.toLowerCase()) || filters.code !== undefined && parsed.definition.code !== filters.code.toUpperCase()
      || filters.enabled !== undefined && parsed.definition.enabled !== filters.enabled
      || filters.warehouseId !== undefined && (parsed.kind !== "locations"
        || parsed.definition.warehouseId.toLowerCase() !== filters.warehouseId.toLowerCase())) throw new MasterDataResponseError();
    ids.add(parsed.definition.id.toLowerCase());
    return parsed;
  });
  if (rows.some((row, index) => index > 0 && rows[index - 1]!.definition.code >= row.definition.code)) throw new MasterDataResponseError();
  return { rows, offset, limit };
}

export function mayCreateWarehouse(): boolean {
  const fields = new Set(sessionState.subject?.grants.filter(grant => grant.permission === "wms.warehouses.write").flatMap(grant => grant.fields));
  return !!sessionState.subject && ["Code", "Name"].every(field => fields.has(field));
}
export function warehouseDraftError(draftCode: string, name: string): string {
  if (!code.test(draftCode)) return "代码须为 1 至 64 个 ASCII 字母、数字、-、_ 或 .，不自动去除空白。";
  if (!validName(name)) return "名称须为 1 至 128 个字符，不能含控制字符或边界空白。";
  return "";
}
export interface WarehouseCreationCommand { code: string; name: string; operation: string }
export interface WarehouseCreationReceipt { decision: 0 | 1; id: string; version: string }
export async function createWarehouse(command: WarehouseCreationCommand, signal: AbortSignal): Promise<WarehouseCreationReceipt> {
  const error = warehouseDraftError(command.code, command.name);
  if (error || !validIdentity(command.operation)) throw new Error(error || "创建操作 UUID 无效。");
  let value: unknown;
  try {
    value = await api("/api/wms/warehouses", { method: "POST", signal,
      headers: { "Idempotency-Key": command.operation }, body: JSON.stringify({ code: command.code, name: command.name }) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("创建响应无效，提交结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !["decision", "id", "version"].every(field => field in value)
    || value.decision !== 0 && value.decision !== 1 || !validIdentity(value.id) || !validIdentity(value.version))
    throw new Error("创建响应无效，提交结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
