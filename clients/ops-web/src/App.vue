<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { RouterView, useRoute, useRouter } from "vue-router";
import DensityControl from "./components/DensityControl.vue";
import LoginPage from "./features/identity/LoginPage.vue";
import { useSessionActivity } from "./features/identity/useSessionActivity";
import { api, canRead, forgetIdentity, sessionState } from "./features/identity/identity-api";
import { moduleCatalog } from "./features/module-catalog";
import { getNavigation } from "./features/navigation";
import { useVisualPreferences, type DensityMode } from "./features/visual-preferences";
const route = useRoute();
const router = useRouter();
const loginTenant = ref("");
const activity = useSessionActivity();
const online = ref(navigator.onLine);
const error = ref("");
const catalogReloading = ref(false);
const navigationCollapsed = ref(true);
const visualPreferences = useVisualPreferences(import.meta.env.MODE);
const density = computed({
  get: () => visualPreferences.density.value,
  set: (mode: DensityMode) => visualPreferences.setDensity(mode),
});
const navigation = computed(() => getNavigation(moduleCatalog.isEnabled.bind(moduleCatalog), canRead));
function go(path: string) {
  if (!navigation.value.some(item => item.path === path)) path = "/account/sessions";
  void router.push(path).catch(failure => { error.value = failure instanceof Error ? failure.message : "导航失败，请重试。"; });
}
function connection() { online.value = navigator.onLine; }
function interaction(event: Event) { if (event.isTrusted) void activity.interact(); }
function startTargetTenantLogin(tenantId: string) { loginTenant.value = tenantId; void router.replace("/login"); }
async function reloadModuleCatalog() {
  catalogReloading.value = true;
  try { await moduleCatalog.load(true); }
  finally { catalogReloading.value = false; }
}
async function logout() {
  error.value = "";
  try { await api("/api/identity/logout", { method: "POST" }); forgetIdentity(); }
  catch (failure) { error.value = failure instanceof Error ? failure.message : "登出未确认，请重试。"; }
}
onMounted(() => { addEventListener("pointerdown", interaction); addEventListener("keydown", interaction); addEventListener("online", connection); addEventListener("offline", connection); });
onMounted(() => { void moduleCatalog.load(); });
onUnmounted(() => { removeEventListener("pointerdown", interaction); removeEventListener("keydown", interaction); removeEventListener("online", connection); removeEventListener("offline", connection); });
</script>
<template>
  <header class="app-header"><a href="/account/sessions" @click.prevent="go('/account/sessions')" class="brand">WES <span>运行管理</span></a>
    <span class="environment">受控软件环境 · 不代表现场控制许可</span>
    <div v-if="sessionState.subject" class="header-context">
      <span class="tenant-context"><span>当前租户</span><strong>{{ sessionState.subject.tenantId }}</strong></span>
      <DensityControl v-model="density" :storage-warning="visualPreferences.storageWarning.value" />
      <button type="button" @click="logout">退出当前会话</button>
    </div>
  </header>
  <p v-if="!online" role="status" class="notice">当前离线：仅可查看已加载内容，写入不会排队或重放。</p>
  <p v-if="error" role="alert" class="error">{{ error }}</p>
  <p v-if="activity.error.value" role="alert" class="error">{{ activity.error.value }}</p>
  <main v-if="!sessionState.subject || route.path === '/login'" class="login-main"><LoginPage :initial-tenant="loginTenant"
    :return-url="typeof route.query.returnUrl === 'string' ? route.query.returnUrl : undefined" @signed-in="go" /></main>
  <div v-else class="app-layout" :class="{ 'navigation-collapsed': navigationCollapsed }" :data-density="density">
    <aside class="sidebar">
      <div class="sidebar-context"><p class="eyebrow">当前租户</p><p class="identifier">{{ sessionState.subject.tenantId }}</p></div>
      <button type="button" class="navigation-toggle" data-test="navigation-toggle"
        :aria-expanded="!navigationCollapsed" aria-controls="operations-navigation"
        :aria-label="navigationCollapsed ? '展开管理导航' : '收起管理导航'"
        :title="navigationCollapsed ? '展开管理导航' : '收起管理导航'"
        @click="navigationCollapsed = !navigationCollapsed">
        <span aria-hidden="true">{{ navigationCollapsed ? "›" : "‹" }}</span>
        <span class="nav-toggle-label">{{ navigationCollapsed ? "展开导航" : "收起导航" }}</span>
      </button>
      <p v-if="moduleCatalog.state.status === 'loading' || moduleCatalog.state.status === 'unloaded'"
        role="status" class="hint">正在读取部署模块配置…</p>
      <div v-else-if="moduleCatalog.state.status === 'error'" role="alert" class="error">
        <p>{{ moduleCatalog.state.error }}</p>
        <button type="button" :disabled="catalogReloading" @click="reloadModuleCatalog">
          {{ catalogReloading ? "正在读取…" : "重新读取模块配置" }}
        </button>
      </div>
      <nav v-else id="operations-navigation" aria-label="管理导航">
        <a v-for="item in navigation" :key="item.path" :href="item.path"
          :aria-label="item.name" :title="item.name"
          :aria-current="route.path === item.path ? 'page' : undefined" @click.prevent="go(item.path)">
          <span class="nav-short" aria-hidden="true">{{ item.short }}</span><span class="nav-label">{{ item.name }}</span>
        </a>
      </nav>
      <p class="hint">菜单仅供导航。每次操作由服务端重新校验当前权限。</p></aside>
    <main class="workspace" :key="`${sessionState.subject.tenantId}:${sessionState.subject.userId}`">
      <RouterView v-slot="{ Component }"><component :is="Component" @reauthenticate="startTargetTenantLogin" /></RouterView>
    </main>
  </div>
</template>
