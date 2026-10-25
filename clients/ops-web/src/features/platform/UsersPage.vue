<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import ColumnSettings from "../../components/ColumnSettings.vue";
import QueryState from "../../components/QueryState.vue";
import { useColumns } from "../../components/useColumns";
import { api, ApiError, canRead, sessionState } from "../identity/identity-api";
import { useDirectoryCandidates } from "./useDirectoryCandidates";

interface UserRow { id: string; account: string | null; name: string | null; departmentId: string | null;
  enabled: boolean | null; version: string | null; credentialVersion: string | null; roleIds: string[] | null }
interface DepartmentRow { id: string; code: string | null; name: string | null; enabled: boolean | null; version: string | null;
  parentId?: string | null; parentName?: string | null }
interface RoleRow { id: string; code: string | null; enabled: boolean | null }
interface Page<T> { authorized: boolean; rows: T[] }
const rows = ref<UserRow[]>([]);
const departmentCatalog = useDirectoryCandidates<DepartmentRow>("/api/platform/departments");
const roleCatalog = useDirectoryCandidates<RoleRow>("/api/platform/roles");
const { rows: departments, more: moreDepartments } = departmentCatalog;
const { rows: roles, more: moreRoles } = roleCatalog;
const rolesReady = ref(false);
const loading = ref(false);
const busy = ref(false);
const error = ref("");
const queryError = ref("");
const candidateError = ref("");
const message = ref("");
const online = ref(navigator.onLine);
const offset = ref(0);
const filters = reactive({ account: "", name: "", departmentId: "", enabled: "" });
const selectedUser = ref<UserRow | null>(null);
const mode = ref<"create" | "edit" | "credential" | "disable-credential" | "roles" | null>(null);
const form = reactive({ account: "", name: "", departmentId: "", enabled: true, password: "", roleIds: [] as string[] });
const departmentEditor = ref<DepartmentRow | "new" | null>(null);
const departmentForm = reactive({ code: "", name: "", enabled: true, parentId: "" });
const parentEditor = ref<DepartmentRow | null>(null);
const parentValue = ref("");
let generation = 0;
let controller: AbortController | undefined;
const lastSuccessfulQuery = ref<string | null>(null);
const hasIdentity = computed(() => sessionState.subject !== null && sessionState.token !== null);
function queryFingerprint() {
  return JSON.stringify({
    tenantId: sessionState.subject?.tenantId,
    userId: sessionState.subject?.userId,
    grants: sessionState.subject?.grants,
    offset: offset.value,
    filters,
  });
}
const currentQuery = computed(queryFingerprint);
const queryState = computed(() => loading.value
  ? rows.value.length ? "stale" : "loading"
  : queryError.value ? rows.value.length ? "stale" : "error"
    : rows.value.length || error.value ? "ready" : "empty");
const queryStale = computed(() => loading.value || queryError.value.length > 0);
const emptyMessage = computed(() => lastSuccessfulQuery.value !== currentQuery.value
  ? "筛选条件已更改，请提交筛选以读取对应用户。"
  : filters.account || filters.name || filters.departmentId || filters.enabled
    ? "无匹配用户，请清除筛选。"
    : "尚无可读用户，可新建用户档案。");
const staleMessage = computed(() => loading.value
  ? "正在刷新；页面保留的是最近一次已加载的用户目录。"
  : `刷新失败，仍保留最近一次已加载的用户目录：${queryError.value}`);
