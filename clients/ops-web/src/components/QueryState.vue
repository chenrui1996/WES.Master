<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";

const props = withDefaults(defineProps<{
  label: string;
  state: "loading" | "error" | "empty" | "stale" | "ready";
  busy?: boolean;
  errorMessage?: string;
  staleMessage?: string;
}>(), {
  busy: false,
  errorMessage: "读取失败，请重试。",
});

const emit = defineEmits<{
  retry: [];
}>();

const showDelayedLoading = ref(false);
let loadingTimer: ReturnType<typeof setTimeout> | undefined;

watch(() => props.state, state => {
  if (loadingTimer !== undefined) clearTimeout(loadingTimer);
  showDelayedLoading.value = false;
  if (state === "loading") {
    loadingTimer = setTimeout(() => { showDelayedLoading.value = true; }, 300);
  }
}, { immediate: true });

onBeforeUnmount(() => {
  if (loadingTimer !== undefined) clearTimeout(loadingTimer);
});
</script>

<template>
  <section class="query-state" role="region" :aria-label="label"
    :aria-busy="busy || state === 'loading' ? 'true' : 'false'">
    <p v-if="state === 'loading' && !showDelayedLoading" class="sr-only" role="status" aria-live="polite">
      正在加载{{ label }}…
    </p>

    <div v-else-if="state === 'loading'" class="query-state-loading">
      <p class="sr-only" role="status" aria-live="polite">正在加载{{ label }}…</p>
      <slot name="loading">
        <p class="query-state-message">正在加载{{ label }}…</p>
      </slot>
    </div>

    <div v-else-if="state === 'error'" class="query-state-message query-state-error" role="alert">
      <p>无法加载{{ label }}：{{ errorMessage }}</p>
      <button type="button" @click="emit('retry')">重试</button>
    </div>

    <div v-else-if="state === 'empty'" class="query-state-message" role="status">
      <p>暂无{{ label }}</p>
      <slot name="empty" />
    </div>

    <template v-else>
      <div v-if="state === 'stale'" class="query-state-stale">
        <p role="status" aria-live="polite">
          {{ staleMessage ?? `显示的是最近一次已加载的${label}，刷新未完成。` }}
        </p>
        <button type="button" @click="emit('retry')">重新加载{{ label }}</button>
      </div>
      <slot />
    </template>
  </section>
</template>

<style scoped>
.query-state { min-width: 0; }
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}
.query-state-message, .query-state-stale {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--wes-space-3);
  margin: var(--wes-space-3) 0;
  padding: var(--wes-space-3) var(--wes-space-4);
  border: 1px solid var(--wes-color-border-soft);
  border-radius: var(--wes-radius);
  background: var(--wes-color-surface);
}
.query-state-message p, .query-state-stale p { margin: 0; }
.query-state-error {
  border-color: var(--wes-color-danger);
  background: var(--wes-color-danger-soft);
}
.query-state-error button, .query-state-stale button { min-height: 44px; }
</style>
