import { Component, inject, input, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { RouterLink } from "@angular/router";
import { Commands, SavedCommand } from "../core/commands";
import { Badge, ErrorBox } from "./ui";
@Component({
  selector: "app-recovery",
  standalone: true,
  imports: [CommonModule, RouterLink, Badge, ErrorBox],
  template: ` <section class="panel recovery">
    <div class="panel-heading">
      <div>
        <h2>Saved requests</h2>
        <p>
          Stored in this browser. Retries keep the original request identity and
          payload.
        </p>
      </div>
      <span class="count">{{ visible().length }}</span>
    </div>
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
          ><app-badge [value]="c.state" />
        </summary>
        <p>{{ c.message }}</p>
        <dl>
          <dt>Request key</dt>
          <dd class="mono">{{ c.key }}</dd>
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
          @if (
            ["order", "receipt", "cancel"].includes(c.kind) &&
            !["verified", "archived"].includes(c.state) &&
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
          @if (c.state === "verified") {
            <button class="text-button" (click)="commands.archive(c.id)">
              Hide local record
            </button>
          }
          <button class="text-button" (click)="acknowledge(c)">
            Acknowledge after investigation
          </button>
        </div>
      </details>
    }
  </section>`,
})
export class Recovery {
  commands = inject(Commands);
  kind = input("");
  error = signal("");
  visible = () =>
    this.commands
      .entries()
      .filter(
        (c) =>
          c.state !== "archived" &&
          (!this.kind() || this.kind().split(",").includes(c.kind)),
      );
  acknowledge(c: SavedCommand) {
    if (
      window.confirm(
        "Hide this local recovery record after your investigation? This does not cancel the request, change stock or verify its outcome. Its saved payload stays in browser storage.",
      )
    )
      this.commands.archive(c.id);
  }
  async retry(c: SavedCommand, reconcile = false) {
    this.error.set("");
    try {
      await this.commands.run(c.id, reconcile);
    } catch (e) {
      this.error.set((e as Error).message);
    }
  }
}
