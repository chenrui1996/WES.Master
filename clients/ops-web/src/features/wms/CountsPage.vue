<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, useId, watch } from "vue";
import QueryState from "../../components/QueryState.vue";
import CountConfirmation from "./CountConfirmation.vue";
import { ApiError, sessionState } from "../identity/identity-api";
import { countActionError, countInputError, countScaled, countStates, countTitles, CountResponseError, mayExecuteCount,
  mayReadCount, mayReviewCount, readCount, readCountReview, type CountCommand, type CountDetail, type CountKind, type CountReview } from "./counts";

const props = defineProps<{ id?: string }>();
const inputId = useId(); const countId = ref(props.id ?? ""); const scope = computed(() => JSON.stringify(sessionState.subject));
const readable = computed(mayReadCount); const creatable = computed(() => mayExecuteCount("create"));
const record = shallowRef<CountDetail>(); const review = shallowRef<CountReview>(); const command = shallowRef<CountCommand>();
const summary = ref(""); const reviewSummary = ref(""); const busy = ref(false); const fresh = ref(false); const reviewFresh = ref(false);
const error = ref(""); const loadedAt = ref(""); const online = ref(navigator.onLine); const creating = ref(false);
const selectedKind = ref<Exclude<CountKind, "create">>(); const draft = reactive({ code: "", warehouseId: "", locationId: "" });
const action = reactive({ lineId: "", quantity: "", locationId: "", itemId: "", containerId: "", reason: "" });
const selfApproval = computed(() => review.value?.lines.some(line => line.difference !== null && countScaled(line.difference) !== 0n
  && line.countedBy?.toLowerCase() === sessionState.subject?.userId.toLowerCase()) ?? false);
const kinds: Exclude<CountKind, "create">[] = ["start", "records", "adjustment", "invalidation"];
const allowedKinds = computed(() => kinds.filter(kind => {
  const current = record.value;
  if (!current || !mayExecuteCount(kind)) return false;
  if (kind === "start") return current.state === 0 || current.state === 4;
  if (kind === "adjustment") return current.state === 2 && reviewFresh.value && !!review.value && !selfApproval.value;
  return current.state === 1 || current.state === 2;
}));
const state = computed(() => busy.value ? record.value ? "stale" : "loading"
  : record.value ? fresh.value ? "ready" : "stale" : error.value ? "error" : "empty");
