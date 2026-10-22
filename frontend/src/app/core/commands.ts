import { Injectable, computed, signal, inject } from "@angular/core";
import { Api, errorMessage, problemOf, unknownOutcome } from "./api";
import { Order, Problem } from "./models";

export type CommandKind =
  "order" | "cancel" | "receipt" | "stocktake" | "product";
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
}
export const storageKey = "qubeerp.commands.v1";
@Injectable({ providedIn: "root" })
export class Commands {
  private api = inject(Api);
  readonly entries = signal<SavedCommand[]>(this.load());
  readonly busy = signal<string[]>([]);
  readonly storageError = signal("");
  readonly unresolved = computed(() =>
    this.entries().filter((c) =>
      ["prepared", "pending", "uncertain", "attention"].includes(c.state),
    ),
  );
  private load(): SavedCommand[] {
    try {
      const raw = JSON.parse(localStorage.getItem(storageKey) || "[]");
      return Array.isArray(raw)
        ? raw.filter(
            (c) =>
              c &&
              typeof c.id === "string" &&
              ["order", "cancel", "receipt", "stocktake", "product"].includes(
                c.kind,
              ) &&
              c.payload &&
              typeof c.key === "string",
          )
        : [];
    } catch {
      return [];
    }
  }
  private persist(entries: SavedCommand[]) {
    // A command is never sent unless its stable identity and snapshot can first be saved.
    try {
      localStorage.setItem(storageKey, JSON.stringify(entries));
      this.entries.set(entries);
      this.storageError.set("");
    } catch {
      this.storageError.set(
        "Browser storage is unavailable or full. Cannot safely save request state. An existing request may already have completed; inspect its original saved identity and backend state before sending another command.",
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
    this.persist([command, ...this.entries()]);
    return command;
  }
  get(id: string) {
    return this.entries().find((c) => c.id === id);
  }
  private update(id: string, patch: Partial<SavedCommand>) {
    this.persist(
      this.entries().map((c) => (c.id === id ? { ...c, ...patch } : c)),
    );
  }
  async run(id: string, reconcile = false): Promise<SavedCommand | undefined> {
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
        c.state !== "archived" &&
        (c.orderId === order.orderId ||
          (c.kind === "cancel" && c.target === order.orderId)),
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
  archive(id: string) {
    this.update(id, { state: "archived" });
  }
}
