import { Component, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormBuilder, ReactiveFormsModule, Validators } from "@angular/forms";
import { ActivatedRoute, RouterLink } from "@angular/router";
import { Api, errorMessage } from "../core/api";
import { Commands } from "../core/commands";
import { Order } from "../core/models";
import { Page, Badge, Loading, ErrorBox } from "../shared/ui";
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
    Recovery,
  ],
  template: ` <app-page
    section="ORDER WORKSPACE"
    title="Order details"
    subtitle="View order items, status and cancellation details."
    ><div actions class="button-row">
      <a class="button secondary" routerLink="/orders">← All orders</a
      ><button class="secondary" [disabled]="loading()" (click)="load()">
        ↻ Refresh
      </button>
    </div>
    <app-error [message]="error()" />
    @if (loading()) {
      <app-loading />
    } @else if (order(); as o) {
      <section class="panel">
        <div class="panel-heading">
          <div>
            <h2>
              Order <span class="mono">{{ o.orderId.slice(0, 8) }}</span>
            </h2>
            <small class="mono">{{ o.orderId }}</small>
          </div>
          <app-badge [value]="o.status" />
        </div>
        <div class="detail-grid">
          <div>
            <span>Created · MYT</span
            ><strong>{{
              o.createdAtUtc | date: "dd MMM yyyy, HH:mm:ss" : "+0800"
            }}</strong>
          </div>
          <div>
            <span>Last updated · MYT</span
            ><strong>{{
              o.updatedAtUtc | date: "dd MMM yyyy, HH:mm:ss" : "+0800"
            }}</strong>
          </div>
          <div>
            <span>Backend total</span
            ><strong>{{
              o.totalAmount | currency: "MYR" : "symbol-narrow"
            }}</strong>
          </div>
        </div>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Product snapshot</th>
                <th class="numeric">Unit price</th>
                <th class="numeric">Quantity</th>
                <th class="numeric">Line total</th>
              </tr>
            </thead>
            <tbody>
              @for (l of o.lines; track l.productId) {
                <tr>
                  <td>
                    <strong>{{ l.name }}</strong
                    ><small
                      >{{ l.sku }} ·
                      <span class="mono">{{ l.productId }}</span></small
                    >
                  </td>
                  <td class="numeric">
                    {{ l.unitPrice | currency: "MYR" : "symbol-narrow" }}
                  </td>
                  <td class="numeric">{{ l.quantity }}</td>
                  <td class="numeric">
                    {{ l.lineTotal | currency: "MYR" : "symbol-narrow" }}
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="panel-footer">
          <a routerLink="/reservations" [queryParams]="{ id: o.orderId }"
            >Inspect inventory reservation ↗</a
          ><span class="muted">Historical order lines and prices</span>
        </div>
      </section>
      @if (o.failureCode) {
        <div class="notice danger">
          Failure code: <strong>{{ o.failureCode }}</strong>
        </div>
      }
      @if (o.shortfalls.length) {
        <section class="panel">
          <div class="panel-heading"><h2>Recorded shortages</h2></div>
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Product</th>
                  <th class="numeric">Requested</th>
                  <th class="numeric">Available then</th>
                </tr>
              </thead>
              <tbody>
                @for (s of o.shortfalls; track s.productId) {
                  <tr>
                    <td class="mono">{{ s.productId }}</td>
                    <td class="numeric">{{ s.requested }}</td>
                    <td class="numeric">{{ s.available }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }
      @if (o.status === "RequiresReview") {
        <div class="notice danger">
          <strong>Manual investigation required.</strong> Review this order, its
          reservation and the stock ledger. This page does not reconcile review
          states or change stock automatically.
        </div>
      }
      @if (o.status === "Pending") {
        <div class="notice">
          <strong>Order outcome pending.</strong> Retry the original saved
          request below. If it is not in this browser, use the original
          submitting client with its exact idempotency key and payload. A new
          key creates a separate order.
        </div>
      }
      @if (o.status === "CancellationPending") {
        <div class="notice">
          Cancellation is pending. Check again to update its status.
        </div>
        <button [disabled]="saving()" (click)="reconcile()">
          Reconcile cancellation
        </button>
      }
      @if (o.cancellationReason) {
        <div class="notice">
          <strong>Original cancellation reason:</strong>
          {{ o.cancellationReason }}
        </div>
      }
      @if (o.status === "Confirmed") {
        <section class="panel">
          <div class="panel-heading">
            <div>
              <h2>Cancel order</h2>
            </div>
          </div>
          <form class="form-body" [formGroup]="form" (ngSubmit)="cancel()">
            <label
              >Cancellation reason<textarea
                formControlName="reason"
                rows="3"
                maxlength="500"
                placeholder="Why is this order being cancelled?"
              ></textarea>
            </label>
            <p class="muted">
              The first accepted reason is retained. Pending or uncertain
              results stay visible in saved requests.
            </p>
            <button class="danger-button" [disabled]="form.invalid || saving()">
              {{ saving() ? "Awaiting backend…" : "Cancel order" }}
            </button>
          </form>
        </section>
      }
    }
    <app-error [message]="commandError()" /><app-recovery kind="order,cancel"
  /></app-page>`,
})
export class OrderDetail {
  api = inject(Api);
  commands = inject(Commands);
  route = inject(ActivatedRoute);
  fb = inject(FormBuilder);
  id = "";
  order = signal<Order | null>(null);
  loading = signal(false);
  saving = signal(false);
  error = signal("");
  commandError = signal("");
  generation = 0;
  form = this.fb.nonNullable.group({
    reason: [
      "",
      [
        Validators.required,
        Validators.maxLength(500),
        Validators.pattern(/\S/),
      ],
    ],
  });
  constructor() {
    this.route.paramMap.subscribe((p) => {
      this.id = p.get("id") || "";
      void this.load();
    });
  }
  async load() {
    const g = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    try {
      const o = await this.api.order(this.id);
      if (g === this.generation) {
        this.order.set(o);
        this.commands.observeOrder(o);
      }
    } catch (e) {
      if (g === this.generation) {
        this.error.set(errorMessage(e));
        this.order.set(null);
      }
    } finally {
      if (g === this.generation) this.loading.set(false);
    }
  }
  async cancel() {
    if (this.form.invalid || this.saving()) return;
    const existing = this.commands
      .entries()
      .find(
        (c) =>
          c.kind === "cancel" && c.target === this.id && c.state !== "archived",
      );
    await this.execute(existing?.id, false);
  }
  async reconcile() {
    const existing = this.commands
      .entries()
      .find((c) => c.kind === "cancel" && c.target === this.id);
    await this.execute(existing?.id, true);
  }
  async execute(existingId: string | undefined, reconcile: boolean) {
    this.saving.set(true);
    this.commandError.set("");
    try {
      const c = existingId
        ? this.commands.get(existingId)!
        : this.commands.prepare("cancel", this.id, {
            reason:
              this.order()?.cancellationReason ||
              this.form.getRawValue().reason.trim(),
          });
      const r = await this.commands.run(c.id, reconcile);
      if (r?.state !== "verified")
        this.commandError.set(
          r?.message || "Cancellation has not been verified.",
        );
      await this.load();
    } catch (e) {
      this.commandError.set((e as Error).message);
    } finally {
      this.saving.set(false);
    }
  }
}
