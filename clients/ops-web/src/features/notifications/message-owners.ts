import type { ModuleCode } from "../module-catalog";

export interface OwnerPermission { moduleCode: ModuleCode; permission: string }
export const messageOwners = [
  { moduleCode: "Platform", permission: "platform.messages.read" },
  { moduleCode: "Wms", permission: "wms.messages.read" },
] as const satisfies readonly OwnerPermission[];
export type MessageOwner = typeof messageOwners[number]["moduleCode"];

export function availableMessageOwners(isEnabled: (module: ModuleCode) => boolean, canRead: (permission: string) => boolean) {
  return messageOwners.filter(owner => isEnabled(owner.moduleCode) && canRead(owner.permission));
}
