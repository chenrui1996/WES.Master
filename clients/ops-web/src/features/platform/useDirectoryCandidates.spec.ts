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
