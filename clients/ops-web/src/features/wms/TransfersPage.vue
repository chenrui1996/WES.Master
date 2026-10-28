<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import TransferConfirmation from "./TransferConfirmation.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { mayExecuteTransfer, mayReadTransfer, readTransfer, transferActionError, transferInputError, TransferResponseError,
  transferStates, transferExecutions, transferPostings, transferTitles,
  type TransferCommand, type TransferDetail, type TransferDraftLine, type TransferKind, type TransferLine } from "./transfers";

type ActionKind = Exclude<TransferKind, "create">;
const props = defineProps<{ id?: string }>();
const inputId = useId(); const transferId = ref(props.id ?? ""); const scope = computed(() => JSON.stringify(sessionState.subject));
const readable = computed(mayReadTransfer); const creatable = computed(() => mayExecuteTransfer("create"));
const record = shallowRef<TransferDetail>(); const command = shallowRef<TransferCommand>(); const summary = ref("");
const busy = ref(false); const fresh = ref(false); const error = ref(""); const loadedAt = ref("");
const online = ref(navigator.onLine); const creating = ref(false); const selectedKind = ref<ActionKind>();
const draft = reactive<{ code: string; warehouseId: string; lines: TransferDraftLine[] }>({ code: "", warehouseId: "", lines: [] });
const action = reactive({ lineId: "", sourceLocationId: "", targetLocationId: "", containerId: "", quantity: "", reason: "" });
function eligible(line: TransferLine, kind: ActionKind): boolean {
  switch (kind) {
    case "dispatch": return line.branch === 0 && line.executionStatus === 0;
    case "arrivals": return line.branch === 0 && line.executionStatus === 1;
    case "synchronizations": return line.executionStatus === 2 && (line.postingStatus === 1 || line.postingStatus === 3);
    case "unknown": return line.executionStatus === 1;
    default: return false;
  }
}
const allowedKinds = computed(() => (["approval", "dispatch", "arrivals", "synchronizations", "unknown", "closure"] as const).filter(kind => {
  const current = record.value;
  if (!current || !mayExecuteTransfer(kind)) return false;
  if (kind === "approval") return current.state === 0;
  if (kind === "closure") return [0, 1, 2].includes(current.state) && current.lines.some(line => line.executionStatus === 0)
    && current.lines.every(line => line.executionStatus === 0 || line.postingStatus === 2);
  return [1, 2].includes(current.state) && current.lines.some(line => eligible(line, kind));
}));
const actionLines = computed(() => {
  const kind = selectedKind.value;
  return kind ? record.value?.lines.filter(line => eligible(line, kind)) ?? [] : [];
});
const state = computed(() => busy.value ? record.value ? "stale" : "loading"
  : record.value ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() { ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; }
function clear() { cancel(); record.value = undefined; loadedAt.value = ""; error.value = ""; selectedKind.value = undefined; }
function resetAction() { Object.assign(action, { sourceLocationId: "", targetLocationId: "", containerId: "", quantity: "", reason: "" }); }
watch(() => props.id, value => { command.value = undefined; creating.value = false; transferId.value = value ?? ""; clear(); });
watch(transferId, () => { command.value = undefined; clear(); }, { flush: "sync" });
watch(scope, () => {
  clear(); creating.value = false; draft.code = ""; draft.warehouseId = ""; draft.lines = []; resetAction(); summary.value = "";
}, { flush: "sync" });
watch(() => action.lineId, resetAction, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value || command.value) return;
  cancel(); busy.value = true; error.value = ""; selectedKind.value = undefined;
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await readTransfer(transferId.value, signal);
    if (current()) { record.value = result; fresh.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof TransferResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      record.value = undefined; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "移库读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function addLine() {
  if (draft.lines.length < 100) draft.lines.push({ id: crypto.randomUUID(), sourceBalanceId: "", targetBalanceId: "", quantity: "", branch: 0 });
}
function startDraft() {
  creating.value = !creating.value;
  if (creating.value) {
    draft.code = ""; draft.warehouseId = ""; draft.lines = []; error.value = ""; addLine();
  }
}
function prepare(kind: ActionKind) {
  if (!online.value || !fresh.value || busy.value || command.value || !allowedKinds.value.includes(kind)) return;
  resetAction(); selectedKind.value = kind; action.lineId = actionLines.value[0]?.id ?? ""; error.value = "";
}
function previewDraft() {
  if (!creatable.value || !online.value || command.value) return;
  const request: TransferCommand = { kind: "create", operation: crypto.randomUUID(),
    input: { code: draft.code, warehouseId: draft.warehouseId, lines: draft.lines.map(line => ({ ...line })) } };
  error.value = transferInputError(request); if (error.value) return;
  command.value = request; summary.value = `新草稿 ${draft.code}；仓库 ${draft.warehouseId}；完整 ${draft.lines.length} 行。创建不预留、不发运输。`;
}
function previewAction() {
  const current = record.value; const kind = selectedKind.value;
  if (!current || !kind || !fresh.value || busy.value || command.value || !online.value || !allowedKinds.value.includes(kind)) return;
  const common = { id: current.id, operation: crypto.randomUUID() }; const expectedVersion = current.version;
  let request: TransferCommand;
  if (kind === "approval") request = { ...common, kind, input: { expectedVersion,
    balances: current.lines.flatMap(line => [{ balanceId: line.sourceBalanceId, expectedVersion: line.sourceVersion },
      { balanceId: line.targetBalanceId, expectedVersion: line.targetVersion }]) } };
  else if (kind === "closure") request = { ...common, kind, input: { expectedVersion, reason: action.reason } };
  else if (kind === "arrivals") request = { ...common, kind, input: { expectedVersion, lineId: action.lineId,
    sourceLocationId: action.sourceLocationId, targetLocationId: action.targetLocationId, containerId: action.containerId,
    quantity: action.quantity, reason: action.reason } };
  else if (kind === "unknown") request = { ...common, kind, input: { expectedVersion, lineId: action.lineId, reason: action.reason } };
  else request = { ...common, kind, input: { expectedVersion, lineId: action.lineId } };
  error.value = transferActionError(current, request); if (error.value) return;
  command.value = request;
  summary.value = `原移库单 ${current.code}；仓库 ${current.warehouseId}；状态 ${transferStates[current.state]}；原版本 ${current.version}。`;
}
function attempted() { cancel(); selectedKind.value = undefined; }
function readWritten(id: string) { command.value = undefined; creating.value = false; transferId.value = id; clear(); void load(); }
function closeConfirmation() { command.value = undefined; creating.value = false; selectedKind.value = undefined; }
</script>

<template>
  <section class="transfers-page" :aria-labelledby="`${inputId}-title`">
    <h1 :id="`${inputId}-title`">多行移库与到达补账</h1>
    <p class="notice">同仓、同容器单正库存维度整量搬运，不拆容器。审核全部行原子预留；执行按固定单元逐行确认。
      已到待同步、补账失败或Unknown仍继续保护，不得重运或普通关闭。不同单位不合计；本页不证明现场安全。</p>
    <p v-if="!readable" role="alert">缺少完整移库读取字段权限。</p>
    <template v-else>
      <form data-test="transfer-query" @submit.prevent="load">
        <label :for="`${inputId}-transfer-id`">移库单 UUID</label>
        <input :id="`${inputId}-transfer-id`" v-model="transferId" name="transferId" autocomplete="off" :disabled="!!command || busy">
        <button type="submit" :disabled="!online || busy || !!command">读取当前移库详情</button>
      </form>
      <button v-if="creatable && !command" type="button" data-test="prepare-transfer-create" :disabled="!online || busy" @click="startDraft">准备创建多行移库草稿</button>
      <form v-if="creating && !command && creatable" data-test="transfer-create-form" @submit.prevent="previewDraft">
        <h2>创建移库草稿</h2>
        <label :for="`${inputId}-code`">单号</label><input :id="`${inputId}-code`" v-model="draft.code" name="code" maxlength="64">
        <label :for="`${inputId}-warehouse`">仓库 UUID</label><input :id="`${inputId}-warehouse`" v-model="draft.warehouseId" name="warehouseId">
        <fieldset v-for="(line, index) in draft.lines" :key="line.id">
          <legend>计划行 {{ index + 1 }} · {{ line.id }}</legend>
          <label :for="`${inputId}-${line.id}-source`">来源账维度 UUID</label>
          <input :id="`${inputId}-${line.id}-source`" v-model="line.sourceBalanceId" name="sourceBalanceId">
          <label :for="`${inputId}-${line.id}-target`">目标零余额账维度 UUID</label>
          <input :id="`${inputId}-${line.id}-target`" v-model="line.targetBalanceId" name="targetBalanceId">
          <label :for="`${inputId}-${line.id}-quantity`">完整单元数量（十进制 string）</label>
          <input :id="`${inputId}-${line.id}-quantity`" v-model="line.quantity" name="quantity" inputmode="decimal">
          <label :for="`${inputId}-${line.id}-branch`">执行分支</label>
          <select :id="`${inputId}-${line.id}-branch`" v-model="line.branch" name="branch"><option :value="0">人工搬运</option><option :value="1">需可信运输（尚未接入）</option></select>
          <button type="button" :disabled="draft.lines.length <= 1" @click="draft.lines.splice(index, 1)">移除计划行 {{ index + 1 }}</button>
        </fieldset>
        <button type="button" data-test="transfer-add-line" :disabled="draft.lines.length >= 100" @click="addLine">增加计划行（最多100行）</button>
        <button type="submit" :disabled="!online || busy">预览全部移库草稿行</button>
      </form>
      <QueryState label="移库详情" :state="state" :busy="busy" :error-message="error" @retry="load">
        <template #empty><p>输入已知移库单 UUID 读取，或按当前权限创建多行草稿。</p></template>
        <article v-if="record" data-test="transfer-detail" :aria-labelledby="`${inputId}-detail-title`">
          <h2 :id="`${inputId}-detail-title`">{{ record.code }} · {{ transferStates[record.state] }}</h2>
          <dl><dt>单据 UUID / 版本</dt><dd>{{ record.id }} / {{ record.version }}</dd><dt>仓库</dt><dd>{{ record.warehouseId }}</dd>
            <dt>创建 / 读取 UTC</dt><dd>{{ record.createdAt }} / {{ loadedAt }}</dd></dl>
          <section v-for="line in record.lines" :key="line.id" class="transfer-line" :aria-labelledby="`${inputId}-${line.id}`">
            <h3 :id="`${inputId}-${line.id}`">行 {{ line.id }}</h3>
            <dl><dt>执行单元</dt><dd>{{ line.executionId }}</dd>
              <dt>物料 / 单位 / 精度</dt><dd>{{ line.itemId }} / {{ line.unitId }} / {{ line.precision }}</dd>
              <dt>批次 / 容器</dt><dd>{{ line.batch || "不启用批次" }} / {{ line.containerId }}</dd>
              <dt>来源 / 目标库位</dt><dd>{{ line.sourceLocationId }} / {{ line.targetLocationId }}</dd>
              <dt>来源账维度 / 版本</dt><dd>{{ line.sourceBalanceId }} / {{ line.sourceVersion }}</dd>
              <dt>目标账维度 / 版本</dt><dd>{{ line.targetBalanceId }} / {{ line.targetVersion }}</dd>
              <dt>计划 / 仍受保护预留（本单位）</dt><dd>{{ line.plannedQuantity }} / {{ line.reservedQuantity }}</dd>
              <dt>已移 / 已关闭未执行（本单位）</dt><dd>{{ line.movedQuantity }} / {{ line.closedQuantity }}</dd>
              <dt>执行状态 / 记账状态</dt><dd>{{ transferExecutions[line.executionStatus] }} / {{ transferPostings[line.postingStatus] }}</dd>
              <dt>分支</dt><dd>{{ line.branch === 0 ? "人工" : "需可信运输；尚未接入，禁止人工伪完成" }}</dd></dl>
          </section>
          <div v-if="!command" class="actions">
            <button v-for="kind in allowedKinds" :key="kind" type="button" :data-test="`prepare-transfer-${kind}`"
              :disabled="!online || !fresh || busy" @click="prepare(kind)">{{ transferTitles[kind] }}</button>
          </div>
          <form v-if="selectedKind && !command && fresh && allowedKinds.includes(selectedKind)" data-test="transfer-action-form" @submit.prevent="previewAction">
            <h3>{{ transferTitles[selectedKind] }}</h3>
            <template v-if="selectedKind !== 'approval' && selectedKind !== 'closure'">
              <label :for="`${inputId}-line`">固定执行行</label><select :id="`${inputId}-line`" v-model="action.lineId" name="lineId">
                <option v-for="line in actionLines" :key="line.id" :value="line.id">{{ line.id }} · {{ line.containerId }}</option>
              </select>
            </template>
            <p v-if="selectedKind === 'approval'">固定全部来源/目标余额当前版本；任一行失败不产生部分预留。</p>
            <p v-if="selectedKind === 'closure'">仅释放确认未执行余量，已移事实永久保留；在途或未知不能取消。</p>
            <p v-if="selectedKind === 'synchronizations'">只补耐久到达单元账目，不再次搬运。</p>
            <template v-if="selectedKind === 'arrivals'">
              <label :for="`${inputId}-scan-source`">扫码来源库位 UUID</label><input :id="`${inputId}-scan-source`" v-model="action.sourceLocationId" name="scanSourceLocationId">
              <label :for="`${inputId}-scan-target`">扫码目标库位 UUID</label><input :id="`${inputId}-scan-target`" v-model="action.targetLocationId" name="scanTargetLocationId">
              <label :for="`${inputId}-scan-container`">扫码容器 UUID</label><input :id="`${inputId}-scan-container`" v-model="action.containerId" name="scanContainerId">
              <label :for="`${inputId}-quantity`">完整到达数量</label><input :id="`${inputId}-quantity`" v-model="action.quantity" name="quantity" inputmode="decimal">
            </template>
            <template v-if="selectedKind === 'arrivals' || selectedKind === 'unknown' || selectedKind === 'closure'">
              <label :for="`${inputId}-reason`">原因 / 确认说明</label><textarea :id="`${inputId}-reason`" v-model="action.reason" name="reason" maxlength="256" />
            </template>
            <button type="submit" :disabled="!online || busy">预览{{ transferTitles[selectedKind] }}</button>
          </form>
        </article>
      </QueryState>
    </template>
    <p v-if="error" role="alert">{{ error }}</p>
    <TransferConfirmation v-if="command" :command="command" :summary="summary" @attempted="attempted" @read="readWritten" @close="closeConfirmation" />
    <p v-if="!online" role="status">当前离线，不发送、不排队，恢复连接后显式读取或手工恢复原操作。</p>
  </section>
</template>

<style scoped>
.transfers-page { display: grid; gap: var(--wes-space-4); min-width: 0; overflow-wrap: anywhere; }
form, article, .transfer-line, fieldset { display: grid; gap: var(--wes-space-2); min-width: 0; }
.transfer-line, fieldset { border: 1px solid var(--wes-color-border); border-radius: .4rem; padding: var(--wes-space-3); }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; }
input, select, textarea, button { max-width: 100%; min-width: 0; }
button { white-space: normal; }
.actions { display: flex; gap: var(--wes-space-2); flex-wrap: wrap; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
