import { Component, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { Api, errorMessage } from "../core/api";
import { Movement, Reservation } from "../core/models";
import { Page, Badge, Loading, ErrorBox, Empty } from "../shared/ui";
import { guidPattern } from "./inventory";
import { ProductSelect } from "../shared/product-select";
import { ReservationCreate } from "./reservation-create";
import { Commands } from "../core/commands";
import { Recovery } from "../shared/recovery";
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
    Empty,
    ReservationCreate,
    Recovery,
  ],
  template: ` <app-page
      section="RESERVATION AUDIT"
      title="Reservations"
      subtitle="Create a standalone stock hold or inspect an existing reservation."
      ><button actions (click)="create()">＋ Create reservation</button>
      <section class="panel">
        <form class="toolbar" [formGroup]="form" (ngSubmit)="load()">
          <label class="grow"
            >Reservation / order ID<input
              formControlName="id"
              placeholder="Paste reservation or order UUID" /></label
          ><button [disabled]="form.invalid || loading()">
            Find reservation
          </button>
        </form>
      </section>
      <app-error [message]="error()" />
      @if (loading()) {
        <app-loading />
      } @else if (record(); as r) {
        <section class="panel">
          <div class="panel-heading">
            <div>
              <h2>Reservation details</h2>
              <small class="mono">{{ r.reservationId }}</small>
            </div>
            <app-badge [value]="r.status" />
          </div>
          <div class="notice inset">
            Original quantities requested for this reservation.
          </div>
          @if (r.failureCode) {
            <div class="notice danger inset">
              Failure code: <strong>{{ r.failureCode }}</strong>
            </div>
          }
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Original product ID</th>
                  <th class="numeric">Requested quantity</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (i of r.items; track i.productId) {
                  <tr>
                    <td class="mono">{{ i.productId }}</td>
                    <td class="numeric">{{ i.quantity | number }}</td>
                    <td>
                      <a
                        routerLink="/inventory"
                        [queryParams]="{ productId: i.productId }"
                        >Current inventory ↗</a
                      >
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (!r.items.length) {
            <p class="padded muted">
              No reservation items found.
            </p>
          }
          @if (r.shortfalls.length) {
            <div class="panel-heading">
              <h2>Recorded shortages</h2>

            </div>
            <div class="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Product ID</th>
                    <th class="numeric">Requested</th>
                    <th class="numeric">Available then</th>
                  </tr>
                </thead>
                <tbody>
                  @for (s of r.shortfalls; track s.productId) {
                    <tr>
                      <td class="mono">{{ s.productId }}</td>
                      <td class="numeric">{{ s.requested }}</td>
                      <td class="numeric">{{ s.available }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          <div class="panel-footer">
            <span [hidden]="!manual(r.reservationId)" class="muted"
              >Standalone reservation · no sales order created</span
            ><a
              [hidden]="manual(r.reservationId)"
              [routerLink]="['/orders', r.reservationId]"
              >Look for related order ↗</a
            ><span class="muted">This lookup is read-only.</span>
          </div>
        </section>
      } @else if (!error()) {
        <section class="panel">
          <app-empty
            title="Find a reservation"
            text="Use an order ID or reservation ID to inspect the durable inventory outcome."
          />
        </section>
      }
      <app-recovery kind="reservation" />
    </app-page>
    @if (open()) {
      <app-reservation-create
        (closed)="open.set(false)"
        (submitted)="created($event)"
      />
    }`,
})
export class Reservations {
  api = inject(Api);
  commands = inject(Commands);
  open = signal(false);
  manual(id: string) {
    return this.commands
      .entries()
      .some((c) => c.kind === "reservation" && c.payload.reservationId === id);
  }
  create() {
    if (
      this.commands.unresolved().some((c) => c.kind === "reservation") &&
      !window.confirm(
        "An earlier reservation is unresolved. A new reservation ID creates a separate stock hold. Continue with a separate reservation?",
      )
    )
      return;
    this.open.set(true);
  }
  created(id: string) {
    this.form.patchValue({ id });
    void this.load();
  }
  fb = inject(FormBuilder);
  route = inject(ActivatedRoute);
  record = signal<Reservation | null>(null);
  loading = signal(false);
  error = signal("");
  generation = 0;
  form = this.fb.nonNullable.group({
    id: ["", [Validators.required, Validators.pattern(guidPattern)]],
  });
  constructor() {
    this.route.queryParamMap.subscribe((q) => {
      const id = q.get("id");
      if (id) {
        this.form.patchValue({ id });
        if (this.form.valid) void this.load();
      }
    });
  }
  async load() {
    if (this.form.invalid) return;
    const g = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    this.record.set(null);
    try {
      const r = await this.api.reservation(this.form.getRawValue().id);
      if (g === this.generation) {
        this.record.set(r);
        this.commands.observeReservation(r);
      }
    } catch (e) {
      if (g === this.generation) this.error.set(errorMessage(e));
    } finally {
      if (g === this.generation) this.loading.set(false);
    }
  }
}
@Component({
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    Page,
    Loading,
    ErrorBox,
    Empty,
    ProductSelect,
  ],
  template: ` <app-page
    section="AUDIT TRAIL"
    title="Stock movements"
    subtitle="A read-only, product-specific ledger of physical and reserved stock changes."
    ><section class="panel">
      <div class="toolbar movement-picker">
        <app-product-select
          class="grow"
          [selectedId]="form.controls.id.value"
          (selected)="select($event)"
        /><button
          class="secondary"
          [disabled]="form.invalid || loading()"
          (click)="load()"
        >
          ↻ Refresh ledger
        </button>
      </div>
      <app-error [message]="error()" />
      @if (loading()) {
        <app-loading />
      } @else if (rows().length) {
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Movement / MYT</th>
                <th>Operation reference</th>
                <th class="numeric">On-hand Δ</th>
                <th class="numeric">Reserved Δ</th>
                <th class="numeric">On hand after</th>
                <th class="numeric">Reserved after</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              @for (m of rows(); track m.id) {
                <tr>
                  <td>
                    <strong>{{ m.movementType }}</strong
                    ><small>{{
                      m.createdAtUtc | date: "dd MMM yyyy, HH:mm:ss" : "+0800"
                    }}</small>
                  </td>
                  <td class="mono">{{ m.operationId }}</td>
                  <td
                    class="numeric"
                    [class.positive]="m.onHandDelta > 0"
                    [class.negative]="m.onHandDelta < 0"
                  >
                    {{ m.onHandDelta > 0 ? "+" : "" }}{{ m.onHandDelta }}
                  </td>
                  <td class="numeric">
                    {{ m.reservedDelta > 0 ? "+" : "" }}{{ m.reservedDelta }}
                  </td>
                  <td class="numeric">{{ m.onHandAfter }}</td>
                  <td class="numeric">{{ m.reservedAfter }}</td>
                  <td>{{ m.reason || "—" }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (!error()) {
        <app-empty
          title="No movements loaded"
          text="Select a product to read its audit ledger. New products may have no movements."
        />
      }
      <div class="pagination">
        <span
          >Oldest first · {{ rows().length }} rows on this page · Historical
          balances</span
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
    </section></app-page
  >`,
})
export class Movements {
  api = inject(Api);
  fb = inject(FormBuilder);
  route = inject(ActivatedRoute);
  rows = signal<Movement[]>([]);
  loading = signal(false);
  error = signal("");
  more = signal(false);
  offset = 0;
  generation = 0;
  form = this.fb.nonNullable.group({
    id: ["", [Validators.required, Validators.pattern(guidPattern)]],
  });
  select(id: string) {
    this.form.patchValue({ id });
    this.offset = 0;
    this.rows.set([]);
    this.more.set(false);
    if (this.form.valid) void this.load();
    else {
      this.generation++;
      this.loading.set(false);
      this.error.set("");
    }
  }
  constructor() {
    this.route.queryParamMap.subscribe((q) => {
      const id = q.get("productId");
      if (id) {
        this.form.patchValue({ id });
        this.offset = 0;
        if (this.form.valid) void this.load();
      }
    });
  }
  async load() {
    if (this.form.invalid) return;
    const g = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    try {
      const r = await this.api.movements(
        this.form.getRawValue().id,
        this.offset,
      );
      if (g === this.generation) {
        this.rows.set(r.slice(0, 20));
        this.more.set(r.length > 20);
      }
    } catch (e) {
      if (g === this.generation) {
        this.error.set(errorMessage(e));
        this.rows.set([]);
      }
    } finally {
      if (g === this.generation) this.loading.set(false);
    }
  }
}
