<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import type { InboundDetail } from "./inbound";
import { executeInbound, inboundReceiptError, mayExecuteInbound,
  type InboundExecutionCommand, type InboundExecutionKind, type InboundExecutionReceipt } from "./inbound-execution";

const props = defineProps<{ order: InboundDetail; kind: InboundExecutionKind }>();
const emit = defineEmits<{ attempted: []; read: [id: string]; close: [] }>();
const id = useId();
const order = shallowRef<InboundDetail | undefined>({ ...props.order, lines: props.order.lines.map(line => ({ ...line })),
  receipts: props.order.receipts.map(receipt => ({ ...receipt })) });
const kind = props.kind;
const title = kind === "approval" ? "审核" : "收货";
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const error = ref(""); const uncertain = ref(false); const online = ref(navigator.onLine);
const command = shallowRef<InboundExecutionCommand>(); const receipt = shallowRef<InboundExecutionReceipt>();
const authorized = computed(() => mayExecuteInbound(kind));
const fingerprint = computed(() => JSON.stringify(sessionState.subject));
const form = reactive({ lineId: props.order.lines[0]?.id ?? "", quantity: "", containerId: "", receiptLocationId: "",
  targetLocationId: "", balanceId: "", expectedBalanceVersion: "" });
const resourceInputs = [
  ["containerId", "合法容器 UUID"], ["receiptLocationId", "收货位 UUID"], ["targetLocationId", "目标位 UUID"],
  ["balanceId", "合法收货余额 UUID"], ["expectedBalanceVersion", "原余额版本 UUID"],
] as const;
let controller = new AbortController(); let sequence = 0; let deadline: ReturnType<typeof setTimeout> | undefined;
function cancel() {
  if (deadline !== undefined) clearTimeout(deadline);
  deadline = undefined; ++sequence; controller.abort(); controller = new AbortController();
}
watch([fingerprint, () => props.order.id, () => props.order.version, () => props.kind], () => {
  cancel(); order.value = undefined; command.value = undefined; receipt.value = undefined;
  for (const key of Object.keys(form) as (keyof typeof form)[]) form[key] = "";
  phase.value = "invalidated"; error.value = ""; uncertain.value = false;
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "连接中断，结果不明；恢复后只可手工恢复原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function preview() {
  if (phase.value !== "draft" || !authorized.value || !online.value || !order.value) return;
  if (kind === "approval" ? order.value.state !== 0 : ![1, 2].includes(order.value.state)) {
    error.value = "原单状态不允许本次操作，请关闭并重新读取。"; return;
  }
  const common = { id: order.value.id, operation: crypto.randomUUID() };
  if (kind === "approval") command.value = { ...common, kind, input: { expectedVersion: order.value.version } };
  else {
    const input = { expectedVersion: order.value.version, expectedBalanceVersion: form.expectedBalanceVersion,
      lineId: form.lineId, executionId: crypto.randomUUID(), quantity: form.quantity, containerId: form.containerId,
      receiptLocationId: form.receiptLocationId, targetLocationId: form.targetLocationId, balanceId: form.balanceId };
    error.value = inboundReceiptError(order.value, input); if (error.value) return;
    command.value = { ...common, kind, input };
  }
  phase.value = "preview"; error.value = "";
}
async function submit() {
  if (!authorized.value || !online.value || !command.value
    || !(["preview", "unknown"].includes(phase.value) || phase.value === "rejected" && uncertain.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = fingerprint.value; const request = command.value;
  phase.value = "submitting"; error.value = ""; emit("attempted");
  const current = () => sequence === ticket && scope === fingerprint.value && !signal.aborted;
  deadline = setTimeout(() => {
    if (!current()) return;
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "10 秒内未取得执行确认，结果不明；禁止换键盲发。";
  }, 10000);
  try {
    const result = await executeInbound(request, signal);
    if (current()) { receipt.value = result; uncertain.value = false; phase.value = "confirmed"; }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 405, 409, 422].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof ApiError && [404, 405].includes(failure.status)
      ? "执行入口未开放或单据/资源未找到；请核对独立部署开关及当前权限，不会自动重试。"
      : failure instanceof Error ? failure.message : "执行结果未确认，请核对原操作。";
  } finally { if (ticket === sequence && deadline !== undefined) { clearTimeout(deadline); deadline = undefined; } }
}
</script>

<template>
  <section class="inbound-execution" :aria-labelledby="`${id}-title`" :aria-busy="phase === 'submitting'">
    <h2 :id="`${id}-title`">入库{{ title }}确认</h2>
    <p class="notice">审核不入账；收货仅登记真实收货位库存，不控制设备。异址收货仍待运输，不能视为目标到达。
      未确认不可换键；导航或刷新会丢失内存上下文，请记录原操作 UUID 和全部输入。</p>
    <p v-if="!online" role="status">当前离线，禁止提交、不排队，恢复后不会自动重试。</p>
    <p v-if="phase === 'invalidated' || !authorized" role="alert">当前主体或原单已改变，或缺少完整字段权限，请关闭并重新准备。</p>
    <form v-else-if="phase === 'draft'" @submit.prevent="preview">
      <fieldset :disabled="!online">
        <legend>{{ title }}输入</legend>
        <template v-if="kind === 'receipts'">
          <label :for="`${id}-line`">真实计划行</label>
          <select :id="`${id}-line`" v-model="form.lineId" name="lineId">
            <option v-for="line in order?.lines" :key="line.id" :value="line.id">{{ line.id }} · 待收 {{ line.remaining }}</option>
          </select>
          <label :for="`${id}-quantity`">本次收货数量</label>
          <input :id="`${id}-quantity`" v-model="form.quantity" name="quantity" inputmode="decimal" maxlength="16" autocomplete="off" />
          <template v-for="[key, label] in resourceInputs" :key="key">
            <label :for="`${id}-${key}`">{{ label }}</label>
            <input :id="`${id}-${key}`" v-model="form[key]" :name="key" maxlength="36" autocomplete="off" spellcheck="false" />
          </template>
        </template>
        <p v-else>仅审核已登记合法物料/单位的当前草稿；启用状态及当前权限由服务端再次验证。</p>
        <button type="submit">预览入库{{ title }}</button>
      </fieldset>
    </form>
    <div v-if="order && command" data-test="inbound-execution-preview">
      <h3>原单与全部输入预览</h3>
      <dl><dt>原单号</dt><dd>{{ order.code }}</dd><dt>原单 UUID</dt><dd>{{ command.id }}</dd>
        <dt>仓库 UUID</dt><dd>{{ order.warehouseId }}</dd><dt>操作类型</dt><dd>{{ title }}</dd>
        <dt>操作 UUID</dt><dd>{{ command.operation }}</dd></dl>
      <ol><li v-for="line in order.lines" :key="line.id">
        行 {{ line.id }}；物料 {{ line.itemId }}；基础单位 {{ line.unitId }}；批次 {{ line.batch || "无批次" }}；
        精度 {{ line.precision }}；计划 {{ line.planned }}；已收 {{ line.received }}；待收 {{ line.remaining }}
      </li></ol>
      <dl><dt>原单期望版本</dt><dd>{{ command.input.expectedVersion }}</dd>
        <template v-if="command.kind === 'receipts'">
          <dt>原余额版本</dt><dd>{{ command.input.expectedBalanceVersion }}</dd>
          <dt>本次计划行 UUID</dt><dd>{{ command.input.lineId }}</dd>
          <dt>收货执行 UUID</dt><dd>{{ command.input.executionId }}</dd>
          <dt>本次收货数量</dt><dd>{{ command.input.quantity }}</dd>
          <dt>合法容器 UUID</dt><dd>{{ command.input.containerId }}</dd>
          <dt>收货位 UUID</dt><dd>{{ command.input.receiptLocationId }}</dd>
          <dt>目标位 UUID</dt><dd>{{ command.input.targetLocationId }}</dd>
          <dt>合法收货余额 UUID</dt><dd>{{ command.input.balanceId }}</dd>
        </template>
      </dl>
      <p v-if="command.kind === 'receipts' && command.input.receiptLocationId.toLowerCase() !== command.input.targetLocationId.toLowerCase()"
        class="notice">异址收货：只入收货位账，仍待运输和有效最终到达，不是上架完成。</p>
      <button v-if="phase === 'preview'" type="button" data-test="confirm-inbound-execution"
        :disabled="!online || !authorized" @click="submit">确认入库{{ title }}</button>
      <button v-if="phase === 'unknown' || phase === 'rejected' && uncertain" type="button" data-test="retry-inbound-execution"
        :disabled="!online || !authorized" @click="submit">手工恢复原操作</button>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="phase === 'unknown'" role="status">结果不明，不代表未执行；原类型、单据、键和全部输入已冻结。</p>
    <p v-if="phase === 'rejected'" role="status">本次请求被拒绝。{{ uncertain ? "先前不明结果仍需查证，禁止换键盲发。" : "未取得执行确认，请关闭后重新读取。" }}</p>
    <p v-if="receipt" role="status">已确认原执行身份：{{ receipt.id }}。原执行版本：{{ receipt.version }}；不是当前版本或目标到达证明。</p>
    <button v-if="receipt && command" type="button" data-test="read-executed-inbound" :disabled="!online || !authorized"
      @click="emit('read', command.id)">读取入库单当前详情</button>
    <button type="button" :disabled="phase === 'submitting' || uncertain" @click="emit('close')">关闭执行面板</button>
  </section>
</template>

<style scoped>
.inbound-execution { min-width: 0; margin-block: var(--wes-space-6); overflow-wrap: anywhere; }
form, fieldset { display: grid; gap: var(--wes-space-2); min-width: 0; }
fieldset { padding: var(--wes-space-3); }
input, select, button { max-width: 100%; min-width: 0; }
input, select { width: 100%; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; } ol { padding-inline-start: var(--wes-space-5); }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
