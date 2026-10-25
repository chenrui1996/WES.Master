import { computed, ref, watch } from "vue";
import { isRecord, sessionState } from "../features/identity/identity-api";

/** Column metadata only. Never persists rows, authorization or preferences across identities. */
export interface AuthorizedColumn { key: string; label: string; required?: boolean }
export const MIN_COLUMN_WIDTH = 128;
export const MAX_COLUMN_WIDTH = 800;

export interface ColumnPreferenceOptions {
  pageId: string;
  tableId: string | (() => string);
  environment?: string;
  storageProvider?: () => Pick<Storage, "getItem" | "setItem">;
}

interface StoredColumnPreference { version: 1; selected: string[]; widths?: Record<string, number> }
const invalidPreferenceWarning = "列偏好格式无效，已恢复为当前可读列默认值。";
const unavailableStorageWarning = "无法读取或保存列偏好；当前选择仅在本次页面会话有效。";

export function columnPreferenceKey(
  environment: string,
  tenantId: string,
  userId: string,
  pageId: string,
  tableId: string,
): string {
  return `wes-ops-web:columns:v1:${[environment, tenantId, userId, pageId, tableId].map(encodeURIComponent).join(":")}`;
}

function parsePreference(value: string): StoredColumnPreference | null {
  let parsed: unknown;
  try { parsed = JSON.parse(value); }
  catch (failure) {
    if (failure instanceof SyntaxError) return null;
    throw failure;
  }
  if (!isRecord(parsed) || parsed.version !== 1 || !Array.isArray(parsed.selected)
    || !parsed.selected.every((item: unknown) => typeof item === "string")) return null;
  if (parsed.widths !== undefined && !isColumnWidths(parsed.widths)) return null;
  return { version: 1, selected: parsed.selected,
    ...(parsed.widths === undefined ? {} : { widths: parsed.widths }) };
}

function isColumnWidths(value: unknown): value is Record<string, number> {
  return isRecord(value) && Object.values(value).every((width: unknown) =>
    typeof width === "number" && Number.isInteger(width) && width >= MIN_COLUMN_WIDTH && width <= MAX_COLUMN_WIDTH);
}

/** The caller supplies only fields present in the current server projection. Revocation drops preferences. */
export function useColumns(source: () => AuthorizedColumn[], options: ColumnPreferenceOptions) {
  const available = computed(source);
  const selected = ref<string[]>([]);
  const widths = ref<Record<string, number>>({});
  const preferenceWarning = ref<string | null>(null);
  const hydratedScope = ref<string | null>(null);
  const environment = options.environment ?? import.meta.env.MODE;
  const preferenceScope = computed(() => {
    const subject = sessionState.subject;
    if (!subject) return null;
    const tableId = typeof options.tableId === "function" ? options.tableId() : options.tableId;
    return columnPreferenceKey(environment, subject.tenantId, subject.userId, options.pageId, tableId);
  });
  const storageProvider = options.storageProvider ?? (() => window.localStorage);

  watch(available, (columns, previous) => {
    const permitted = new Set(columns.map(column => column.key));
    const old = new Set(previous?.map(column => column.key) ?? []);
    selected.value = [
      ...selected.value.filter(key => permitted.has(key)),
      ...columns.filter(column => !old.has(column.key) && !selected.value.includes(column.key)).map(column => column.key),
    ];
    for (const column of columns)
      if (column.required && !selected.value.includes(column.key)) selected.value.unshift(column.key);
  }, { immediate: true });

  watch(preferenceScope, () => {
    hydratedScope.value = null;
    preferenceWarning.value = null;
    selected.value = [];
    widths.value = {};
  }, { flush: "sync" });

  watch(available, columns => {
    if (!columns.length) return;
    const permitted = new Set(columns.map(column => column.key));
    widths.value = Object.fromEntries(Object.entries(widths.value).filter(([key]) => permitted.has(key)));
  }, { flush: "sync" });

  watch([preferenceScope, available], ([key, columns]) => {
    if (!key || !columns.length || hydratedScope.value === key) return;
    let stored: StoredColumnPreference | null = null;
    try {
      const value = storageProvider().getItem(key);
      if (value !== null) stored = parsePreference(value);
      if (value !== null && stored === null) preferenceWarning.value = invalidPreferenceWarning;
    } catch (failure) {
      if (!(failure instanceof DOMException)) throw failure;
      preferenceWarning.value = unavailableStorageWarning;
    }

    const saved = stored?.selected ?? [];
    const permitted = new Set(columns.map(column => column.key));
    const ordered = [...new Set(saved.filter(key => permitted.has(key)))];
    for (const column of columns)
      if (!saved.includes(column.key)) ordered.push(column.key);
    for (const column of columns)
      if (column.required && !ordered.includes(column.key)) ordered.unshift(column.key);
    hydratedScope.value = key;
    widths.value = Object.fromEntries(Object.entries(stored?.widths ?? {})
      .filter(([columnKey]) => permitted.has(columnKey)));
    selected.value = ordered;
  }, { immediate: true, flush: "sync" });

  watch([selected, widths], ([selectedValue, widthValues]) => {
    const key = preferenceScope.value;
    if (!key || hydratedScope.value !== key) return;
    const permitted = new Set(available.value.map(column => column.key));
    const normalized = [...new Set(selectedValue.filter(column => permitted.has(column)))];
    for (const column of available.value)
      if (column.required && !normalized.includes(column.key)) normalized.unshift(column.key);
    try {
      const normalizedWidths = Object.fromEntries(Object.entries(widthValues)
        .filter(([columnKey, width]) => permitted.has(columnKey)
          && Number.isInteger(width) && width >= MIN_COLUMN_WIDTH && width <= MAX_COLUMN_WIDTH));
      storageProvider().setItem(key, JSON.stringify({ version: 1, selected: normalized,
        ...(Object.keys(normalizedWidths).length ? { widths: normalizedWidths } : {}) }));
      preferenceWarning.value = null;
    } catch (failure) {
      if (!(failure instanceof DOMException)) throw failure;
      preferenceWarning.value = unavailableStorageWarning;
    }
  }, { deep: true });

  const visible = computed(() => selected.value.flatMap(key => {
    const column = available.value.find(item => item.key === key);
    return column ? [column] : [];
  }));
  function widthStyle(key: string) {
    const width = widths.value[key];
    return width === undefined ? undefined : { width: `${width}px` };
  }
  return { available, selected, visible, widths, widthStyle, preferenceWarning };
}
