import { TestBed } from "@angular/core/testing";
import { HttpErrorResponse, HttpResponse } from "@angular/common/http";
import { Api } from "./api";
import { Commands, storageKey } from "./commands";
describe("Durable command recovery", () => {
  let commands: Commands;
  const api = { command: vi.fn() };
  beforeEach(() => {
    localStorage.clear();
    api.command.mockReset();
    TestBed.configureTestingModule({
      providers: [Commands, { provide: Api, useValue: api }],
    });
    commands = TestBed.inject(Commands);
  });
  afterEach(() => TestBed.resetTestingModule());
  it("saves an independent payload before sending and replays the same key after a lost response", async () => {
    const payload = {
      items: [{ productId: crypto.randomUUID(), quantity: 3 }],
    };
    const c = commands.prepare("order", "", payload);
    payload.items[0].quantity = 99;
    api.command.mockImplementationOnce(async () => {
      expect(
        JSON.parse(localStorage.getItem(storageKey)!)[0].payload.items[0]
          .quantity,
      ).toBe(3);
      throw new HttpErrorResponse({ status: 0 });
    });
    await commands.run(c.id);
    expect(commands.get(c.id)?.state).toBe("uncertain");
    api.command.mockResolvedValueOnce(
      new HttpResponse({
        status: 201,
        body: { orderId: "order-1", status: "Confirmed" },
      }),
    );
    await commands.run(c.id);
    expect(api.command.mock.calls[0]).toEqual(api.command.mock.calls[1]);
    expect(api.command.mock.calls[1][4]).toBe(c.key);
    expect(commands.get(c.id)?.state).toBe("verified");
  });
  it("does not claim cancellation success from a 200 without Cancelled state", async () => {
    const c = commands.prepare("cancel", "order-2", {
      reason: "Duplicate purchase",
    });
    api.command.mockResolvedValue(
      new HttpResponse({
        status: 200,
        body: { orderId: "order-2", status: "CancellationPending" },
      }),
    );
    await commands.run(c.id);
    expect(commands.get(c.id)?.state).toBe("attention");
  });
  it("keeps a 202 pending across browser reloads", async () => {
    const c = commands.prepare("order", "", {
      items: [{ productId: "p", quantity: 1 }],
    });
    api.command.mockResolvedValue(
      new HttpResponse({
        status: 202,
        body: { orderId: "order-3", status: "Pending" },
      }),
    );
    await commands.run(c.id);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [Commands, { provide: Api, useValue: api }],
    });
    const reloaded = TestBed.inject(Commands);
    expect(reloaded.get(c.id)?.state).toBe("pending");
    expect(reloaded.get(c.id)?.key).toBe(c.key);
  });
  it("retains the original receipt operation ID and quantity on retry", async () => {
    const operationId = crypto.randomUUID();
    const c = commands.prepare("receipt", "p", {
      operationId,
      quantity: 4,
      reason: "Delivery",
    });
    api.command.mockRejectedValueOnce(new HttpErrorResponse({ status: 503 }));
    await commands.run(c.id);
    api.command.mockResolvedValueOnce(
      new HttpResponse({ status: 200, body: { operationId } }),
    );
    await commands.run(c.id);
    expect(api.command.mock.calls[0][3]).toEqual(api.command.mock.calls[1][3]);
    expect(commands.get(c.id)?.state).toBe("verified");
  });
  it("blocks blind absolute count replay after a timeout", async () => {
    const c = commands.prepare("stocktake", "p", {
      onHand: 10,
      reason: "Physical count",
    });
    api.command.mockRejectedValue(new HttpErrorResponse({ status: 0 }));
    await commands.run(c.id);
    await expect(commands.run(c.id)).rejects.toThrow("Inspect");
    expect(api.command).toHaveBeenCalledTimes(1);
  });
  it("persists uncertainty before a non-idempotent request is in flight", async () => {
    const c = commands.prepare("product", "", {
      sku: "ABC",
      name: "Item",
      unitPrice: 4,
    });
    api.command.mockImplementation(async () => {
      expect(JSON.parse(localStorage.getItem(storageKey)!)[0].state).toBe(
        "uncertain",
      );
      return new HttpResponse({ status: 201, body: { id: "p" } });
    });
    await commands.run(c.id);
    expect(commands.get(c.id)?.state).toBe("verified");
  });
  it("sends nothing when durable storage fails", () => {
    const spy = vi
      .spyOn(Storage.prototype, "setItem")
      .mockImplementation(() => {
        throw new DOMException("Quota exceeded");
      });
    expect(() => commands.prepare("order", "", { items: [] })).toThrow(
      "Browser storage is unavailable",
    );
    expect(api.command).not.toHaveBeenCalled();
    spy.mockRestore();
  });
  it("retains shortages and order reference returned in a rejected problem", async () => {
    const c = commands.prepare("order", "", {
      items: [{ productId: "p", quantity: 8 }],
    });
    api.command.mockRejectedValue(
      new HttpErrorResponse({
        status: 409,
        error: {
          orderId: "rejected-1",
          orderStatus: "Rejected",
          title: "Insufficient stock",
          shortfalls: [{ productId: "p", requested: 8, available: 2 }],
        },
      }),
    );
    await commands.run(c.id);
    expect(commands.get(c.id)?.orderId).toBe("rejected-1");
    expect(commands.get(c.id)?.problem?.shortfalls?.[0].requested).toBe(8);
  });
  it("retains a previously archived request and verifies it against backend state", async () => {
    const c = commands.prepare("cancel", "o", { reason: "Duplicate" });
    localStorage.setItem(
      storageKey,
      JSON.stringify([{ ...c, state: "archived" }]),
    );
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [Commands, { provide: Api, useValue: api }],
    });
    const restored = TestBed.inject(Commands);
    expect(restored.get(c.id)?.key).toBe(c.key);
    expect(restored.get(c.id)?.payload).toEqual(c.payload);
    restored.observeOrder({ orderId: "o", status: "Cancelled" } as any);
    expect(restored.get(c.id)?.state).toBe("verified");
  });
  it("keeps backend reads usable when local cache updates fail", () => {
    commands.prepare("cancel", "o", { reason: "Duplicate" });
    const spy = vi
      .spyOn(Storage.prototype, "setItem")
      .mockImplementation(() => {
        throw new DOMException("Quota exceeded");
      });
    try {
      expect(() =>
        commands.observeOrder({ orderId: "o", status: "Cancelled" } as any),
      ).not.toThrow();
      expect(commands.storageError()).toContain("may already have completed");
      expect(api.command).not.toHaveBeenCalled();
    } finally {
      spy.mockRestore();
    }
  });
  it("replays manual reservations with the original ID and product quantities after a lost response", async () => {
    const reservationId = crypto.randomUUID();
    const payload = {
      reservationId,
      items: [
        { productId: "p", quantity: 3 },
        { productId: "q", quantity: 2 },
      ],
    };
    const c = commands.prepare("reservation", reservationId, payload);
    payload.items[0].quantity = 99;
    api.command.mockRejectedValueOnce(new HttpErrorResponse({ status: 0 }));
    await commands.run(c.id);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [Commands, { provide: Api, useValue: api }],
    });
    const reloaded = TestBed.inject(Commands);
    api.command.mockResolvedValueOnce(
      new HttpResponse({
        status: 200,
        body: { reservationId, status: "Reserved" },
      }),
    );
    await reloaded.run(c.id);
    expect(api.command.mock.calls[0]).toEqual(api.command.mock.calls[1]);
    expect(api.command.mock.calls[1][2]).toBe("/api/inventory/reservations");
    expect(api.command.mock.calls[1][3].items[0].quantity).toBe(3);
    expect(reloaded.get(c.id)?.state).toBe("verified");
  });
  it("verifies rejected manual reservations from a read while preserving original shortfalls", async () => {
    const reservationId = crypto.randomUUID();
    const c = commands.prepare("reservation", reservationId, {
      reservationId,
      items: [{ productId: "p", quantity: 7 }],
    });
    api.command.mockRejectedValue(
      new HttpErrorResponse({
        status: 409,
        error: {
          reservationId,
          title: "Insufficient stock",
          shortfalls: [{ productId: "p", requested: 7, available: 1 }],
        },
      }),
    );
    await commands.run(c.id);
    commands.observeReservation({
      reservationId,
      status: "Rejected",
      failureCode: "INSUFFICIENT_STOCK",
      items: [{ productId: "p", quantity: 7 }],
      shortfalls: [{ productId: "p", requested: 7, available: 1 }],
    });
    expect(commands.get(c.id)?.state).toBe("verified");
    expect(commands.get(c.id)?.status).toBe("Rejected");
    expect(commands.get(c.id)?.problem?.shortfalls?.[0].requested).toBe(7);
  });
  it.each(["{truncated", "null", '[{"id":"lost"}]'])("preserves unreadable history and blocks sending: %s", async (raw) => {
    localStorage.setItem(storageKey, raw);
    expect(() => commands.prepare("order", "", { items: [] })).toThrow("unreadable request history");
    expect(localStorage.getItem(storageKey)).toBe(raw);
    expect(api.command).not.toHaveBeenCalled();
  });
  it("retains a request saved by another tab after this instance loaded", () => {
    const original = commands.prepare("order", "", { items: [] });
    const other = { ...original, id: "other-tab", key: "other-key" };
    localStorage.setItem(storageKey, JSON.stringify([other, original]));
    commands.prepare("receipt", "p", { quantity: 1, operationId: "op" });
    expect(commands.get(other.id)?.key).toBe("other-key");
    expect(JSON.parse(localStorage.getItem(storageKey)!)).toHaveLength(3);
  });
  it("reads the latest saved state before replaying a non-idempotent command", async () => {
    const c = commands.prepare("stocktake", "p", { onHand: 10, reason: "Count" });
    localStorage.setItem(storageKey, JSON.stringify([{ ...c, state: "uncertain" }]));
    await expect(commands.run(c.id)).rejects.toThrow("Inspect");
    expect(api.command).not.toHaveBeenCalled();
  });
  it("records a reversible stocktake review without removing history or verifying the outcome", async () => {
    const c = commands.prepare("stocktake", "p", {
      onHand: 10,
      reason: "Count",
    });
    api.command.mockRejectedValue(new HttpErrorResponse({ status: 0 }));
    await commands.run(c.id);
    commands.recordInventoryCheck(c.id, true);
    expect(commands.get(c.id)?.inventoryCheckedAt).toBeTruthy();
    expect(commands.get(c.id)?.state).toBe("uncertain");
    expect(commands.get(c.id)?.payload).toEqual(c.payload);
    commands.recordInventoryCheck(c.id, false);
    expect(commands.get(c.id)?.inventoryCheckedAt).toBeUndefined();
    expect(commands.get(c.id)?.state).toBe("uncertain");
    expect(api.command).toHaveBeenCalledTimes(1);
  });
});
