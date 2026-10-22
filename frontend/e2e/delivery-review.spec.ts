import { test, expect } from "@playwright/test";

test("unreadable request history is preserved and prevents a new product command", async ({ page }) => {
  const raw = '{"incomplete-request-history":';
  await page.goto("/products");
  await page.evaluate((value) => localStorage.setItem("qubeerp.commands.v1", value), raw);
  await page.reload();
  await page.getByRole("button", { name: "Create product" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("SKU", { exact: true }).fill("BLOCKED-" + Date.now());
  await dialog.getByLabel("Product name").fill("Must never be submitted");
  await dialog.getByLabel("Unit price (MYR)").fill("1.00");
  let writes = 0;
  page.on("request", request => {
    if (request.method() === "POST" && request.url().endsWith("/gateway/products/api/products")) writes++;
  });
  await dialog.getByRole("button", { name: "Create product" }).click();
  await expect(dialog.getByRole("alert")).toContainText("unreadable request history");
  expect(writes).toBe(0);
  expect(await page.evaluate(() => localStorage.getItem("qubeerp.commands.v1"))).toBe(raw);
});
