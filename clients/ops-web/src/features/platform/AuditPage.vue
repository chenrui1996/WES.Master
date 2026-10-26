<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import ColumnSettings from "../../components/ColumnSettings.vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { useColumns } from "../../components/useColumns";
import { ApiError, api, isRecord, sessionState } from "../identity/identity-api";
interface AuditRow { id: string; occurredAt: string; actorId: string | null; targetId: string | null; correlationId: string | null; action: number | null; beforeVersion: string | null; afterVersion: string | null }
interface EventRow { id: string; occurredAt: string; kind: number | null; correlationId: string | null; accountDigest: string | null; sourceDigest: string | null; sessionId: string | null }
const mode = ref("audit"); const from = ref(new Date(Date.now() - 86400000).toISOString().slice(0, 16));
const to = ref(new Date().toISOString().slice(0, 16)); const offset = ref(0);
const pageSize = ref(25);
const kind = ref(""); const action = ref(""); const correlation = ref(""); const rows = ref<(AuditRow | EventRow)[]>([]);
const error = ref(""); const busy = ref(false); let controller = new AbortController(); let sequence = 0;
let lastSuccessfulQuery: string | null = null;
const snapshotId = ref<string | null>(null); const snapshotExpiresAt = ref<string | null>(null);
const snapshotCount = ref<number | null>(null);
const currentQuery = computed(() => JSON.stringify([
  mode.value, from.value, to.value, offset.value, pageSize.value, kind.value, action.value, correlation.value, snapshotId.value,
]));
const queryState = computed(() => busy.value
  ? rows.value.length ? "stale" : "loading"
  : error.value ? rows.value.length ? "stale" : "error"
    : rows.value.length ? "ready" : "empty");
const staleMessage = computed(() => busy.value
  ? "正在刷新；页面保留的是最近一次已加载的证据。"
  : `刷新失败，仍保留最近一次已加载的证据：${error.value}`);
