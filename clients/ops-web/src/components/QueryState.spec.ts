import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import QueryState from "./QueryState.vue";

describe("QueryState", () => {
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
