import type { ModuleCode } from "./module-catalog";

const entries = [
  { path: "/account/sessions", name: "会话与安全", short: "会" },
  { path: "/admin/users", name: "用户与部门", short: "用", module: "Platform", permission: ["platform.users.read"] },
  { path: "/admin/roles", name: "角色与权限", short: "角", module: "Platform", permission: ["platform.roles.read"] },
  { path: "/admin/tenants", name: "租户", short: "租", module: "Platform", permission: ["platform.tenants.read"] },
  { path: "/admin/configuration", name: "配置与语言", short: "配", module: "Platform", permission: ["platform.settings.read", "platform.dictionary.read"] },
  { path: "/admin/audit", name: "审计与登录事件", short: "审", module: "Platform", permission: ["platform.audit.read"] },
  { path: "/files", name: "暂存文件", short: "文", module: "Platform", permission: ["platform.files.read"] },
  { path: "/wms/locations", name: "仓库与库位详情", short: "仓", module: "Wms", permission: ["wms.warehouses.read", "wms.locations.read"] },
] as const;

export function getNavigation(
  isModuleEnabled: (module: ModuleCode) => boolean,
  canRead: (permission: string) => boolean,
) {
  return entries.filter(entry => {
    if (!("module" in entry)) return true;
    if (!isModuleEnabled(entry.module)) return false;
    return entry.permission.some(canRead);
  });
}
