<script setup lang="ts">
import { useId } from "vue";

const props = withDefaults(defineProps<{
  label: string;
  offset: number;
  limit: number;
  count: number;
  busy?: boolean;
  failed?: boolean;
  selectId?: string;
}>(), { busy: false, failed: false });
const emit = defineEmits<{ change: [offset: number, limit: number] }>();
const generatedId = useId();
const sizes = [25, 50, 100];
function resize(event: Event) {
  if (!(event.target instanceof HTMLSelectElement) || !sizes.includes(Number(event.target.value)))
    throw new Error("每页条数须为 25、50 或 100。");
  emit("change", 0, Number(event.target.value));
}
</script>

<template>
  <nav class="actions" :aria-label="props.label">
    <label :for="selectId ?? generatedId">每页条数
      <select :id="selectId ?? generatedId" :value="limit" :disabled="busy" @change="resize">
        <option v-for="size in sizes" :key="size" :value="size">{{ size }}</option>
      </select>
    </label>
    <button type="button" data-test="previous-page" :disabled="busy || offset === 0"
      @click="emit('change', Math.max(0, offset - limit), limit)">上一页</button>
    <span aria-live="polite">实时 · 第 {{ Math.floor(offset / limit) + 1 }} 页 · 本页 {{ count }} 条（最多 {{ limit }} 条）</span>
    <button type="button" data-test="next-page" :disabled="busy || failed || count !== limit"
      @click="emit('change', offset + limit, limit)">下一页</button>
    <span class="hint">实时数据变化时不保证跨请求无丢重。</span>
  </nav>
</template>
