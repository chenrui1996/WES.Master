<script setup lang="ts">
import type { DensityMode } from "../features/visual-preferences";

defineProps<{
  storageWarning: string | null;
}>();

const density = defineModel<DensityMode>({ required: true });
</script>

<template>
  <div class="density-control">
    <label>
      <span>显示密度</span>
      <select v-model="density">
        <option value="compact">紧凑</option>
        <option value="standard">标准</option>
        <option value="comfortable">舒适</option>
      </select>
    </label>
    <p v-if="storageWarning" class="density-warning" role="status">{{ storageWarning }}</p>
  </div>
</template>
