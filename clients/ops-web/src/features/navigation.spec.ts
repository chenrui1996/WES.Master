import { describe, expect, it } from "vitest";
import { getNavigation } from "./navigation";

describe("management navigation", () => {
  it.each([["wms.outbound.read", "/wms/outbound"], ["wms.outbound.pick", "/wms/picking"], ["wms.counts.read", "/wms/counts"]])("gates %s to its named WMS page", (permission, path) => {
    expect(getNavigation(module => module === "Wms", value => value === permission).map(item => item.path))
      .toEqual(["/account/sessions", path]);
    expect(getNavigation(module => module === "Platform", value => value === permission).map(item => item.path)).toEqual(["/account/sessions"]);
  });
  it("exposes inbound only with the Wms module and independent read permission", () => {
    expect(getNavigation(module => module === "Wms", permission => permission === "wms.inbound.read").map(item => item.path))
      .toEqual(["/account/sessions", "/wms/inbound"]);
    expect(getNavigation(module => module === "Platform", permission => permission === "wms.inbound.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
  });
  it("exposes stock history only with the Wms module and independent ledger permission", () => {
    expect(getNavigation(module => module === "Wms", permission => permission === "wms.ledger.read").map(item => item.path))
      .toEqual(["/account/sessions", "/wms/ledger"]);
    expect(getNavigation(module => module === "Platform", permission => permission === "wms.ledger.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
  });
  it("exposes inventory only through the Wms module and its independent read permission", () => {
    expect(getNavigation(module => module === "Wms", permission => permission === "wms.inventory.read").map(item => item.path))
      .toEqual(["/account/sessions", "/wms/inventory"]);
    expect(getNavigation(module => module === "Platform", permission => permission === "wms.inventory.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
  });
  it("allows WMS messages with its own module and permission, never a cross-owner pair", () => {
    expect(getNavigation(module => module === "Wms", permission => permission === "wms.messages.read").map(item => item.path))
      .toEqual(["/account/sessions", "/notifications/messages"]);
    expect(getNavigation(module => module === "Platform", permission => permission === "wms.messages.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
    expect(getNavigation(module => module === "Wms", permission => permission === "platform.messages.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
  });
  it("preserves Platform message diagnostics with its own module and read permission", () => {
    expect(getNavigation(module => module === "Platform", permission => permission === "platform.messages.read").map(item => item.path))
      .toEqual(["/account/sessions", "/notifications/messages"]);
    expect(getNavigation(() => false, permission => permission === "platform.messages.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
  });
  it.each(["wms.warehouses.read", "wms.locations.read"])("gates WMS details by module and either resource permission %s", permission => {
    expect(getNavigation(module => module === "Wms", value => value === permission).map(item => item.path))
      .toEqual(["/account/sessions", "/wms/locations"]);
    expect(getNavigation(() => false, value => value === permission).map(item => item.path)).toEqual(["/account/sessions"]);
  });
  it("keeps session navigation independent of modules and gates entries by module and permission", () => {
    const moduleEnabled = (module: string) => module === "Platform";
    const permissions = new Set(["platform.users.read", "platform.dictionary.read"]);
    const navigation = getNavigation(moduleEnabled, permission => permissions.has(permission));

    expect(navigation.map(item => item.path)).toEqual([
      "/account/sessions",
      "/admin/users",
      "/admin/configuration",
    ]);
  });

  it("does not show feature entries before the module catalog is ready", () => {
    const navigation = getNavigation(() => false, () => true);
    expect(navigation.map(item => item.path)).toEqual(["/account/sessions"]);
  });
  it("shows the staging file workspace only with its read permission and Platform module", () => {
    expect(getNavigation(() => true, permission => permission === "platform.files.read").map(item => item.path))
      .toEqual(["/account/sessions", "/files"]);
    expect(getNavigation(() => false, permission => permission === "platform.files.read").map(item => item.path))
      .toEqual(["/account/sessions"]);
  });
});
