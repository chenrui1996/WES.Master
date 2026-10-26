<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import ColumnSettings from "../../components/ColumnSettings.vue";
import QueryState from "../../components/QueryState.vue";
import RealtimePager from "../../components/RealtimePager.vue";
import { useColumns, type ColumnWidthClass } from "../../components/useColumns";
import { api, ApiError, canRead, forgetIdentity, isRecord, sessionState } from "./identity-api";

interface Family {
  id: string; userId: string | null; version: string | null; client: number | null; current: boolean | null;
  createdAt: string | null; lastActivityAt: string | null; expiresAt: string | null; revokedAt: string | null;
  requiresPasswordChange: boolean | null; state: number | null;
}
const rows = ref<Family[]>([]);
const mode = ref<"self" | "admin">("self");
const loading = ref(false);
const busy = ref(false);
const error = ref("");
const queryError = ref("");
const success = ref("");
const pending = ref<Family | null>(null);
const dialog = ref<HTMLDialogElement>();
const cancel = ref<HTMLButtonElement>();
const oldPassword = ref("");
const newPassword = ref("");
const repeat = ref("");
const passwordBusy = ref(false);
const filters = reactive({ state: "", from: "", to: "", userId: "" });
const pageSize = ref(25);
const offset = ref(0);
let generation = 0;
let controller: AbortController | undefined;
let returnFocus: HTMLElement | null = null;
let lastSuccessfulQuery: string | null = null;
const adminAllowed = computed(() => canRead("platform.sessions.read"));
const hasIdentity = computed(() => sessionState.subject !== null && sessionState.token !== null);
const currentQuery = computed(() => JSON.stringify({
  tenantId: sessionState.subject?.tenantId,
  userId: sessionState.subject?.userId,
  grants: sessionState.subject?.grants,
  mode: mode.value,
  filters,
  offset: offset.value,
  limit: pageSize.value,
}));
const queryState = computed(() => loading.value
  ? rows.value.length ? "stale" : "loading"
  : queryError.value ? rows.value.length ? "stale" : "error"
    : rows.value.length ? "ready" : "empty");
const staleMessage = computed(() => loading.value
  ? "正在刷新；页面保留的是最近一次已加载的会话列表。"
  : `刷新失败，仍保留最近一次已加载的会话列表：${queryError.value}`);
function fieldAllowed(field: string) {
  return mode.value === "self" || (sessionState.subject?.grants.some(grant =>
    grant.permission === "platform.sessions.read" && grant.fields.includes(field)) ?? false);
}
const stateFilterAllowed = computed(() => fieldAllowed("State"));
const userFilterAllowed = computed(() => mode.value === "admin" && fieldAllowed("UserId"));
const definitions = [
  { key: "id", label: "会话标识", required: true, widthClass: "identity" },
  { key: "userId", label: "用户标识", widthClass: "identity" },
  { key: "client", label: "终端", widthClass: "status" },
  { key: "createdAt", label: "创建时间", widthClass: "time" },
  { key: "lastActivityAt", label: "最近活动", widthClass: "time" },
  { key: "expiresAt", label: "绝对过期", widthClass: "time" },
  { key: "revokedAt", label: "撤销时间", widthClass: "time" },
  { key: "requiresPasswordChange", label: "需要改密", widthClass: "status" },
  { key: "current", label: "是否当前", widthClass: "status" },
  { key: "state", label: "状态", widthClass: "status" },
] satisfies { key: keyof Family; label: string; required?: boolean; widthClass: ColumnWidthClass }[];
const {
  available, selected, visible, widths, widthStyle, fixedFirstColumn, fixedPositions,
  fixedColumnClass, fixedColumnStyle, actionColumnStyle, pinningWarning, setTableFrame,
  preferenceWarning, columnViews,
} = useColumns(() => rows.value.length
  ? definitions.filter(column => column.key === "id" || (mode.value === "self" && column.key !== "userId")
    || rows.value.some(row => row[column.key] !== null)) : [],
  { pageId: "identity-sessions", tableId: "sessions", fixedActionWidth: 160 });

