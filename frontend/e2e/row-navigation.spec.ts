import { test, expect } from "@playwright/test";

test("product and order rows open their details while status controls stay independent", async ({ page, request }) => {
  const sku = "ROW-" + Date.now();
  const created = await request.post("/gateway/products/api/products", {
    data: { sku, name: "Row navigation product", unitPrice: 2.5 },
  });
  expect(created.status()).toBe(201);
  const product = await created.json();
  expect((await request.put("/gateway/inventory/api/inventory/" + product.id, {
    data: { onHand: 10, reason: "Row navigation opening count" },
  })).status()).toBe(200);
  const submitted = await request.post("/gateway/orders/api/orders", {
    headers: { "Idempotency-Key": crypto.randomUUID() },
    data: { items: [{ productId: product.id, quantity: 1 }] },
  });
  expect(submitted.status()).toBe(201);
  const order = await submitted.json();
  await page.goto("/products");
  await page.getByLabel("Search catalogue").fill(sku);
  await page.getByRole("button", { name: "Search", exact: true }).click();
  const row = page.getByRole("row").filter({ hasText: sku });
  await expect(row.locator("app-badge .badge")).toHaveCSS("background-color", "rgb(226, 244, 232)");
  page.once("dialog", dialog => dialog.accept());
  await row.getByRole("button", { name: "Deactivate " + sku, exact: true }).click();
  await expect(row.locator("app-badge")).toContainText("Inactive");
  await expect(row.locator("app-badge .badge")).toHaveCSS("background-color", "rgb(237, 240, 243)");
  await expect(page).toHaveURL(/\/products$/);
  const bounds = await row.boundingBox();
  if (!bounds) throw new Error("Product row is not visible");
  await page.mouse.click(bounds.x + bounds.width * 0.65, bounds.y + bounds.height / 2);
  await expect(page).toHaveURL(new RegExp("/inventory\\?productId=" + product.id));
  await page.goto("/products");
  await page.getByLabel("Search catalogue").fill(sku);
  await page.getByRole("button", { name: "Search", exact: true }).click();
  const link = page.getByRole("link", { name: "View inventory for Row navigation product", exact: true });
  await link.focus();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(new RegExp("/inventory\\?productId=" + product.id));
  await page.goto("/orders");
  const orderRow = page.getByRole("row").filter({ hasText: order.orderId.slice(0, 8) });
  await expect(orderRow).toHaveCount(1);
  const orderBounds = await orderRow.boundingBox();
  if (!orderBounds) throw new Error("Order row is not visible");
  await page.mouse.click(orderBounds.x + orderBounds.width * 0.6, orderBounds.y + orderBounds.height / 2);
  await expect(page).toHaveURL(new RegExp("/orders/" + order.orderId + "$"));
  expect((await request.post("/gateway/orders/api/orders/" + order.orderId + "/cancel", {
    data: { reason: "Row navigation check complete" },
  })).status()).toBe(200);
});
