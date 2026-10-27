<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { StockLedgerResponseError, mayReadStockLedger, queryStockLedger, type StockLedgerRow } from "./stock-ledger";

const id = useId();
const dimensions = [
  { key: "warehouseId", label: "仓库 UUID" }, { key: "locationId", label: "账面库位 UUID" },
  { key: "itemId", label: "物料 UUID" }, { key: "containerId", label: "容器 UUID" },
  { key: "sourceDocumentId", label: "来源单据 UUID" }, { key: "correlationId", label: "关联 UUID" },
] as const;
const filters = reactive({ warehouseId: "", locationId: "", itemId: "", containerId: "", sourceDocumentId: "",
  correlationId: "", batchMode: "all", batch: "", kind: "", from: "", before: "" });
const kindNames = ["收货 Receipt", "发运 Shipment", "移库 Transfer"] as const;
const readable = computed(mayReadStockLedger);
const scope = computed(() => JSON.stringify(sessionState.subject));
const rows = shallowRef<StockLedgerRow[]>([]);
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
    const result = await queryStockLedger(offset.value, limit.value, {
      warehouseId: filters.warehouseId || undefined, locationId: filters.locationId || undefined,
      itemId: filters.itemId || undefined, containerId: filters.containerId || undefined,
      sourceDocumentId: filters.sourceDocumentId || undefined, correlationId: filters.correlationId || undefined,
      batch: filters.batchMode === "exact" ? filters.batch : undefined, kind: filters.kind === "" ? undefined : Number(filters.kind),
      from: filters.from || undefined, before: filters.before || undefined,
    }, signal);
    if (current()) { rows.value = result; fresh.value = true; loaded.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof StockLedgerResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      rows.value = []; loaded.value = false; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "流水读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function move(nextOffset: number, nextLimit: number) {
  if (!online.value || !readable.value || busy.value) return;
  clear(); offset.value = nextOffset; limit.value = nextLimit; void load();
}
</script>

<template>
  <section class="ledger-page" aria-labelledby="stock-ledger-title">
    <h1 id="stock-ledger-title">库存流水</h1>
    <p class="notice">WMS 不可变账本按发生时间、流水 UUID 升序显示；正量收入、负量支出，前后余额为记账当时值。
      各行基础单位精确显示，不跨单位求和。本页不是固定核对快照，不提供编辑、删除、改账或导出；来源身份不是物理完成证据。</p>
    <p v-if="!readable" role="alert">缺少流水 All 范围及完整字段读取权限，不能查询。</p>
    <p v-if="!online" class="notice" role="status">当前离线，已加载流水仅为陈旧参考；恢复连接后请手工重新读取。</p>
    <form class="ledger-filters" @submit.prevent="load">
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
      <div><label :for="`${id}-kind`">记账类型</label>
        <select :id="`${id}-kind`" v-model="filters.kind" name="kind">
          <option value="">全部类型</option><option v-for="(name, kind) in kindNames" :key="kind" :value="String(kind)">{{ name }}</option>
        </select></div>
      <div><label :for="`${id}-from`">起始 UTC（包含）</label>
        <input :id="`${id}-from`" v-model="filters.from" name="from" :aria-describedby="`${id}-time-help`" placeholder="2026-10-27T00:00:00.000001Z" autocomplete="off" /></div>
      <div><label :for="`${id}-before`">结束 UTC（不包含）</label>
        <input :id="`${id}-before`" v-model="filters.before" name="before" :aria-describedby="`${id}-time-help`" placeholder="2026-10-28T00:00:00Z" autocomplete="off" /></div>
      <p :id="`${id}-time-help`">时间可留空；使用 UTC ISO 格式，最多微秒精度，不自动转换时区或舍入。</p>
      <button type="submit" :disabled="!readable || !online || busy">读取当前流水</button>
    </form>
    <p v-if="error && rows.length" class="error" role="alert">{{ error }}</p>
    <QueryState label="当前库存流水" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧流水，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>{{ loaded ? "当前授权筛选没有流水，不表示没有物理搬运或其他仓库无记录。" : "尚未查询；请显式读取当前授权的库存流水。" }}</p></template>
      <ul v-if="rows.length" class="ledger-rows" data-test="stock-ledger-rows">
        <li v-for="row in rows" :key="row.id">
          <h2>{{ kindNames[row.kind] }} · {{ row.quantity }}</h2>
          <dl>
            <dt>流水 UUID</dt><dd>{{ row.id }}</dd><dt>记账 UUID</dt><dd>{{ row.postingId }}</dd>
            <dt>余额 UUID</dt><dd>{{ row.balanceId }}</dd><dt>仓库 UUID</dt><dd>{{ row.warehouseId }}</dd>
            <dt>账面库位 UUID</dt><dd>{{ row.locationId }}</dd><dt>物料 UUID</dt><dd>{{ row.itemId }}</dd>
            <dt>批次</dt><dd>{{ row.batch || "无批次" }}</dd><dt>容器 UUID</dt><dd>{{ row.containerId }}</dd>
            <dt>基础单位 UUID</dt><dd>{{ row.unitId }}</dd><dt>单位精度</dt><dd>{{ row.precision }}</dd>
            <dt>记账前余额</dt><dd>{{ row.before }}</dd><dt>记账后余额</dt><dd>{{ row.after }}</dd>
            <dt>来源单据 UUID</dt><dd>{{ row.sourceDocumentId }}</dd><dt>来源行 UUID</dt><dd>{{ row.sourceLineId }}</dd>
            <dt>执行单元 UUID</dt><dd>{{ row.executionUnitId }}</dd><dt>关联 UUID</dt><dd>{{ row.correlationId }}</dd>
            <dt>发生时间 UTC</dt><dd><time :datetime="row.occurredAt">{{ row.occurredAt }}</time></dd>
          </dl>
        </li>
      </ul>
    </QueryState>
    <RealtimePager label="流水分页" :offset="offset" :limit="limit" :count="rows.length"
      :busy="busy || !online || !readable" :failed="!!error || !fresh || offset >= 10000" @change="move" />
  </section>
</template>

<style scoped>
.ledger-page { min-width: 0; }
.ledger-filters { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); margin-block: var(--wes-space-4); }
.ledger-filters > div { flex: 1 1 16rem; min-width: 0; }
.ledger-filters input, .ledger-filters select { width: 100%; }
.ledger-rows { list-style: none; padding: 0; display: grid; gap: var(--wes-space-3); }
.ledger-rows li { min-width: 0; overflow-wrap: anywhere; padding: var(--wes-space-4); background: var(--wes-color-surface); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; overflow-wrap: anywhere; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
