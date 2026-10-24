import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import DensityControl from "./DensityControl.vue";

describe("DensityControl", () => {
  it("labels the native density selector and emits the selected mode", async () => {
    const wrapper = mount(DensityControl, {
      props: {
        modelValue: "compact",
        storageWarning: null,
      },
    });

    expect(wrapper.get("label").text()).toContain("显示密度");
    expect(wrapper.get("label").element.contains(wrapper.get("select").element)).toBe(true);
    await wrapper.get("select").setValue("standard");
    expect(wrapper.emitted("update:modelValue")).toEqual([["standard"]]);
    wrapper.unmount();
  });

  it("announces persistence problems without disabling the selector", () => {
    const wrapper = mount(DensityControl, {
      props: {
        modelValue: "compact",
        storageWarning: "无法保存显示密度；本次选择仅在当前会话有效。",
      },
    });

    expect(wrapper.get('[role="status"]').text()).toContain("无法保存显示密度");
    expect(wrapper.get("select").element.disabled).toBe(false);
    wrapper.unmount();
  });
});
