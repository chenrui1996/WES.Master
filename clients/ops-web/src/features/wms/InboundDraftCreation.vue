<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, useId, watch } from "vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { createInboundDraft, inboundDraftError, mayCreateInboundDraft,
  type InboundDraftCommand, type InboundDraftLine, type InboundDraftReceipt } from "./inbound-drafts";

const emit = defineEmits<{ attempted: []; read: [id: string]; close: [] }>();
const id = useId();
const phase = ref<"draft" | "preview" | "submitting" | "unknown" | "confirmed" | "rejected" | "invalidated">("draft");
const code = ref(""); const warehouse = ref(""); const error = ref(""); const uncertain = ref(false);
const online = ref(navigator.onLine);
const lines = ref<InboundDraftLine[]>([newLine()]);
const command = shallowRef<InboundDraftCommand>();
const receipt = shallowRef<InboundDraftReceipt>();
const authorized = computed(mayCreateInboundDraft);
const fingerprint = computed(() => JSON.stringify(sessionState.subject));
let controller = new AbortController(); let sequence = 0;
let deadline: ReturnType<typeof setTimeout> | undefined;
function newLine(): InboundDraftLine { return { id: crypto.randomUUID(), itemId: "", unitId: "", batch: "", precision: 0, planned: "" }; }
function cancel() {
  if (deadline !== undefined) clearTimeout(deadline);
  deadline = undefined; ++sequence; controller.abort(); controller = new AbortController();
}
watch(fingerprint, () => {
  cancel(); command.value = undefined; receipt.value = undefined; code.value = ""; warehouse.value = ""; lines.value = [];
  phase.value = "invalidated"; error.value = "";
}, { flush: "sync" });
function connection() {
  online.value = navigator.onLine;
  if (!online.value && phase.value === "submitting") {
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "连接中断，创建结果不明；恢复后只可手工恢复原操作。";
  }
}
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
function addLine() { if (phase.value === "draft" && online.value && authorized.value && lines.value.length < 100) lines.value.push(newLine()); }
function removeLine(lineId: string) {
  if (phase.value === "draft" && online.value && authorized.value && lines.value.length > 1)
    lines.value = lines.value.filter(line => line.id !== lineId);
}
function preview() {
  if (phase.value !== "draft" || !authorized.value || !online.value) return;
  const body = { code: code.value.toUpperCase(), warehouseId: warehouse.value, lines: lines.value.map(line => ({ ...line })) };
  error.value = inboundDraftError(body);
  if (error.value) return;
  command.value = { ...body, operation: crypto.randomUUID() }; phase.value = "preview";
}
async function submit() {
  if (!authorized.value || !online.value || !command.value || !["preview", "unknown"].includes(phase.value)) return;
  cancel(); const ticket = sequence; const signal = controller.signal; const scope = fingerprint.value; const request = command.value;
  phase.value = "submitting"; error.value = ""; emit("attempted");
  const current = () => sequence === ticket && scope === fingerprint.value && !signal.aborted;
  deadline = setTimeout(() => {
    if (!current()) return;
    cancel(); uncertain.value = true; phase.value = "unknown";
    error.value = "10 秒内未取得创建确认，结果不明；请保留原操作，不能换键盲发。";
  }, 10000);
  try {
    const result = await createInboundDraft(request, signal);
    if (current()) { receipt.value = result; phase.value = "confirmed"; }
  } catch (failure) {
    if (!current()) return;
    const definite = failure instanceof ApiError && [400, 401, 403, 404, 405, 409].includes(failure.status);
    if (!definite) uncertain.value = true;
    phase.value = definite ? "rejected" : "unknown";
    error.value = failure instanceof ApiError && [404, 405].includes(failure.status)
      ? "草稿入口未开放或父仓未找到，请核对独立写开关及当前父仓权限；不会自动重试。"
      : failure instanceof Error ? failure.message : "创建结果未确认，请核对原操作。";
  } finally {
    if (ticket === sequence && deadline !== undefined) { clearTimeout(deadline); deadline = undefined; }
  }
}
</script>

