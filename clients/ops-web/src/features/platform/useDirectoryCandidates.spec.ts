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
    const read = () => directory.read(new AbortController().signal);
    return { ...directory, read };
  },
  template: `<div>
    <button data-test="read" @click="read">Read</button>
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
