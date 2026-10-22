import { Component, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { Api, errorMessage } from "../core/api";
import { Product, Stock } from "../core/models";
import { Commands } from "../core/commands";
import { Page, Badge, Loading, ErrorBox } from "../shared/ui";
import { Recovery } from "../shared/recovery";
export const guidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
@Component({
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterLink,
    Page,
    Badge,
    Loading,
    ErrorBox,
    Recovery,
  ],
  template: ` <app-page
    section="STOCK CONTROL"
    title="Inventory"
    subtitle="Physical counts, available stock and traceable receipts."
  >
    <section class="panel">
      <form class="toolbar" [formGroup]="lookup" (ngSubmit)="load()">
        <label class="grow"
          >Product ID<input
            formControlName="id"
            placeholder="Paste product UUID"
            list="inventory-products"
          /><datalist id="inventory-products">
            @for (p of products(); track p.id) {
              <option [value]="p.id">{{ p.sku }} · {{ p.name }}</option>
            }
          </datalist></label
        ><button [disabled]="lookup.invalid || loading()">
          Load inventory
        </button>
      </form>
    </section>
    <app-error [message]="error()" />
    @if (loading()) {
      <app-loading />
    }
    @if (id() && !loading()) {
      <div class="section-heading">
        <div>
          <h2>{{ product()?.name || "Product inventory" }}</h2>
          <p>
            <span class="mono">{{ product()?.sku || id() }}</span>
            @if (product()) {
              <app-badge
                [value]="product()!.isActive ? 'Active' : 'Inactive'"
              />
            }
          </p>
        </div>
        <a
          class="button secondary"
          routerLink="/movements"
          [queryParams]="{ productId: id() }"
          >View movement ledger ↗</a
        >
      </div>
      @if (stock(); as s) {
        <div class="metrics">
          <article class="metric">
            <span>On hand</span><strong>{{ s.onHand | number }}</strong
            ><small>Physical stock recorded by Inventory</small>
          </article>
          <article class="metric">
            <span>Reserved</span><strong>{{ s.reserved | number }}</strong
            ><small>Currently allocated to reservations</small>
          </article>
          <article class="metric accent">
            <span>Available</span
            ><strong>{{ s.availableQuantity | number }}</strong
            ><small>Authoritative available quantity</small>
          </article>
        </div>
      }
      @if (missing()) {
        <div class="notice">
          Inventory is not initialised for this product. Enter an opening
          physical count below.
        </div>
      }
      @if (blocked()) {
        <div class="notice">
          An earlier stock command for this product is unresolved. Retry its
          original saved request, or investigate and acknowledge it before
          creating another adjustment or receipt.
        </div>
      }
      @if (stock() || missing()) {
        <div class="two-column">
          <section class="panel">
            <div class="panel-heading">
              <div>
                <h2>
                  {{ missing() ? "Opening count" : "Stocktake adjustment" }}
                </h2>
                <p>
                  Set the absolute physical total, including reserved units.
                </p>
              </div>
              <span class="panel-icon">▤</span>
            </div>
            <form
              class="form-body"
              [formGroup]="countForm"
              (ngSubmit)="submit('stocktake')"
            >
              <label
                >Counted on-hand quantity<input
                  type="number"
                  formControlName="onHand"
                  min="0"
                  step="1" /></label
              ><label
                >Reason<textarea
                  formControlName="reason"
                  maxlength="300"
                  rows="3"
                  placeholder="Opening count or stocktake reference"
                ></textarea>
              </label>
              <p class="muted">
                The backend enforces reservation constraints. This is an
                absolute count, not a quantity to add.
              </p>
              <button [disabled]="countForm.invalid || saving() || blocked()">
                {{ saving() ? "Awaiting backend…" : "Save physical count" }}
              </button>
            </form>
          </section>
          <section class="panel">
            <div class="panel-heading">
              <div>
                <h2>Receive stock</h2>
                <p>Add a delivery using a durable operation reference.</p>
              </div>
              <span class="panel-icon">↙</span>
            </div>
            <form
              class="form-body"
              [formGroup]="receiptForm"
              (ngSubmit)="submit('receipt')"
            >
              <label
                >Quantity received<input
                  type="number"
                  formControlName="quantity"
                  min="1"
                  step="1" /></label
              ><label
                >Reason / delivery reference<textarea
                  formControlName="reason"
                  maxlength="300"
                  rows="3"
                  placeholder="e.g. Delivery note DN-1042"
                ></textarea>
              </label>
              <p class="muted">
                Retries use the same operation ID and original quantity. Current
                balances are refreshed after a verified receipt.
              </p>
              <button
                [disabled]="
                  receiptForm.invalid || !stock() || saving() || blocked()
                "
              >
                Record receipt
              </button>
            </form>
          </section>
        </div>
      }
    }
    <app-error [message]="commandError()" />
    @if (message()) {
      <div class="notice success" role="status">{{ message() }}</div>
    }
    <app-recovery kind="receipt,stocktake"
  /></app-page>`,
})
export class Inventory {
  api = inject(Api);
  commands = inject(Commands);
  fb = inject(FormBuilder);
  route = inject(ActivatedRoute);
  products = signal<Product[]>([]);
  product = signal<Product | null>(null);
  stock = signal<Stock | null>(null);
  id = signal("");
  error = signal("");
  commandError = signal("");
  message = signal("");
  loading = signal(false);
  saving = signal(false);
  missing = signal(false);
  generation = 0;
  lookup = this.fb.nonNullable.group({
    id: ["", [Validators.required, Validators.pattern(guidPattern)]],
  });
  countForm = this.fb.nonNullable.group({
    onHand: [
      0,
      [
        Validators.required,
        Validators.min(0),
        Validators.max(2147483647),
        Validators.pattern(/^\d+$/),
      ],
    ],
    reason: [
      "",
      [
        Validators.required,
        Validators.pattern(/\S/),
        Validators.maxLength(300),
      ],
    ],
  });
  receiptForm = this.fb.nonNullable.group({
    quantity: [
      1,
      [
        Validators.required,
        Validators.min(1),
        Validators.max(2147483647),
        Validators.pattern(/^\d+$/),
      ],
    ],
    reason: [
      "",
      [
        Validators.required,
        Validators.pattern(/\S/),
        Validators.maxLength(300),
      ],
    ],
  });
  constructor() {
    this.api
      .products(0, 200)
      .then((p) => this.products.set(p))
      .catch(() => {});
    this.route.queryParamMap.subscribe((q) => {
      const id = q.get("productId");
      if (id) {
        this.lookup.patchValue({ id });
        if (this.lookup.valid) void this.load();
      }
    });
  }
  async load() {
    if (this.lookup.invalid) return;
    const g = ++this.generation;
    const id = this.lookup.getRawValue().id.trim();
    this.id.set(id);
    this.loading.set(true);
    this.error.set("");
    this.stock.set(null);
    this.product.set(null);
    this.missing.set(false);
    const results = await Promise.allSettled([
      this.api.stock(id),
      this.api.product(id),
    ]);
    if (g !== this.generation) return;
    const [s, p] = results;
    if (s.status === "fulfilled") {
      this.stock.set(s.value);
      this.countForm.patchValue({ onHand: s.value.onHand });
    } else if ((s.reason as any).status === 404) {
      this.missing.set(true);
    } else {
      this.error.set(errorMessage(s.reason));
    }
    if (p.status === "fulfilled") this.product.set(p.value);
    else if (!this.error())
      this.error.set(
        "Product details are unavailable. Inventory data, if shown, remains authoritative.",
      );
    this.loading.set(false);
  }
  blocked() {
    return this.commands
      .entries()
      .some(
        (c) =>
          ["receipt", "stocktake"].includes(c.kind) &&
          c.target === this.id() &&
          ["prepared", "pending", "uncertain"].includes(c.state),
      );
  }
  async submit(kind: "receipt" | "stocktake") {
    const form = kind === "receipt" ? this.receiptForm : this.countForm;
    if (form.invalid || this.saving() || this.blocked()) return;
    this.saving.set(true);
    this.commandError.set("");
    this.message.set("");
    try {
      const payload =
        kind === "receipt"
          ? {
              ...this.receiptForm.getRawValue(),
              operationId: crypto.randomUUID(),
            }
          : { ...this.countForm.getRawValue() };
      payload.reason = payload.reason.trim();
      const c = this.commands.prepare(kind, this.id(), payload);
      const r = await this.commands.run(c.id);
      if (r?.state === "verified") {
        this.message.set(
          kind === "receipt"
            ? "Receipt verified by the backend. The saved receipt contains its historical balance snapshot."
            : "Physical count verified by the backend.",
        );
        form.controls.reason.reset("");
        await this.load();
      } else {
        this.commandError.set(
          r?.message ||
            "Outcome unresolved. Inspect saved requests before sending another command.",
        );
      }
    } catch (e) {
      this.commandError.set((e as Error).message);
    } finally {
      this.saving.set(false);
    }
  }
}
