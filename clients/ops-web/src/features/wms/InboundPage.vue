<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import InboundCatalog from "./InboundCatalog.vue";
import InboundDraftCreation from "./InboundDraftCreation.vue";
import { mayCreateInboundDraft, mayReviseInboundDraft } from "./inbound-drafts";
import { ApiError, sessionState } from "../identity/identity-api";
import { InboundResponseError, mayReadInbound, readInbound, type InboundDetail } from "./inbound";

const props = defineProps<{ id?: string }>();
const inputId = useId();
const orderId = ref(props.id ?? "");
const readable = computed(mayReadInbound);
const creatable = computed(mayCreateInboundDraft);
const creating = ref(false);
const editing = shallowRef<InboundDetail>();
const revisable = computed(mayReviseInboundDraft);
const scope = computed(() => JSON.stringify(sessionState.subject));
const record = shallowRef<InboundDetail>();
const busy = ref(false); const fresh = ref(false); const error = ref(""); const loadedAt = ref("");
const online = ref(navigator.onLine);
const catalogKey = ref(0);
const states = ["草稿", "已审核", "执行中（部分收货或待上架）", "已完成"] as const;
const state = computed(() => busy.value ? record.value ? "stale" : "loading"
  : record.value ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); record.value = undefined; error.value = ""; loadedAt.value = ""; }
watch(() => props.id, value => { orderId.value = value ?? ""; clear(); });
watch([scope, orderId], clear, { flush: "sync" });
watch([scope, orderId], () => { editing.value = undefined; }, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value) return;
  cancel(); busy.value = true; error.value = "";
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await readInbound(orderId.value, signal);
    if (current()) { record.value = result; fresh.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof InboundResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      record.value = undefined; loadedAt.value = "";
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) ++catalogKey.value;
    }
    error.value = failure instanceof Error ? failure.message : "入库读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function select(id: string) {
  if (!readable.value || !online.value || busy.value) return;
  orderId.value = id; clear(); void load();
}
function attempted() { clear(); ++catalogKey.value; }
</script>

<template>
  <section class="inbound-page" aria-labelledby="inbound-title">
    <h1 id="inbound-title">入库收货详情</h1>
    <p class="notice">目录与详情数量按各行基础单位精确显示，不跨单位求和。
      收货成功不等于目标到位；异址未关联运输时仍待运输，不显示假上架完成。草稿另需当前写权限及部署开关；审核与收货入口尚未开放。</p>
    <button v-if="creatable && !creating && !editing" type="button" :disabled="!online || busy" @click="creating = true">准备入库草稿</button>
    <InboundDraftCreation v-if="creating" @attempted="attempted" @read="select" @close="creating = false" />
    <InboundDraftCreation v-if="editing" :draft="editing" @attempted="attempted" @read="select" @close="editing = undefined" />
    <p v-if="!readable" role="alert">缺少入库完整字段读取权限，不能查询。</p>
    <p v-if="!online" class="notice" role="status">当前离线，已加载详情仅为陈旧参考；恢复连接后请手工重新读取。</p>
    <form class="inbound-query" @submit.prevent="load">
      <div><label :for="inputId">入库单 UUID</label>
        <input :id="inputId" v-model="orderId" name="orderId" maxlength="36" autocomplete="off" spellcheck="false" /></div>
      <button type="submit" :disabled="!readable || !online || busy">读取当前入库单</button>
    </form>
    <InboundCatalog :key="catalogKey" :readable="readable" :blocked="busy" @select="select" @denied="clear" />
    <p v-if="error && record" class="error" role="alert">{{ error }}</p>
    <QueryState label="入库详情" :state="state" :busy="busy" :error-message="error"
      :stale-message="`陈旧详情，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
      <template #empty><p>尚未查询；请显式读取当前授权的入库单。未找到不代表空单，也不代表收货完成。</p></template>
      <article v-if="record" data-test="inbound-detail">
        <h2>{{ record.code }}</h2>
        <button v-if="revisable && record.state === 0 && !creating && !editing" type="button"
          :disabled="!online || busy || !fresh" @click="editing = record">准备修订入库草稿</button>
        <dl><dt>入库单 UUID</dt><dd>{{ record.id }}</dd>
          <dt>仓库 UUID</dt><dd>{{ record.warehouseId }}</dd>
          <dt>状态</dt><dd>{{ states[record.state] }}</dd>
          <dt>版本</dt><dd>{{ record.version }}</dd><dt>创建时间（UTC）</dt><dd>{{ record.createdAt }}</dd></dl>
        <h2>计划与收货进度</h2>
        <ul class="inbound-cards">
          <li v-for="line in record.lines" :key="line.id">
            <h3>计划行 {{ line.id }}</h3>
            <dl><dt>物料 UUID</dt><dd>{{ line.itemId }}</dd><dt>基础单位 UUID</dt><dd>{{ line.unitId }}</dd>
              <dt>批次</dt><dd>{{ line.batch || "无批次" }}</dd><dt>单位精度</dt><dd>{{ line.precision }}</dd>
              <dt>计划数量</dt><dd>{{ line.planned }}</dd><dt>已收数量</dt><dd>{{ line.received }}</dd>
              <dt>待收数量</dt><dd>{{ line.remaining }}</dd></dl>
          </li>
        </ul>
        <h2>真实收货单元</h2>
        <p v-if="!record.receipts.length">该单尚无收货事实，不代表实物不存在。</p>
        <ul v-else class="inbound-cards">
          <li v-for="unit in record.receipts" :key="unit.id">
            <h3>收货单元 {{ unit.id }}</h3>
            <dl><dt>计划行 UUID</dt><dd>{{ unit.lineId }}</dd><dt>容器 UUID</dt><dd>{{ unit.containerId }}</dd>
              <dt>收货位 UUID</dt><dd>{{ unit.receiptLocationId }}</dd><dt>目标位 UUID</dt><dd>{{ unit.targetLocationId }}</dd>
              <dt>收货数量</dt><dd>{{ unit.quantity }}</dd><dt>上架状态</dt>
              <dd>{{ !unit.requiresTransport ? "同址收货，无需运输" : unit.arrived ? "最终到达已确认" : "待上架，不能视为已到位" }}</dd>
              <dt>运输关联</dt><dd>{{ unit.transportId ?? (unit.requiresTransport ? "尚未关联，待运输" : "无需运输") }}</dd></dl>
          </li>
        </ul>
      </article>
    </QueryState>
  </section>
</template>

<style scoped>
.inbound-page { min-width: 0; }
.inbound-query { display: flex; flex-wrap: wrap; align-items: end; gap: var(--wes-space-3); margin-block: var(--wes-space-4); }
.inbound-query > div { flex: 1 1 20rem; min-width: 0; }
.inbound-query input { width: 100%; }
.inbound-cards { list-style: none; padding: 0; display: grid; gap: var(--wes-space-3); }
.inbound-cards li { min-width: 0; padding: var(--wes-space-4); background: var(--wes-color-surface); border: 1px solid var(--wes-color-border-soft); border-radius: var(--wes-radius); }
h2, h3 { overflow-wrap: anywhere; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; overflow-wrap: anywhere; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
