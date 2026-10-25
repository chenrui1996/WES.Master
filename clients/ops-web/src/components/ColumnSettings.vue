<script setup lang="ts">
import { computed, ref, watch } from "vue";
import type { ColumnViewController } from "./useColumns";
import { columnWidthPreset, type AuthorizedColumn, type FixedPosition } from "./useColumns";

const props = defineProps<{ columns: AuthorizedColumn[]; preferenceWarning?: string | null; columnViews?: ColumnViewController }>();
const selected = defineModel<string[]>({ required: true });
const widths = defineModel<Record<string, number>>("widths", { default: () => ({}) });
const fixedFirstColumn = defineModel<boolean>("fixedFirstColumn", { default: true });
const fixedPositions = defineModel<Record<string, FixedPosition>>("fixedPositions", { default: () => ({}) });
const settingsForm = ref<HTMLFormElement | null>(null);
const draftSelected = ref([...selected.value]);
const draftWidths = ref({ ...widths.value });
const draftFixedFirstColumn = ref(fixedFirstColumn.value);
const draftFixedPositions = ref({ ...fixedPositions.value });
type ViewPrompt = { kind: "create" | "rename" } | { kind: "switch"; target: string | null } | { kind: "restore" | "delete" };
const viewPrompt = ref<ViewPrompt | null>(null);
const viewName = ref("");
const viewSelection = ref("");
const savedViews = computed(() => props.columnViews?.views.value ?? []);
const activeViewId = computed(() => props.columnViews?.activeViewId.value ?? null);
const activeViewDirty = computed(() => props.columnViews?.viewDirty.value ?? false);
const viewError = computed(() => props.columnViews?.viewError.value ?? null);
const draftDirty = computed(() => JSON.stringify({
  selected: draftSelected.value, widths: draftWidths.value, fixedFirstColumn: draftFixedFirstColumn.value,
  fixedPositions: draftFixedPositions.value,
}) !== JSON.stringify({
  selected: selected.value, widths: widths.value, fixedFirstColumn: fixedFirstColumn.value,
  fixedPositions: fixedPositions.value,
}));
watch(activeViewId, id => { viewSelection.value = id ?? ""; }, { immediate: true });
function resetDraft() {
  draftSelected.value = [...selected.value];
  draftWidths.value = { ...widths.value };
  draftFixedFirstColumn.value = fixedFirstColumn.value;
  draftFixedPositions.value = { ...fixedPositions.value };
}
watch([selected, widths, fixedFirstColumn], resetDraft, { deep: true });
watch(fixedPositions, () => { draftFixedPositions.value = { ...fixedPositions.value }; }, { deep: true });
watch(() => props.columns, resetDraft);
const ordered = computed(() => [
  ...draftSelected.value.flatMap(key => props.columns.filter(column => column.key === key)),
  ...props.columns.filter(column => !draftSelected.value.includes(column.key)),
]);
function toggle(column: AuthorizedColumn) {
  if (column.required) return;
  draftSelected.value = draftSelected.value.includes(column.key)
    ? draftSelected.value.filter(key => key !== column.key) : [...draftSelected.value, column.key];
}
function move(key: string, delta: number) {
  const keys = draftSelected.value.filter(value => props.columns.some(column => column.key === value));
  const index = keys.indexOf(key);
  const target = index + delta;
  if (index < 0 || target < 0 || target >= keys.length) return;
  [keys[index], keys[target]] = [keys[target]!, keys[index]!];
  draftSelected.value = keys;
}
function updateWidth(key: string, event: Event) {
  const input = event.currentTarget;
  if (!(input instanceof HTMLInputElement)) return;
  const column = props.columns.find(item => item.key === key);
  if (!column) return;
  const preset = columnWidthPreset(column);
  const raw = input.value;
  const next = { ...draftWidths.value };
  input.setCustomValidity("");
  if (!raw) {
    delete next[key];
  } else {
    const width = Number(raw);
    if (!Number.isInteger(width) || width < preset.minWidth || width > preset.maxWidth) {
      input.setCustomValidity(`列宽须为 ${preset.minWidth} 至 ${preset.maxWidth} 像素的整数。`);
      input.reportValidity();
      return;
    }
    next[key] = width;
  }
  draftWidths.value = next;
}
function updateFixedPosition(key: string, event: Event) {
  const input = event.currentTarget;
  if (!(input instanceof HTMLSelectElement)) return;
  const next = { ...draftFixedPositions.value };
  if (input.value === "left" || input.value === "right") next[key] = input.value;
  else delete next[key];
  draftFixedPositions.value = next;
}
function apply() {
  selected.value = [...draftSelected.value];
  widths.value = { ...draftWidths.value };
  fixedFirstColumn.value = draftFixedFirstColumn.value;
  fixedPositions.value = { ...draftFixedPositions.value };
}
function clearWidthValidation() {
  settingsForm.value?.querySelectorAll<HTMLInputElement>('input[type="number"]')
    .forEach(input => input.setCustomValidity(""));
}
function cancel() {
  resetDraft();
  clearWidthValidation();
}
function restoreDefaults() {
  draftSelected.value = props.columns.map(column => column.key);
  draftWidths.value = {};
  draftFixedFirstColumn.value = true;
  draftFixedPositions.value = {};
  clearWidthValidation();
}
function requestViewSwitch(target: string | null) {
  if (!props.columnViews || target === activeViewId.value) return;
  viewSelection.value = activeViewId.value ?? "";
  if (activeViewDirty.value || draftDirty.value) {
    viewPrompt.value = { kind: "switch", target };
    return;
  }
  props.columnViews.activateView(target);
}
function openViewNamePrompt(kind: "create" | "rename") {
  viewName.value = kind === "rename"
    ? savedViews.value.find(view => view.id === activeViewId.value)?.name ?? ""
    : "";
  viewPrompt.value = { kind };
}
function confirmViewPrompt() {
  if (!props.columnViews || !viewPrompt.value) return;
  const prompt = viewPrompt.value;
  if (prompt.kind === "create") {
    if (props.columnViews.createView(viewName.value)) return;
  } else if (prompt.kind === "rename") {
    if (props.columnViews.renameView(viewName.value)) return;
  } else if (prompt.kind === "switch") {
    props.columnViews.activateView(prompt.target);
  } else if (prompt.kind === "restore") {
    props.columnViews.restoreView();
  } else {
    if (props.columnViews.removeView()) return;
  }
  viewPrompt.value = null;
}
function cancelViewPrompt() {
  viewPrompt.value = null;
  viewSelection.value = activeViewId.value ?? "";
}
</script>

