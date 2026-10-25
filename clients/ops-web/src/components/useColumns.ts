import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import type { ComputedRef, Ref } from "vue";
import { isRecord, sessionState } from "../features/identity/identity-api";

/** Column metadata only. Never persists rows, authorization or preferences across identities. */
export type ColumnWidthClass = "identity" | "status" | "numeric" | "time" | "description";
export interface AuthorizedColumn {
  key: string;
  label: string;
  required?: boolean;
  widthClass?: ColumnWidthClass;
}
export interface ColumnWidthPreset { defaultWidth: number; minWidth: number; maxWidth: number }
export interface ColumnLayout {
  selected: string[];
  widths: Record<string, number>;
  fixedFirstColumn: boolean;
  knownColumns: string[];
}
export interface SavedColumnView extends ColumnLayout {
  id: string;
  name: string;
}
export interface ColumnViewController {
  views: Ref<SavedColumnView[]>;
  activeViewId: Ref<string | null>;
  viewDirty: ComputedRef<boolean>;
  viewError: Ref<string | null>;
  activateView: (id: string | null) => void;
  createView: (name: string) => string | null;
  saveView: () => string | null;
  renameView: (name: string) => string | null;
  restoreView: () => void;
  removeView: () => string | null;
}
export const MIN_COLUMN_WIDTH = 100;
export const MAX_COLUMN_WIDTH = 600;
const MAX_STORED_COLUMN_WIDTH = 800;
const columnWidthPresets: Record<ColumnWidthClass, ColumnWidthPreset> = {
  identity: { defaultWidth: 200, minWidth: 160, maxWidth: MAX_COLUMN_WIDTH },
  status: { defaultWidth: 120, minWidth: 100, maxWidth: MAX_COLUMN_WIDTH },
  numeric: { defaultWidth: 120, minWidth: 100, maxWidth: MAX_COLUMN_WIDTH },
  time: { defaultWidth: 200, minWidth: 180, maxWidth: MAX_COLUMN_WIDTH },
  description: { defaultWidth: 240, minWidth: 180, maxWidth: MAX_COLUMN_WIDTH },
};

export function columnWidthPreset(column: AuthorizedColumn): ColumnWidthPreset {
  return columnWidthPresets[column.widthClass ?? "identity"];
}

export interface ColumnPreferenceOptions {
  pageId: string;
  tableId: string | (() => string);
  environment?: string;
  storageProvider?: () => Pick<Storage, "getItem" | "setItem">;
}

interface StoredColumnPreference {
  version: 1;
  selected: string[];
  knownColumns?: string[];
  widths?: Record<string, number>;
  fixedFirstColumn?: boolean;
  views?: SavedColumnView[];
}
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
    || !parsed.selected.every((item: unknown) => typeof item === "string")
    || parsed.knownColumns !== undefined && (!Array.isArray(parsed.knownColumns)
      || !parsed.knownColumns.every((item: unknown) => typeof item === "string"))
    || parsed.fixedFirstColumn !== undefined && typeof parsed.fixedFirstColumn !== "boolean") return null;
  if (parsed.widths !== undefined && !isColumnWidths(parsed.widths)) return null;
  if (parsed.views !== undefined && (!Array.isArray(parsed.views) || !parsed.views.every(isSavedColumnView))) return null;
  return { version: 1, selected: parsed.selected,
    ...(parsed.knownColumns === undefined ? {} : { knownColumns: parsed.knownColumns }),
    ...(parsed.widths === undefined ? {} : { widths: parsed.widths }),
    ...(parsed.fixedFirstColumn === undefined ? {} : { fixedFirstColumn: parsed.fixedFirstColumn }),
    ...(parsed.views === undefined ? {} : { views: parsed.views.map((view: SavedColumnView) => ({
      id: view.id, name: view.name, selected: view.selected, knownColumns: view.knownColumns,
      widths: view.widths, fixedFirstColumn: view.fixedFirstColumn,
    })) }) };
}

