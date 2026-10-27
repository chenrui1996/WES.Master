<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { InventoryResponseError, mayReadInventory, queryInventory, type InventoryRow } from "./inventory";

const id = useId();
const dimensions = [
  { key: "warehouseId", label: "仓库 UUID" }, { key: "locationId", label: "账面库位 UUID" },
  { key: "itemId", label: "物料 UUID" }, { key: "containerId", label: "容器 UUID" },
] as const;
const filters = reactive({ warehouseId: "", locationId: "", itemId: "", containerId: "", batchMode: "all", batch: "" });
const readable = computed(mayReadInventory);
const scope = computed(() => JSON.stringify(sessionState.subject));
const rows = shallowRef<InventoryRow[]>([]);
const offset = ref(0); const limit = ref(25);
const busy = ref(false); const fresh = ref(false); const loaded = ref(false); const error = ref("");
const loadedAt = ref(""); const online = ref(navigator.onLine);
const state = computed(() => busy.value ? rows.value.length ? "stale" : "loading"
  : rows.value.length ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); rows.value = []; loaded.value = false; loadedAt.value = ""; error.value = ""; }
watch([scope, () => JSON.stringify(filters)], () => { clear(); offset.value = 0; }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value) return;
  cancel(); busy.value = true; error.value = "";
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await queryInventory(offset.value, limit.value, {
      warehouseId: filters.warehouseId || undefined, locationId: filters.locationId || undefined,
      itemId: filters.itemId || undefined, containerId: filters.containerId || undefined,
      batch: filters.batchMode === "exact" ? filters.batch : undefined,
    }, signal);
    if (current()) { rows.value = result; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof InventoryResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      rows.value = []; loaded.value = false; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "库存读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function move(nextOffset: number, nextLimit: number) {
  if (!online.value || !readable.value || busy.value) return;
  clear(); offset.value = nextOffset; limit.value = nextLimit; void load();
}
</script>

<template>
  <section class="inventory-page" aria-labelledby="inventory-title">
    <h1 id="inventory-title">库存与保护量</h1>
    <p class="notice">WMS 账面定义不是实物位置或执行保管权；Available 只是读取时观察值，不是分配许可。
      数量按各行基础单位精确显示，不跨单位求和。Reserved / InTransit / Unknown 可重叠，Protected 按份额并集只扣一次。
      本页实时查询不是固定核对快照；不提供直接改余额或导出。</p>
    <p v-if="!readable" role="alert">缺少库存 All 范围及完整字段读取权限，不能查询。</p>
    <p v-if="!online" class="notice" role="status">当前离线，已加载库存仅为陈旧参考；恢复连接后请手工重新读取。</p>
    <form class="inventory-filters" @submit.prevent="load">
      <div v-for="dimension in dimensions" :key="dimension.key">
        <label :for="`${id}-${dimension.key}`">{{ dimension.label }}</label>
        <input :id="`${id}-${dimension.key}`" v-model="filters[dimension.key]" :name="dimension.key" maxlength="36" autocomplete="off" spellcheck="false" />
      </div>
      <div><label :for="`${id}-batch-mode`">批次筛选</label>
        <select :id="`${id}-batch-mode`" v-model="filters.batchMode" name="batchMode">
          <option value="all">全部批次</option><option value="exact">精确批次（空串表示无批次）</option>
        </select></div>
      <div v-if="filters.batchMode === 'exact'"><label :for="`${id}-batch`">精确批次</label>
        <input :id="`${id}-batch`" v-model="filters.batch" name="batch" maxlength="128" autocomplete="off" /></div>
      <button type="submit" :disabled="!readable || !online || busy">读取当前库存</button>
    </form>
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="当前库存" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧库存，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>{{ loaded ? "当前授权筛选没有库存维度，不表示实物为空或其他仓库无库存。" : "尚未查询；请显式读取当前授权的库存维度。" }}</p></template>
      <ul v-if="rows.length" class="inventory-rows" data-test="inventory-rows">
        <li v-for="row in rows" :key="row.id">
          <h2>库存维度</h2>
          <dl>
            <dt>余额 UUID</dt><dd>{{ row.id }}</dd>
            <dt>仓库 UUID</dt><dd>{{ row.warehouseId }}</dd>
            <dt>账面库位 UUID</dt><dd>{{ row.locationId }}</dd>
            <dt>物料 UUID</dt><dd>{{ row.itemId }}</dd>
            <dt>批次</dt><dd>{{ row.batch || "无批次" }}</dd>
            <dt>容器 UUID</dt><dd>{{ row.containerId }}</dd>
            <dt>基础单位 UUID</dt><dd>{{ row.unitId }}</dd>
            <dt>单位精度</dt><dd>{{ row.precision }}</dd>
            <dt>账面 Quantity</dt><dd>{{ row.quantity }}</dd>
            <dt>预留 Reserved</dt><dd>{{ row.reserved }}</dd>
            <dt>在途 InTransit</dt><dd>{{ row.inTransit }}</dd>
            <dt>待核对 Unknown</dt><dd>{{ row.unknown }}</dd>
            <dt>保护并集 Protected</dt><dd>{{ row.protected }}</dd>
            <dt>可用 Available</dt><dd>{{ row.available }}</dd>
            <dt>版本</dt><dd>{{ row.version }}</dd>
          </dl>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="库存分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !readable" :failed="!!error || !fresh || offset >= 10000" @change="move" />
  </section>
</template>

<style scoped>
.inventory-page { min-width: 0; }
.inventory-filters { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); margin-block: var(--wes-space-4); }
.inventory-filters > div { flex: 1 1 16rem; min-width: 0; }
.inventory-filters input, .inventory-filters select { width: 100%; }
.inventory-rows { list-style: none; padding: 0; display: grid; gap: var(--wes-space-3); }
.inventory-rows li { min-width: 0; padding: var(--wes-space-4); background: var(--wes-color-surface); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; overflow-wrap: anywhere; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
