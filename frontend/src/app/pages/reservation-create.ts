import { Component, inject, output, signal } from "@angular/core";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { Commands } from "../core/commands";
import { DialogFocus } from "../shared/dialog-focus";
import { ProductSelect } from "../shared/product-select";
import { ErrorBox } from "../shared/ui";
@Component({
  selector: "app-reservation-create",
  standalone: true,
  imports: [ReactiveFormsModule, DialogFocus, ProductSelect, ErrorBox],
  template: ` <div class="modal-backdrop">
    <section
      class="modal wide"
      appDialogFocus
      role="dialog"
      aria-modal="true"
      aria-labelledby="reservation-create-title"
      (keydown.escape)="closed.emit()"
    >
      <div class="panel-heading">
        <div>
          <h2 id="reservation-create-title">Create reservation</h2>
          <p>Reserve stock directly through InventoryService.</p>
        </div>
        <button class="icon-button" aria-label="Close" (click)="closed.emit()">
          ×
        </button>
      </div>
      <form [formGroup]="form" (ngSubmit)="submit()">
        <div class="form-body">
          <div class="notice">
            This creates a standalone stock hold, not a sales order. For a
            customer purchase, create an order instead. InventoryService decides
            whether all requested stock can be reserved.
          </div>
          <label
            >Reservation ID<input class="mono" [value]="reservationId" readonly
          /></label>
          <div formArrayName="items">
            @for (line of items.controls; track line; let i = $index) {
              <div class="reservation-line" [formGroupName]="i">
                <div class="line-heading">
                  <h3>Product {{ i + 1 }}</h3>
                  <button
                    class="text-button"
                    type="button"
                    [disabled]="items.length === 1 || saving()"
                    (click)="items.removeAt(i)"
                    aria-label="Remove reservation line"
                  >
                    Remove
                  </button>
                </div>
                <app-product-select
                  [selectedId]="line.controls.productId.value"
                  (selected)="line.controls.productId.setValue($event)"
                /><label class="quantity"
                  >Quantity<input
                    type="number"
                    formControlName="quantity"
                    min="1"
                    max="10000"
                    step="1"
                /></label>
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
          <p class="muted">
            Up to 50 distinct products, 1–10,000 units per line. The original
            reservation ID and quantities are saved before sending and reused on
            retry.
          </p>
          <app-error [message]="error()" />
        </div>
        <div class="modal-actions">
          <button type="button" class="secondary" (click)="closed.emit()">
            Close</button
          ><button [disabled]="form.invalid || saving() || sent()">
            {{ saving() ? "Awaiting inventory…" : "Reserve stock" }}
          </button>
        </div>
      </form>
    </section>
  </div>`,
})
export class ReservationCreate {
  fb = inject(FormBuilder);
  commands = inject(Commands);
  closed = output<void>();
  submitted = output<string>();
  reservationId = crypto.randomUUID();
  saving = signal(false);
  sent = signal(false);
  error = signal("");
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
  form = this.fb.group({ items: this.fb.array([this.line()]) });
  get items() {
    return this.form.controls.items;
  }
  addLine() {
    this.items.push(this.line());
  }
  async submit() {
    if (this.form.invalid || this.saving() || this.sent()) return;
    const items = this.items.getRawValue();
    if (new Set(items.map((i) => i.productId)).size !== items.length) {
      this.error.set(
        "Use each product once. Combine quantities into one line.",
      );
      return;
    }
    this.saving.set(true);
    this.error.set("");
    try {
      const c = this.commands.prepare("reservation", this.reservationId, {
        reservationId: this.reservationId,
        items,
      });
      this.sent.set(true);
      await this.commands.run(c.id);
      this.submitted.emit(this.reservationId);
      this.closed.emit();
    } catch (e) {
      this.error.set((e as Error).message);
    } finally {
      this.saving.set(false);
    }
  }
}
