<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { MasterDataResponseError, queryMasterData, type MasterData, type ResourceKind } from "./master-data";

const props = defineProps<{ kind: ResourceKind; readable: boolean }>();
const emit = defineEmits<{ select: [id: string]; denied: [] }>();
const formId = useId();
const code = ref(""); const enabled = ref(""); const warehouse = ref("");
const rows = shallowRef<MasterData[]>([]);
const offset = ref(0); const limit = ref(25);
const busy = ref(false); const fresh = ref(false); const loaded = ref(false); const error = ref("");
const loadedAt = ref(""); const online = ref(navigator.onLine);
const scope = computed(() => JSON.stringify(sessionState.subject));
const state = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
  : rows.value.length ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() {
  ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false;
}
function clear() { cancel(); rows.value = []; loaded.value = false; loadedAt.value = ""; error.value = ""; }
function reset() { clear(); offset.value = 0; }
watch([code, enabled, warehouse, scope, () => props.readable], reset, { flush: "sync" });
watch(() => props.kind, () => { code.value = ""; enabled.value = ""; warehouse.value = ""; reset(); }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!props.readable || !online.value || busy.value) return;
  cancel(); busy.value = true; error.value = "";
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const page = await queryMasterData(props.kind, offset.value, limit.value, {
      code: code.value || undefined, enabled: enabled.value === "" ? undefined : enabled.value === "true",
      warehouseId: props.kind === "locations" ? warehouse.value || undefined : undefined,
    }, signal);
    if (current()) { rows.value = page.rows; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof MasterDataResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      rows.value = []; loadedAt.value = ""; loaded.value = false;
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) emit("denied");
    }
    error.value = failure instanceof Error ? failure.message : "目录读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function move(nextOffset: number, nextLimit: number) {
  if (!online.value || !props.readable || busy.value) return;
  clear(); offset.value = nextOffset; limit.value = nextLimit; void load();
}
function select(id: string) {
  if (fresh.value && online.value && props.readable && !busy.value && rows.value.some(row => row.definition.id === id)) emit("select", id);
}
</script>

<template>
  <section class="master-data-catalog" aria-labelledby="master-data-catalog-title">
    <h2 id="master-data-catalog-title">{{ props.kind === "warehouses" ? "仓库" : "库位" }}实时目录</h2>
    <form class="catalog-filters" @submit.prevent="load">
      <div><label :for="`${formId}-code`">精确代码</label>
        <input :id="`${formId}-code`" v-model="code" name="catalogCode" maxlength="64" autocomplete="off" /></div>
      <div><label :for="`${formId}-enabled`">主数据状态筛选</label>
        <select :id="`${formId}-enabled`" v-model="enabled" name="catalogEnabled">
          <option value="">全部授权状态</option><option value="false">禁用</option><option value="true">启用</option>
        </select></div>
      <div v-if="props.kind === 'locations'"><label :for="`${formId}-warehouse`">固定仓库 UUID 筛选</label>
        <input :id="`${formId}-warehouse`" v-model="warehouse" name="catalogWarehouse" maxlength="36" autocomplete="off" /></div>
      <button type="submit" :disabled="busy || !online || !props.readable">读取目录</button>
    </form>
    <p v-if="!online" class="notice" role="status">目录离线：已加载行均为陈旧参考，不可选择；恢复后请手工重新读取。</p>
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="当前授权主数据目录" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧目录，不可选择；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty>
        <p>{{ loaded ? "当前授权范围及筛选下没有目录行，不表示系统无主数据。" : "尚未查询目录；筛选后显式读取，不自动加载。" }}</p>
      </template>
      <ul v-if="rows.length" class="catalog-rows" data-test="master-data-catalog">
        <li v-for="row in rows" :key="row.definition.id">
          <div><strong>{{ row.definition.code }}</strong> · {{ row.definition.name }}</div>
          <p>{{ row.definition.enabled ? "主数据启用（不代表可分配）" : "主数据禁用" }}</p>
          <p class="hint">{{ row.definition.id }}</p>
          <button type="button" data-test="select-master-data" :disabled="!fresh || busy || !online || !props.readable"
            :aria-label="`读取${props.kind === 'warehouses' ? '仓库' : '库位'} ${row.definition.code} 当前详情`"
            @click="select(row.definition.id)">选择并重读当前详情</button>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="主数据目录分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !props.readable" :failed="!fresh || offset + limit > 10000" @change="move" />
  </section>
</template>

<style scoped>
.master-data-catalog { margin-block: var(--wes-space-6); min-width: 0; }
.catalog-filters { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); }
.catalog-filters > div { display: grid; gap: var(--wes-space-2); flex: 1 1 12rem; min-width: 0; max-width: 100%; }
.catalog-filters input, .catalog-filters select { width: 100%; min-width: 0; }
.catalog-rows { list-style: none; padding: 0; display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 22rem), 1fr)); gap: var(--wes-space-3); }
.catalog-rows li { min-width: 0; overflow-wrap: anywhere; padding: var(--wes-space-4); background: var(--wes-color-surface); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
.catalog-rows p { margin-block: var(--wes-space-2); }
.catalog-rows button { max-width: 100%; white-space: normal; }
</style>