function isSavedColumnView(value: unknown): value is SavedColumnView {
  return isRecord(value) && typeof value.id === "string" && value.id.length > 0
    && typeof value.name === "string" && value.name.trim().length > 0 && value.name.length <= 48
    && Array.isArray(value.selected) && value.selected.every((item: unknown) => typeof item === "string")
    && Array.isArray(value.knownColumns) && value.knownColumns.every((item: unknown) => typeof item === "string")
    && isColumnWidths(value.widths) && typeof value.fixedFirstColumn === "boolean";
}

function isColumnWidths(value: unknown): value is Record<string, number> {
  return isRecord(value) && Object.values(value).every((width: unknown) =>
    typeof width === "number" && Number.isInteger(width) && width >= MIN_COLUMN_WIDTH && width <= MAX_STORED_COLUMN_WIDTH);
}

function copyLayout(layout: ColumnLayout): ColumnLayout {
  return { selected: [...layout.selected], widths: { ...layout.widths }, fixedFirstColumn: layout.fixedFirstColumn,
    knownColumns: [...layout.knownColumns] };
}

/** The caller supplies only fields present in the current server projection. Revocation drops preferences. */
export function useColumns(source: () => AuthorizedColumn[], options: ColumnPreferenceOptions) {
  const available = computed(source);
  const selected = ref<string[]>([]);
  const widths = ref<Record<string, number>>({});
  const fixedFirstColumn = ref(true);
  const views = ref<SavedColumnView[]>([]);
  const activeViewId = ref<string | null>(null);
  const defaultLayout = ref<ColumnLayout>({ selected: [], widths: {}, fixedFirstColumn: true, knownColumns: [] });
  const activeViewSnapshot = ref<ColumnLayout | null>(null);
  const viewError = ref<string | null>(null);
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
  const viewDirty = computed(() => activeViewSnapshot.value !== null
    && JSON.stringify(activeViewSnapshot.value) !== JSON.stringify(currentLayout()));

  function currentLayout(): ColumnLayout {
    return { selected: [...selected.value], widths: { ...widths.value }, fixedFirstColumn: fixedFirstColumn.value,
      knownColumns: available.value.map(column => column.key) };
  }

  function normalizeLayout(layout: ColumnLayout, columns: AuthorizedColumn[]): ColumnLayout {
    const permitted = new Set(columns.map(column => column.key));
    const selectedKeys = [...new Set(layout.selected.filter(key => permitted.has(key)))];
    const known = new Set(layout.knownColumns);
    for (const column of columns)
      if (!known.has(column.key) && !selectedKeys.includes(column.key)) selectedKeys.push(column.key);
    for (const column of columns)
      if (column.required && !selectedKeys.includes(column.key)) selectedKeys.unshift(column.key);
    const normalizedWidths = Object.fromEntries(Object.entries(layout.widths).filter(([key, width]) => {
      const column = columns.find(item => item.key === key);
      if (!column || !Number.isInteger(width)) return false;
      const preset = columnWidthPreset(column);
      return width >= preset.minWidth && width <= preset.maxWidth;
    }));
    return { selected: selectedKeys, widths: normalizedWidths, fixedFirstColumn: layout.fixedFirstColumn,
      knownColumns: columns.map(column => column.key) };
  }

  function persistPreference(layout: ColumnLayout, savedViews: SavedColumnView[]): boolean {
    const key = preferenceScope.value;
    if (!key || hydratedScope.value !== key) return false;
    const normalized = normalizeLayout(layout, available.value);
    try {
      storageProvider().setItem(key, JSON.stringify({
        version: 1,
        selected: normalized.selected,
        knownColumns: normalized.knownColumns,
        ...(Object.keys(normalized.widths).length ? { widths: normalized.widths } : {}),
        ...(normalized.fixedFirstColumn ? { fixedFirstColumn: true } : {}),
        ...(savedViews.length ? { views: savedViews } : {}),
      }));
      preferenceWarning.value = null;
      return true;
    } catch (failure) {
      if (!(failure instanceof DOMException)) throw failure;
      preferenceWarning.value = unavailableStorageWarning;
      return false;
    }
  }

  function setLayout(layout: ColumnLayout) {
    selected.value = [...layout.selected];
    widths.value = { ...layout.widths };
    fixedFirstColumn.value = layout.fixedFirstColumn;
  }

  function activateView(id: string | null) {
    if (id === null) {
      activeViewId.value = null;
      activeViewSnapshot.value = null;
      setLayout(defaultLayout.value);
      return;
    }
    const view = views.value.find(item => item.id === id);
    if (!view) {
      viewError.value = "该个人视图已不存在，请重新读取视图列表。";
      return;
    }
    const layout = normalizeLayout(view, available.value);
    activeViewId.value = view.id;
    activeViewSnapshot.value = copyLayout(layout);
    setLayout(layout);
    viewError.value = null;
  }

  function validateViewName(rawName: string, currentId?: string): string | null {
    const name = rawName.trim();
    if (!name || name.length > 48) return "视图名称须为1至48个字符。";
    if (views.value.some(view => view.id !== currentId && view.name.toLocaleLowerCase() === name.toLocaleLowerCase()))
      return "此名称已用于当前表格的个人视图。";
    return null;
  }

  function createView(rawName: string): string | null {
    const name = rawName.trim();
    const validation = validateViewName(name);
    if (validation) { viewError.value = validation; return validation; }
    const layout = normalizeLayout(currentLayout(), available.value);
    const id = `view-${globalThis.crypto.randomUUID()}`;
    const nextViews = [...views.value, { id, name, ...copyLayout(layout) }];
    if (!persistPreference(defaultLayout.value, nextViews)) {
      viewError.value = unavailableStorageWarning;
      return unavailableStorageWarning;
    }
    views.value = nextViews;
    activateView(id);
    viewError.value = null;
    return null;
  }

  function saveView(): string | null {
    const id = activeViewId.value;
    if (!id) {
      const message = "默认视图列偏好独立保存；请使用“另存为”创建个人视图。";
      viewError.value = message;
      return message;
    }
    const layout = normalizeLayout(currentLayout(), available.value);
    const nextViews = views.value.map(view => view.id === id ? { ...view, ...copyLayout(layout) } : view);
    if (!persistPreference(defaultLayout.value, nextViews)) {
      viewError.value = unavailableStorageWarning;
      return unavailableStorageWarning;
    }
    views.value = nextViews;
    activeViewSnapshot.value = copyLayout(layout);
    viewError.value = null;
    return null;
  }

  function renameView(rawName: string): string | null {
    const id = activeViewId.value;
    if (!id) {
      const message = "默认视图不可重命名。";
      viewError.value = message;
      return message;
    }
    const validation = validateViewName(rawName, id);
    if (validation) { viewError.value = validation; return validation; }
    const nextViews = views.value.map(view => view.id === id ? { ...view, name: rawName.trim() } : view);
    if (!persistPreference(defaultLayout.value, nextViews)) {
      viewError.value = unavailableStorageWarning;
      return unavailableStorageWarning;
    }
    views.value = nextViews;
    viewError.value = null;
    return null;
  }

  function restoreView() {
    const view = views.value.find(item => item.id === activeViewId.value);
    if (view) activateView(view.id);
  }

  function removeView(): string | null {
    const id = activeViewId.value;
    if (!id) {
      const message = "默认视图不可删除。";
      viewError.value = message;
      return message;
    }
    const nextViews = views.value.filter(view => view.id !== id);
    if (!persistPreference(defaultLayout.value, nextViews)) {
      viewError.value = unavailableStorageWarning;
      return unavailableStorageWarning;
    }
    views.value = nextViews;
    activateView(null);
    viewError.value = null;
    return null;
  }

  function onStorage(event: StorageEvent) {
    if (event.key !== preferenceScope.value) return;
    preferenceWarning.value = "检测到其他标签页修改了此表格偏好；当前页面未自动应用，请重新载入页面后确认。";
  }
  onMounted(() => window.addEventListener("storage", onStorage));
  onUnmounted(() => window.removeEventListener("storage", onStorage));

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
    viewError.value = null;
    views.value = [];
    activeViewId.value = null;
    activeViewSnapshot.value = null;
    defaultLayout.value = { selected: [], widths: {}, fixedFirstColumn: true, knownColumns: [] };
    selected.value = [];
    widths.value = {};
    fixedFirstColumn.value = true;
  }, { flush: "sync" });

  watch(available, columns => {
    if (!columns.length) return;
    const permitted = new Set(columns.map(column => column.key));
    widths.value = Object.fromEntries(Object.entries(widths.value).filter(([key]) => permitted.has(key)));
    views.value = views.value.map(view => ({ ...view, ...normalizeLayout(view, columns) }));
    defaultLayout.value = normalizeLayout(defaultLayout.value, columns);
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

    const fallbackLayout: ColumnLayout = {
      selected: columns.map(column => column.key),
      widths: {},
      fixedFirstColumn: columns.some(column => column.required),
      knownColumns: columns.map(column => column.key),
    };
    const storedLayout = stored ? normalizeLayout({
      selected: stored.selected,
      knownColumns: stored.knownColumns ?? [],
      widths: stored.widths ?? {},
      fixedFirstColumn: stored.fixedFirstColumn ?? columns.some(column => column.required),
    }, columns) : fallbackLayout;
    const hasOutOfRangeWidth = Object.entries(stored?.widths ?? {}).some(([columnKey, width]) => {
      const column = columns.find(item => item.key === columnKey);
      if (!column) return false;
      const preset = columnWidthPreset(column);
      return width < preset.minWidth || width > preset.maxWidth;
    });
    if (hasOutOfRangeWidth)
      preferenceWarning.value = "部分列宽超出当前字段范围，已恢复为基准宽度。";
    const normalizedViews = (stored?.views ?? []).map(view => ({
      id: view.id, name: view.name, ...normalizeLayout(view, columns),
    })).filter((view, index, all) =>
      all.findIndex(candidate => candidate.id === view.id) === index
      && all.findIndex(candidate => candidate.name.toLocaleLowerCase() === view.name.toLocaleLowerCase()) === index);
    defaultLayout.value = copyLayout(storedLayout);
    views.value = normalizedViews;
    activeViewId.value = null;
    activeViewSnapshot.value = null;
    setLayout(storedLayout);
    hydratedScope.value = key;
  }, { immediate: true, flush: "sync" });

  watch([selected, widths, fixedFirstColumn], () => {
    const key = preferenceScope.value;
    if (!key || hydratedScope.value !== key) return;
    if (activeViewId.value !== null) return;
    defaultLayout.value = normalizeLayout(currentLayout(), available.value);
    persistPreference(defaultLayout.value, views.value);
  }, { deep: true });

  const visible = computed(() => selected.value.flatMap(key => {
    const column = available.value.find(item => item.key === key);
    return column ? [column] : [];
  }));
  function widthStyle(key: string) {
    const column = available.value.find(item => item.key === key);
    if (!column) return undefined;
    const width = widths.value[key] ?? columnWidthPreset(column).defaultWidth;
    return { width: `${width}px` };
  }
  const columnViews: ColumnViewController = {
    views, activeViewId, viewDirty, viewError, activateView, createView, saveView, renameView, restoreView, removeView,
  };
  return { available, selected, visible, widths, widthStyle, fixedFirstColumn, preferenceWarning, columnViews };
}
