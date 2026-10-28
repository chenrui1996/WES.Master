import { afterEach, describe, expect, it, vi } from "vitest";
import { forgetIdentity, sessionState } from "./features/identity/identity-api";
import { moduleCatalog } from "./features/module-catalog";
import { router } from "./router";

afterEach(() => {
  vi.restoreAllMocks();
  forgetIdentity();
});

describe("module-aware routes", () => {
  it.each([["wms.outbound.read", "outbound"], ["wms.outbound.pick", "picking"], ["wms.counts.read", "counts"],
    ["wms.transfers.read", "transfers"]])("gates known WMS UUID route for %s", async (permission, page) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true); vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(true);
    await router.push(`/wms/${page}/77966ccd-f1dd-412d-8e45-adb475608842`);
    expect(router.currentRoute.value.params.id).toBe("77966ccd-f1dd-412d-8e45-adb475608842");
    await router.push("/account/sessions");
  });
  it.each([
    [true, "wms.transfers.read", "/wms/transfers"], [true, "wms.inventory.read", "/account/sessions"],
    [false, "wms.transfers.read", "/module-unavailable"],
  ])("gates transfer navigation with enabled=%s permission=%s", async (enabled, permission, expected) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true); vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(enabled);
    await router.push("/account/sessions"); await router.push("/wms/transfers");
    expect(router.currentRoute.value.path).toBe(expected); await router.push("/account/sessions");
  });
  it.each([
    [true, "wms.counts.read", "/wms/counts"], [true, "wms.inventory.read", "/account/sessions"],
    [false, "wms.counts.read", "/module-unavailable"],
  ])("gates count navigation with enabled=%s permission=%s", async (enabled, permission, expected) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true); vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(enabled);
    await router.push("/account/sessions"); await router.push("/wms/counts");
    expect(router.currentRoute.value.path).toBe(expected); await router.push("/account/sessions");
  });
  it("preserves the approved known-ID inbound detail route behind its own authority", async () => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission: "wms.inbound.read", scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true); vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(true);
    const id = "77966ccd-f1dd-412d-8e45-adb475608842";
    await router.push(`/wms/inbound/${id}`);
    expect(router.currentRoute.value.params.id).toBe(id);
    expect(router.currentRoute.value.path).toBe(`/wms/inbound/${id}`);
    await router.push("/account/sessions");
  });
  it.each([
    [true, "wms.inbound.read", "/wms/inbound"], [true, "wms.inventory.read", "/account/sessions"],
    [false, "wms.inbound.read", "/module-unavailable"],
  ])("gates inbound navigation with enabled=%s permission=%s", async (enabled, permission, expected) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true); vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(enabled);
    await router.push("/account/sessions"); await router.push("/wms/inbound");
    expect(router.currentRoute.value.path).toBe(expected); await router.push("/account/sessions");
  });
  it.each([
    [true, "wms.ledger.read", "/wms/ledger"], [true, "wms.inventory.read", "/account/sessions"],
    [false, "wms.ledger.read", "/module-unavailable"],
  ])("gates ledger navigation with enabled=%s permission=%s", async (enabled, permission, expected) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 2, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true); vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(enabled);
    await router.push("/account/sessions"); await router.push("/wms/ledger");
    expect(router.currentRoute.value.path).toBe(expected); await router.push("/account/sessions");
  });
  it.each([
    [true, "wms.inventory.read", "/wms/inventory"],
    [true, "wms.warehouses.read", "/account/sessions"],
    [false, "wms.inventory.read", "/module-unavailable"],
  ])("gates inventory navigation with enabled=%s permission=%s", async (enabled, permission, expected) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 2, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(enabled);
    await router.push("/account/sessions"); await router.push("/wms/inventory");
    expect(router.currentRoute.value.path).toBe(expected);
    await router.push("/account/sessions");
  });
  it.each([false, true])("fails closed for the multi-owner message route when catalog ready=%s but no owner is enabled", async ready => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission: "wms.messages.read", scope: 0, departments: [], fields: ["EventId"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(ready);
    const enabled = vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(false);
    await router.push("/account/sessions"); await router.push("/notifications/messages");
    expect(router.currentRoute.value.path).toBe("/module-unavailable");
    expect(router.currentRoute.value.query.reason).toBe(ready ? "disabled" : "catalog");
    if (ready) expect(router.currentRoute.value.query.module).toBe("Wms");
    else expect(enabled).not.toHaveBeenCalled();
    await router.push("/account/sessions");
  });
  it.each([
    ["wms.messages.read", "Wms", "/notifications/messages"],
    ["wms.messages.read", "Platform", "/account/sessions"],
    ["platform.messages.read", "Wms", "/account/sessions"],
  ])("pairs message permission %s with enabled owner %s", async (permission, module, expected) => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 0, departments: [], fields: ["EventId"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockImplementation(code => code === module);
    await router.push("/account/sessions");
    await router.push("/notifications/messages");
    expect(router.currentRoute.value.path).toBe(expected);
    await router.push("/account/sessions");
  });
  it.each([true, false])("gates direct Platform message navigation with read permission %s", async allowed => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: allowed ? [{ permission: "platform.messages.read", scope: 2, departments: [], fields: ["EventId"] }] : [] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(true);
    await router.push("/notifications/messages");
    expect(router.currentRoute.value.path).toBe(allowed ? "/notifications/messages" : "/account/sessions");
    await router.push("/account/sessions");
  });
  it.each(["wms.warehouses.read", "wms.locations.read"])("allows WMS details with either resource read permission %s", async permission => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission, scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(true);
    await router.push("/wms/locations");
    expect(router.currentRoute.value.path).toBe("/wms/locations");
    await router.push("/account/sessions");
  });

  it("denies WMS direct navigation without either permission", async () => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(true);
    await router.push("/wms/locations");
    expect(router.currentRoute.value.path).toBe("/account/sessions");
  });

  it("rejects disabled WMS direct navigation even when resource permission exists", async () => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission: "wms.locations.read", scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(false);
    await router.push("/wms/locations");
    expect(router.currentRoute.value.path).toBe("/module-unavailable");
    expect(router.currentRoute.value.query.module).toBe("Wms");
  });

  it("does not interpret a failed catalog as an enabled WMS module", async () => {
    sessionState.subject = { tenantId: "tenant", userId: "user", sessionId: "session", requiresPasswordChange: false,
      credentialVersion: "version", grants: [{ permission: "wms.locations.read", scope: 0, departments: [], fields: ["Id"] }] };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(false);
    const enabled = vi.spyOn(moduleCatalog, "isEnabled");
    await router.push("/wms/locations");
    expect(router.currentRoute.value.path).toBe("/module-unavailable");
    expect(router.currentRoute.value.query.reason).toBe("catalog");
    expect(enabled).not.toHaveBeenCalled();
  });

  it("redirects direct navigation to a disabled module before checking permissions", async () => {
    sessionState.subject = {
      tenantId: "tenant", userId: "user", sessionId: "session",
      requiresPasswordChange: false, credentialVersion: "version", grants: [],
    };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(true);
    vi.spyOn(moduleCatalog, "isEnabled").mockReturnValue(false);

    await router.push("/admin/users");

    expect(router.currentRoute.value.path).toBe("/module-unavailable");
    expect(router.currentRoute.value.query).toMatchObject({
      module: "Platform", reason: "disabled", returnUrl: "/admin/users",
    });
  });

  it("routes catalog failures to an explicit unavailable state", async () => {
    sessionState.subject = {
      tenantId: "tenant", userId: "user", sessionId: "session",
      requiresPasswordChange: false, credentialVersion: "version", grants: [],
    };
    vi.spyOn(moduleCatalog, "load").mockResolvedValue(false);
    const isEnabled = vi.spyOn(moduleCatalog, "isEnabled");

    await router.push("/admin/audit");

    expect(router.currentRoute.value.path).toBe("/module-unavailable");
    expect(router.currentRoute.value.query.reason).toBe("catalog");
    expect(isEnabled).not.toHaveBeenCalled();
  });

  it("does not gate the login or session routes on the module catalog", async () => {
    const load = vi.spyOn(moduleCatalog, "load");
    await router.push("/login");
    expect(router.currentRoute.value.path).toBe("/login");
    expect(load).not.toHaveBeenCalled();

    sessionState.subject = {
      tenantId: "tenant", userId: "user", sessionId: "session",
      requiresPasswordChange: false, credentialVersion: "version", grants: [],
    };
    await router.push("/account/sessions");
    expect(router.currentRoute.value.path).toBe("/account/sessions");
    expect(load).not.toHaveBeenCalled();
  });
});
