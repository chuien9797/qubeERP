import { Component, inject, input, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { RouterLink } from "@angular/router";
import { Commands, SavedCommand } from "../core/commands";
import { Badge, ErrorBox } from "./ui";
@Component({
  selector: "app-recovery",
  standalone: true,
  imports: [CommonModule, RouterLink, Badge, ErrorBox],
  template: ` <details class="panel recovery">
    <summary class="panel-heading history-heading">
      <div>
        <h2>Request history</h2>
        <p>
          Saved in this browser. Expand to inspect past requests or retry an
          interrupted operation.
        </p>
      </div>
      <span class="count">{{ visible().length }}</span>
    </summary>
    <app-error [message]="error() || commands.storageError()" />
    @if (!visible().length) {
      <p class="muted padded">No saved requests for this workspace yet.</p>
    }
    @for (c of visible(); track c.id) {
      <details class="request">
        <summary>
          <span
            ><strong>{{ c.kind | titlecase }}</strong>
            <span class="mono">{{
              c.createdAt | date: "dd MMM, HH:mm" : "+0800"
            }}</span></span
          ><app-badge
            [value]="c.state"
            [label]="c.state === 'archived' ? 'Historical' : ''"
          />
        </summary>
        <p>{{ c.message }}</p>
        <dl>
          <dt>
            {{
              c.kind === "receipt"
                ? "Operation ID"
                : c.kind === "reservation"
                  ? "Reservation ID"
                  : "Request key"
            }}
          </dt>
          <dd class="mono">
            {{
              c.kind === "receipt"
                ? c.payload.operationId
                : c.kind === "reservation"
                  ? c.payload.reservationId
                  : c.key
            }}
          </dd>
          @if (c.status) {
            <dt>Backend status</dt>
            <dd>{{ c.status }}</dd>
          }
        </dl>
        <pre>{{ c.payload | json }}</pre>
        @if (c.problem?.shortfalls?.length) {
          <pre>{{ c.problem!.shortfalls | json }}</pre>
        }
        <div class="button-row">
          @if (c.orderId) {
            <a class="button secondary" [routerLink]="['/orders', c.orderId]"
              >Verify order</a
            >
          }
          @if (c.kind === "receipt" || c.kind === "stocktake") {
            <a
              class="button secondary"
              routerLink="/inventory"
              [queryParams]="{ productId: c.target }"
              >Inspect inventory</a
            >
          }
          @if (c.kind === "reservation") {
            <a
              class="button secondary"
              routerLink="/reservations"
              [queryParams]="{ id: c.payload.reservationId }"
              >Check reservation</a
            >
          }
          @if (
            ["order", "receipt", "cancel", "reservation"].includes(c.kind) &&
            c.state !== "verified" &&
            c.status !== "RequiresReview"
          ) {
            <button
              [disabled]="commands.busy().includes(c.id)"
              (click)="retry(c)"
            >
              {{
                commands.busy().includes(c.id)
                  ? "Awaiting response…"
                  : "Retry original request"
              }}
            </button>
            @if (c.kind === "cancel") {
              <button
                class="secondary"
                [disabled]="commands.busy().includes(c.id)"
                (click)="retry(c, true)"
              >
                Reconcile cancellation
              </button>
            }
          }
          @if (c.kind === "product" || c.kind === "stocktake") {
            <p class="muted">
              This command has no idempotent replay contract. Inspect backend
              data before submitting another command.
            </p>
          }
          @if (c.kind === "stocktake" && c.state === "uncertain") {
            @if (c.inventoryCheckedAt) {
              <p class="muted">
                Inventory checked manually
                {{ c.inventoryCheckedAt | date: "dd MMM, HH:mm" : "+0800" }}.
                The original request remains uncertain.
              </p>
              <button
                class="text-button"
                (click)="commands.recordInventoryCheck(c.id, false)"
              >
                Reopen inventory check
              </button>
            } @else {
              <button
                class="secondary"
                [disabled]="commands.busy().includes(c.id)"
                (click)="recordCheck(c)"
              >
                Record manual inventory check
              </button>
            }
          }
        </div>
      </details>
    }
  </details>`,
})
export class Recovery {
  commands = inject(Commands);
  kind = input("");
  error = signal("");
  visible = () =>
    this.commands
      .entries()
      .filter((c) => !this.kind() || this.kind().split(",").includes(c.kind));
  async retry(c: SavedCommand, reconcile = false) {
    this.error.set("");
    try {
      await this.commands.run(c.id, reconcile);
    } catch (e) {
      this.error.set((e as Error).message);
    }
  }
  recordCheck(c: SavedCommand) {
    if (
      window.confirm(
        "Have you checked the current stock and movement ledger for this request? This records your manual check and permits a new physical count. It does not verify the original outcome, retry it, or remove this history. You can reopen the check later.",
      )
    )
      this.commands.recordInventoryCheck(c.id, true);
  }
}
