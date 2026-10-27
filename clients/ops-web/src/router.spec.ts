import { afterEach, describe, expect, it, vi } from "vitest";
import { forgetIdentity, sessionState } from "./features/identity/identity-api";
import { moduleCatalog } from "./features/module-catalog";
import { router } from "./router";

afterEach(() => {
  vi.restoreAllMocks();
  forgetIdentity();
});

describe("module-aware routes", () => {
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