const departmentCandidates = computed(() => departments.value.filter(row => row.enabled === true));
const roleCandidates = computed(() => roles.value.filter(row => row.enabled === true));
const departmentMap = computed(() => new Map(departments.value.map(row => [row.id, row])));
const departmentOutline = computed(() => departments.value.map(row => {
  const path = [row]; const seen = new Set([row.id]); let parent = row.parentId;
  while (parent && !seen.has(parent)) {
    seen.add(parent); const node = departmentMap.value.get(parent); if (!node) break;
    path.unshift(node); parent = node.parentId;
  }
  return { row, depth: path.length - 1, key: path.map(node => node.code ?? node.id).join("/") };
}).sort((left, right) => left.key < right.key ? -1 : left.key > right.key ? 1 : 0));
const parentCandidates = computed(() => departmentCandidates.value.filter(row => {
  if (!departmentWritable(["ParentId"], row.id)) return false;
  const seen = new Set<string>(); let node: DepartmentRow | undefined = row;
  while (node) {
    if (node.id === parentEditor.value?.id || seen.has(node.id)) return false;
    seen.add(node.id); node = node.parentId ? departmentMap.value.get(node.parentId) : undefined;
  }
  return true;
}));
const unavailableRoles = computed(() => form.roleIds.filter(id => !roleCandidates.value.some(role => role.id === id)));
const createFields = ["Account", "Name", "DepartmentId", "Enabled"];
const profileFields = ["Name", "DepartmentId", "Enabled"];
const canCreateUser = computed(() => departmentCandidates.value.some(department => writable(createFields, undefined, department.id)));
const profileDepartments = computed(() => departmentCandidates.value.filter(department =>
  writable(mode.value === "create" ? createFields : profileFields, selectedUser.value ?? undefined, department.id)));