let controller = new AbortController(); let sequence = 0;
function cancel() {
  ++sequence; controller.abort(); controller = new AbortController(); busy.value = false; fresh.value = false; reviewFresh.value = false;
}
function clear() {
  cancel(); record.value = undefined; review.value = undefined; loadedAt.value = ""; error.value = ""; selectedKind.value = undefined;
}
function resetAction() { Object.assign(action, { quantity: "", locationId: "", itemId: "", containerId: "", reason: "" }); }
watch(() => props.id, value => { command.value = undefined; creating.value = false; countId.value = value ?? ""; clear(); });
watch(countId, () => { command.value = undefined; clear(); }, { flush: "sync" });
watch(scope, () => {
  clear(); creating.value = false; draft.code = ""; draft.warehouseId = ""; draft.locationId = "";
  resetAction(); reviewSummary.value = ""; summary.value = "";
}, { flush: "sync" });
watch(() => draft.warehouseId, () => { draft.locationId = ""; });
watch(() => action.lineId, resetAction, { flush: "sync" });
function connection() { online.value = navigator.onLine; if (!online.value) cancel(); }
onMounted(() => { addEventListener("online", connection); addEventListener("offline", connection); });
onUnmounted(() => { cancel(); removeEventListener("online", connection); removeEventListener("offline", connection); });
async function load() {
  if (!readable.value || !online.value || busy.value || command.value) return;
  cancel(); review.value = undefined; busy.value = true; error.value = ""; selectedKind.value = undefined;
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await readCount(countId.value, signal);
    if (current()) { record.value = result; fresh.value = true; loadedAt.value = new Date().toISOString(); }
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof CountResponseError || failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      record.value = undefined; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "盘点读取未确认，请显式重试。";
  } finally { if (ticket === sequence) busy.value = false; }
}
async function loadReview() {
  if (!record.value || !fresh.value || busy.value || command.value || !online.value || !mayReviewCount()) return;
  const original = record.value;
  cancel(); review.value = undefined; busy.value = true; error.value = ""; selectedKind.value = undefined;
  const ticket = sequence; const signal = controller.signal; const fingerprint = scope.value;
  const current = () => ticket === sequence && fingerprint === scope.value && !signal.aborted;
  try {
    const result = await readCountReview(original.id, signal);
    if (!current()) return;
    if (result.version !== original.version) {
      error.value = "复核快照版本与盲盘详情不同；请显式重读当前盲盘详情，不自动合并。"; return;
    }
    review.value = result; reviewFresh.value = true; fresh.value = true;
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      record.value = undefined; loadedAt.value = "";
    }
    error.value = failure instanceof Error ? failure.message : "差异复核读取未确认，请重新读取。";
  } finally { if (ticket === sequence) busy.value = false; }
}
function prepare(kind: Exclude<CountKind, "create">) {
  if (!online.value || !fresh.value || busy.value || command.value || !allowedKinds.value.includes(kind) || !record.value) return;
  resetAction(); action.lineId = record.value.lines[0]?.id ?? ""; selectedKind.value = kind; error.value = "";
}
function previewDraft() {
  if (!creatable.value || !online.value || command.value) return;
  const request: CountCommand = { kind: "create", operation: crypto.randomUUID(), input: { ...draft } };
  error.value = countInputError(request); if (error.value) return;
  command.value = request; reviewSummary.value = "";
  summary.value = `新草稿 ${draft.code}；仓库 ${draft.warehouseId}；库位 ${draft.locationId}。创建不锁库位、不调账。`;
}
function previewAction() {
  const current = record.value; const kind = selectedKind.value;
  if (!current || !kind || !fresh.value || busy.value || command.value || !online.value || !allowedKinds.value.includes(kind)) return;
  const common = { id: current.id, operation: crypto.randomUUID() }; const expectedVersion = current.version;
  let request: CountCommand;
  if (kind === "start") request = { ...common, kind, input: { expectedVersion } };
  else if (kind === "records") request = { ...common, kind, input: { expectedVersion, lineId: action.lineId,
    quantity: action.quantity, locationId: action.locationId, itemId: action.itemId, containerId: action.containerId, reason: action.reason } };
  else request = { ...common, kind, input: { expectedVersion, reason: action.reason } };
  error.value = countActionError(current, request); if (error.value) return;
  command.value = request;
  summary.value = `原盘点单 ${current.code}；仓库 ${current.warehouseId}；库位 ${current.locationId}；状态 ${countStates[current.state]}。`;
  reviewSummary.value = kind === "adjustment" && review.value
    ? review.value.lines.map(line => `行 ${line.id}；单位 ${line.unitId}；实盘 ${line.quantity}；账面 ${line.bookQuantity}；差异 ${line.difference}`).join("\n") : "";
}
function attempted() { cancel(); review.value = undefined; selectedKind.value = undefined; }
function readWritten(id: string) { command.value = undefined; creating.value = false; countId.value = id; clear(); void load(); }
function closeConfirmation() { command.value = undefined; creating.value = false; selectedKind.value = undefined; reviewSummary.value = ""; }
</script>

