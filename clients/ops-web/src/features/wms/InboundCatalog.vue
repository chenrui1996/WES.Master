<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { InboundResponseError, queryInbound, type InboundSummary } from "./inbound";

const props = defineProps<{ readable: boolean; blocked: boolean }>();
const emit = defineEmits<{ select: [id: string]; denied: [] }>();
const id = useId();
const code = ref(""); const warehouse = ref(""); const status = ref("");
const rows = shallowRef<InboundSummary[]>([]);
const offset = ref(0); const limit = ref(25);
const busy = ref(false); const fresh = ref(false); const loaded = ref(false); const error = ref("");
const loadedAt = ref(""); const online = ref(navigator.onLine);
const scope = computed(() => JSON.stringify(sessionState.subject));
const state = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
  : rows.value.length ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
const statuses = ["草稿", "已审核", "执行中（部分收货或待上架）", "已完成"] as const;
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); rows.value = []; loaded.value = false; error.value = ""; loadedAt.value = ""; }
watch([code, warehouse, status, scope, () => props.readable], () => { clear(); offset.value = 0; }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!props.readable || props.blocked || !online.value || busy.value) return;
  cancel(); busy.value = true; error.value = "";
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await queryInbound(offset.value, limit.value, { code: code.value || undefined,
      warehouseId: warehouse.value || undefined, state: status.value === "" ? undefined : Number(status.value) }, signal);
    if (current()) { rows.value = result; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof InboundResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      rows.value = []; loadedAt.value = ""; loaded.value = false;
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) emit("denied");
    }
    error.value = failure instanceof Error ? failure.message : "入库目录读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function move(nextOffset: number, nextLimit: number) {
  if (!online.value || !props.readable || props.blocked || busy.value) return;
  clear(); offset.value = nextOffset; limit.value = nextLimit; void load();
}
function select(id: string) {
  if (fresh.value && online.value && props.readable && !props.blocked && !busy.value && rows.value.some(row => row.id === id)) emit("select", id);
}
</script>

<template>
  <section class="inbound-catalog" aria-labelledby="inbound-catalog-title">
    <h2 id="inbound-catalog-title">入库实时目录</h2>
    <form class="catalog-filters" @submit.prevent="load">
      <div><label :for="`${id}-code`">精确入库单号</label>
        <input :id="`${id}-code`" v-model="code" name="inboundCode" maxlength="64" autocomplete="off" /></div>
      <div><label :for="`${id}-warehouse`">入库仓库 UUID 筛选</label>
        <input :id="`${id}-warehouse`" v-model="warehouse" name="inboundWarehouse" maxlength="36" autocomplete="off" /></div>
      <div><label :for="`${id}-status`">入库状态筛选</label>
        <select :id="`${id}-status`" v-model="status" name="inboundState">
          <option value="">全部授权状态</option>
          <option v-for="(label, value) in statuses" :key="value" :value="String(value)">{{ label }}</option>
        </select></div>
      <button type="submit" :disabled="busy || props.blocked || !online || !props.readable">读取入库目录</button>
    </form>
    <p v-if="!online" class="notice" role="status">目录离线，已加载行不可选择；恢复后请手工重新读取。</p>
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="当前授权入库目录" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧目录，不可选择；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>{{ loaded ? "当前授权范围及筛选下没有入库单，不表示其他范围没有单据。" : "尚未查询目录；筛选后显式读取，不自动加载。" }}</p></template>
      <ul v-if="rows.length" class="catalog-rows" data-test="inbound-catalog">
        <li v-for="row in rows" :key="row.id">
          <h3>{{ row.code }}</h3>
          <p>{{ statuses[row.state] }}</p><p>{{ row.id }}</p><p>仓库：{{ row.warehouseId }}</p>
          <button type="button" :disabled="!fresh || busy || props.blocked || !online || !props.readable"
            :aria-label="`读取入库单 ${row.code} 当前详情`" @click="select(row.id)">选择并重读当前详情</button>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="入库目录分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || props.blocked || !online || !props.readable" :failed="!fresh || offset + limit > 10000" @change="move" />
  </section>
</template>

<style scoped>
.inbound-catalog { margin-block: var(--wes-space-6); min-width: 0; }
.catalog-filters { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); }
.catalog-filters > div { display: grid; gap: var(--wes-space-2); flex: 1 1 12rem; min-width: 0; }
.catalog-filters input, .catalog-filters select { width: 100%; min-width: 0; }
.catalog-rows { list-style: none; padding: 0; display: grid; gap: var(--wes-space-3); }
.catalog-rows li { min-width: 0; overflow-wrap: anywhere; padding: var(--wes-space-4); background: var(--wes-color-surface); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
.catalog-rows button { max-width: 100%; white-space: normal; }
</style>