const {
  available, selected, visible, widths, widthStyle, fixedFirstColumn, fixedPositions,
  fixedColumnClass, fixedColumnStyle, actionColumnStyle, pinningWarning, setTableFrame,
  preferenceWarning, columnViews,
} = useColumns(() => rows.value.length ? [
  { key: "id", label: "用户标识", required: true, widthClass: "identity" },
  ...rows.value.some(row => row.account !== null) ? [{ key: "account", label: "账号", widthClass: "identity" as const }] : [],
  ...rows.value.some(row => row.name !== null) ? [{ key: "name", label: "姓名", widthClass: "identity" as const }] : [],
  ...rows.value.some(row => row.departmentId !== null) ? [{ key: "departmentId", label: "部门", widthClass: "identity" as const }] : [],
  ...rows.value.some(row => row.enabled !== null) ? [{ key: "enabled", label: "状态", widthClass: "status" as const }] : [],
  ...rows.value.some(row => row.roleIds !== null) ? [{ key: "roleIds", label: "角色", widthClass: "description" as const }] : [],
] : [], { pageId: "platform-users", tableId: "directory", fixedActionWidth: 160 });
function closeEditor() {
  mode.value = null; selectedUser.value = null;
  Object.assign(form, { account: "", name: "", departmentId: "", enabled: true, password: "", roleIds: [] });
}
function clear(preserveRows = false) {
  generation++; controller?.abort();
  if (!preserveRows) {
    rows.value = []; lastSuccessfulQuery.value = null;
  }
  departmentCatalog.reset(); roleCatalog.reset(); rolesReady.value = false;
  closeEditor(); departmentEditor.value = null; parentEditor.value = null; parentValue.value = "";
  Object.assign(departmentForm, { code: "", name: "", enabled: true, parentId: "" });
  loading.value = false; busy.value = false; queryError.value = "";
}
function fail(reason: unknown) {
  if (reason instanceof ApiError && [403, 412, 428].includes(reason.status)) clear();
  error.value = reason instanceof Error ? reason.message : "用户操作失败。请重新读取后检查权限。";
  if (reason instanceof ApiError && [412, 428].includes(reason.status)) error.value = `版本已失效，请重新读取后显式提交。${error.value}`;
}
async function load() {
  if (!hasIdentity.value || !canRead("platform.users.read")) return;
  const queryKey = currentQuery.value;
  const preserveRows = rows.value.length > 0 && lastSuccessfulQuery.value === queryKey;
  clear(preserveRows); error.value = ""; queryError.value = "";
  candidateError.value = "关联候选读取失败或无权查看。请重新读取；未加载的关联不能提交。"; message.value = "";
  const current = generation;
  controller = new AbortController();
  const signal = controller.signal;
  loading.value = true;
  const query = new URLSearchParams({ offset: String(offset.value), limit: "50" });
  for (const key of ["account", "name", "departmentId", "enabled"] as const)
    if (filters[key]) query.set(key, filters[key]);
  async function candidates() {
    const results = await Promise.allSettled([
      canRead("platform.departments.read") ? departmentCatalog.read(signal) : Promise.resolve(null),
      canRead("platform.roles.read") ? roleCatalog.read(signal) : Promise.resolve(null),
    ]);
    if (current !== generation) return;
    const [departmentResult, roleResult] = results;
    if (departmentResult.status === "fulfilled" && departmentResult.value?.authorized === true)
      departments.value = departmentResult.value.rows;
    if (roleResult.status === "fulfilled" && roleResult.value?.authorized === true) {
      roles.value = roleResult.value.rows;
      rolesReady.value = true;
    }
    if (results.some(result => result.status === "rejected" || result.value?.authorized === false))
      candidateError.value = "关联候选读取失败或无权查看。请重新读取；未加载的关联不能提交。";
  }
  const candidateLoad = candidates();
  try {
    const response = await api<Page<UserRow>>(`/api/platform/users?${query}`, { signal });
    if (current !== generation) return;
    if (response.authorized !== true || !Array.isArray(response.rows)) throw new ApiError(403, "DirectoryDenied", "未提供");
    rows.value = response.rows;
    lastSuccessfulQuery.value = queryKey;
  } catch (reason) {
    if (current === generation) {
      fail(reason);
      queryError.value = error.value;
    }
  }
  finally {
    await candidateLoad;
    if (current === generation) loading.value = false;
  }
}
async function moreCandidates(kind: "departments" | "roles") {
  if (busy.value || loading.value || !controller) return;
  const current = generation;
  const catalog = kind === "departments" ? departmentCatalog : roleCatalog;
  candidateError.value = "关联候选读取失败或无权查看。请重新读取；未加载的关联不能提交。";
  try {
    await catalog.read(controller.signal, true);
  } catch (reason) {
    if (current === generation) candidateError.value = `后续关联候选读取失败，请重试。${reason instanceof Error ? reason.message : ""}`;
  }
}
function writable(fields: string[], row?: UserRow, targetDepartmentId?: string) {
  const subject = sessionState.subject;
  const departmentId = targetDepartmentId ?? row?.departmentId;
  const allowed = new Set(subject?.grants.filter(grant => grant.permission === "platform.users.write"
    && (grant.scope === 2 || grant.scope === 0 && row?.id === subject.userId
      || grant.scope === 1 && departmentId && grant.departments.includes(departmentId))).flatMap(grant => grant.fields));
  return fields.every(field => allowed.has(field));
}
function departmentAllowed(action: "read" | "write", fields: string[], id?: string) {
  const allowed = new Set(sessionState.subject?.grants.filter(grant => grant.permission === `platform.departments.${action}`
    && (grant.scope === 2 || grant.scope === 1 && id && grant.departments.includes(id))).flatMap(grant => grant.fields));
  return fields.every(field => allowed.has(field));
}
function departmentWritable(fields: string[], id?: string) { return departmentAllowed("write", fields, id); }
function open(next: NonNullable<typeof mode.value>, row?: UserRow) {
  if (busy.value || queryStale.value) return;
  closeEditor(); departmentEditor.value = null; parentEditor.value = null; mode.value = next;
  selectedUser.value = row ? { ...row, roleIds: row.roleIds ? [...row.roleIds] : null } : null;
  if (row) Object.assign(form, { account: row.account ?? "", name: row.name ?? "",
    departmentId: profileDepartments.value.some(item => item.id === row.departmentId) ? row.departmentId : "",
    enabled: row.enabled === true, roleIds: row.roleIds ? [...row.roleIds] : [] });
  error.value = ""; message.value = "";
}
async function write(path: string, method: string, body: object,
  successMessage = "已保存。列表已重新读取；新用户仍需单独设置凭据。", allowNoContent = false) {
  if (busy.value) return;
  if (!online.value) { error.value = "当前离线，未发送写入。恢复连接后请重新读取。"; return; }
  const current = generation;
  const identity = sessionState.subject;
  busy.value = true; error.value = ""; message.value = "";
  try {
    const result = await api<{ decision: number } | undefined>(path, { method, body: JSON.stringify(body), signal: controller?.signal });
    if (current !== generation) return;
    if (result === undefined ? !allowNoContent : result.decision !== 0)
      throw new Error("写入未被接受。请重新读取后检查输入及权限。");
    await load();
    if (identity === sessionState.subject) message.value = successMessage;
  } catch (reason) { if (current === generation) fail(reason); }
  finally { if (current === generation) busy.value = false; }
}
function submitUser() {
  const row = selectedUser.value;
  if (mode.value === "disable-credential") {
    if (!row?.credentialVersion || row.enabled !== true || !writable(["Enabled"], row)) {
      error.value = "当前凭据版本或禁用权限不可用，请重新读取。"; return;
    }
    void write(`/api/platform/users/${encodeURIComponent(row.id)}/disable`, "POST",
      { expectedVersion: row.credentialVersion }, "登录凭据已停用，现有会话已撤销；列表已重新读取。", true);
    return;
  }
  if (mode.value === "credential") {
    if (!row?.version || !form.password || !writable(["Credential"], row)) { error.value = "需要当前版本、凭据权限及新密码。"; return; }
    const password = form.password; form.password = "";
    void write(`/api/platform/users/${encodeURIComponent(row.id)}/credential`, "POST", { expectedVersion: row.version, password });
    return;
  }
  if (mode.value === "roles") {
    if (!rolesReady.value || !row?.version || row.roleIds === null || !writable(["Roles"], row)
      || form.roleIds.some(id => !roleCandidates.value.some(role => role.id === id))) { error.value = "角色或版本不可用，请重新读取。"; return; }
    void write(`/api/platform/users/${encodeURIComponent(row.id)}/roles`, "PUT", { expectedVersion: row.version, roleIds: form.roleIds });
    return;
  }
  if (!form.name.trim() || !profileDepartments.value.some(item => item.id === form.departmentId)
    || mode.value === "create" && !form.account.trim()) { error.value = "请填写账号、姓名并选择当前启用部门。"; return; }
  if (mode.value === "create" && writable(createFields, undefined, form.departmentId)) {
    void write("/api/platform/users", "POST", { account: form.account.trim(), name: form.name.trim(), departmentId: form.departmentId });
  } else if (row?.version && writable(profileFields, row) && writable(profileFields, row, form.departmentId)) {
    void write(`/api/platform/users/${encodeURIComponent(row.id)}`, "PUT", {
      expectedVersion: row.version, name: form.name.trim(), departmentId: form.departmentId, enabled: form.enabled,
    });
  } else error.value = "没有当前可写版本或权限，请重新读取。";
}
function openDepartment(row?: DepartmentRow) {
  if (busy.value) return;
  closeEditor(); parentEditor.value = null; departmentEditor.value = row ? { ...row } : "new";
  Object.assign(departmentForm, { code: row?.code ?? "", name: row?.name ?? "", enabled: row?.enabled ?? true, parentId: "" });
}
function submitDepartment() {
  const row = departmentEditor.value;
  if (!row || !departmentForm.name.trim() || row === "new" && !departmentForm.code.trim()) {
    error.value = "请填写部门编码和名称。"; return;
  }
  if (row === "new" && departmentWritable(["Code", "Name", "Enabled"]))
    void write("/api/platform/departments", "POST", { code: departmentForm.code.trim(), name: departmentForm.name.trim(),
      ...(departmentForm.parentId ? { parentId: departmentForm.parentId } : {}) });
  else if (row !== "new" && row.version && departmentWritable(["Name", "Enabled"], row.id)) void write(`/api/platform/departments/${encodeURIComponent(row.id)}`, "PUT", {
    expectedVersion: row.version, name: departmentForm.name.trim(), enabled: departmentForm.enabled,
  });
  else error.value = "部门版本或权限不可用，请重新读取。";
}
function openParent(row: DepartmentRow) {
  if (busy.value || !row.version || !departmentAllowed("read", ["ParentId"], row.id) || !departmentWritable(["ParentId"], row.id)) return;
  closeEditor(); departmentEditor.value = null; parentEditor.value = { ...row }; parentValue.value = row.parentId ?? "";
}
function submitParent() {
  const row = parentEditor.value;
  if (!row?.version || !departmentWritable(["ParentId"], row.id)
    || parentValue.value && !parentCandidates.value.some(candidate => candidate.id === parentValue.value)) {
    error.value = "上级候选或当前版本不可用，请重新读取并选择可写的启用部门。"; return;
  }
  void write(`/api/platform/departments/${encodeURIComponent(row.id)}/parent`, "PUT",
    { expectedVersion: row.version, parentId: parentValue.value || null });
}
function departmentName(id: string | null) {
  return id === null ? "未授权" : departments.value.find(row => row.id === id)?.name ?? id;
}
function page(delta: number) { if (busy.value || loading.value) return; offset.value = Math.max(0, offset.value + delta); void load(); }
function connectivity() {
  online.value = navigator.onLine;
  if (!online.value) {
    clear();
    error.value = "当前离线，已清除编辑和关联选择。恢复连接后请重新读取。";
    queryError.value = error.value;
  }
}
window.addEventListener("online", connectivity); window.addEventListener("offline", connectivity);
watch(filters, () => {
  if (lastSuccessfulQuery.value !== currentQuery.value) {
    rows.value = [];
    lastSuccessfulQuery.value = null;
    queryError.value = "";
    error.value = "";
  }
}, { flush: "sync" });
watch([() => sessionState.subject, () => sessionState.token], () => {
  clear(); offset.value = 0; error.value = ""; candidateError.value = "关联候选读取失败或无权查看。请重新读取；未加载的关联不能提交。"; message.value = "";
  Object.assign(filters, { account: "", name: "", departmentId: "", enabled: "" });
  if (hasIdentity.value) void load();
}, { deep: true, immediate: true, flush: "sync" });
onBeforeUnmount(() => {
  clear(); window.removeEventListener("online", connectivity); window.removeEventListener("offline", connectivity);
});
</script>

