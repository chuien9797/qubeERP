import { Injectable, computed, signal, inject } from "@angular/core";
import { Api, errorMessage, problemOf, unknownOutcome } from "./api";
import { Order, Problem, Reservation } from "./models";

export type CommandKind =
  "order" | "cancel" | "receipt" | "stocktake" | "product" | "reservation";
export interface SavedCommand {
  id: string;
  kind: CommandKind;
  target: string;
  key: string;
  payload: any;
  createdAt: string;
  state:
    | "prepared"
    | "pending"
    | "uncertain"
    | "verified"
    | "attention"
    | "archived";
  orderId?: string;
  status?: string;
  message?: string;
  problem?: Problem;
  response?: any;
  inventoryCheckedAt?: string;
}
export const storageKey = "qubeerp.commands.v1";
@Injectable({ providedIn: "root" })
export class Commands {
  private api = inject(Api);
  readonly storageError = signal("");
  readonly entries = signal<SavedCommand[]>(this.load());
  readonly busy = signal<string[]>([]);
  readonly unresolved = computed(() =>
    this.entries().filter((c) =>
      ["prepared", "pending", "uncertain", "attention"].includes(c.state),
    ),
  );
  private readStored(): SavedCommand[] {
    const raw = JSON.parse(localStorage.getItem(storageKey) ?? "[]");
    if (
      !Array.isArray(raw) ||
      !raw.every(
        (c) =>
          c &&
          typeof c.id === "string" &&
          ["order", "cancel", "receipt", "stocktake", "product", "reservation"].includes(c.kind) &&
          c.payload &&
          typeof c.payload === "object" &&
          typeof c.key === "string" &&
          typeof c.target === "string" &&
          typeof c.createdAt === "string" &&
          ["prepared", "pending", "uncertain", "verified", "attention", "archived"].includes(c.state),
      )
    ) throw new Error("Invalid request history");
    if (new Set(raw.map((c) => c.id)).size !== raw.length)
      throw new Error("Duplicate request identities");
    return raw;
  }
  private load(): SavedCommand[] {
    try {
      const stored = this.readStored();
      this.storageError.set("");
      return stored;
    } catch {
      this.storageError.set(
        "Saved request history could not be read. Command submission is blocked to preserve the original records. Restore browser storage before submitting another request.",
      );
      return [];
    }
  }
  private persist(change: (stored: SavedCommand[]) => SavedCommand[]) {
    // A command is never sent unless its stable identity and snapshot can first be saved.
    try {
      // Read again before writing: another tab may have saved a request since this instance loaded.
      // Invalid/unreadable history must never be silently replaced with an empty history.
      const entries = change(this.readStored());
      localStorage.setItem(storageKey, JSON.stringify(entries));
      this.entries.set(entries);
      this.storageError.set("");
    } catch {
      this.storageError.set(
        "Browser storage is unavailable, full or contains unreadable request history. Cannot safely save request state. An existing request may already have completed; preserve its original saved identity and inspect backend state before sending another command.",
      );
      throw new Error(this.storageError());
    }
  }
  prepare(kind: CommandKind, target: string, payload: unknown): SavedCommand {
    const id = crypto.randomUUID();
    const command: SavedCommand = {
      id,
      kind,
      target,
      key: id,
      payload: structuredClone(payload),
      createdAt: new Date().toISOString(),
      state: "prepared",
    };
    this.persist((stored) => [command, ...stored]);
    return command;
  }
  get(id: string) {
    return this.entries().find((c) => c.id === id);
  }
  private update(id: string, patch: Partial<SavedCommand>) {
    this.persist((stored) => {
      if (!stored.some((c) => c.id === id))
        throw new Error("Saved request identity is missing");
      return stored.map((c) => (c.id === id ? { ...c, ...patch } : c));
    });
  }
  async run(id: string, reconcile = false): Promise<SavedCommand | undefined> {
    const stored = this.load();
    if (this.storageError()) throw new Error(this.storageError());
    this.entries.set(stored);
    const c = this.get(id);
    if (!c || this.busy().includes(id)) return c;
    if (
      (c.kind === "stocktake" || c.kind === "product") &&
      c.state !== "prepared"
    )
      throw new Error(
        "Inspect the saved record and current backend data before making a new command.",
      );
    this.update(id, {
      state: "uncertain",
      message: "Request submitted; awaiting backend verification.",
    });
    this.busy.update((v) => [...v, id]);
    try {
      const service =
        c.kind === "order" || c.kind === "cancel"
          ? "orders"
          : c.kind === "product"
            ? "products"
            : "inventory";
      const path =
        c.kind === "order"
          ? "/api/orders"
          : c.kind === "reservation"
            ? "/api/inventory/reservations"
            : c.kind === "cancel"
              ? `/api/orders/${c.target}/${reconcile ? "reconcile-cancellation" : "cancel"}`
              : c.kind === "product"
                ? "/api/products"
                : `/api/inventory/${c.target}${c.kind === "receipt" ? "/receipts" : ""}`;
      const response = await this.api.command(
        service,
        c.kind === "stocktake" ? "PUT" : "POST",
        path,
        reconcile ? undefined : structuredClone(c.payload),
        c.kind === "order" ? c.key : undefined,
      );
      const body = response.body;
      const status = body?.status;
      const verified =
        c.kind === "order"
          ? ["Confirmed", "Rejected", "Cancelled", "Fulfilled"].includes(status)
          : c.kind === "cancel"
            ? status === "Cancelled"
            : c.kind === "reservation"
              ? ["Reserved", "Released", "Consumed", "Rejected"].includes(
                  status,
                )
              : response.status === 200 || response.status === 201;
      this.update(id, {
        state: verified
          ? "verified"
          : response.status === 202
            ? "pending"
            : "attention",
        status,
        orderId: body?.orderId ?? c.orderId,
        response: body,
        problem: undefined,
        message: verified
          ? "Backend outcome verified."
          : response.status === 202
            ? "The backend outcome is pending. Keep this request and retry or reconcile."
            : "The response needs investigation; completion has not been verified.",
      });
    } catch (error) {
      const problem = problemOf(error);
      this.update(id, {
        state: unknownOutcome(error) ? "uncertain" : "attention",
        orderId: problem.orderId ?? c.orderId,
        status: problem.orderStatus,
        problem,
        message: errorMessage(error),
      });
    } finally {
      this.busy.update((v) => v.filter((value) => value !== id));
    }
    return this.get(id);
  }
  observeOrder(order: Order) {
    const matching = this.entries().filter(
      (c) =>
        c.orderId === order.orderId ||
        (c.kind === "cancel" && c.target === order.orderId),
    );
    for (const c of matching) {
      // A GET of Pending never proves reservation/release failed; review states never auto-retry.
      const verified =
        c.kind === "cancel"
          ? order.status === "Cancelled"
          : ["Confirmed", "Rejected", "Cancelled", "Fulfilled"].includes(
              order.status,
            );
      try {
        this.update(c.id, {
          status: order.status,
          orderId: order.orderId,
          state: verified
            ? "verified"
            : order.status === "RequiresReview"
              ? "attention"
              : c.state,
        });
      } catch {
        // A local cache failure must not hide a successful read of backend state.
        // persist exposes the storage error; command submission remains blocked.
        break;
      }
    }
  }
  observeReservation(reservation: Reservation) {
    for (const c of this.entries().filter(
      (c) =>
        c.kind === "reservation" &&
        c.payload.reservationId === reservation.reservationId,
    )) {
      if (
        !["Reserved", "Released", "Consumed", "Rejected"].includes(
          reservation.status,
        )
      )
        continue;
      try {
        this.update(c.id, {
          state: "verified",
          status: reservation.status,
          response: reservation,
          message: "Reservation outcome verified by the backend.",
        });
      } catch {
        break;
      }
    }
  }
  recordInventoryCheck(id: string, checked: boolean) {
    const c = this.get(id);
    if (!c || c.kind !== "stocktake" || c.state !== "uncertain") return;
    // This records a reversible local review, not a verified backend outcome.
    this.update(id, {
      inventoryCheckedAt: checked ? new Date().toISOString() : undefined,
    });
  }
}
