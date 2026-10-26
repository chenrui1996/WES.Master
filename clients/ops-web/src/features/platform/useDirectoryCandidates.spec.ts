import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import { useDirectoryCandidates } from "./useDirectoryCandidates";

function deferredResponse() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>(done => { resolve = done; });
  return { promise, resolve };
}

const host = defineComponent({
  setup() {
    const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
    const read = (code?: string) => directory.read(new AbortController().signal, false, code);
    const readMore = () => directory.read(new AbortController().signal, true);
    return { ...directory, read, readMore };
  },
  template: `<div>
    <button data-test="read" @click="read()">Read</button>
    <button data-test="read-a" @click="read('A')">Read A</button>
    <button data-test="read-ab" @click="read('AB')">Read AB</button>
    <button data-test="more" @click="readMore">More</button>
    <button data-test="reset" @click="reset">Reset</button>
    <output data-test="ids">{{ rows.map(row => row.id).join(",") }}</output>
    <output data-test="loading">{{ loading }}</output>
  </div>`,
});

function response(id: string) {
  return new Response(JSON.stringify({ authorized: true, rows: [{ id }] }));
}

afterEach(() => { vi.unstubAllGlobals(); });

it("ignores a stale response without clearing the current request loading state", async () => {
  const stale = deferredResponse();
  const current = deferredResponse();
  vi.stubGlobal("fetch", vi.fn().mockReturnValueOnce(stale.promise).mockReturnValueOnce(current.promise));
  const wrapper = mount(host);

  await wrapper.get('[data-test="read"]').trigger("click");
  await wrapper.get('[data-test="reset"]').trigger("click");
  await wrapper.get('[data-test="read"]').trigger("click");
  expect(wrapper.get('[data-test="loading"]').text()).toBe("true");

  stale.resolve(response("stale"));
  await flushPromises();
  expect(wrapper.get('[data-test="ids"]').text()).toBe("");
  expect(wrapper.get('[data-test="loading"]').text()).toBe("true");

  current.resolve(response("current"));
  await flushPromises();
  expect(wrapper.get('[data-test="ids"]').text()).toBe("current");
  expect(wrapper.get('[data-test="loading"]').text()).toBe("false");
  wrapper.unmount();
});

it("keeps the exact-code filter and advances by 25 when loading another candidate page", async () => {
  const fetcher = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({ authorized: true,
      rows: Array.from({ length: 25 }, (_, index) => ({ id: `d-${index}` })) })))
    .mockResolvedValueOnce(new Response(JSON.stringify({ authorized: true, rows: [{ id: "d-25" }] })));
  vi.stubGlobal("fetch", fetcher);
  const wrapper = mount(host);

  await wrapper.get('[data-test="read-ab"]').trigger("click");
  await flushPromises();
  await wrapper.get('[data-test="more"]').trigger("click");
  await flushPromises();

  expect(fetcher.mock.calls[0]?.[0]).toContain("offset=0&limit=25&code=AB");
  expect(fetcher.mock.calls[1]?.[0]).toContain("offset=25&limit=25&code=AB");
  expect(wrapper.get('[data-test="ids"]').text()).toContain("d-0");
  expect(wrapper.get('[data-test="ids"]').text()).toContain("d-25");
  wrapper.unmount();
});

it("does not replace current candidates when a pre-reset response arrives last", async () => {
  const stale = deferredResponse();
  const current = deferredResponse();
  vi.stubGlobal("fetch", vi.fn().mockReturnValueOnce(stale.promise).mockReturnValueOnce(current.promise));
  const wrapper = mount(host);

  await wrapper.get('[data-test="read"]').trigger("click");
  await wrapper.get('[data-test="reset"]').trigger("click");
  await wrapper.get('[data-test="read"]').trigger("click");

  current.resolve(response("current"));
  await flushPromises();
  expect(wrapper.get('[data-test="ids"]').text()).toBe("current");

  stale.resolve(response("stale"));
  await flushPromises();
  expect(wrapper.get('[data-test="ids"]').text()).toBe("current");
  expect(wrapper.get('[data-test="loading"]').text()).toBe("false");
  wrapper.unmount();
});

it("lets the newest exact-code query replace candidates when responses arrive out of order", async () => {
  const first = deferredResponse();
  const second = deferredResponse();
  const fetcher = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  vi.stubGlobal("fetch", fetcher);
  const wrapper = mount(host);

  await wrapper.get('[data-test="read-a"]').trigger("click");
  await wrapper.get('[data-test="read-ab"]').trigger("click");
  expect(fetcher).toHaveBeenCalledTimes(2);
  expect(fetcher.mock.calls[0]?.[0]).toContain("code=A");
  expect(fetcher.mock.calls[1]?.[0]).toContain("code=AB");

  second.resolve(response("AB"));
  await flushPromises();
  first.resolve(response("A"));
  await flushPromises();
  expect(wrapper.get('[data-test="ids"]').text()).toBe("AB");
  wrapper.unmount();
});

