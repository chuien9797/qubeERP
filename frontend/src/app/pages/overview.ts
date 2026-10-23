import { Component, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { RouterLink } from "@angular/router";
import { Api, errorMessage } from "../core/api";
import { Order } from "../core/models";
import { Page, Badge, Loading, ErrorBox, Empty } from "../shared/ui";
import { Recovery } from "../shared/recovery";
@Component({
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    Page,
    Badge,
    Loading,
    ErrorBox,
    Empty,
    Recovery,
  ],
  template: ` <app-page
    section="OPERATIONS OVERVIEW"
    title="A clear view of your operations"
    subtitle="Live order activity and local requests that may need your attention."
    ><button actions class="secondary" (click)="load()" [disabled]="loading()">
      ↻ Refresh activity
    </button>
    <div class="welcome-banner">
      <div class="banner-label">QUBEERP WORKSPACE</div>
      <h2>Stock under control.<br />Every movement accounted for.</h2>
      <p>
        Manage products, reservations and orders in one place.
      </p>
      <div class="button-row">
        <a class="button" routerLink="/orders">Open orders →</a
        ><a class="button secondary" routerLink="/inventory"
          >Inspect inventory</a
        >
      </div>
      <div class="banner-art" aria-hidden="true">
        <div></div>
        <div></div>
        <div></div>
      </div>
    </div>
    <div class="section-heading">
      <div>
        <h2>Recent order snapshot</h2>
        <p>
          Latest {{ orders().length }} orders.
        </p>
      </div>
      <a routerLink="/orders">Browse orders ↗</a>
    </div>
    <app-error [message]="error()" />
    @if (loading()) {
      <app-loading />
    } @else if (!error()) {
      <div class="metrics">
        <article class="metric">
          <span>Orders in this window</span
          ><strong>{{ orders().length }}</strong
          ><small>Up to 20 most recent orders</small>
        </article>
        <article class="metric">
          <span>Confirmed in this window</span
          ><strong>{{ count("Confirmed") }}</strong
          ><small>Stock reserved</small>
        </article>
        <article class="metric accent">
          <span>Needs attention in this window</span
          ><strong>{{ attention() }}</strong
          ><small>Pending, cancellation pending or review</small>
        </article>
      </div>
      <section class="panel">
        <div class="panel-heading">
          <div>
            <h2>Recent activity</h2>
            <p>Newest orders first</p>
          </div>
          <a routerLink="/orders">View all orders →</a>
        </div>
        @if (orders().length) {
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Order reference</th>
                  <th>Created</th>
                  <th>Status</th>
                  <th class="numeric">Total</th>
                </tr>
              </thead>
              <tbody>
                @for (o of orders().slice(0, 6); track o.orderId) {
                  <tr class="navigable-row">
                    <td>
                      <a class="mono row-link" [routerLink]="['/orders', o.orderId]"
                        [attr.aria-label]="'View order ' + o.orderId"
                        >{{ o.orderId.slice(0, 8) }}…</a
                      >
                    </td>
                    <td>
                      {{
                        o.createdAtUtc | date: "dd MMM yyyy, HH:mm" : "+0800"
                      }}
                    </td>
                    <td><app-badge [value]="o.status" /></td>
                    <td class="numeric">
                      {{ o.totalAmount | currency: "MYR" : "symbol-narrow" }}
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          <app-empty
            title="No order activity yet"
            text="Create an order after setting up products and stock."
          />
        }
      </section>
    }
    <app-recovery
  /></app-page>`,
})
export class Overview {
  api = inject(Api);
  orders = signal<Order[]>([]);
  loading = signal(false);
  error = signal("");
  constructor() {
    void this.load();
  }
  count(s: string) {
    return this.orders().filter((o) => o.status === s).length;
  }
  attention() {
    return this.orders().filter((o) =>
      ["Pending", "CancellationPending", "RequiresReview"].includes(o.status),
    ).length;
  }
  async load() {
    this.loading.set(true);
    this.error.set("");
    try {
      this.orders.set(await this.api.orders(0, 20));
    } catch (e) {
      this.error.set(errorMessage(e));
      this.orders.set([]);
    } finally {
      this.loading.set(false);
    }
  }
}
@Component({
  standalone: true,
  imports: [RouterLink, Page],
  template: `<app-page
    title="Page not found"
    subtitle="This address does not match a QubeERP workspace page."
    ><a class="button" routerLink="/dashboard">Return to overview</a></app-page
  >`,
})
export class NotFound {}