<template>
  <section class="inbound-creation" :aria-labelledby="`${id}-title`" :aria-busy="phase === 'submitting'">
    <h2 :id="`${id}-title`">创建入库草稿</h2>
    <p class="notice">仅保存计划，不审核、不收货、不启用仓位，不产生库存或运输。
      未确认不可换键盲发；关闭、导航或刷新会丢失内存上下文，请先记录操作 UUID 和全部原输入。</p>
    <p v-if="!online" role="status">当前离线，禁止提交、不排队，恢复后不会自动重试。</p>
    <p v-if="phase === 'invalidated' || !authorized" role="alert">当前主体或字段权限已改变，请关闭并重新准备。</p>
    <form v-else-if="phase === 'draft'" @submit.prevent="preview">
      <label :for="`${id}-code`">新入库单号</label>
      <input :id="`${id}-code`" v-model="code" name="draftCode" maxlength="64" autocomplete="off" :disabled="!online" />
      <label :for="`${id}-warehouse`">计划仓库 UUID</label>
      <input :id="`${id}-warehouse`" v-model="warehouse" name="draftWarehouse" maxlength="36" autocomplete="off" :disabled="!online" />
      <fieldset v-for="(line, index) in lines" :key="line.id" :disabled="!online">
        <legend>计划行 {{ index + 1 }}</legend><p>稳定行 UUID：{{ line.id }}</p>
        <label :for="`${id}-${line.id}-item`">物料 UUID</label>
        <input :id="`${id}-${line.id}-item`" v-model="line.itemId" name="draftItem" maxlength="36" autocomplete="off" />
        <label :for="`${id}-${line.id}-unit`">基础单位 UUID</label>
        <input :id="`${id}-${line.id}-unit`" v-model="line.unitId" name="draftUnit" maxlength="36" autocomplete="off" />
        <label :for="`${id}-${line.id}-batch`">批次（可空）</label>
        <input :id="`${id}-${line.id}-batch`" v-model="line.batch" name="draftBatch" maxlength="128" autocomplete="off" />
        <label :for="`${id}-${line.id}-precision`">单位精度</label>
        <select :id="`${id}-${line.id}-precision`" v-model="line.precision" name="draftPrecision">
          <option v-for="value in 7" :key="value" :value="value - 1">{{ value - 1 }}</option>
        </select>
        <label :for="`${id}-${line.id}-quantity`">计划数量</label>
        <input :id="`${id}-${line.id}-quantity`" v-model="line.planned" name="draftPlanned" inputmode="decimal" maxlength="29" autocomplete="off" />
        <button type="button" :disabled="lines.length === 1" :aria-label="`移除计划行 ${index + 1}`" @click="removeLine(line.id)">移除此行</button>
      </fieldset>
      <button type="button" :disabled="!online || lines.length >= 100" @click="addLine">增加计划行</button>
      <button type="submit" :disabled="!online">预览入库草稿</button>
    </form>
    <div v-if="command" data-test="inbound-draft-preview">
      <dl><dt>规范单号</dt><dd>{{ command.code }}</dd><dt>计划仓库 UUID</dt><dd>{{ command.warehouseId }}</dd>
        <dt>操作 UUID</dt><dd>{{ command.operation }}</dd></dl>
      <ol><li v-for="line in command.lines" :key="line.id">
        行 {{ line.id }}；物料 {{ line.itemId }}；基础单位 {{ line.unitId }}；批次 {{ line.batch || "无批次" }}；
        精度 {{ line.precision }}；计划 {{ line.planned }}
      </li></ol>
      <button v-if="phase === 'preview'" type="button" data-test="confirm-inbound-draft" :disabled="!online || !authorized" @click="submit">确认保存入库草稿</button>
      <button v-if="phase === 'unknown'" type="button" data-test="retry-inbound-draft" :disabled="!online || !authorized" @click="submit">手工恢复原操作</button>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="phase === 'unknown'" role="status">结果不明，不代表未创建。</p>
    <p v-if="phase === 'rejected'" role="status">本次请求被拒绝。{{ uncertain ? "先前不明结果仍需查证，禁止换键盲发。" : "未取得创建确认。" }}</p>
    <p v-if="receipt" role="status">已确认入库草稿身份：{{ receipt.id }}。这不是当前版本、审核或收货确认。</p>
    <button v-if="receipt" type="button" data-test="read-created-inbound" :disabled="!online || !authorized" @click="emit('read', receipt.id)">读取已创建入库单当前详情</button>
    <button type="button" :disabled="phase === 'submitting'" @click="emit('close')">关闭草稿面板</button>
  </section>
</template>

<style scoped>
.inbound-creation { min-width: 0; margin-block: var(--wes-space-6); overflow-wrap: anywhere; }
form, fieldset { display: grid; gap: var(--wes-space-2); min-width: 0; }
fieldset { margin-block: var(--wes-space-3); padding: var(--wes-space-3); }
input, select, button { max-width: 100%; min-width: 0; }
input, select { width: 100%; }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; } ol { padding-inline-start: var(--wes-space-5); }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