<template>
  <details class="column-settings">
    <summary>列设置</summary>
    <p class="hint">仅显示当前可读字段。勾选列后可用上移、下移或方向键调整顺序；宽度上限为 600 像素，留空恢复字段基准宽度。应用前的修改仅为草稿。</p>
    <p v-if="preferenceWarning" class="notice" role="status">{{ preferenceWarning }}</p>
    <section v-if="columnViews" class="view-controls" aria-label="个人视图">
      <label>个人视图
        <select :value="viewSelection" aria-label="当前个人视图"
          @change="requestViewSwitch(($event.currentTarget as HTMLSelectElement).value || null)">
          <option value="">默认视图</option>
          <option v-for="view in savedViews" :key="view.id" :value="view.id">{{ view.name }}</option>
        </select>
      </label>
      <span v-if="activeViewDirty" class="hint" role="status">视图有未保存更改</span>
      <div class="column-actions">
        <button type="button" data-test="save-view" :disabled="draftDirty"
          @click="activeViewId ? columnViews.saveView() : openViewNamePrompt('create')">
          {{ activeViewId ? "保存视图" : "保存视图为…" }}
        </button>
        <button type="button" data-test="save-view-as" :disabled="draftDirty" @click="openViewNamePrompt('create')">另存为</button>
        <button v-if="activeViewId" type="button" data-test="restore-view" :disabled="!activeViewDirty && !draftDirty"
          @click="viewPrompt = { kind: 'restore' }">恢复视图</button>
        <button v-if="activeViewId" type="button" data-test="rename-view" @click="openViewNamePrompt('rename')">重命名</button>
        <button v-if="activeViewId" type="button" data-test="delete-view" @click="viewPrompt = { kind: 'delete' }">删除视图</button>
      </div>
      <p class="hint">个人视图仅保存列顺序、显隐、宽度和固定列偏好；不保存筛选、搜索、行数据或待写内容。</p>
      <p v-if="viewError" role="alert" class="notice">{{ viewError }}</p>
      <section v-if="viewPrompt" class="view-prompt" role="group" aria-label="确认个人视图操作">
        <template v-if="viewPrompt.kind === 'create' || viewPrompt.kind === 'rename'">
          <label :for="`column-view-name-${viewPrompt.kind}`">视图名称
            <input :id="`column-view-name-${viewPrompt.kind}`" v-model="viewName" maxlength="48" required>
          </label>
          <button type="button" data-test="confirm-view" @click="confirmViewPrompt">
            {{ viewPrompt.kind === "create" ? "创建个人视图" : "保存新名称" }}
          </button>
        </template>
        <p v-else-if="viewPrompt.kind === 'switch'">切换视图将放弃当前未保存的工作副本。</p>
        <p v-else-if="viewPrompt.kind === 'restore'">恢复将放弃当前工作副本并载入上次保存的视图。</p>
        <p v-else>删除个人视图后将返回默认视图；不会删除任何业务数据。</p>
        <button v-if="viewPrompt.kind !== 'create' && viewPrompt.kind !== 'rename'" type="button"
          data-test="confirm-view" @click="confirmViewPrompt">确认</button>
        <button type="button" data-test="cancel-view" @click="cancelViewPrompt">取消</button>
      </section>
    </section>
    <label class="pin-control"><input v-model="draftFixedFirstColumn" type="checkbox" data-test="fixed-first-column">固定首列，横向滚动时保持可见</label>
    <form ref="settingsForm" @submit.prevent="apply">
      <ul>
        <li v-for="column in ordered" :key="column.key" :data-test="`column-${column.key}`"
          :tabindex="draftSelected.includes(column.key) ? 0 : -1" :aria-label="`${column.label}，上或下方向键调整顺序`"
          @keydown.up.prevent="move(column.key, -1)" @keydown.down.prevent="move(column.key, 1)">
          <span v-if="column.required">{{ column.label }}（必显）</span>
          <label v-else><input type="checkbox" :value="column.key" :checked="draftSelected.includes(column.key)"
            @change="toggle(column)" />{{ column.label }}</label>
          <label v-if="column.key !== columns.find(item => item.required)?.key" class="pin-control">
            固定位置
            <select :value="draftFixedPositions[column.key] ?? ''" :aria-label="`${column.label}固定位置`"
              :data-test="`fixed-position-${column.key}`" @change="updateFixedPosition(column.key, $event)">
              <option value="">中间</option>
              <option value="left">左侧</option>
              <option value="right">右侧</option>
            </select>
          </label>
          <template v-if="draftSelected.includes(column.key)">
            <label class="width-control">列宽
              <input :data-test="`width-${column.key}`" type="number" :min="columnWidthPreset(column).minWidth"
                :max="columnWidthPreset(column).maxWidth" :placeholder="String(columnWidthPreset(column).defaultWidth)"
                :aria-label="`${column.label}列宽（像素）`" step="1" :value="draftWidths[column.key] ?? ''"
                @change="updateWidth(column.key, $event)">
              <span>px</span>
            </label>
            <button v-if="draftSelected.indexOf(column.key) > 0" type="button" :aria-label="`上移${column.label}`" @click="move(column.key, -1)">上移</button>
            <button v-if="draftSelected.indexOf(column.key) < draftSelected.length - 1" type="button" :aria-label="`下移${column.label}`" @click="move(column.key, 1)">下移</button>
          </template>
        </li>
      </ul>
      <div class="column-actions">
        <button type="submit" data-test="apply-columns">应用</button>
        <button type="button" data-test="cancel-columns" @click="cancel">取消</button>
        <button type="button" data-test="restore-columns" @click="restoreDefaults">恢复默认列</button>
      </div>
    </form>
  </details>
