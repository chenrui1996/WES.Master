import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import QueryState from "./QueryState.vue";

describe("QueryState", () => {
  afterEach(() => vi.useRealTimers());

  it("announces loading without rendering result content", () => {
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "loading", busy: true },
      slots: { default: "<p>消息行</p>" },
    });

    expect(wrapper.get('[role="region"]').attributes("aria-busy")).toBe("true");
    expect(wrapper.get('[role="status"]').text()).toContain("正在加载消息");
    expect(wrapper.find("p").text()).not.toBe("消息行");
    wrapper.unmount();
  });

  it("delays a page-shaped loading placeholder until a request is observably slow", async () => {
    vi.useFakeTimers();
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "loading", busy: true },
      slots: {
        loading: '<div data-test="loading-placeholder">消息表格占位行</div>',
      },
    });

    await vi.advanceTimersByTimeAsync(200);
    expect(wrapper.find('[data-test="loading-placeholder"]').exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(100);
    expect(wrapper.get('[data-test="loading-placeholder"]').text()).toContain("消息表格占位行");
    expect(wrapper.get('[role="status"]').text()).toContain("正在加载消息");
    wrapper.unmount();
  });

  it("cancels the delayed placeholder when the query finishes early", async () => {
    vi.useFakeTimers();
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "loading" },
      slots: { loading: '<div data-test="loading-placeholder">占位行</div>' },
    });

    await vi.advanceTimersByTimeAsync(200);
    await wrapper.setProps({ state: "ready" });
    await vi.advanceTimersByTimeAsync(200);
    expect(wrapper.find('[data-test="loading-placeholder"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it("shows an explicit error and emits retry", async () => {
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "error", errorMessage: "服务暂不可用" },
    });

    expect(wrapper.get('[role="alert"]').text()).toContain("服务暂不可用");
    await wrapper.get("button").trigger("click");
    expect(wrapper.emitted("retry")).toHaveLength(1);
    wrapper.unmount();
  });

  it("shows an empty state and accepts a scoped empty action", () => {
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "empty" },
      slots: { empty: "<button type=\"button\">清除筛选</button>" },
    });

    expect(wrapper.get('[role="status"]').text()).toContain("暂无消息");
    expect(wrapper.get("button").text()).toBe("清除筛选");
    wrapper.unmount();
  });

  it("keeps stale results visible and offers an accessible refresh action", async () => {
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "stale" },
      slots: { default: "<p>最近一次已加载结果</p>" },
    });

    expect(wrapper.get('[role="status"]').text()).toContain("显示的是最近一次已加载的消息");
    expect(wrapper.findAll("p").map(paragraph => paragraph.text()))
      .toContain("最近一次已加载结果");
    await wrapper.get("button").trigger("click");
    expect(wrapper.emitted("retry")).toHaveLength(1);
    wrapper.unmount();
  });

  it("renders authorized result content in the ready state", () => {
    const wrapper = mount(QueryState, {
      props: { label: "消息", state: "ready" },
      slots: { default: "<p>已授权消息</p>" },
    });

    expect(wrapper.get('[role="region"]').attributes("aria-busy")).toBe("false");
    expect(wrapper.get("p").text()).toBe("已授权消息");
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    wrapper.unmount();
  });
});
