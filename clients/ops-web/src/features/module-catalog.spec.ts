import { describe, expect, it, vi } from "vitest";
import { moduleCodes, ModuleCatalog, parseModuleCatalog } from "./module-catalog";

function catalog(enabled: (typeof moduleCodes)[number][] = ["Platform"]) {
  return {
    modules: moduleCodes.map(code => ({ code, enabled: enabled.includes(code) })),
  };
}

describe("module catalog", () => {
  it("accepts the complete public allowlist and exposes only enabled module entries", async () => {
    const request = vi.fn(async () => new Response(JSON.stringify(catalog(["Platform", "Wms"]))));
    const client = new ModuleCatalog(request);

    expect(await client.load()).toBe(true);
    expect(client.state.status).toBe("ready");
    expect(client.isEnabled("Platform")).toBe(true);
    expect(client.isEnabled("Wms")).toBe(true);
    expect(client.isEnabled("Simulator")).toBe(false);
    expect(request).toHaveBeenCalledWith("/api/config/features", expect.objectContaining({
      cache: "no-store",
      credentials: "same-origin",
    }));
  });

  it("rejects missing, duplicate, unknown, and invalid catalog entries", () => {
    expect(() => parseModuleCatalog({ modules: [] })).toThrow("模块配置响应无效");
    expect(() => parseModuleCatalog({ modules: [
      ...catalog().modules.slice(0, -1),
      { code: "Platform", enabled: false },
    ] })).toThrow("模块配置响应无效");
    expect(() => parseModuleCatalog({ modules: [
      ...catalog().modules.slice(0, -1),
      { code: "Unknown", enabled: true },
    ] })).toThrow("模块配置响应无效");
    expect(() => parseModuleCatalog({ modules: [
      ...catalog().modules.slice(0, -1),
      { code: "Simulator", enabled: "true" },
    ] })).toThrow("模块配置响应无效");
  });

  it("never defaults modules open after a catalog failure and allows explicit retry", async () => {
    const request = vi.fn()
      .mockResolvedValueOnce(new Response("unavailable", { status: 503 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(catalog(["Platform"]))));
    const client = new ModuleCatalog(request);

    expect(await client.load()).toBe(false);
    expect(client.state.status).toBe("error");
    expect(client.state.error).toContain("HTTP 503");
    expect(client.isEnabled("Platform")).toBe(false);

    expect(await client.load(true)).toBe(true);
    expect(client.state.status).toBe("ready");
    expect(client.isEnabled("Platform")).toBe(true);
  });

  it("coalesces concurrent catalog reads into one request", async () => {
    let resolve: ((response: Response) => void) | undefined;
    const request = vi.fn(() => new Promise<Response>(done => { resolve = done; }));
    const client = new ModuleCatalog(request);

    const first = client.load();
    const second = client.load();
    resolve?.(new Response(JSON.stringify(catalog())));

    expect(await Promise.all([first, second])).toEqual([true, true]);
    expect(request).toHaveBeenCalledTimes(1);
  });
});
