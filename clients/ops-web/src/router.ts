import { createRouter, createWebHistory } from "vue-router";
import { canRead, sessionState } from "./features/identity/identity-api";
import LoginPage from "./features/identity/LoginPage.vue";

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", redirect: "/account/sessions" },
    { path: "/login", component: LoginPage },
    { path: "/account/sessions", component: () => import("./features/identity/SessionPage.vue") },
    { path: "/admin/users", component: () => import("./features/platform/UsersPage.vue"), meta: { permission: "platform.users.read" } },
    { path: "/admin/roles", component: () => import("./features/platform/RolesPage.vue"), meta: { permission: "platform.roles.read" } },
    { path: "/admin/tenants", component: () => import("./features/platform/TenantsPage.vue"), meta: { permission: "platform.tenants.read" } },
    { path: "/admin/configuration", component: () => import("./features/platform/SettingsPage.vue"), meta: { configuration: true } },
    { path: "/admin/audit", component: () => import("./features/platform/AuditPage.vue"), meta: { permission: "platform.audit.read" } },
    { path: "/:pathMatch(.*)*", redirect: "/account/sessions" },
  ],
});
router.beforeEach(to => {
  if (!sessionState.subject && to.path !== "/login") return { path: "/login", query: { returnUrl: to.path } };
  if (sessionState.subject && to.path === "/login") return "/account/sessions";
  if ((typeof to.meta.permission === "string" && !canRead(to.meta.permission))
    || (to.meta.configuration && !canRead("platform.settings.read") && !canRead("platform.dictionary.read")))
    return "/account/sessions";
});