const auditGrants = computed(() => sessionState.subject?.grants.filter(grant => grant.permission === "platform.audit.read") ?? []);
const fields = computed(() => new Set(auditGrants.value.flatMap(grant => grant.fields)));
const canReadAudit = computed(() => {
  const required = ["Id", "OccurredAt"];
  const all = new Set(auditGrants.value.filter(grant => grant.scope === 2).flatMap(grant => grant.fields));
  const own = new Set(auditGrants.value.filter(grant => grant.scope === 0 || grant.scope === 2).flatMap(grant => grant.fields));
  return required.every(field => all.has(field)) || required.every(field => own.has(field));
});
const {
  available, selected, visible, widths, widthStyle, fixedFirstColumn, fixedPositions,
  fixedColumnClass, fixedColumnStyle, pinningWarning, setTableFrame, preferenceWarning, columnViews,
} = useColumns(() => [
  { key: "id", label: "标识", field: "Id", required: true, widthClass: "identity" as const },
  { key: "occurredAt", label: "发生时间（UTC）", field: "OccurredAt", required: true, widthClass: "time" as const },
  { key: "correlationId", label: "关联标识", field: "CorrelationId", widthClass: "identity" as const },
  ...(mode.value === "audit" ? [
    { key: "actorId", label: "操作者", field: "ActorId", widthClass: "identity" as const },
    { key: "targetId", label: "目标", field: "TargetId", widthClass: "identity" as const },
    { key: "action", label: "动作码", field: "Action", widthClass: "status" as const },
    { key: "beforeVersion", label: "原版本", field: "BeforeVersion", widthClass: "identity" as const },
    { key: "afterVersion", label: "后继版本", field: "AfterVersion", widthClass: "identity" as const },
  ] : [
    { key: "kind", label: "事件码", field: "Kind", widthClass: "status" as const },
    { key: "accountDigest", label: "账号假名绑定", field: "AccountDigest", widthClass: "identity" as const },
    { key: "sourceDigest", label: "来源假名绑定", field: "SourceDigest", widthClass: "identity" as const },
    { key: "sessionId", label: "会话", field: "SessionId", widthClass: "identity" as const },
  ]),
].filter(column => fields.value.has(column.field)), { pageId: "platform-audit", tableId: () => mode.value });
function invalidate(preserveRows = false) {
  sequence++; controller.abort(); controller = new AbortController(); busy.value = false;
  if (!preserveRows) { rows.value = []; lastSuccessfulQuery = null; }
}
function clearSnapshot() {
  snapshotId.value = null; snapshotExpiresAt.value = null; snapshotCount.value = null;
}
function queryParameters() {
  const start = Date.parse(`${from.value}Z`); const end = Date.parse(`${to.value}Z`);
  if (!Number.isFinite(start) || !Number.isFinite(end) || start >= end) {
    throw new Error("请输入有效的 UTC 起止时间，结束时间必须晚于开始时间。");
  }
  const params = new URLSearchParams({ from: new Date(start).toISOString(), to: new Date(end).toISOString() });
  if (mode.value === "login-events" && kind.value && fields.value.has("Kind")) params.set("kind", kind.value);
  if (mode.value === "audit" && action.value && fields.value.has("Action")) params.set("action", action.value);
  if (correlation.value && fields.value.has("CorrelationId")) params.set("correlationId", correlation.value);
  return params;
}
function projectRows(value: unknown, requestedMode: string): (AuditRow | EventRow)[] {
  if (!isRecord(value) || !Array.isArray(value.rows) || value.rows.length > pageSize.value)
    throw new Error("审计响应无效，请重新读取。");
  return value.rows.map((row: unknown) => {
    if (!isRecord(row) || typeof row.id !== "string" || typeof row.occurredAt !== "string" || !Number.isFinite(Date.parse(row.occurredAt)))
      throw new Error("审计行格式无效，已丢弃本页。");
    const text = (key: string) => typeof row[key] === "string" ? row[key] : null;
    if (requestedMode === "audit") return { id: row.id, occurredAt: row.occurredAt, correlationId: text("correlationId"),
      actorId: text("actorId"), targetId: text("targetId"), action: typeof row.action === "number" ? row.action : null,
      beforeVersion: text("beforeVersion"), afterVersion: text("afterVersion") };
    return { id: row.id, occurredAt: row.occurredAt, correlationId: text("correlationId"), accountDigest: text("accountDigest"),
      sourceDigest: text("sourceDigest"), sessionId: text("sessionId"), kind: typeof row.kind === "number" ? row.kind : null };
  });
}
function applySnapshotPage(value: unknown, requestedMode: string, expectedId: string | null) {
  if (expectedId) {
    if (!isRecord(value) || value.snapshotId !== expectedId || typeof value.expiresAt !== "string"
      || !Number.isFinite(Date.parse(value.expiresAt)) || typeof value.count !== "number"
      || !Number.isInteger(value.count) || value.count < 0 || value.count > 10_000)
      throw new Error("固定核对快照响应无效，已丢弃本页。");
    snapshotExpiresAt.value = value.expiresAt;
    snapshotCount.value = value.count;
  }
  rows.value = projectRows(value, requestedMode);
}
async function read() {
  const fingerprint = currentQuery.value;
  invalidate(lastSuccessfulQuery === fingerprint);
  const request = sequence; const signal = controller.signal; const requestedMode = mode.value;
  error.value = "";
  let params: URLSearchParams;
  try { params = queryParameters(); }
  catch (failure) {
    rows.value = []; lastSuccessfulQuery = null;
    error.value = failure instanceof Error ? failure.message : "审计查询参数无效。"; return;
  }
  params.set("offset", String(offset.value)); params.set("limit", String(pageSize.value));
  const requestedSnapshot = requestedMode === "audit" ? snapshotId.value : null;
  const path = requestedSnapshot
    ? `/api/platform/audit/snapshots/${requestedSnapshot}?${params.toString()}`
    : `/api/platform/${requestedMode}?${params.toString()}`;
  busy.value = true;
  try {
    const data: unknown = await api(path, { signal });
    if (request !== sequence || fingerprint !== currentQuery.value) return;
    if (!requestedSnapshot && isRecord(data) && data.authorized === false) throw new ApiError(403, "AuditReadForbidden", "未提供");
    if (!requestedSnapshot && (!isRecord(data) || data.authorized !== true))
      throw new Error("审计响应无效，请重新读取。");
    applySnapshotPage(data, requestedMode, requestedSnapshot);
    lastSuccessfulQuery = fingerprint;
  } catch (failure) {
    if (!signal.aborted && request === sequence) {
      if (failure instanceof ApiError && [401, 403].includes(failure.status)) {
        rows.value = []; lastSuccessfulQuery = null; clearSnapshot();
      } else if (failure instanceof ApiError && failure.status === 410 && requestedSnapshot) {
        rows.value = []; lastSuccessfulQuery = null; clearSnapshot();
        error.value = "核对快照已过期，请重新创建快照或返回实时查询。";
        return;
      }
      error.value = failure instanceof Error ? failure.message : "审计读取失败。";
    }
  }
  finally { if (request === sequence) busy.value = false; }
}
async function createSnapshot() {
  if (busy.value || mode.value !== "audit") return;
  invalidate(); clearSnapshot(); offset.value = 0; error.value = "";
  const request = sequence; const signal = controller.signal; const requestedMode = mode.value;
  let params: URLSearchParams;
  try { params = queryParameters(); }
  catch (failure) {
    error.value = failure instanceof Error ? failure.message : "审计查询参数无效。"; return;
  }
  busy.value = true;
  try {
    const data: unknown = await api("/api/platform/audit/snapshots", {
      method: "POST", body: JSON.stringify({ from: params.get("from"), to: params.get("to"),
        limit: pageSize.value, action: params.get("action") ? Number(params.get("action")) : null,
        correlationId: params.get("correlationId") || null }), signal,
    });
    if (request !== sequence || requestedMode !== mode.value) return;
    if (!isRecord(data) || typeof data.snapshotId !== "string"
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(data.snapshotId))
      throw new Error("固定核对快照响应无效，已丢弃本页。");
    snapshotId.value = data.snapshotId;
    applySnapshotPage(data, requestedMode, data.snapshotId);
    lastSuccessfulQuery = currentQuery.value;
  } catch (failure) {
    if (!signal.aborted && request === sequence) {
      clearSnapshot(); rows.value = []; lastSuccessfulQuery = null;
      error.value = failure instanceof ApiError && failure.status === 413
        ? "核对快照范围超过10,000条，请缩小时间范围或筛选条件。"
        : failure instanceof Error ? failure.message : "创建核对快照失败。";
    }
  }
  finally { if (request === sequence) busy.value = false; }
}
function submitLiveQuery() {
  clearSnapshot(); offset.value = 0; void read();
}
function returnToLive() {
  if (busy.value) return;
  clearSnapshot(); offset.value = 0; void read();
}
function page(nextOffset: number, limit: number) {
  if (busy.value) return;
  pageSize.value = limit; offset.value = nextOffset; void read();
}
function cell(row: AuditRow | EventRow, key: string) {
  if (key === "id") return row.id;
  if (key === "occurredAt") return row.occurredAt;
  if (key === "correlationId") return row.correlationId;
  if ("actorId" in row) {
    switch (key) { case "actorId": return row.actorId; case "targetId": return row.targetId; case "action": return row.action; case "beforeVersion": return row.beforeVersion; case "afterVersion": return row.afterVersion; }
  } else {
    switch (key) { case "kind": return row.kind; case "accountDigest": return row.accountDigest; case "sourceDigest": return row.sourceDigest; case "sessionId": return row.sessionId; }
  }
  return null;
}
watch([mode, from, to, kind, action, correlation], () => {
  invalidate(); clearSnapshot(); offset.value = 0; error.value = "";
}, { flush: "sync" });
watch(pageSize, () => { invalidate(); offset.value = 0; error.value = ""; }, { flush: "sync" });
watch([() => sessionState.subject, () => sessionState.token], () => {
  invalidate(); clearSnapshot(); offset.value = 0; kind.value = ""; action.value = ""; correlation.value = "";
  error.value = "身份或权限已变更，旧证据已清除，请重新查询。";
}, { deep: true, flush: "sync" });
onMounted(read); onUnmounted(() => { controller.abort(); sequence++; });
</script>
<template>
  <section class="wes-page"><header><p class="eyebrow">只读证据</p><h1>审计与登录事件</h1><p>历史记录不可编辑、删除或批量改写。实时分页不保证跨请求无丢重；业务审计可建立10分钟固定核对快照，不提供导出。</p></header>
    <form class="toolbar" @submit.prevent="submitLiveQuery">
      <label for="audit-mode">证据类型<select id="audit-mode" v-model="mode"><option value="audit">业务审计</option><option value="login-events">登录安全事件（需 All 范围）</option></select></label>
      <label for="audit-from">开始时间（UTC，含）<input id="audit-from" v-model="from" type="datetime-local" required></label>
      <label for="audit-to">结束时间（UTC，不含）<input id="audit-to" v-model="to" type="datetime-local" required></label>
      <label v-if="mode === 'login-events' && fields.has('Kind')" for="event-kind">事件码<input id="event-kind" v-model="kind" inputmode="numeric" pattern="[0-9]+"></label>
      <label v-if="mode === 'audit' && fields.has('Action')" for="audit-action">审计动作码<input id="audit-action" v-model="action" inputmode="numeric" pattern="[0-9]+"></label>
      <label v-if="fields.has('CorrelationId')" for="event-correlation">关联 UUID<input id="event-correlation" v-model="correlation" maxlength="36"></label>
      <button type="submit" :disabled="busy">查询实时证据</button>
      <button v-if="mode === 'audit' && canReadAudit" type="button" data-test="create-audit-snapshot"
        :disabled="busy" @click="createSnapshot">创建固定核对快照</button>
      <button v-if="snapshotId" type="button" :disabled="busy" @click="returnToLive">返回实时查询</button>
    </form><ColumnSettings :columns="available" v-model="selected" v-model:widths="widths"
      v-model:fixed-first-column="fixedFirstColumn" v-model:fixed-positions="fixedPositions"
      :preference-warning="preferenceWarning" :column-views="columnViews" />
    <QueryState label="当前权限下的证据" :state="queryState" :busy="busy" :error-message="error"
      :stale-message="staleMessage" @retry="read">
      <template #empty>该区间没有当前可见记录。可以调整时间后重新查询。</template>
      <p v-if="snapshotId && snapshotExpiresAt" role="status" class="snapshot-status">
        固定核对快照：{{ snapshotCount }} 条；有效至 {{ snapshotExpiresAt }}。授权仍会在每次翻页时重新检查。
      </p>
      <div :ref="setTableFrame" class="table-scroll">
        <p class="table-fixed-notice" role="status">可视区域较窄，固定列暂时停用；请横向滚动查看，避免固定列遮挡内容。</p>
        <p v-if="pinningWarning" class="table-pinning-warning" role="status">{{ pinningWarning }}</p>
        <table><caption>{{ mode === "audit" ? "不可改业务审计" : "登录安全事件" }}</caption>
        <thead><tr><th v-for="column in visible" :key="column.key" scope="col" :class="fixedColumnClass(column.key)"
          :style="[widthStyle(column.key), fixedColumnStyle(column.key)]">{{ column.label }}</th></tr></thead>
        <tbody><tr v-for="row in rows" :key="row.id"><td v-for="column in visible" :key="column.key" class="identifier"
          :class="fixedColumnClass(column.key)" :style="fixedColumnStyle(column.key)">{{ cell(row, column.key) ?? "未授权 / 不适用" }}</td></tr></tbody>
      </table></div>
    </QueryState>
    <RealtimePager label="证据分页" select-id="audit-limit" :offset="offset" :limit="pageSize" :count="rows.length"
      :busy="busy" :failed="!!error" :consistency="snapshotId ? 'snapshot' : 'realtime'" @change="page" />
  </section>
</template>
