import { test, expect } from "@playwright/test";

test("a stale physical count requires reload and a reviewed new submission", async ({ page, request }) => {
  const productResponse = await request.post("/gateway/products/api/products", {
    data: { sku: "COUNT-" + Date.now(), name: "Concurrent physical count", unitPrice: 1 },
  });
  expect(productResponse.status()).toBe(201);
  const product = await productResponse.json();
  const path = "/gateway/inventory/api/inventory/" + product.id;
  const opening = await request.put(path, { data: { onHand: 100, reason: "Opening count" } });
  expect(opening.status()).toBe(200);
  const initial = await opening.json();
  await page.goto("/inventory?productId=" + product.id);
  const count = page.getByLabel("Counted on-hand quantity");
  await expect(count).toHaveValue("100");
  await count.fill("95");
  await page.getByLabel("Reason", { exact: true }).fill("Warehouse physical count");
  const delivery = await request.post(path + "/receipts", {
    data: { operationId: crypto.randomUUID(), quantity: 20, reason: "Delivery from another employee" },
  });
  expect(delivery.status()).toBe(201);
  const writes: any[] = [];
  page.on("request", req => {
    if (req.method() === "PUT" && req.url().endsWith(path)) writes.push(req.postDataJSON());
  });
  await page.getByRole("button", { name: "Save physical count", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Stock changed since this count was loaded" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Save physical count", exact: true })).toBeDisabled();
  expect(writes).toHaveLength(1);
  expect(writes[0].expectedVersion).toBe(initial.version);
  const refreshed = await (await request.get(path)).json();
  expect(refreshed.onHand).toBe(120);
  expect(refreshed.version).not.toBe(initial.version);
  expect(await (await request.get(path + "/movements")).json()).toHaveLength(2);
  await page.getByRole("button", { name: "Reload inventory", exact: true }).click();
  await expect(count).toHaveValue("120");
  expect(writes).toHaveLength(1);
  await count.fill("95");
  await page.getByLabel("Reason", { exact: true }).fill("Reviewed count after delivery");
  await page.getByRole("button", { name: "Save physical count", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Physical count saved." })).toBeVisible();
  expect(writes).toHaveLength(2);
  expect(writes[1].expectedVersion).toBe(refreshed.version);
  expect((await (await request.get(path)).json()).onHand).toBe(95);
  const ledger = await (await request.get(path + "/movements")).json();
  expect(ledger).toHaveLength(3);
  expect(ledger[2].onHandDelta).toBe(-25);
  const saved = await page.evaluate(() => JSON.parse(localStorage.getItem("qubeerp.commands.v1") || "[]"));
  const counts = saved.filter((entry: any) => entry.kind === "stocktake" && entry.target === product.id);
  expect(counts).toHaveLength(2);
  const rejected = counts.find((entry: any) => entry.problem?.errorCode === "STOCK_VERSION_CONFLICT");
  expect(rejected.payload.expectedVersion).toBe(initial.version);
  expect(rejected.payload.onHand).toBe(95);
});
