<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import OutboundConfirmation from "./OutboundConfirmation.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { validIdentity } from "./master-data";
import { mayExecuteOutbound, mayReadOutbound, outboundActionError, outboundInputError, outboundScaled, outboundStates, outboundUnshipped,
  outboundTitles, OutboundResponseError, readOutbound, type OutboundCommand, type OutboundDetail, type OutboundKind } from "./outbound";

const props = defineProps<{ id?: string; picking?: boolean }>();
const inputId = useId(); const orderId = ref(props.id ?? "");
const readable = computed(mayReadOutbound); const creatable = computed(() => mayExecuteOutbound("create"));
const scope = computed(() => JSON.stringify(sessionState.subject));
const record = shallowRef<OutboundDetail>(); const command = shallowRef<OutboundCommand>(); const summary = ref("");
const busy = ref(false); const fresh = ref(false); const error = ref(""); const loadedAt = ref(""); const online = ref(navigator.onLine);
const creating = ref(false); const selectedKind = ref<Exclude<OutboundKind, "create">>();
const draft = reactive({ code: "", warehouseId: "", lines: [{ id: crypto.randomUUID(), balanceId: "", quantity: "", branch: 0 as 0 | 1 }] });
const action = reactive({ lineId: "", pickedQuantity: "0", shortageQuantity: "0", quantity: "", reason: "",
  balances: [] as { balanceId: string; expectedVersion: string }[] });
const kinds: Exclude<OutboundKind, "create">[] = ["approval", "picking", "shortage-release", "shipment", "cancellation", "close-remainder"];
const allowedKinds = computed(() => kinds.filter(kind => {
  const order = record.value;
  if (!order || !mayExecuteOutbound(kind) || order.state >= 3) return false;
  if (kind === "approval") return order.state === 0;
  if (kind === "cancellation") return !order.lines.some(line => outboundScaled(line.pickedQuantity) > 0n);
  if (order.state === 0) return false;
  if (kind === "shipment") return order.lines.some(line => line.branch === 0 && outboundScaled(line.pickedQuantity) > 0n && outboundScaled(line.shippedQuantity) === 0n);
  if (kind === "shortage-release") return order.lines.some(line => outboundScaled(line.shortageQuantity) > outboundScaled(line.releasedQuantity));
  if (kind === "close-remainder") return order.lines.some(line => outboundScaled(line.shippedQuantity) > 0n);
  return true;
}));
const state = computed(() => busy.value ? record.value ? "stale" : "loading"
  : record.value ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); record.value = undefined; loadedAt.value = ""; error.value = ""; selectedKind.value = undefined; }