function parseRows(value: unknown, admin: boolean): Family[] {
  const payload = admin && isRecord(value) && value.authorized === true ? value.rows : admin ? null : value;
  if (!Array.isArray(payload)) throw new Error("会话响应格式无效，请重新读取。");
  const parsed = payload.map((item: unknown): Family => {
    if (!isRecord(item) || typeof item.id !== "string" || !item.id.trim())
      throw new Error("会话响应格式无效，请重新读取。");
    const record = item;
    function read<T>(key: string, guard: (value: unknown) => value is T, nullable = admin): T | null {
      const value = record[key];
      if (value == null && nullable) return null;
      if (!guard(value)) throw new Error("会话响应格式无效，请重新读取。");
      return value;
    }
    const text = (value: unknown): value is string => typeof value === "string" && value.length > 0;
    const date = (value: unknown): value is string => text(value) && Number.isFinite(Date.parse(value));
    const boolean = (value: unknown): value is boolean => typeof value === "boolean";
    return {
      id: item.id, userId: read("userId", text, true), version: read("version", text),
      client: read("client", (value): value is number => value === 0 || value === 1),
      current: read("current", boolean), createdAt: read("createdAt", date), lastActivityAt: read("lastActivityAt", date),
      expiresAt: read("expiresAt", date), revokedAt: read("revokedAt", date, true),
      requiresPasswordChange: read("requiresPasswordChange", boolean),
      state: read("state", (value): value is number => typeof value === "number" && [0, 1, 2, 3].includes(value)),
    };
  });
  if (new Set(parsed.map(row => row.id)).size !== parsed.length) throw new Error("会话响应格式无效，请重新读取。");
  return parsed;
}
function close() {
  if (dialog.value?.open) dialog.value.close();
  pending.value = null;
  if (returnFocus?.isConnected) returnFocus.focus();
  returnFocus = null;
}
function invalidate(preserveRows = false) {
  generation++; controller?.abort();
  if (!preserveRows) { rows.value = []; lastSuccessfulQuery = null; }
  loading.value = false; close();
}
function failure(reason: unknown, fallback: string) { error.value = reason instanceof Error ? reason.message : fallback; }
async function read() {
  const queryKey = currentQuery.value;
  const preserveRows = rows.value.length > 0 && lastSuccessfulQuery === queryKey;
  invalidate(preserveRows); error.value = ""; queryError.value = ""; success.value = "";
  if (!hasIdentity.value || (mode.value === "admin" && !adminAllowed.value)) return;
  const limit = pageSize.value;
  const from = filters.from ? new Date(filters.from) : null;
  const to = filters.to ? new Date(filters.to) : null;
  if (![25, 50, 100].includes(limit) || !Number.isInteger(offset.value) || offset.value < 0) {
    error.value = "每页条数须为 25、50 或 100。"; return;
  }
  if ((from && !Number.isFinite(from.getTime())) || (to && !Number.isFinite(to.getTime())) || (from && to && from >= to)) {
    error.value = "创建时间范围无效，结束时间必须晚于开始时间。"; return;
  }
  if (filters.state && (!stateFilterAllowed.value || !["active", "idle", "expired", "revoked"].includes(filters.state))) {
    error.value = "无权按此状态筛选。"; return;
  }
  if (filters.userId && (!userFilterAllowed.value || !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(filters.userId))) {
    error.value = "用户标识须为当前可读的 UUID。"; return;
  }
  const query = new URLSearchParams({ offset: String(offset.value), limit: String(limit) });
  if (filters.state) query.set("state", filters.state);
  if (from) query.set("from", from.toISOString());
  if (to) query.set("to", to.toISOString());
  if (filters.userId) query.set("userId", filters.userId);
  const current = generation;
  const admin = mode.value === "admin";
  controller = new AbortController(); loading.value = true;
  try {
    const value = await api<unknown>(`/api/identity/${admin ? "admin-sessions" : "sessions"}?${query}`, { signal: controller.signal });
    if (current === generation && queryKey === currentQuery.value) {
      rows.value = parseRows(value, admin);
      lastSuccessfulQuery = queryKey;
    }
  } catch (reason) {
    if (current === generation) {
      close();
      queryError.value = reason instanceof Error ? reason.message : "会话读取失败，请重试。";
      if (reason instanceof ApiError && [401, 403].includes(reason.status)) {
        rows.value = []; lastSuccessfulQuery = null;
      } else if (lastSuccessfulQuery !== queryKey) rows.value = [];
    }
  } finally { if (current === generation) loading.value = false; }
}
function switchMode(value: "self" | "admin") {
  if (busy.value || (value === "admin" && !adminAllowed.value)) return;
  invalidate(); mode.value = value;
  Object.assign(filters, { state: "", from: "", to: "", userId: "" }); pageSize.value = 25;
  offset.value = 0; void read();
}
function page(nextOffset: number, limit: number) {
  if (loading.value || busy.value) return;
  offset.value = nextOffset; pageSize.value = limit; void read();
}
function canRevoke(row: Family) {
  return row.version !== null && row.state !== 3 && (mode.value === "self" || canRead("platform.sessions.revoke"));
}
async function confirm(row: Family, event: Event) {
  if (busy.value || loading.value || !canRevoke(row)) return;
  returnFocus = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
  pending.value = { ...row }; const current = generation;
  await nextTick();
  if (current !== generation || !pending.value) return;
  dialog.value?.showModal(); cancel.value?.focus();
}
async function revoke() {
  const target = pending.value;
  if (!target || busy.value || !canRevoke(target) || !rows.value.some(row => row.id === target.id && row.version === target.version)) return;
  const current = generation; busy.value = true; error.value = ""; success.value = "";
  try {
    await api(`/api/identity/sessions/${encodeURIComponent(target.id)}/revoke`, {
      method: "POST", body: JSON.stringify({ expectedVersion: target.version }),
    });
    if (current !== generation) return;
    close();
    if (target.current === true) forgetIdentity();
    else await read();
    success.value = "所选会话已撤销。其他会话与已派发任务不会被误作取消。";
  } catch (reason) {
    if (current === generation) { invalidate(); failure(reason, "撤销未确认，请重新读取后手动重试。"); }
  } finally { busy.value = false; }
}
async function changePassword() {
  if (passwordBusy.value || !hasIdentity.value) return;
  error.value = ""; success.value = "";
  if (newPassword.value !== repeat.value) { error.value = "两次新密码不一致，请重新检查。"; return; }
  const size = Array.from(newPassword.value).length;
  if (size < 15 || size > 128) { error.value = "新密码须为 15 至 128 个 Unicode 字符。"; return; }
  const subject = sessionState.subject;
  const current = generation;
  passwordBusy.value = true;
  try {
    await api("/api/identity/password", { method: "POST", body: JSON.stringify({
      expectedVersion: subject?.credentialVersion, oldPassword: oldPassword.value, newPassword: newPassword.value,
    }) });
    oldPassword.value = ""; newPassword.value = ""; repeat.value = "";
    if (sessionState.subject === subject) forgetIdentity();
  } catch (reason) { if (current === generation) failure(reason, "改密未确认，请重新读取后手动重试。"); }
  finally { passwordBusy.value = false; }
}
function display(row: Family, key: string) {
  const value = row[key as keyof Family];
  if (value === null) return "—";
  if (key === "state") return ["活跃", "闲置", "已过期", "已撤销"][Number(value)] ?? "—";
  if (key === "client") return value === 0 ? "Web" : "PDA";
  if (typeof value === "boolean") return value ? "是" : "否";
  if (["createdAt", "lastActivityAt", "expiresAt", "revokedAt"].includes(key))
    return new Date(String(value)).toLocaleString("zh-CN", { hour12: false });
  return String(value);
}
function summary(id: string) { return id.length > 24 ? `${id.slice(0, 12)}…${id.slice(-8)}` : id; }
watch(filters, () => { invalidate(); offset.value = 0; error.value = ""; queryError.value = ""; success.value = ""; }, { flush: "sync" });
watch(() => JSON.stringify([sessionState.subject, sessionState.token]), () => {
  invalidate(); error.value = ""; queryError.value = ""; success.value = ""; offset.value = 0;
  oldPassword.value = ""; newPassword.value = ""; repeat.value = "";
  if (!adminAllowed.value) mode.value = "self";
  Object.assign(filters, { state: "", from: "", to: "", userId: "" }); pageSize.value = 25;
}, { flush: "sync" });
onMounted(read);
onUnmounted(invalidate);
</script>