</template>

<style scoped>
.column-settings { margin-block: var(--wes-space-3); }
summary { cursor: pointer; min-height: 44px; padding-block: var(--wes-space-2); }
ul { max-width: 38rem; margin: 0; padding: 0; border: 1px solid var(--wes-color-border-soft); background: var(--wes-color-surface); list-style: none; }
li { display: flex; flex-wrap: wrap; align-items: center; gap: var(--wes-space-2); padding: var(--wes-space-1) var(--wes-space-2); border-bottom: 1px solid var(--wes-color-border-soft); }
li > span, li > label { flex: 1; min-width: 8rem; margin: 0; }
label { display: flex; min-height: 44px; align-items: center; gap: var(--wes-space-2); }
.width-control { flex: 0 1 auto; min-width: 10rem; }
.width-control input { width: 6rem; min-width: 6rem; min-height: 2.75rem; padding-inline: .5rem; }
input { width: 20px; min-width: 20px; min-height: 20px; }
button { white-space: nowrap; }
.column-actions { display: flex; flex-wrap: wrap; gap: var(--wes-space-2); margin-top: var(--wes-space-2); }
.view-controls { display: grid; gap: var(--wes-space-2); max-width: 48rem; margin-block: var(--wes-space-2); }
.view-controls > label { justify-content: flex-start; }
.view-controls select, .view-prompt input { min-height: 2.75rem; }
.view-prompt { display: grid; gap: var(--wes-space-2); padding: var(--wes-space-3); border: 1px solid var(--wes-color-border-soft); background: var(--wes-color-workspace); }
</style>