watch(() => props.id, value => { command.value = undefined; creating.value = false; orderId.value = value ?? ""; clear(); });
watch(() => props.picking, () => { command.value = undefined; creating.value = false; clear(); });
watch(orderId, clear, { flush: "sync" });
watch(scope, () => {
  clear(); creating.value = false; draft.code = ""; draft.warehouseId = ""; draft.lines = [];
  Object.assign(action, { lineId: "", pickedQuantity: "0", shortageQuantity: "0", quantity: "", reason: "", balances: [] });
}, { flush: "sync" });
watch(() => draft.warehouseId, () => { draft.lines = [{ id: crypto.randomUUID(), balanceId: "", quantity: "", branch: 0 }]; });
watch(() => action.lineId, () => { action.quantity = ""; action.pickedQuantity = "0"; action.shortageQuantity = "0"; action.reason = ""; });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value || command.value) return;
  cancel(); busy.value = true; error.value = ""; selectedKind.value = undefined;
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await readOutbound(orderId.value, signal);
    if (current()) { record.value = result; fresh.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof OutboundResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      record.value = undefined; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "出库读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function prepare(kind: Exclude<OutboundKind, "create">) {
  if (!online.value || !fresh.value || busy.value || !allowedKinds.value.includes(kind) || !record.value) return;
  selectedKind.value = kind; error.value = "";
  Object.assign(action, { lineId: record.value.lines[0]?.id ?? "", quantity: "", pickedQuantity: "0", shortageQuantity: "0", reason: "",
    balances: record.value.lines.map(line => ({ balanceId: line.balanceId, expectedVersion: "" })) });
}
function previewDraft() {
  if (!creatable.value || !online.value || command.value) return;
  const request: OutboundCommand = { kind: "create", operation: crypto.randomUUID(),
    input: { code: draft.code, warehouseId: draft.warehouseId, lines: draft.lines.map(line => ({ ...line })) } };
  error.value = outboundInputError(request); if (error.value) return;
  command.value = request; summary.value = `新草稿 ${draft.code}；仓库 ${draft.warehouseId}；${draft.lines.length}行。不预留、不扣账。`;
}
function addDraftLine() {
  if (draft.lines.length < 100 && validIdentity(draft.warehouseId))
    draft.lines.push({ id: crypto.randomUUID(), balanceId: "", quantity: "", branch: 0 });
}
function previewAction() {
  const order = record.value; const kind = selectedKind.value;
  if (!order || !kind || !fresh.value || busy.value || command.value || !online.value || !mayExecuteOutbound(kind)) return;
  const common = { id: order.id, operation: crypto.randomUUID() }; const expectedVersion = order.version;
  let request: OutboundCommand;
  switch (kind) {
    case "approval": request = { ...common, kind, input: { expectedVersion, balances: action.balances.map(row => ({ ...row })) } }; break;
    case "picking": request = { ...common, kind, input: { expectedVersion, lineId: action.lineId, pickedQuantity: action.pickedQuantity,
      shortageQuantity: action.shortageQuantity, reason: action.reason } }; break;
    case "shortage-release": request = { ...common, kind, input: { expectedVersion, lineId: action.lineId, quantity: action.quantity, reason: action.reason } }; break;
    case "shipment": request = { ...common, kind, input: { expectedVersion, lineId: action.lineId, quantity: action.quantity } }; break;
    default: request = { ...common, kind, input: { expectedVersion, reason: action.reason } };
  }
  error.value = outboundActionError(order, request); if (error.value) return;
  command.value = request;
  summary.value = `原出库单 ${order.code}；仓库 ${order.warehouseId}；状态 ${outboundStates[order.state]}。`;
}
function attempted() { cancel(); selectedKind.value = undefined; }
function readWritten(id: string) { command.value = undefined; creating.value = false; orderId.value = id; clear(); void load(); }
function closeConfirmation() { command.value = undefined; creating.value = false; selectedKind.value = undefined; }
</script>

