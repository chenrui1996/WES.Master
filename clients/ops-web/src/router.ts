import { createRouter, createWebHistory } from "vue-router";
import { canRead, sessionState } from "./features/identity/identity-api";
import { moduleCatalog, type ModuleCode } from "./features/module-catalog";
import LoginPage from "./features/identity/LoginPage.vue";
import { messageOwners, type OwnerPermission } from "./features/notifications/message-owners";

declare module "vue-router" {
  interface RouteMeta {
    moduleCode?: ModuleCode;
    permission?: string;
    anyPermission?: readonly string[];
    configuration?: boolean;
    ownerPermissions?: readonly OwnerPermission[];
  }
}

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/", redirect: "/account/sessions" },
    { path: "/login", component: LoginPage },
    { path: "/module-unavailable", component: () => import("./features/ModuleUnavailablePage.vue") },
    { path: "/account/sessions", component: () => import("./features/identity/SessionPage.vue") },
    { path: "/admin/users", component: () => import("./features/platform/UsersPage.vue"), meta: { moduleCode: "Platform", permission: "platform.users.read" } },
    { path: "/admin/roles", component: () => import("./features/platform/RolesPage.vue"), meta: { moduleCode: "Platform", permission: "platform.roles.read" } },
    { path: "/admin/tenants", component: () => import("./features/platform/TenantsPage.vue"), meta: { moduleCode: "Platform", permission: "platform.tenants.read" } },
    { path: "/admin/configuration", component: () => import("./features/platform/SettingsPage.vue"), meta: { moduleCode: "Platform", configuration: true } },
    { path: "/admin/audit", component: () => import("./features/platform/AuditPage.vue"), meta: { moduleCode: "Platform", permission: "platform.audit.read" } },
    { path: "/files", component: () => import("./features/platform/FilesPage.vue"), meta: { moduleCode: "Platform", permission: "platform.files.read" } },
    { path: "/notifications/messages", component: () => import("./features/notifications/MessageDiagnosticsPage.vue"),
      meta: { ownerPermissions: messageOwners } },
    { path: "/wms/locations", component: () => import("./features/wms/MasterDataPage.vue"),
      meta: { moduleCode: "Wms", anyPermission: ["wms.warehouses.read", "wms.locations.read"] } },
    { path: "/wms/inventory", component: () => import("./features/wms/InventoryPage.vue"),
      meta: { moduleCode: "Wms", permission: "wms.inventory.read" } },
    { path: "/wms/ledger", component: () => import("./features/wms/StockLedgerPage.vue"),
      meta: { moduleCode: "Wms", permission: "wms.ledger.read" } },
    { path: "/:pathMatch(.*)*", redirect: "/account/sessions" },
  ],
});
router.beforeEach(async to => {
  if (!sessionState.subject && to.path !== "/login") return { path: "/login", query: { returnUrl: to.path } };
  if (sessionState.subject && to.path === "/login") return "/account/sessions";
  if (to.meta.ownerPermissions) {
    const owners = to.meta.ownerPermissions;
    if (!await moduleCatalog.load())
      return { path: "/module-unavailable", query: { module: owners[0]?.moduleCode, reason: "catalog", returnUrl: to.fullPath } };
    const enabled = owners.filter(owner => moduleCatalog.isEnabled(owner.moduleCode));
    if (!enabled.length)
      return { path: "/module-unavailable", query: { module: owners.find(owner => canRead(owner.permission))?.moduleCode ?? owners[0]?.moduleCode,
        reason: "disabled", returnUrl: to.fullPath } };
    if (!enabled.some(owner => canRead(owner.permission))) return "/account/sessions";
  }
  if (to.meta.moduleCode) {
    const moduleCode = to.meta.moduleCode;
    if (!await moduleCatalog.load())
      return { path: "/module-unavailable", query: { module: moduleCode, reason: "catalog", returnUrl: to.fullPath } };
    if (!moduleCatalog.isEnabled(moduleCode))
      return { path: "/module-unavailable", query: { module: moduleCode, reason: "disabled", returnUrl: to.fullPath } };
  }
  if ((typeof to.meta.permission === "string" && !canRead(to.meta.permission))
    || (to.meta.anyPermission && !to.meta.anyPermission.some(canRead))
    || (to.meta.configuration && !canRead("platform.settings.read") && !canRead("platform.dictionary.read")))
    return "/account/sessions";
});
