<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { columnWidthPreset, type AuthorizedColumn } from "./useColumns";

const props = defineProps<{ columns: AuthorizedColumn[]; preferenceWarning?: string | null }>();
const selected = defineModel<string[]>({ required: true });
const widths = defineModel<Record<string, number>>("widths", { default: () => ({}) });
const fixedFirstColumn = defineModel<boolean>("fixedFirstColumn", { default: true });
const settingsForm = ref<HTMLFormElement | null>(null);
const draftSelected = ref([...selected.value]);
const draftWidths = ref({ ...widths.value });
const draftFixedFirstColumn = ref(fixedFirstColumn.value);
function resetDraft() {
  draftSelected.value = [...selected.value];
  draftWidths.value = { ...widths.value };
  draftFixedFirstColumn.value = fixedFirstColumn.value;
}
watch([selected, widths, fixedFirstColumn], resetDraft, { deep: true });
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
function apply() {
  selected.value = [...draftSelected.value];
  widths.value = { ...draftWidths.value };
  fixedFirstColumn.value = draftFixedFirstColumn.value;
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
  clearWidthValidation();
}
</script>

<template>
  <details class="column-settings">
    <summary>列设置</summary>
    <p class="hint">仅显示当前可读字段。勾选列后可用上移、下移或方向键调整顺序；宽度上限为 600 像素，留空恢复字段基准宽度。应用前的修改仅为草稿。</p>
    <p v-if="preferenceWarning" class="notice" role="status">{{ preferenceWarning }}</p>
    <label class="pin-control"><input v-model="draftFixedFirstColumn" type="checkbox" data-test="fixed-first-column">固定首列，横向滚动时保持可见</label>
    <form ref="settingsForm" @submit.prevent="apply">
      <ul>
        <li v-for="column in ordered" :key="column.key" :data-test="`column-${column.key}`"
          :tabindex="draftSelected.includes(column.key) ? 0 : -1" :aria-label="`${column.label}，上或下方向键调整顺序`"
          @keydown.up.prevent="move(column.key, -1)" @keydown.down.prevent="move(column.key, 1)">
          <span v-if="column.required">{{ column.label }}（必显）</span>
          <label v-else><input type="checkbox" :value="column.key" :checked="draftSelected.includes(column.key)"
            @change="toggle(column)" />{{ column.label }}</label>
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
</style>
