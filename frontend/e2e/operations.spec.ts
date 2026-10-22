import { test, expect } from "@playwright/test";
// These are live integration tests. They create uniquely named assessment products
// and business records in the running local APIs; they never mock successful calls.
test("catalogue, counts, receipts, multi-item order, cancellation and historical reservation", async ({
  page,
  request,
}) => {
  const suffix = Date.now().toString();
  const sku = "UI-" + suffix;
  const secondSku = "UI-B-" + suffix;
  await page.goto("/products");
  await page.getByRole("button", { name: "Create product" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("SKU", { exact: true }).fill(sku);
  await dialog.getByLabel("Product name").fill("Frontend assessment " + suffix);
  await dialog.getByLabel("Unit price (MYR)").fill("12.50");
  await dialog.getByRole("button", { name: "Create product" }).click();
  await expect(dialog).not.toBeVisible();
  await page.getByLabel("Search catalogue").fill(sku);
  await page.getByRole("button", { name: "Search", exact: true }).click();
  const row = page.getByRole("row").filter({ hasText: sku });
  await expect(row).toHaveCount(1);
  await expect(row).toContainText("Active");
  await row.getByRole("link", { name: "View stock" }).click();
  const productId = new URL(page.url()).searchParams.get("productId")!;
  await expect(
    page.getByRole("heading", { name: "Opening count" }),
  ).toBeVisible();
  await page.getByLabel("Counted on-hand quantity").fill("20");
  await page
    .getByLabel("Reason", { exact: true })
    .fill("Frontend assessment opening count");
  await page.getByRole("button", { name: "Save physical count" }).click();
  await expect(
    page.getByRole("heading", { name: "Stocktake adjustment" }),
  ).toBeVisible();
  await page.getByLabel("Quantity received").fill("5");
  await page
    .getByLabel("Reason / delivery reference")
    .fill("Frontend assessment delivery");
  await page.getByRole("button", { name: "Record receipt" }).click();
  await expect(
    page.getByRole("status").filter({ hasText: "Receipt verified" }),
  ).toBeVisible();
  const second = await request.post("/gateway/products/api/products", {
    data: {
      sku: secondSku,
      name: "Second assessment line " + suffix,
      unitPrice: 7.25,
    },
  });
  expect(second.status()).toBe(201);
  const secondProduct = await second.json();
  expect(
    (
      await request.put(
        "/gateway/inventory/api/inventory/" + secondProduct.id,
        { data: { onHand: 12, reason: "Frontend multi-item setup" } },
      )
    ).ok(),
  ).toBeTruthy();
  await page.goto("/orders");
  await page.getByRole("button", { name: "Create order" }).click();
  await page.getByLabel("Search products for order").fill(suffix);
  await page.getByRole("button", { name: "Search catalogue" }).click();
  await expect(
    page
      .getByRole("dialog")
      .locator("select")
      .first()
      .getByRole("option", { name: new RegExp(sku) }),
  ).toBeAttached();
  await page
    .getByRole("dialog")
    .getByRole("combobox", { name: "Product", exact: true })
    .first()
    .selectOption(productId);
  await page
    .getByRole("dialog")
    .getByLabel("Quantity", { exact: true })
    .first()
    .fill("3");
  await page.getByRole("button", { name: "Add product line" }).click();
  await page
    .getByRole("dialog")
    .getByRole("combobox", { name: "Product", exact: true })
    .nth(1)
    .selectOption(secondProduct.id);
  await page
    .getByRole("dialog")
    .getByLabel("Quantity", { exact: true })
    .nth(1)
    .fill("2");
  await page.getByRole("button", { name: "Submit order" }).click();
  await expect(page).toHaveURL(/\/orders\/[0-9a-f-]+$/);
  const orderId = page.url().split("/").at(-1)!;
  await expect(page.locator(".panel-heading app-badge").first()).toContainText(
    "Confirmed",
  );
  await expect(page.locator(".detail-grid")).toContainText("52.00");
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem("qubeerp.commands.v1")!),
  );
  const original = saved.find((c: any) => c.kind === "order");
  expect(original.payload.items).toHaveLength(2);
  const replay = await request.post("/gateway/orders/api/orders", {
    headers: { "Idempotency-Key": original.key },
    data: original.payload,
  });
  expect(replay.status()).toBe(200);
  expect((await replay.json()).orderId).toBe(orderId);
  await page
    .getByLabel("Cancellation reason")
    .fill("Frontend assessment cancellation");
  await page.getByRole("button", { name: "Cancel order", exact: true }).click();
  await expect(page.locator(".panel-heading app-badge").first()).toContainText(
    "Cancelled",
  );
  await page
    .getByRole("link", { name: "Inspect inventory reservation" })
    .click();
  await expect(page.locator(".panel-heading app-badge")).toContainText(
    "Released",
  );
  await expect(
    page.getByRole("cell", { name: productId, exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("cell", { name: secondProduct.id, exact: true }),
  ).toBeVisible();
  const stockBefore = await (
    await request.get("/gateway/inventory/api/inventory/" + productId)
  ).json();
  const ledgerBefore = await (
    await request.get(
      "/gateway/inventory/api/inventory/" + productId + "/movements",
    )
  ).json();
  await page.getByRole("button", { name: "Find reservation" }).click();
  await expect(
    page.getByRole("cell", { name: "3", exact: true }),
  ).toBeVisible();
  expect(
    await (
      await request.get("/gateway/inventory/api/inventory/" + productId)
    ).json(),
  ).toEqual(stockBefore);
  expect(
    await (
      await request.get(
        "/gateway/inventory/api/inventory/" + productId + "/movements",
      )
    ).json(),
  ).toEqual(ledgerBefore);
  expect(stockBefore).toMatchObject({
    onHand: 25,
    reserved: 0,
    availableQuantity: 25,
  });
  await page.goto("/movements?productId=" + productId);
  await expect(page.getByRole("table")).toContainText(
    "Frontend assessment delivery",
  );
  await page.screenshot({
    path: "test-results/stock-movements.png",
    fullPage: true,
  });
});
test("dashboard, status dropdown, responsive navigation and genuine readiness", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/dashboard");
  await expect(
    page.getByRole("heading", { name: "Recent activity", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "test-results/overview-desktop.png",
    fullPage: true,
  });
  await page.goto("/orders");
  await expect(
    page
      .getByRole("combobox", { name: "Status", exact: true })
      .getByRole("option", { name: "RequiresReview", exact: true }),
  ).toBeAttached();
  await page.goto("/status");
  await expect(
    page.locator("app-badge").filter({ hasText: "ready" }),
  ).toHaveCount(3);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Toggle navigation" }).click();
  await page
    .getByRole("navigation")
    .getByRole("link", { name: "Products", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Products", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "test-results/products-mobile.png",
    fullPage: true,
  });
  expect(errors).toEqual([]);
});

test("a lost order response survives reload and retries without a second reservation", async ({
  page,
  request,
}) => {
  const suffix = Date.now().toString();
  const sku = "LOST-" + suffix;
  const created = await request.post("/gateway/products/api/products", {
    data: { sku, name: "Lost response assessment", unitPrice: 3 },
  });
  expect(created.status()).toBe(201);
  const product = await created.json();
  expect(
    (
      await request.put("/gateway/inventory/api/inventory/" + product.id, {
        data: { onHand: 10, reason: "Lost response setup" },
      })
    ).ok(),
  ).toBeTruthy();
  let lose = true;
  const submitted: { key: string | undefined; body: any }[] = [];
  await page.route("**/gateway/orders/api/orders", async (route) => {
    if (route.request().method() !== "POST") {
      await route.continue();
      return;
    }
    submitted.push({
      key: route.request().headers()["idempotency-key"],
      body: route.request().postDataJSON(),
    });
    if (lose) {
      lose = false;
      const actual = await route.fetch();
      expect(actual.status()).toBe(201);
      await route.abort("failed");
    } else await route.continue();
  });
  await page.goto("/orders");
  await page.getByRole("button", { name: "Create order" }).click();
  await page.getByLabel("Search products for order").fill(sku);
  await page.getByRole("button", { name: "Search catalogue" }).click();
  await expect(
    page.getByRole("dialog").locator("option").filter({ hasText: sku }),
  ).toHaveCount(1);
  await page
    .getByRole("dialog")
    .getByRole("combobox", { name: "Product", exact: true })
    .selectOption(product.id);
  await page.getByRole("button", { name: "Submit order" }).click();
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await expect(page.locator(".request app-badge").first()).toContainText(
    "uncertain",
  );
  await page.reload();
  await page.locator(".request summary").first().click();
  await page
    .getByRole("button", { name: "Retry original request" })
    .first()
    .click();
  await expect(page.locator(".request app-badge").first()).toContainText(
    "verified",
  );
  expect(submitted).toHaveLength(2);
  expect(submitted[0]).toEqual(submitted[1]);
  expect(
    await (
      await request.get("/gateway/inventory/api/inventory/" + product.id)
    ).json(),
  ).toMatchObject({ onHand: 10, reserved: 1, availableQuantity: 9 });
});

test("shortages remain visible and an unavailable API never becomes a fabricated balance", async ({
  page,
  request,
}) => {
  const created = await request.post("/gateway/products/api/products", {
    data: {
      sku: "SHORT-" + Date.now(),
      name: "Shortage assessment",
      unitPrice: 2,
    },
  });
  const p = await created.json();
  await request.put("/gateway/inventory/api/inventory/" + p.id, {
    data: { onHand: 0, reason: "Shortage setup" },
  });
  const rejected = await request.post("/gateway/orders/api/orders", {
    headers: { "Idempotency-Key": crypto.randomUUID() },
    data: { items: [{ productId: p.id, quantity: 4 }] },
  });
  expect(rejected.status()).toBe(409);
  const problem = await rejected.json();
  await page.goto("/reservations?id=" + problem.orderId);
  await expect(page.locator(".panel-heading app-badge")).toContainText(
    "Rejected",
  );
  await expect(
    page.getByRole("heading", { name: "Recorded shortages" }),
  ).toBeVisible();
  await expect(page.getByRole("cell", { name: p.id, exact: true })).toHaveCount(
    2,
  );
  await page.route("**/gateway/inventory/api/inventory/" + p.id, (route) =>
    route.abort("failed"),
  );
  await page.goto("/inventory?productId=" + p.id);
  await expect(page.getByRole("alert")).toBeVisible();
  await expect(page.locator(".metric")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Record receipt" }),
  ).toHaveCount(0);
});
