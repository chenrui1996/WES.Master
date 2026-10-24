<script setup lang="ts">
import { computed } from "vue";
import type { AuthorizedColumn } from "./useColumns";

const props = defineProps<{ columns: AuthorizedColumn[] }>();
const selected = defineModel<string[]>({ required: true });
const ordered = computed(() => [
  ...selected.value.flatMap(key => props.columns.filter(column => column.key === key)),
  ...props.columns.filter(column => !selected.value.includes(column.key)),
]);
function toggle(column: AuthorizedColumn) {
  if (column.required) return;
  selected.value = selected.value.includes(column.key)
    ? selected.value.filter(key => key !== column.key) : [...selected.value, column.key];
}
function move(key: string, delta: number) {
  const keys = selected.value.filter(value => props.columns.some(column => column.key === value));
  const index = keys.indexOf(key);
  const target = index + delta;
  if (index < 0 || target < 0 || target >= keys.length) return;
  [keys[index], keys[target]] = [keys[target]!, keys[index]!];
  selected.value = keys;
}
</script>

<template>
  <details class="column-settings">
    <summary>列设置</summary>
    <p class="hint">仅显示当前可读字段。勾选列后可用上移、下移或方向键调整顺序。</p>
    <ul>
      <li v-for="column in ordered" :key="column.key" :data-test="`column-${column.key}`"
        :tabindex="selected.includes(column.key) ? 0 : -1" :aria-label="`${column.label}，上或下方向键调整顺序`"
        @keydown.up.prevent="move(column.key, -1)" @keydown.down.prevent="move(column.key, 1)">
        <span v-if="column.required">{{ column.label }}（必显）</span>
        <label v-else><input type="checkbox" :value="column.key" :checked="selected.includes(column.key)"
          @change="toggle(column)" />{{ column.label }}</label>
        <template v-if="selected.includes(column.key)">
          <button v-if="selected.indexOf(column.key) > 0" type="button" :aria-label="`上移${column.label}`" @click="move(column.key, -1)">上移</button>
          <button v-if="selected.indexOf(column.key) < selected.length - 1" type="button" :aria-label="`下移${column.label}`" @click="move(column.key, 1)">下移</button>
        </template>
      </li>
    </ul>
  </details>
</template>

<style scoped>
.column-settings { margin-block: var(--wes-space-3); }
summary { cursor: pointer; min-height: 44px; padding-block: var(--wes-space-2); }
ul { max-width: 38rem; margin: 0; padding: 0; border: 1px solid var(--wes-color-border-soft); background: var(--wes-color-surface); list-style: none; }
li { display: flex; flex-wrap: wrap; align-items: center; gap: var(--wes-space-2); padding: var(--wes-space-1) var(--wes-space-2); border-bottom: 1px solid var(--wes-color-border-soft); }
li > span, li > label { flex: 1; min-width: 8rem; margin: 0; }
label { display: flex; min-height: 44px; align-items: center; gap: var(--wes-space-2); }
input { width: 20px; min-width: 20px; min-height: 20px; }
button { white-space: nowrap; }
</style>