<template>
  <section class="wes-page" aria-labelledby="sessions-title">
    <header><p class="eyebrow">我的账号</p><h1 id="sessions-title">会话与安全</h1></header>
    <p v-if="sessionState.subject?.requiresPasswordChange" class="notice" role="status">当前使用旧格式凭据，只有受限会话。请完成合规改密后重新登录，不能借此会话操作普通业务。</p>
    <p v-if="error" role="alert" class="error">{{ error }}</p><p v-if="success" role="status">{{ success }}</p>
    <nav class="toolbar" aria-label="会话范围">
      <button type="button" :aria-pressed="mode === 'self'" :disabled="busy" @click="switchMode('self')">我的会话</button>
      <button v-if="adminAllowed" type="button" data-test="admin-tab" :aria-pressed="mode === 'admin'" :disabled="busy" @click="switchMode('admin')">管理会话</button>
    </nav>
    <form data-test="session-filters" class="toolbar" aria-label="会话筛选" @submit.prevent="read">
      <label v-if="stateFilterAllowed" for="session-state">状态
        <select id="session-state" v-model="filters.state"><option value="">全部状态</option><option value="active">活跃</option>
          <option value="idle">闲置</option><option value="expired">已过期</option><option value="revoked">已撤销</option></select>
      </label>
      <label for="session-from">创建起始（本地时间，含）<input id="session-from" v-model="filters.from" type="datetime-local"></label>
      <label for="session-to">创建结束（本地时间，不含）<input id="session-to" v-model="filters.to" type="datetime-local"></label>
      <label v-if="userFilterAllowed" for="session-user">用户 UUID<input id="session-user" v-model.trim="filters.userId" type="text"></label>
      <button type="button" data-test="reload" :disabled="loading || busy" @click="read">重新读取会话</button>
      <span v-if="loading" role="status">正在读取当前会话状态</span>
    </form>
    <p class="hint">筛选变更后请重新读取。状态由服务端判定；管理范围与字段以服务端当前授权为准。</p>
    <ColumnSettings :columns="available" v-model="selected" v-model:widths="widths"
      v-model:fixed-first-column="fixedFirstColumn" v-model:fixed-positions="fixedPositions"
      :preference-warning="preferenceWarning" :column-views="columnViews" />
    <QueryState label="会话列表" :state="queryState" :busy="loading" :error-message="queryError"
      :stale-message="staleMessage" @retry="read">
      <template #empty><p>当前没有可展示的会话。请重新读取，必要时重新登录。</p></template>
      <div :ref="setTableFrame" class="table-scroll" :aria-busy="loading">
        <p class="table-fixed-notice" role="status">可视区域较窄，固定列暂时停用；请横向滚动查看，避免固定列遮挡内容。</p>
        <p v-if="pinningWarning" class="table-pinning-warning" role="status">{{ pinningWarning }}</p>
        <table class="table-fixed-actions"><caption>{{ mode === 'self' ? '当前账号的会话' : '当前授权范围内的管理会话' }}（第 {{ Math.floor(offset / pageSize) + 1 }} 页）</caption>
          <thead><tr><th v-for="column in visible" :key="column.key" scope="col" :class="fixedColumnClass(column.key)"
            :style="[widthStyle(column.key), fixedColumnStyle(column.key)]">{{ column.label }}</th><th scope="col" :style="actionColumnStyle">操作</th></tr></thead>
          <tbody><tr v-for="row in rows" :key="row.id">
            <td v-for="column in visible" :key="column.key" :class="fixedColumnClass(column.key)" :style="fixedColumnStyle(column.key)">
              <template v-if="column.key === 'id'">
                <span data-test="session-id" class="identifier" :title="row.id">{{ summary(row.id) }}</span>
                <details><summary>完整会话标识</summary><span class="identifier">{{ row.id }}</span></details>
              </template>
              <template v-else>{{ display(row, column.key) }}</template>
            </td>
            <td :style="actionColumnStyle"><button v-if="canRevoke(row)" data-test="revoke" :disabled="busy || loading || queryState === 'stale'" @click="confirm(row, $event)">撤销此会话</button></td>
          </tr></tbody>
        </table>
      </div>
    </QueryState>
    <RealtimePager label="会话分页" select-id="session-limit" :offset="offset" :limit="pageSize" :count="rows.length"
      :busy="loading || busy" :failed="!!queryError" @change="page" />
    <fieldset class="editor"><legend>修改密码并撤销全部既有会话</legend>
      <p>新密码不得包含泄漏值或最近 5 个已设置值（含当前值）。成功后所有浏览器 / PDA 会话需要重新登录。</p>
      <form @submit.prevent="changePassword" :aria-busy="passwordBusy">
        <label for="old-password">当前密码</label><input id="old-password" v-model="oldPassword" type="password" required autocomplete="current-password">
        <label for="new-password">新密码（15 至 128 个 Unicode 字符）</label><input id="new-password" v-model="newPassword" type="password" required autocomplete="new-password">
        <label for="repeat-password">再次输入新密码</label><input id="repeat-password" v-model="repeat" type="password" required autocomplete="new-password">
        <div class="actions"><button type="submit" class="primary" :disabled="passwordBusy || !hasIdentity">确认改密并退出全部会话</button></div>
      </form>
    </fieldset>
    <dialog ref="dialog" aria-labelledby="revoke-title" aria-describedby="revoke-description" @cancel="close" @close="pending = null">
      <h2 id="revoke-title">撤销所选会话</h2><p id="revoke-description">只撤销会话 <span class="identifier">{{ pending?.id }}</span>。该终端后续请求将被拒绝，但不会撤回已经派发的业务或设备命令。</p>
      <div class="actions"><button ref="cancel" @click="close">取消</button><button data-test="confirm-revoke" @click="revoke" :disabled="busy || !pending">确认撤销</button></div>
    </dialog>
  </section>
</template>

<style scoped>
.toolbar { flex-wrap: wrap; }
.identifier { overflow-wrap: anywhere; }
dialog { max-width: min(36rem, calc(100vw - 2rem)); }
details > summary { cursor: pointer; min-height: 2.75rem; padding-block: .5rem; }
</style>
