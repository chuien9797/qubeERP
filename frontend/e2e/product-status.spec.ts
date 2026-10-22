import { test, expect } from "@playwright/test";

test("activation controls preserve stock/history and verify the real catalogue after lost responses", async ({
  page,
  request,
}) => {
  const sku = "STATUS-" + Date.now();
  const created = await request.post("/gateway/products/api/products", {
    data: { sku, name: "Product status assessment", unitPrice: 6.5 },
  });
  expect(created.status()).toBe(201);
  const product = await created.json();
  expect(
    (
      await request.put("/gateway/inventory/api/inventory/" + product.id, {
        data: { onHand: 10, reason: "Product status test opening count" },
      })
    ).status(),
  ).toBe(200);
  const orderResponse = await request.post("/gateway/orders/api/orders", {
    headers: { "Idempotency-Key": crypto.randomUUID() },
    data: { items: [{ productId: product.id, quantity: 2 }] },
  });
  expect(orderResponse.status()).toBe(201);
  const order = await orderResponse.json();
  const readStock = async () =>
    await (
      await request.get("/gateway/inventory/api/inventory/" + product.id)
    ).json();
  const readLedger = async () =>
    await (
      await request.get(
        "/gateway/inventory/api/inventory/" + product.id + "/movements",
      )
    ).json();
  const readReservation = async () =>
    await (
      await request.get(
        "/gateway/inventory/api/inventory/reservations/" + order.orderId,
      )
    ).json();
  const before = {
    stock: await readStock(),
    ledger: await readLedger(),
    reservation: await readReservation(),
    order: await (
      await request.get("/gateway/orders/api/orders/" + order.orderId)
    ).json(),
  };
  await page.goto("/products");
  await page.getByLabel("Search catalogue").fill(sku);
  await page.getByRole("button", { name: "Search", exact: true }).click();
  const row = page.getByRole("row").filter({ hasText: sku });
  await expect(row).toHaveCount(1);
  let writes = 0;
  page.on("request", (r) => {
    if (
      r.method() === "PATCH" &&
      r.url().includes("/" + product.id + "/status")
    )
      writes++;
  });
  page.once("dialog", (d) => d.dismiss());
  await row
    .getByRole("button", { name: "Deactivate " + sku, exact: true })
    .click();
  expect(writes).toBe(0);
  page.once("dialog", (d) => d.accept());
  await row
    .getByRole("button", { name: "Deactivate " + sku, exact: true })
    .click();
  await expect(
    page.getByRole("status").filter({ hasText: "is now inactive" }),
  ).toBeVisible();
  await expect(row.locator("app-badge")).toContainText("Inactive");
  expect(await readStock()).toEqual(before.stock);
  expect(await readLedger()).toEqual(before.ledger);
  expect(await readReservation()).toEqual(before.reservation);
  expect(
    await (
      await request.get("/gateway/orders/api/orders/" + order.orderId)
    ).json(),
  ).toEqual(before.order);
  const blocked = await request.post("/gateway/orders/api/orders", {
    headers: { "Idempotency-Key": crypto.randomUUID() },
    data: { items: [{ productId: product.id, quantity: 1 }] },
  });
  expect(blocked.status()).toBe(422);
  expect((await blocked.json()).errorCode).toBe("INVALID_PRODUCT");
  expect(await readStock()).toEqual(before.stock);
  expect(await readLedger()).toEqual(before.ledger);
  const inactive = await (
    await request.get("/gateway/products/api/products/" + product.id)
  ).json();
  expect(
    (
      await request.patch(
        "/gateway/products/api/products/" + product.id + "/status",
        { data: { isActive: false } },
      )
    ).status(),
  ).toBe(204);
  expect(
    await (
      await request.get("/gateway/products/api/products/" + product.id)
    ).json(),
  ).toEqual(inactive);
  page.once("dialog", (d) => d.accept());
  await row
    .getByRole("button", { name: "Reactivate " + sku, exact: true })
    .click();
  await expect(
    page.getByRole("status").filter({ hasText: "is now active" }),
  ).toBeVisible();
  const newOrderResponse = await request.post("/gateway/orders/api/orders", {
    headers: { "Idempotency-Key": crypto.randomUUID() },
    data: { items: [{ productId: product.id, quantity: 1 }] },
  });
  expect(newOrderResponse.status()).toBe(201);
  const newOrder = await newOrderResponse.json();
  for (const id of [order.orderId, newOrder.orderId]) {
    const cancel = await request.post(
      "/gateway/orders/api/orders/" + id + "/cancel",
      { data: { reason: "Complete product status assessment" } },
    );
    expect(cancel.status()).toBe(200);
    expect((await cancel.json()).status).toBe("Cancelled");
  }
  expect(
    (
      await request.put("/gateway/inventory/api/inventory/" + product.id, {
        data: {
          onHand: 0,
          reason: "Verify zero stock does not deactivate catalogue",
        },
      })
    ).status(),
  ).toBe(200);
  expect(
    (
      await (
        await request.get("/gateway/products/api/products/" + product.id)
      ).json()
    ).isActive,
  ).toBe(true);
  await page.route(
    "**/gateway/products/api/products/" + product.id + "/status",
    async (route) => {
      const actual = await route.fetch();
      expect(actual.status()).toBe(204);
      await route.abort("failed");
    },
  );
  page.once("dialog", (d) => d.accept());
  await row
    .getByRole("button", { name: "Deactivate " + sku, exact: true })
    .click();
  await expect(
    page.getByRole("status").filter({ hasText: "response was unavailable" }),
  ).toBeVisible();
  await expect(row.locator("app-badge")).toContainText("Inactive");
  expect(writes).toBe(3);
  await page.screenshot({
    path: "test-results/product-status-controls.png",
    fullPage: true,
  });
});