<template>
  <section class="wes-page" aria-labelledby="users-title" :aria-busy="loading || busy">
    <header><h1 id="users-title">用户与部门</h1><p class="hint">用户档案与登录凭据分别管理。</p></header>
    <p v-if="!hasIdentity || !canRead('platform.users.read')" role="status">无权读取用户目录，请使用有权账号登录。</p>
    <template v-else>
      <p v-if="!online" class="notice" role="status">当前离线，只能在恢复连接后重新读取。</p>
      <p v-if="error && !queryError" role="alert" class="error">{{ error }}</p>
      <p v-if="candidateError" role="alert" class="notice">{{ candidateError }}</p>
      <p v-if="message" role="status">{{ message }}</p>
      <form class="toolbar" aria-label="用户筛选" @submit.prevent="offset = 0; load()">
        <label>账号<input v-model="filters.account" name="filterAccount" maxlength="128" /></label>
        <label>姓名<input v-model="filters.name" name="filterName" maxlength="128" /></label>
        <label>部门<select v-model="filters.departmentId"><option value="">全部可读部门</option>
          <option v-for="department in departmentCandidates" :key="department.id" :value="department.id">{{ department.name ?? department.code ?? department.id }}</option></select></label>
        <label>状态<select v-model="filters.enabled"><option value="">全部</option><option value="true">启用</option><option value="false">停用</option></select></label>
        <button type="submit">筛选 / 重新读取</button>
        <button type="button" @click="Object.assign(filters, { account: '', name: '', departmentId: '', enabled: '' }); offset = 0; load()">清除筛选</button>
      </form>
      <p v-if="filters.account || filters.name || filters.departmentId || filters.enabled" class="hint">当前筛选：{{ filters.account }} {{ filters.name }} {{ departmentName(filters.departmentId || null) }} {{ filters.enabled === 'true' ? '启用' : filters.enabled === 'false' ? '停用' : '' }}</p>
            <div class="actions">
        <button v-if="canCreateUser" data-test="new-user" @click="open('create')">新建用户</button>
        <button v-if="moreDepartments" type="button" data-test="more-departments" :disabled="busy || loading || departmentCatalog.loading.value" @click="moreCandidates('departments')">加载更多部门候选</button>
        <button v-if="moreRoles" type="button" data-test="more-roles" :disabled="busy || loading || roleCatalog.loading.value" @click="moreCandidates('roles')">加载更多角色候选</button>
      </div>
      <QueryState label="用户目录" :state="queryState" :busy="loading" :error-message="queryError"
        :stale-message="staleMessage" @retry="load">
        <template #empty>
          <p>{{ emptyMessage }}</p>
        </template>
        <template v-if="rows.length">
          <ColumnSettings :columns="available" v-model="selected" v-model:widths="widths"
            v-model:fixed-first-column="fixedFirstColumn" v-model:fixed-positions="fixedPositions"
            :preference-warning="preferenceWarning" :column-views="columnViews" />
          <div :ref="setTableFrame" class="table-scroll" tabindex="0" aria-label="用户目录">
            <p class="table-fixed-notice" role="status">可视区域较窄，固定列暂时停用；请横向滚动查看，避免固定列遮挡内容。</p>
            <p v-if="pinningWarning" class="table-pinning-warning" role="status">{{ pinningWarning }}</p>
            <table class="table-fixed-actions"><caption class="hint">当前页用户</caption><thead><tr><th v-for="column in visible" :key="column.key" scope="col" :class="fixedColumnClass(column.key)" :style="[widthStyle(column.key), fixedColumnStyle(column.key)]">{{ column.label }}</th><th scope="col" :style="actionColumnStyle">操作</th></tr></thead>
              <tbody><tr v-for="row in rows" :key="row.id">
                <td v-for="column in visible" :key="column.key" :class="fixedColumnClass(column.key)" :style="fixedColumnStyle(column.key)">
                  <template v-if="column.key === 'id'">{{ row.id }}</template>
                  <template v-else-if="column.key === 'account'">{{ row.account ?? '未授权' }}</template>
                  <template v-else-if="column.key === 'name'">{{ row.name ?? '未授权' }}</template>
                  <template v-else-if="column.key === 'departmentId'">{{ departmentName(row.departmentId) }}</template>
                  <template v-else-if="column.key === 'enabled'">{{ row.enabled === null ? '未授权' : row.enabled ? '启用' : '停用' }}</template>
                  <template v-else-if="column.key === 'roleIds'">{{ row.roleIds === null ? '未授权' : row.roleIds.map(id => roles.find(role => role.id === id)?.code ?? id).join('、') || '未关联' }}</template>
                </td>
                <td :style="actionColumnStyle"><div class="row-actions">
                  <button v-if="row.version && row.name !== null && row.departmentId !== null && row.enabled !== null && writable(['Name', 'DepartmentId', 'Enabled'], row)" data-test="edit-user" :disabled="busy || queryStale" @click="open('edit', row)">编辑</button>
                  <button v-if="row.version && row.enabled === true && writable(['Credential'], row)" data-test="credential-user" :disabled="busy || queryStale" @click="open('credential', row)">设置初始凭据</button>
                  <button v-if="row.credentialVersion && row.enabled === true && writable(['Enabled'], row)" type="button" data-test="disable-credential" :disabled="busy || queryStale" @click="open('disable-credential', row)">停用登录凭据</button>
                  <button v-if="row.version && row.roleIds !== null && writable(['Roles'], row)" data-test="roles-user" :disabled="busy || queryStale" @click="open('roles', row)">关联角色</button>
                </div></td>
              </tr></tbody>
            </table>
          </div>
        </template>
      </QueryState>
      <nav class="actions" aria-label="用户分页"><button v-if="offset > 0" @click="page(-50)">上一页</button><span>第 {{ offset / 50 + 1 }} 页，每页最多 50 条</span><button v-if="rows.length === 50" @click="page(50)">下一页</button></nav>
      <form v-if="mode" data-test="user-form" class="editor" @submit.prevent="submitUser">
        <fieldset :disabled="busy"><legend>{{ mode === 'create' ? '新建用户' : mode === 'edit' ? '用户档案' : mode === 'credential' ? '初始登录凭据' : mode === 'disable-credential' ? '停用登录凭据' : '用户角色' }}</legend>
          <template v-if="mode === 'create' || mode === 'edit'">
            <label v-if="mode === 'create'">账号<input v-model="form.account" name="account" required maxlength="128" autocomplete="off" /></label>
            <p v-else>账号：{{ selectedUser?.account ?? '未授权' }}</p>
            <label>姓名<input v-model="form.name" name="userName" required maxlength="128" /></label>
            <label>部门<select v-model="form.departmentId" name="departmentId" required><option value="">选择启用部门</option>
              <option v-for="department in profileDepartments" :key="department.id" :value="department.id">{{ department.name ?? department.code ?? department.id }}</option></select></label>
            <p class="hint">部门候选每次读取 50 项，可继续加载；仅显示启用且可写的部门。</p>
            <label v-if="mode === 'edit'" class="check"><input v-model="form.enabled" type="checkbox" />启用用户</label>
            <p v-if="mode === 'edit'" class="hint">停用会撤销目标用户的现有会话；重新启用档案不能恢复已停用凭据。</p>
          </template>
          <template v-else-if="mode === 'credential'">
            <p>为 {{ selectedUser?.account ?? selectedUser?.id }} 创建首次凭据，不替换已有密码。</p>
            <label>新密码<input v-model="form.password" name="password" type="password" autocomplete="new-password" required /></label>
            <p class="hint">密码只发送一次；拒绝后需重新输入，不保留密码。</p>
          </template>
          <template v-else-if="mode === 'disable-credential'">
            <p>确定停用 {{ selectedUser?.account ?? selectedUser?.id }} 的登录凭据？此操作不修改用户档案或角色。</p>
            <p class="notice">该用户的全部现有会话将被撤销；停用后不能通过此页面重新启用凭据。</p>
          </template>
          <template v-else>
            <p>替换 {{ selectedUser?.account ?? selectedUser?.id }} 的角色关联。提交前请核对，移除角色立即影响权限。</p>
            <p class="hint">角色候选每次读取 50 项，可继续加载；不可选或尚未加载的已有角色不会自动移除。</p>
            <label v-for="role in roleCandidates" :key="role.id" class="check"><input v-model="form.roleIds" type="checkbox" :value="role.id" />{{ role.code ?? role.id }}</label>
            <p v-for="id in unavailableRoles" :key="id">当前关联不可选角色：{{ roles.find(role => role.id === id)?.code ?? id }}。
              保留会阻止提交；如需移除，请显式确认。
              <button type="button" data-test="remove-unavailable-role" @click="form.roleIds = form.roleIds.filter(roleId => roleId !== id)">移除关联</button>
            </p>
            <p v-if="!rolesReady" class="notice">角色候选未获授权或读取失败，不能提交关联。请重新读取。</p>
            <p v-if="!roleCandidates.length" class="hint">没有已加载的启用角色。空列表提交会移除关联。</p>
          </template>
          <div class="actions"><button type="button" @click="closeEditor">取消</button><button type="submit" class="primary">{{ busy ? '正在提交' : mode === 'credential' ? '确认创建凭据' : mode === 'disable-credential' ? '确认停用凭据' : mode === 'roles' ? '确认替换角色' : '保存用户' }}</button></div>
        </fieldset>
      </form>
      <details v-if="canRead('platform.departments.read')" class="department-section"><summary>部门管理（当前已加载页）</summary>
        <button v-if="departmentWritable(['Code', 'Name', 'Enabled'])" @click="openDepartment()">新建部门</button>
        <p class="hint">层级只表达组织关系，不继承权限或级联停用。上级在其他页或不可读时仅显示已授权投影。</p>
        <ul aria-label="当前页部门树"><li v-for="node in departmentOutline" :key="node.row.id" :data-department="node.row.id" :data-depth="node.depth"
          :style="{ paddingInlineStart: `${Math.min(node.depth, 8) * 1.25}rem` }">
          {{ node.row.name ?? node.row.code ?? node.row.id }} · {{ node.row.enabled === null ? '状态未授权' : node.row.enabled ? '启用' : '停用' }}
          <span v-if="node.row.parentId"> · 上级：{{ node.row.parentName ?? '上级未加载 / 未授权' }} · 已加载层级 {{ node.depth + 1 }}</span>
          <button v-if="departmentWritable(['Name', 'Enabled'], node.row.id) && node.row.version && node.row.name !== null && node.row.enabled !== null" @click="openDepartment(node.row)">编辑部门</button>
          <button v-if="departmentWritable(['ParentId'], node.row.id) && departmentAllowed('read', ['ParentId'], node.row.id) && node.row.version && node.row.parentId !== undefined"
            data-test="move-department" :data-id="node.row.id" @click="openParent(node.row)">调整上级</button>
        </li></ul>
        <form v-if="departmentEditor" class="editor" @submit.prevent="submitDepartment"><fieldset :disabled="busy"><legend>部门资料</legend>
          <label v-if="departmentEditor === 'new'">部门编码<input v-model="departmentForm.code" required maxlength="64" /></label>
          <label>部门名称<input v-model="departmentForm.name" required maxlength="128" /></label>
          <label v-if="departmentEditor === 'new' && departmentWritable(['ParentId'])">上级部门<select v-model="departmentForm.parentId">
            <option value="">无上级（根部门）</option><option v-for="candidate in parentCandidates" :key="candidate.id" :value="candidate.id">{{ candidate.name ?? candidate.id }}</option>
          </select></label>
          <label v-if="departmentEditor !== 'new'" class="check"><input v-model="departmentForm.enabled" type="checkbox" />启用部门</label>
          <p v-if="departmentEditor !== 'new'" class="notice">停用部门会阻止其成员访问。请核对影响后保存。</p>
          <div class="actions"><button type="button" @click="departmentEditor = null">取消</button><button type="submit">保存部门</button></div>
        </fieldset></form>
        <form v-if="parentEditor" data-test="department-parent-form" class="editor" @submit.prevent="submitParent">
          <fieldset :disabled="busy"><legend>调整 {{ parentEditor.name ?? parentEditor.id }} 的上级</legend>
            <p>版本：{{ parentEditor.version }}。当前上级：{{ parentEditor.parentName ?? parentEditor.parentId ?? '根部门' }}；提交由服务端再次检查循环、范围与版本。</p>
            <label for="department-parent">新的上级部门</label><select id="department-parent" v-model="parentValue">
              <option value="">无上级（根部门）</option>
              <option v-if="parentValue && !parentCandidates.some(candidate => candidate.id === parentValue)" :value="parentValue" disabled>当前上级未在可写候选中，请明确重新选择</option>
              <option v-for="candidate in parentCandidates" :key="candidate.id" :value="candidate.id">{{ candidate.name ?? candidate.id }}</option>
            </select><div class="actions"><button type="button" @click="parentEditor = null">取消</button><button type="submit">确认调整此版本</button></div>
          </fieldset>
        </form>
      </details>
    </template>
  </section>
</template>

<style scoped>
section, .editor { min-width: 0; }
.row-actions { display: flex; flex-wrap: wrap; gap: .5rem; }
.check { display: flex; min-height: 44px; align-items: center; gap: .5rem; margin: 0; }
.check input { width: 20px; min-width: 20px; min-height: 20px; }
button { white-space: nowrap; }
.department-section { margin-top: var(--wes-space-4); }
summary { cursor: pointer; min-height: 44px; }
li { padding-block: .5rem; overflow-wrap: anywhere; }
</style>
