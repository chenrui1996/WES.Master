import { api, isRecord, sessionState } from "../identity/identity-api";
import { validIdentity, warehouseFields, locationFields } from "./master-data";

export type StockResourceKind = "units" | "items" | "containers" | "dimensions";
export type StockResourceCommand = { operation: string } & (
  | { kind: "units"; input: { code: string; name: string; countable: boolean; precision: number } }
  | { kind: "items"; input: { code: string; name: string; baseUnitId: string; batchRequired: boolean } }
  | { kind: "containers"; input: { code: string; name: string; warehouseId: string } }
  | { kind: "dimensions"; input: { warehouseId: string; locationId: string; itemId: string; batch: string; containerId: string } }
);
export interface StockResourceReceipt { decision: 0 | 1; id: string; version: string }
export interface StockResourceDefinition { id: string; code: string; name: string; version: string; createdAt: string }
export type StockResourceDetails =
  | { kind: "units"; definition: StockResourceDefinition & { countable: boolean; precision: number } }
  | { kind: "items"; definition: StockResourceDefinition & { baseUnitId: string; batchRequired: boolean } }
  | { kind: "containers"; definition: StockResourceDefinition & { warehouseId: string } };
export const resourceTitles: Record<StockResourceKind, string> = { units: "基准单位", items: "物料", containers: "容器", dimensions: "空账维度" };
const writes: Record<StockResourceKind, string[]> = {
  units: ["Code", "Name", "Countable", "Precision"], items: ["Code", "Name", "BaseUnitId", "BatchRequired"],
  containers: ["Code", "Name", "WarehouseId"], dimensions: ["WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId"],
};
const reads = { units: ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"],
  items: ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"],
  containers: ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"] };
function fields(permission: string, required: string[]): boolean {
  const visible = new Set(sessionState.subject?.grants.filter(grant => grant.permission === permission).flatMap(grant => grant.fields));
  return required.every(field => visible.has(field));
}
export function mayRegisterStockResource(kind: StockResourceKind): boolean {
  return !!sessionState.subject && fields(`wms.${kind}.write`, writes[kind])
    && (kind === "units" || kind === "containers" || fields("wms.units.read", reads.units))
    && (kind === "units" || kind === "items" || fields("wms.warehouses.read", [...warehouseFields]))
    && (kind !== "dimensions" || fields("wms.locations.read", [...locationFields])
      && fields("wms.items.read", reads.items) && fields("wms.containers.read", reads.containers));
}
export function mayReadStockResource(kind: Exclude<StockResourceKind, "dimensions">): boolean {
  return !!sessionState.subject && fields(`wms.${kind}.read`, reads[kind]);
}
export function stockResourceError(command: StockResourceCommand): string {
  if (!validIdentity(command.operation)) return "原操作 UUID 无效。";
  const input = command.input;
  if ("code" in input && (!/^[a-zA-Z0-9._-]{1,64}$/.test(input.code) || !input.name.length || input.name.length > 128
    || input.name !== input.name.trim() || /[\u0000-\u001f\u007f-\u009f]/.test(input.name))) return "请输入合法代码与名称。";
  switch (command.kind) {
    case "units": return Number.isInteger(command.input.precision) && command.input.precision >= 0 && command.input.precision <= 6
      && (!command.input.countable || command.input.precision === 0) ? "" : "精度必须为0至6的整数；计件单位必须为0。";
    case "items": return validIdentity(command.input.baseUnitId) ? "" : "必须指定真实已登记基准单位 UUID。";
    case "containers": return validIdentity(command.input.warehouseId) ? "" : "必须指定真实仓库 UUID。";
    case "dimensions": {
      const { warehouseId, locationId, itemId, containerId, batch } = command.input;
      if (![warehouseId, locationId, itemId, containerId].every(validIdentity)) return "仓库、库位、物料和容器 UUID 必须有效。";
      return batch.length <= 128 && batch === batch.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(batch)
        ? "" : "批次必须为至多128字符的精确值，不接受控制字符或边界空白。";
    }
  }
}
export async function registerStockResource(command: StockResourceCommand, signal: AbortSignal): Promise<StockResourceReceipt> {
  const error = stockResourceError(command); if (error) throw new Error(error);
  let value: unknown;
  try {
    value = await api(`/api/wms/${command.kind}`, { method: "POST", signal,
      headers: { "Idempotency-Key": command.operation }, body: JSON.stringify(command.input) });
  } catch (failure) {
    if (failure instanceof SyntaxError) throw new Error("登记回执无效，结果不明；请保留原操作核对。");
    throw failure;
  }
  if (!isRecord(value) || Object.keys(value).length !== 3 || !["decision", "id", "version"].every(key => Object.hasOwn(value, key))
    || value.decision !== 0 && value.decision !== 1 || !validIdentity(value.id) || !validIdentity(value.version))
    throw new Error("登记回执无效，结果不明；请保留原操作核对。");
  return { decision: value.decision, id: value.id, version: value.version };
}
export async function readStockResource(kind: Exclude<StockResourceKind, "dimensions">, id: string, signal: AbortSignal): Promise<StockResourceDetails> {
  if (!validIdentity(id)) throw new Error("资源 UUID 无效。");
  const value: unknown = await api(`/api/wms/${kind}/${id}`, { signal });
  const expected = reads[kind].map(field => field[0]!.toLowerCase() + field.slice(1));
  if (!isRecord(value) || Object.keys(value).length !== expected.length || !expected.every(key => Object.hasOwn(value, key))
    || !validIdentity(value.id) || value.id.toLowerCase() !== id.toLowerCase() || !validIdentity(value.version)
    || typeof value.code !== "string" || !/^[A-Z0-9._-]{1,64}$/.test(value.code)
    || typeof value.name !== "string" || !value.name.length || value.name.length > 128 || value.name !== value.name.trim()
    || /[\u0000-\u001f\u007f-\u009f]/.test(value.name) || typeof value.createdAt !== "string" || !Number.isFinite(Date.parse(value.createdAt)))
    throw new Error("资源详情响应无效，未显示为当前事实。");
  const common = { id: value.id, code: value.code, name: value.name, version: value.version, createdAt: value.createdAt };
  if (kind === "units" && typeof value.countable === "boolean" && typeof value.precision === "number"
    && Number.isInteger(value.precision) && value.precision >= 0 && value.precision <= 6 && (!value.countable || value.precision === 0))
    return { kind, definition: { ...common, countable: value.countable, precision: value.precision } };
  if (kind === "items" && validIdentity(value.baseUnitId) && typeof value.batchRequired === "boolean")
    return { kind, definition: { ...common, baseUnitId: value.baseUnitId, batchRequired: value.batchRequired } };
  if (kind === "containers" && validIdentity(value.warehouseId))
    return { kind, definition: { ...common, warehouseId: value.warehouseId } };
  throw new Error("资源详情响应无效，未显示为当前事实。");
}
