import { DialogFocus } from "../shared/dialog-focus";
import { Component, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { RouterLink } from "@angular/router";
import { Api, boundedMap, errorMessage } from "../core/api";
import { Product, Stock } from "../core/models";
import { Commands } from "../core/commands";
import { Page, Badge, Empty, Loading, ErrorBox } from "../shared/ui";
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
    Empty,
    Loading,
    ErrorBox,
    Recovery,
  ],
  template: ` <app-page
      section="CATALOGUE"
      title="Products"
      subtitle="Manage your catalogue and inspect live inventory availability."
      ><button actions (click)="open.set(true)">＋ Create product</button>
      <section class="panel">
        <form
          class="toolbar"
          [formGroup]="filters"
          (ngSubmit)="offset = 0; load()"
        >
          <label class="search-field"
            ><span>Search catalogue</span
            ><input
              formControlName="search"
              placeholder="Search SKU or product name"
              maxlength="100" /></label
          ><label
            ><span>Product status</span
            ><select formControlName="active">
              <option value="">All statuses</option>
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select></label
          ><button class="secondary" type="submit">Search</button>
        </form>
        <app-error [message]="error()" />
        @if (loading()) {
          <app-loading />
        } @else if (rows().length) {
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Product</th>
                  <th>Status</th>
                  <th class="numeric">Unit price</th>
                  <th class="numeric">On hand</th>
                  <th class="numeric">Reserved</th>
                  <th class="numeric">Available</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (p of rows(); track p.id) {
                  <tr>
                    <td>
                      <strong>{{ p.name }}</strong
                      ><small class="mono">{{ p.sku }}</small>
                    </td>
                    <td>
                      <app-badge [value]="p.isActive ? 'Active' : 'Inactive'" />
                    </td>
                    <td class="numeric">
                      {{ p.unitPrice | currency: "MYR" : "symbol-narrow" }}
                    </td>
                    @if (balances()[p.id]; as s) {
                      <td class="numeric">{{ s.onHand | number }}</td>
                      <td class="numeric">{{ s.reserved | number }}</td>
                      <td class="numeric">
                        <strong>{{ s.availableQuantity | number }}</strong>
                      </td>
                    } @else {
                      <td colspan="3" class="muted">
                        {{ stockStates()[p.id] || "Loading balance…" }}
                      </td>
                    }
                    <td>
                      <a
                        routerLink="/inventory"
                        [queryParams]="{ productId: p.id }"
                        >View stock ↗</a
                      >
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else if (!error()) {
          <app-empty
            title="No matching products"
            text="Try another search or create your first product."
          />
        }
        <div class="pagination">
          <span
            >{{
              rows().length
                ? "Showing " + (offset + 1) + "–" + (offset + rows().length)
                : "No rows"
            }}
            · Active is a catalogue status, separate from available stock.</span
          >
          <div>
            <button
              class="secondary"
              [disabled]="offset === 0 || loading()"
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
      <app-recovery kind="product"
    /></app-page>
    @if (open()) {
      <div class="modal-backdrop">
        <section
          class="modal"
          appDialogFocus
          role="dialog"
          aria-modal="true"
          (keydown.escape)="open.set(false)"
          aria-labelledby="create-product-title"
        >
          <div class="panel-heading">
            <h2 id="create-product-title">Create product</h2>
            <button
              class="icon-button"
              aria-label="Close"
              (click)="open.set(false)"
            >
              ×
            </button>
          </div>
          <form [formGroup]="form" (ngSubmit)="create()">
            <div class="form-body">
              <p class="muted">
                A new catalogue entry does not initialise stock.
              </p>
              <label
                >SKU<input
                  formControlName="sku"
                  maxlength="50"
                  placeholder="e.g. SKU-1001" /></label
              ><label
                >Product name<input
                  formControlName="name"
                  maxlength="200" /></label
              ><label
                >Description<textarea
                  formControlName="description"
                  rows="3"
                ></textarea></label
              ><label
                >Unit price (MYR)<input
                  formControlName="unitPrice"
                  type="number"
                  min="0"
                  step="0.01"
              /></label>
              @if (form.invalid && form.touched) {
                <p class="field-error">
                  Enter a SKU, name and a non-negative price with up to two
                  decimal places.
                </p>
              }
              <app-error [message]="createError()" />
            </div>
            <div class="modal-actions">
              <button type="button" class="secondary" (click)="open.set(false)">
                Close</button
              ><button [disabled]="saving() || form.invalid">
                {{ saving() ? "Saving…" : "Create product" }}
              </button>
            </div>
          </form>
        </section>
      </div>
    }`,
})
export class Products {
  api = inject(Api);
  commands = inject(Commands);
  fb = inject(FormBuilder);
  rows = signal<Product[]>([]);
  balances = signal<Record<string, Stock>>({});
  stockStates = signal<Record<string, string>>({});
  loading = signal(false);
  error = signal("");
  more = signal(false);
  offset = 0;
  generation = 0;
  open = signal(false);
  saving = signal(false);
  createError = signal("");
  filters = this.fb.nonNullable.group({ search: [""], active: [""] });
  form = this.fb.nonNullable.group({
    sku: [
      "",
      [Validators.required, Validators.maxLength(50), Validators.pattern(/\S/)],
    ],
    name: [
      "",
      [
        Validators.required,
        Validators.maxLength(200),
        Validators.pattern(/\S/),
      ],
    ],
    description: [""],
    unitPrice: [
      0,
      [
        Validators.required,
        Validators.min(0),
        Validators.pattern(/^\d+(\.\d{1,2})?$/),
      ],
    ],
  });
  constructor() {
    void this.load();
  }
  async load() {
    const g = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    try {
      const f = this.filters.getRawValue();
      const data = await this.api.products(
        this.offset,
        21,
        f.search.trim(),
        f.active,
      );
      if (g !== this.generation) return;
      this.rows.set(data.slice(0, 20));
      this.more.set(data.length > 20);
      this.balances.set({});
      this.stockStates.set({});
      this.loading.set(false);
      await boundedMap(data.slice(0, 20), async (p) => {
        try {
          const s = await this.api.stock(p.id);
          if (g === this.generation)
            this.balances.update((v) => ({ ...v, [p.id]: s }));
        } catch (e) {
          if (g === this.generation)
            this.stockStates.update((v) => ({
              ...v,
              [p.id]:
                (e as any).status === 404
                  ? "Not initialised"
                  : "Inventory unavailable",
            }));
        }
      });
    } catch (e) {
      if (g === this.generation) {
        this.error.set(errorMessage(e));
        this.rows.set([]);
      }
    } finally {
      if (g === this.generation) this.loading.set(false);
    }
  }
  async create() {
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.createError.set("");
    try {
      const v = this.form.getRawValue();
      const c = this.commands.prepare("product", "", {
        ...v,
        sku: v.sku.trim(),
        name: v.name.trim(),
        description: v.description.trim() || null,
      });
      const r = await this.commands.run(c.id);
      if (r?.state === "verified") {
        this.open.set(false);
        this.form.reset({ sku: "", name: "", description: "", unitPrice: 0 });
        await this.load();
      } else {
        this.createError.set(r?.message || "Inspect saved request.");
        this.open.set(false);
      }
    } catch (e) {
      this.createError.set((e as Error).message);
    } finally {
      this.saving.set(false);
    }
  }
}
