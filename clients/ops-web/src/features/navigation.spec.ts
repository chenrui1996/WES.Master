import { describe, expect, it } from "vitest";
import { getNavigation } from "./navigation";

describe("management navigation", () => {
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