<template>
  <section class="outbound-page" aria-labelledby="outbound-title">
    <h1 id="outbound-title">{{ picking ? "出库拣选与短缺复核" : "出库预留与发运" }}</h1>
    <p class="notice">审核全行原子预留；实拣/短缺不扣库存、不自动释放。短缺须由非原拣选人的受权操作人确认未执行。
      仅无运输分支允许本行一次直发全部实拣；部分发后未发计划默认开放，需显式关闭余量。需运输分支等待运输，不能手工扣账。</p>
    <OutboundConfirmation v-if="command" :command="command" :summary="summary" @attempted="attempted" @read="readWritten" @close="closeConfirmation" />
    <template v-else>
      <p v-if="!readable" role="alert">缺少出库完整字段读取权限，不能查询或执行。</p>
      <p v-if="!online" role="status">当前离线，详情仅为陈旧参考；恢复连接后请手工重新读取。</p>
      <button v-if="creatable && !creating" type="button" :disabled="!online || busy" data-test="prepare-outbound-create"
        @click="creating = true; selectedKind = undefined">准备创建出库草稿</button>
      <form v-if="creating && creatable" class="outbound-form" data-test="outbound-create-form" @submit.prevent="previewDraft">
        <fieldset :disabled="!online || busy">
          <legend>多行出库草稿 · 来源必须为已登记合法余额</legend>
          <label :for="`${inputId}-code`">出库单号</label><input :id="`${inputId}-code`" v-model="draft.code" name="code" maxlength="64" autocomplete="off" />
          <label :for="`${inputId}-warehouse`">仓库 UUID</label><input :id="`${inputId}-warehouse`" v-model="draft.warehouseId" name="warehouseId" maxlength="36" autocomplete="off" spellcheck="false" />
          <fieldset v-for="(line, index) in draft.lines" :key="line.id">
            <legend>计划行 {{ index + 1 }}</legend><p>行 UUID：{{ line.id }}</p>
            <label :for="`${inputId}-balance-${line.id}`">合法来源余额 UUID</label><input :id="`${inputId}-balance-${line.id}`" v-model="line.balanceId" name="balanceId" maxlength="36" autocomplete="off" spellcheck="false" />
            <label :for="`${inputId}-quantity-${line.id}`">计划数量</label><input :id="`${inputId}-quantity-${line.id}`" v-model="line.quantity" name="plannedQuantity" inputmode="decimal" maxlength="16" autocomplete="off" />
            <label :for="`${inputId}-branch-${line.id}`">执行分支</label><select :id="`${inputId}-branch-${line.id}`" v-model="line.branch" name="branch"><option :value="0">无运输直发</option><option :value="1">需运输（等待运输，不能手工发运）</option></select>
            <button type="button" :disabled="draft.lines.length === 1" :aria-label="`移除计划行 ${index + 1}`" @click="draft.lines.splice(index, 1)">移除此行</button>
          </fieldset>
          <button type="button" :disabled="draft.lines.length >= 100 || !validIdentity(draft.warehouseId)"
            @click="addDraftLine">添加计划行</button>
          <p>不自动分配或更换来源，不跨单位求和；当前基础政策不含效期/FEFO，不能填假日期。</p>
          <button type="submit">预览创建出库草稿</button><button type="button" @click="creating = false">关闭草稿输入</button>
        </fieldset>
      </form>
      <form class="outbound-query" data-test="outbound-query" @submit.prevent="load">
        <label :for="`${inputId}-order`">出库单 UUID</label><input :id="`${inputId}-order`" v-model="orderId" name="orderId" maxlength="36" autocomplete="off" spellcheck="false" />
        <button type="submit" :disabled="!readable || !online || busy">读取当前出库单</button>
      </form>
      <p v-if="error" role="alert">{{ error }}</p>
      <QueryState label="出库详情" :state="state" :busy="busy" :error-message="error"
        :stale-message="`陈旧详情，仅供参考；上次成功读取：${loadedAt || '未确认'}`" @retry="load">
        <template #empty><p>尚未查询。请显式读取当前授权的出库单；未找到不代表空单或已发运。</p></template>
        <article v-if="record" data-test="outbound-detail">
          <h2>{{ record.code }}</h2>
          <dl><dt>出库单 UUID</dt><dd>{{ record.id }}</dd><dt>仓库 UUID</dt><dd>{{ record.warehouseId }}</dd>
            <dt>状态</dt><dd>{{ outboundStates[record.state] }}</dd><dt>版本</dt><dd>{{ record.version }}</dd><dt>创建时间（UTC）</dt><dd>{{ record.createdAt }}</dd></dl>
          <ul class="outbound-lines"><li v-for="line in record.lines" :key="line.id">
            <h3>计划行 {{ line.id }}</h3>
            <dl><dt>来源余额 / 执行 UUID</dt><dd>{{ line.balanceId }} / {{ line.executionId }}</dd>
              <dt>物料 / 单位 UUID</dt><dd>{{ line.itemId }} / {{ line.unitId }}</dd><dt>批次 / 精度</dt><dd>{{ line.batch || "无批次" }} / {{ line.precision }}</dd>
              <dt>源位 / 容器 UUID</dt><dd>{{ line.locationId }} / {{ line.containerId }}</dd><dt>分支</dt><dd>{{ line.branch === 0 ? "无运输直发" : "等待运输，不能手工发运" }}</dd>
              <dt>计划 / 当前预留</dt><dd>{{ line.plannedQuantity }} / {{ line.reservedQuantity }}</dd><dt>实拣 / 短缺</dt><dd>{{ line.pickedQuantity }} / {{ line.shortageQuantity }}</dd>
              <dt>已释放 / 已发</dt><dd>{{ line.releasedQuantity }} / {{ line.shippedQuantity }}</dd>
              <dt>计划未发（非可执行量）</dt><dd>{{ outboundUnshipped(line) }}</dd>
            </dl>
          </li></ul>
          <div class="outbound-actions" v-if="!creating">
            <button v-for="kind in allowedKinds" :key="kind" type="button" :data-test="`prepare-outbound-${kind}`"
              :disabled="!online || busy || !fresh" @click="prepare(kind)">准备{{ outboundTitles[kind] }}</button>
          </div>
          <form v-if="selectedKind && !creating" data-test="outbound-action-form" @submit.prevent="previewAction">
            <fieldset :disabled="!online || busy || !fresh">
              <legend>{{ outboundTitles[selectedKind] }}输入</legend>
              <template v-if="selectedKind === 'approval'">
                <p>须核对全部来源余额当前版本；一行不足将整笔拒绝，不产生有效部分预留。</p>
                <div v-for="row in action.balances" :key="row.balanceId">
                  <label :for="`${inputId}-version-${row.balanceId}`">来源余额 {{ row.balanceId }} 原版本 UUID</label>
                  <input :id="`${inputId}-version-${row.balanceId}`" v-model="row.expectedVersion" name="expectedBalanceVersion" maxlength="36" autocomplete="off" spellcheck="false" />
                </div>
              </template>
              <template v-if="['picking', 'shortage-release', 'shipment'].includes(selectedKind)">
                <label :for="`${inputId}-line`">真实计划行</label><select :id="`${inputId}-line`" v-model="action.lineId" name="lineId">
                  <option v-for="line in record.lines" :key="line.id" :value="line.id" :disabled="selectedKind === 'shipment' && line.branch !== 0">{{ line.id }} · {{ line.branch === 0 ? "直发" : "需运输" }}</option>
                </select>
              </template>
              <template v-if="selectedKind === 'picking'">
                <label :for="`${inputId}-picked`">本次新增实拣数量</label><input :id="`${inputId}-picked`" v-model="action.pickedQuantity" name="pickedQuantity" inputmode="decimal" maxlength="16" />
                <label :for="`${inputId}-short`">本次新增短缺数量</label><input :id="`${inputId}-short`" v-model="action.shortageQuantity" name="shortageQuantity" inputmode="decimal" maxlength="16" />
              </template>
              <template v-if="selectedKind === 'shipment' || selectedKind === 'shortage-release'">
                <label :for="`${inputId}-action-quantity`">{{ selectedKind === "shipment" ? "本行一次直发数量" : "确认未执行的短缺释放数量" }}</label>
                <input :id="`${inputId}-action-quantity`" v-model="action.quantity" name="quantity" inputmode="decimal" maxlength="16" />
              </template>
              <template v-if="selectedKind !== 'approval' && selectedKind !== 'shipment'">
                <label :for="`${inputId}-reason`">事实说明 / 原因</label><textarea :id="`${inputId}-reason`" v-model="action.reason" name="reason" maxlength="512" />
              </template>
              <p v-if="selectedKind === 'shortage-release'">仅确认未执行部分；服务端核对非原拣选人和当前保护，不是调整库存。</p>
              <p v-if="selectedKind === 'close-remainder'">必须先处理实际拣选/未决保护；关闭只记录未发余量，不伪造Shipment。</p>
              <button type="submit">预览{{ outboundTitles[selectedKind] }}</button><button type="button" @click="selectedKind = undefined">关闭动作输入</button>
            </fieldset>
          </form>
        </article>
      </QueryState>
    </template>
  </section>
</template>

<style scoped>
.outbound-page, article, form, fieldset { min-width: 0; overflow-wrap: anywhere; }
form, fieldset { display: grid; gap: var(--wes-space-2); margin-block: var(--wes-space-3); }
fieldset { padding: var(--wes-space-3); }
input, select, textarea, button { max-width: 100%; min-width: 0; }
input, select, textarea { width: 100%; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; }
.outbound-lines { padding-inline-start: var(--wes-space-5); }
.outbound-lines li { margin-block: var(--wes-space-4); }
.outbound-actions { display: flex; flex-wrap: wrap; gap: var(--wes-space-2); }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
