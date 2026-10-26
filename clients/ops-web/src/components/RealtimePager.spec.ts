import { mount } from "@vue/test-utils";
import { expect, it } from "vitest";
import RealtimePager from "./RealtimePager.vue";

it("offers only the approved sizes and requests the first offset on a size change", async () => {
  const wrapper = mount(RealtimePager, { props: { label: "测试分页", offset: 25, limit: 25, count: 25 } });
  expect(wrapper.get("nav").attributes("aria-label")).toBe("测试分页");
  expect(wrapper.findAll("option").map(option => option.element.value)).toEqual(["25", "50", "100"]);
  await wrapper.get("select").setValue("50");
  expect(wrapper.emitted("change")).toEqual([[0, 50]]);
  expect(wrapper.text()).toContain("实时");
  expect(wrapper.text()).toContain("不保证跨请求无丢重");
  wrapper.unmount();
});

it("never infers a total and permits going back from an empty later page", async () => {
  const wrapper = mount(RealtimePager, { props: { label: "测试分页", offset: 50, limit: 25, count: 0 } });
  expect(wrapper.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
  await wrapper.get('[data-test="previous-page"]').trigger("click");
  expect(wrapper.emitted("change")).toEqual([[25, 25]]);
  expect(wrapper.text()).not.toContain("共");
  wrapper.unmount();
});

it("disables paging and size changes while busy, and blocks forward navigation on failure", async () => {
  const wrapper = mount(RealtimePager, { props: { label: "测试分页", offset: 25, limit: 25, count: 25, busy: true } });
  expect(wrapper.findAll("button, select").every(control => control.attributes("disabled") !== undefined)).toBe(true);
  await wrapper.setProps({ busy: false, failed: true });
  expect(wrapper.get('[data-test="next-page"]').attributes("disabled")).toBeDefined();
  expect(wrapper.get('[data-test="previous-page"]').attributes("disabled")).toBeUndefined();
  await wrapper.setProps({ offset: 0, failed: false });
  expect(wrapper.get('[data-test="previous-page"]').attributes("disabled")).toBeDefined();
  expect(wrapper.get('[data-test="next-page"]').attributes("disabled")).toBeUndefined();
  wrapper.unmount();
});