<template>
  <section class="counts-page" aria-labelledby="counts-title">
    <h1 id="counts-title">盲盘与授权差异调整</h1>
    <p class="notice">默认盲盘：应盘行不返回账面量或差异，未盘不是零。记录不自动调账；有非零差异必须由非盘点人复核。
      盘点锁期间禁止影响范围的收发与移动；30分钟仅提醒，不会自动解锁。</p>
    <p v-if="!readable" role="alert">缺少完整盘点读取字段权限。</p>
    <template v-else>
      <form data-test="count-query" @submit.prevent="load">
        <label :for="`${inputId}-count-id`">盘点单 UUID</label>
        <input :id="`${inputId}-count-id`" v-model="countId" name="countId" autocomplete="off" :disabled="!!command || busy">
        <button type="submit" data-test="count-query-button" :disabled="!online || busy || !!command">读取当前盲盘详情</button>
      </form>
      <button v-if="creatable && !command" type="button" data-test="prepare-count-create" :disabled="!online || busy" @click="creating = !creating">准备创建位置盘点计划</button>
      <form v-if="creating && !command && creatable" data-test="count-create-form" @submit.prevent="previewDraft">
        <h2>创建盘点草稿</h2>
        <label :for="`${inputId}-code`">单号</label><input :id="`${inputId}-code`" v-model="draft.code" name="code" maxlength="64">
        <label :for="`${inputId}-warehouse`">仓库 UUID</label><input :id="`${inputId}-warehouse`" v-model="draft.warehouseId" name="warehouseId">
        <label :for="`${inputId}-location`">盘点库位 UUID</label><input :id="`${inputId}-location`" v-model="draft.locationId" name="locationId">
        <p>开始时锁定该库位全部合法账维度（含零余额，1至100行）；有预留、在途或未知执行则阻断。</p>
        <button type="submit" :disabled="!online || busy">预览创建盘点计划</button>
      </form>
      <QueryState label="盘点盲盘详情" :state="state" :busy="busy" :error-message="error" @retry="load">
        <template #empty><p>输入已知盘点单 UUID 读取，或按当前权限创建位置计划。</p></template>
        <article v-if="record" data-test="count-detail" :aria-labelledby="`${inputId}-detail-title`">
          <h2 :id="`${inputId}-detail-title`">{{ record.code }} · {{ countStates[record.state] }}</h2>
          <dl><dt>单据 UUID / 版本</dt><dd>{{ record.id }} / {{ record.version }}</dd>
            <dt>仓库 / 库位</dt><dd>{{ record.warehouseId }} / {{ record.locationId }}</dd>
            <dt>创建 UTC</dt><dd>{{ record.createdAt }}</dd><dt>启动快照 UTC</dt><dd>{{ record.snapshotAt ?? "未启动" }}</dd>
            <dt>锁负责人</dt><dd>{{ record.lockOwnerId ?? "未启动" }}</dd><dt>生效策略</dt><dd>{{ record.policy }}</dd>
            <dt>盲盘读取 UTC</dt><dd>{{ loadedAt }}</dd></dl>
          <p v-if="record.reminderDue" role="status">已到30分钟提醒，请负责人核验；锁仍有效，不会自动解锁。</p>
          <p v-if="record.state === 4" role="status">旧计数全部失效。重新开始将重新取快照并生成新应盘行，必须重新实盘。</p>
          <section v-for="line in record.lines" :key="line.id" class="count-line" :aria-labelledby="`${inputId}-${line.id}`">
            <h3 :id="`${inputId}-${line.id}`">应盘行 {{ line.id }}</h3>
            <dl><dt>物料 / 单位</dt><dd>{{ line.itemId }} / {{ line.unitId }}</dd><dt>容器</dt><dd>{{ line.containerId }}</dd>
              <dt>批次 / 精度</dt><dd>{{ line.batch || "不启用批次" }} / {{ line.precision }}</dd>
              <dt>实盘（本单位）</dt><dd>{{ line.quantity ?? "尚未实盘（不是0）" }}</dd>
              <dt>计数人 / UTC</dt><dd>{{ line.countedBy ?? "尚无" }} / {{ line.countedAt ?? "尚无" }}</dd></dl>
          </section>
          <button v-if="mayReviewCount()" type="button" data-test="count-review" :disabled="!fresh || busy || !online || !!command" @click="loadReview">独立读取受权账面与差异复核</button>
          <section v-if="review && mayReviewCount()" data-test="count-review-detail" :aria-labelledby="`${inputId}-review-title`">
            <h3 :id="`${inputId}-review-title`">独立受权复核（不是盲盘内容）</h3>
            <p v-if="!reviewFresh">复核已陈旧，禁止依据它调账，请重读。</p>
            <dl v-for="line in review.lines" :key="line.id"><dt>行 / 单位</dt><dd>{{ line.id }} / {{ line.unitId }}</dd>
              <dt>账面 / 实盘 / 差异</dt><dd>{{ line.bookQuantity }} / {{ line.quantity ?? "未盘" }} / {{ line.difference ?? "未知，非0" }}</dd></dl>
            <p v-if="selfApproval" role="alert">你是某个非零差异的最终盘点人，不能自行批准；请由另一授权人员复核。</p>
          </section>
          <div v-if="!command" class="actions">
            <button v-for="kind in allowedKinds" :key="kind" type="button" :data-test="`prepare-count-${kind}`"
              :disabled="!online || !fresh || busy" @click="prepare(kind)">{{ countTitles[kind] }}</button>
          </div>
          <form v-if="selectedKind && !command && fresh && allowedKinds.includes(selectedKind)" data-test="count-action-form" @submit.prevent="previewAction">
            <h3>{{ countTitles[selectedKind] }}</h3>
            <template v-if="selectedKind === 'records'">
              <label :for="`${inputId}-line`">应盘行</label><select :id="`${inputId}-line`" v-model="action.lineId" name="lineId">
                <option v-for="line in record.lines" :key="line.id" :value="line.id">{{ line.id }} · {{ line.itemId }} · {{ line.containerId }}</option>
              </select>
              <label :for="`${inputId}-scan-location`">扫码库位 UUID</label><input :id="`${inputId}-scan-location`" v-model="action.locationId" name="scanLocationId">
              <label :for="`${inputId}-scan-item`">扫码物料 UUID</label><input :id="`${inputId}-scan-item`" v-model="action.itemId" name="scanItemId">
              <label :for="`${inputId}-scan-container`">扫码容器 UUID</label><input :id="`${inputId}-scan-container`" v-model="action.containerId" name="scanContainerId">
              <label :for="`${inputId}-quantity`">实盘数量（十进制 string，可为0）</label><input :id="`${inputId}-quantity`" v-model="action.quantity" name="quantity" inputmode="decimal">
            </template>
            <p v-if="selectedKind === 'start'">重新获取全部维度快照和局部锁，不复用旧计数；不能抢占在途或其他有效锁。</p>
            <p v-if="selectedKind === 'invalidation'">明确人工解锁：全部旧计数失效，旧版本不能调整，必须重新盘点。</p>
            <template v-if="selectedKind !== 'start'"><label :for="`${inputId}-reason`">原因 / 实盘修订说明</label>
              <textarea :id="`${inputId}-reason`" v-model="action.reason" name="reason" maxlength="512" /></template>
            <button type="submit" :disabled="!online || busy">预览{{ countTitles[selectedKind] }}</button>
          </form>
        </article>
      </QueryState>
    </template>
    <CountConfirmation v-if="command" :command="command" :summary="summary" :review-summary="reviewSummary"
      @attempted="attempted" @read="readWritten" @close="closeConfirmation" />
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="!online" role="status">当前离线，详情仅为旧数据；禁止提交、不排队，恢复连接不会自动重试。</p>
  </section>
</template>

<style scoped>
.counts-page { min-width: 0; overflow-wrap: anywhere; }
form, .count-line, [data-test="count-review-detail"] { display: grid; gap: var(--wes-space-2); margin-block: var(--wes-space-4); }
dl { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 3fr); gap: var(--wes-space-2); }
dd { margin: 0; } input, select, textarea { width: 100%; min-width: 0; box-sizing: border-box; }
.actions { display: flex; flex-wrap: wrap; gap: var(--wes-space-2); }
button { max-width: 100%; white-space: normal; }
@media (max-width: 40rem) { dl { grid-template-columns: minmax(0, 1fr); } }
</style>
