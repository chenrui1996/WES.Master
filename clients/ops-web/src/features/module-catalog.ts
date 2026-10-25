import { reactive } from "vue";
import { isRecord } from "./identity/identity-api";

export const moduleCodes = [
  "Platform",
  "Wms",
  "Orchestration",
  "WcsStacker",
  "WcsFourWay",
  "Simulator",
] as const;

export type ModuleCode = typeof moduleCodes[number];
export type ModuleCatalogStatus = "unloaded" | "loading" | "ready" | "error";

interface ModuleCatalogResponse {
  modules: { code: ModuleCode; enabled: boolean }[];
}

export function isModuleCode(value: string): value is ModuleCode {
  return moduleCodes.some(code => code === value);
}

export function parseModuleCatalog(value: unknown): ModuleCatalogResponse {
  if (!isRecord(value) || !Array.isArray(value.modules) || value.modules.length !== moduleCodes.length)
    throw new Error("模块配置响应无效，请刷新页面或联系管理员。");

  const seen = new Set<ModuleCode>();
  const modules = value.modules.map((item: unknown) => {
    if (!isRecord(item) || typeof item.code !== "string" || typeof item.enabled !== "boolean"
      || !isModuleCode(item.code) || seen.has(item.code))
      throw new Error("模块配置响应无效，请刷新页面或联系管理员。");
    seen.add(item.code);
    return { code: item.code, enabled: item.enabled };
  });

  if (seen.size !== moduleCodes.length)
    throw new Error("模块配置响应不完整，请刷新页面或联系管理员。");

  return { modules };
}

export class ModuleCatalog {
  readonly state = reactive<{
    status: ModuleCatalogStatus;
    enabled: ReadonlySet<ModuleCode> | null;
    error: string;
  }>({
    status: "unloaded",
    enabled: null,
    error: "",
  });

  private pending: Promise<boolean> | null = null;

  constructor(private readonly request: typeof fetch = globalThis.fetch.bind(globalThis)) {}

  load(force = false): Promise<boolean> {
    if (this.pending) return this.pending;
    if (!force && this.state.status === "ready") return Promise.resolve(true);

    this.state.status = "loading";
    this.state.error = "";
    this.pending = this.fetchCatalog().finally(() => { this.pending = null; });
    return this.pending;
  }

  isEnabled(code: ModuleCode): boolean {
    return this.state.status === "ready" && this.state.enabled?.has(code) === true;
  }

  private async fetchCatalog(): Promise<boolean> {
    try {
      const response = await this.request("/api/config/features", {
        method: "GET",
        cache: "no-store",
        credentials: "same-origin",
        headers: { "X-WES-Client": "ops-web" },
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const parsed = parseModuleCatalog(await response.json());
      this.state.enabled = new Set(parsed.modules.filter(module => module.enabled).map(module => module.code));
      this.state.status = "ready";
      return true;
    } catch (failure) {
      this.state.enabled = null;
      this.state.status = "error";
      this.state.error = failure instanceof Error
        ? `无法读取模块配置（${failure.message}）。请重试或联系管理员。`
        : "无法读取模块配置。请重试或联系管理员。";
      return false;
    }
  }
}

export const moduleCatalog = new ModuleCatalog();