function fullPage() {
  return new Response(JSON.stringify({ authorized: true,
    rows: Array.from({ length: 25 }, (_, index) => ({ id: `d-${index}` })) }));
}

it.each([401, 403, "denied"] as const)("clears protected candidates and more on append %s and permits recovery", async status => {
  const fetcher = vi.fn().mockResolvedValueOnce(fullPage())
    .mockResolvedValueOnce(status === "denied"
      ? new Response('{"authorized":false,"rows":[]}')
      : new Response('{"code":"CandidateDenied"}', { status }))
    .mockResolvedValueOnce(response("recovered"));
  vi.stubGlobal("fetch", fetcher);
  const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
  await directory.read(new AbortController().signal);
  expect(directory.more.value).toBe(true);
  await expect(directory.read(new AbortController().signal, true)).rejects.toThrow();
  expect(directory.rows.value).toEqual([]);
  expect(directory.more.value).toBe(false);
  await directory.read(new AbortController().signal);
  expect(directory.rows.value).toEqual([{ id: "recovered" }]);
});

it.each([401, 403, "denied"] as const)("clears previous scope before a new-filter %s response", async status => {
  const pending = deferredResponse();
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(fullPage()).mockReturnValueOnce(pending.promise));
  const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
  await directory.read(new AbortController().signal, false, "A");
  const read = directory.read(new AbortController().signal, false, "AB");
  const rejection = expect(read).rejects.toThrow();
  expect(directory.rows.value).toEqual([]);
  expect(directory.more.value).toBe(false);
  pending.resolve(status === "denied" ? new Response('{"authorized":false,"rows":[]}')
    : new Response("{}", { status }));
  await rejection;
  expect(directory.rows.value).toEqual([]);
});

it.each(["reset", "abort", "supersede"] as const)(
  "suppresses a late failure after %s without poisoning newer results", async action => {
    const stale = deferredResponse();
    vi.stubGlobal("fetch", vi.fn().mockReturnValueOnce(stale.promise).mockResolvedValueOnce(response("current")));
    const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
    const controller = new AbortController();
    const first = directory.read(controller.signal);
    const outcome = first.then(value => value, () => "stale error");
    if (action === "reset") directory.reset();
    if (action === "abort") controller.abort();
    await directory.read(new AbortController().signal, false, "NEW");
    stale.resolve(new Response('{"code":"OldFailure"}', { status: 403 }));
    expect(await outcome).toBeNull();
    expect(directory.rows.value).toEqual([{ id: "current" }]);
    expect(directory.loading.value).toBe(false);
  });

it("invalidates an externally cancelled request even when transport resolves successfully", async () => {
  const pending = deferredResponse();
  const fetcher = vi.fn().mockReturnValueOnce(pending.promise);
  vi.stubGlobal("fetch", fetcher);
  const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
  const controller = new AbortController();
  const read = directory.read(controller.signal);
  controller.abort();
  expect(directory.loading.value).toBe(false);
  pending.resolve(response("cancelled"));
  expect(await read).toBeNull();
  expect(directory.rows.value).toEqual([]);
  await directory.read(controller.signal);
  expect(fetcher).toHaveBeenCalledTimes(1);
});

it("deduplicates stable IDs while advancing pages and explicitly rejects a current 503", async () => {
  const fetcher = vi.fn().mockResolvedValueOnce(fullPage())
    .mockResolvedValueOnce(new Response('{"authorized":true,"rows":[{"id":"d-0"},{"id":"d-25"}]}'))
    .mockResolvedValueOnce(new Response('{"code":"Unavailable"}', { status: 503 }));
  vi.stubGlobal("fetch", fetcher);
  const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
  await directory.read(new AbortController().signal);
  await directory.read(new AbortController().signal, true);
  expect(directory.rows.value).toHaveLength(26);
  expect(directory.rows.value.filter(row => row.id === "d-0")).toHaveLength(1);
  await expect(directory.read(new AbortController().signal)).rejects.toThrow("Unavailable");
  expect(directory.rows.value).toEqual([]);
  expect(directory.more.value).toBe(false);
});

it("does not let a late call with an already-aborted signal clear newer query state", async () => {
  const pending = deferredResponse();
  const fetcher = vi.fn().mockReturnValueOnce(pending.promise);
  vi.stubGlobal("fetch", fetcher);
  const directory = useDirectoryCandidates<{ id: string }>("/api/platform/departments");
  const obsolete = new AbortController();
  obsolete.abort();
  const current = directory.read(new AbortController().signal, false, "NEW");

  expect(await directory.read(obsolete.signal, false, "OLD")).toBeNull();
  expect(directory.loading.value).toBe(true);
  pending.resolve(response("current"));
  await current;
  expect(directory.rows.value).toEqual([{ id: "current" }]);
  expect(await directory.read(obsolete.signal)).toBeNull();
  expect(directory.rows.value).toEqual([{ id: "current" }]);
  expect(fetcher).toHaveBeenCalledTimes(1);
});
