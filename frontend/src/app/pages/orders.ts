import { DialogFocus } from "../shared/dialog-focus";
import { Component, inject, signal, computed } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { RouterLink, Router } from "@angular/router";
import { Api, errorMessage } from "../core/api";
import { Commands } from "../core/commands";
import { Order, Product, orderStatuses } from "../core/models";
import { Page, Badge, Loading, Empty, ErrorBox } from "../shared/ui";
import { Recovery } from "../shared/recovery";
@Component({
  standalone: true,
  imports: [
    DialogFocus,
    CommonModule,
    ReactiveFormsModule,
    RouterLink,
    Page,
    Badge,
    Loading,
    Empty,
    ErrorBox,
    Recovery,
  ],
  template: ` <app-page
      section="SALES OPERATIONS"
      title="Orders"
      subtitle="Create multi-product orders and track backend-verified outcomes."
      ><button actions (click)="startDraft()">＋ Create order</button>
      <section class="panel">
        <form
          class="toolbar"
          [formGroup]="filter"
          (ngSubmit)="offset = 0; load()"
        >
          <label
            >Status<select formControlName="status">
              <option value="">All statuses</option>
              @for (s of statuses; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select></label
          ><button class="secondary">Apply filter</button
          ><span class="muted grow"
            >Newest first · RequiresReview identifies orders needing manual
            investigation.</span
          ><button class="secondary" type="button" (click)="load()">
            ↻ Refresh
          </button>
        </form>
        <app-error [message]="error()" />
        @if (loading()) {
          <app-loading />
        } @else if (rows().length) {
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Order reference</th>
                  <th>Created · MYT</th>
                  <th>Status</th>
                  <th class="numeric">Lines</th>
                  <th class="numeric">Total</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (o of rows(); track o.orderId) {
                  <tr>
                    <td>
                      <a class="mono" [routerLink]="['/orders', o.orderId]"
                        >{{ o.orderId.slice(0, 8) }}…</a
                      >
                    </td>
                    <td>
                      {{
                        o.createdAtUtc | date: "dd MMM yyyy, HH:mm" : "+0800"
                      }}
                    </td>
                    <td><app-badge [value]="o.status" /></td>
                    <td class="numeric">{{ o.lines.length }}</td>
                    <td class="numeric">
                      {{ o.totalAmount | currency: "MYR" : "symbol-narrow" }}
                    </td>
                    <td>
                      <a [routerLink]="['/orders', o.orderId]">View order ↗</a>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else if (!error()) {
          <app-empty
            title="No matching orders"
            text="Orders will appear here once submitted to the backend."
          />
        }
        <div class="pagination">
          <span>{{ rows().length }} orders on this page</span>
          <div>
            <button
              class="secondary"
              [disabled]="!offset || loading()"
              (click)="offset -= 20; load()"
            >
              Previous</button
            ><button
              class="secondary"
              [disabled]="!more() || loading()"
              (click)="offset += 20; load()"
            >
              Next
            </button>
          </div>
        </div>
      </section>
      <app-recovery kind="order,cancel"
    /></app-page>
    @if (open()) {
      <div class="modal-backdrop">
        <section
          class="modal wide"
          appDialogFocus
          role="dialog"
          aria-modal="true"
          (keydown.escape)="open.set(false)"
          aria-labelledby="order-create-title"
        >
          <div class="panel-heading">
            <div>
              <h2 id="order-create-title">Create order</h2>
              <p>
                Backend product prices and reservation results are
                authoritative.
              </p>
            </div>
            <button
              class="icon-button"
              (click)="open.set(false)"
              aria-label="Close"
            >
              ×
            </button>
          </div>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <div class="form-body">
              <div class="inline-search" [formGroup]="search">
                <input
                  aria-label="Search products for order"
                  formControlName="term"
                  (keydown.enter)="$event.preventDefault(); searchProducts()"
                  placeholder="Search active products by name / SKU"
                /><button
                  type="button"
                  class="secondary"
                  (click)="searchProducts()"
                >
                  Search catalogue
                </button>
              </div>
              <p class="muted">
                Showing up to 200 active catalogue matches. Refine the search to
                find a specific SKU or name.
              </p>
              <app-error [message]="catalogueError()" />
              <div class="notice">
                Estimates use the current catalogue price. An Active product can
                still be out of stock.
              </div>
              <div formArrayName="items">
                @for (line of items.controls; track line; let i = $index) {
                  <div class="order-line" [formGroupName]="i">
                    <label class="grow"
                      >Product<select formControlName="productId">
                        <option value="">Select product</option>
                        @for (p of products(); track p.id) {
                          <option [value]="p.id">
                            {{ p.sku }} · {{ p.name }} · MYR
                            {{ p.unitPrice | number: "1.2-2" }}
                          </option>
                        }
                      </select></label
                    ><label class="quantity"
                      >Quantity<input
                        type="number"
                        formControlName="quantity"
                        min="1"
                        max="10000"
                        step="1" /></label
                    ><button
                      type="button"
                      class="icon-button remove-line"
                      [disabled]="items.length === 1 || saving()"
                      (click)="items.removeAt(i)"
                      aria-label="Remove line"
                    >
                      ×
                    </button>
                  </div>
                }
              </div>
              <button
                class="text-button"
                type="button"
                [disabled]="items.length >= 50 || saving()"
                (click)="addLine()"
              >
                ＋ Add product line
              </button>
              <div class="estimate">
                <span>Estimated total</span
                ><strong>{{
                  estimate() | currency: "MYR" : "symbol-narrow"
                }}</strong>
              </div>
              <p class="muted">
                Up to 50 distinct products, 1–10,000 units per line. A saved key
                and immutable payload are created before submission.
              </p>
              <app-error [message]="submitError()" />
            </div>
            <div class="modal-actions">
              <button type="button" class="secondary" (click)="open.set(false)">
                Close</button
              ><button [disabled]="form.invalid || saving() || submitted()">
                {{ saving() ? "Awaiting backend…" : "Submit order" }}
              </button>
            </div>
          </form>
        </section>
      </div>
    }`,
})
export class Orders {
  api = inject(Api);
  commands = inject(Commands);
  fb = inject(FormBuilder);
  router = inject(Router);
  statuses = orderStatuses;
  filter = this.fb.nonNullable.group({ status: [""] });
  search = this.fb.nonNullable.group({ term: [""] });
  rows = signal<Order[]>([]);
  products = signal<Product[]>([]);
  loading = signal(false);
  error = signal("");
  more = signal(false);
  offset = 0;
  generation = 0;
  open = signal(false);
  saving = signal(false);
  submitted = signal(false);
  submitError = signal("");
  catalogueError = signal("");
  form = this.fb.group({ items: this.fb.array([this.line()]) });
  get items() {
    return this.form.controls.items;
  }
  line() {
    return this.fb.nonNullable.group({
      productId: ["", Validators.required],
      quantity: [
        1,
        [
          Validators.required,
          Validators.min(1),
          Validators.max(10000),
          Validators.pattern(/^\d+$/),
        ],
      ],
    });
  }
  addLine() {
    this.items.push(this.line());
  }
  estimate() {
    return this.items
      .getRawValue()
      .reduce(
        (sum, i) =>
          sum +
          (this.products().find((p) => p.id === i.productId)?.unitPrice || 0) *
            Number(i.quantity),
        0,
      );
  }
  constructor() {
    void this.load();
    void this.searchProducts();
  }
  async searchProducts() {
    this.catalogueError.set("");
    try {
      const found = await this.api.products(
        0,
        200,
        this.search.getRawValue().term.trim(),
        "true",
      );
      const selected = this.items.getRawValue().map((i) => i.productId);
      this.products.set([
        ...found,
        ...this.products().filter(
          (p) => selected.includes(p.id) && !found.some((n) => n.id === p.id),
        ),
      ]);
    } catch (e) {
      this.catalogueError.set(errorMessage(e));
    }
  }
  startDraft() {
    if (
      this.commands.unresolved().some((c) => c.kind === "order") &&
      !window.confirm(
        "An earlier order request is unresolved. Starting a separate purchase creates a new key and may reserve additional stock. Continue with a separate purchase?",
      )
    )
      return;
    this.items.clear();
    this.addLine();
    this.submitted.set(false);
    this.submitError.set("");
    this.open.set(true);
  }
  async load() {
    const g = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    try {
      const r = await this.api.orders(
        this.offset,
        21,
        this.filter.getRawValue().status,
      );
      if (g === this.generation) {
        this.rows.set(r.slice(0, 20));
        this.more.set(r.length > 20);
        for (const o of r) this.commands.observeOrder(o);
      }
    } catch (e) {
      if (g === this.generation) {
        this.rows.set([]);
        this.error.set(errorMessage(e));
      }
    } finally {
      if (g === this.generation) this.loading.set(false);
    }
  }
  async submit() {
    if (this.form.invalid || this.saving() || this.submitted()) return;
    this.submitError.set("");
    const items = this.items.getRawValue();
    if (new Set(items.map((i) => i.productId)).size !== items.length) {
      this.submitError.set(
        "Each product may appear only once. Combine its quantities into one line.",
      );
      return;
    }
    this.saving.set(true);
    try {
      const c = this.commands.prepare("order", "", { items });
      this.submitted.set(true);
      const r = await this.commands.run(c.id);
      this.open.set(false);
      await this.load();
      if (r?.orderId) await this.router.navigate(["/orders", r.orderId]);
    } catch (e) {
      this.submitError.set((e as Error).message);
    } finally {
      this.saving.set(false);
    }
  }
}
