import { Component, input } from "@angular/core";
import { CommonModule } from "@angular/common";

@Component({
  selector: "app-badge",
  standalone: true,
  template: `<span
    class="badge"
    [class.good]="good.includes(value())"
    [class.bad]="bad.includes(value())"
    [class.wait]="wait.includes(value())"
    [class.catalogue-active]="value() === 'Active'"
    [class.catalogue-inactive]="value() === 'Inactive'"
    >{{ label() || value() }}</span
  >`,
})
export class Badge {
  value = input("");
  label = input("");
  good = ["Confirmed", "Reserved", "Active", "ready", "verified"];
  bad = ["Rejected", "RequiresReview", "unavailable", "attention"];
  wait = ["Pending", "CancellationPending", "uncertain", "pending", "checking"];
}
@Component({
  selector: "app-empty",
  standalone: true,
  template: `<div class="empty">
    <span class="empty-symbol" aria-hidden="true">◇</span>
    <h3>{{ title() }}</h3>
    <p>{{ text() }}</p>
  </div>`,
})
export class Empty {
  title = input("Nothing here yet");
  text = input("Records will appear here.");
}
@Component({
  selector: "app-loading",
  standalone: true,
  template: `<div class="loading" role="status">
    <span class="spinner"></span> {{ text() }}
  </div>`,
})
export class Loading {
  text = input("Loading live data…");
}
@Component({
  selector: "app-error",
  standalone: true,
  template: `@if (message()) {
    <div class="notice danger" role="alert">
      <strong>Unable to complete this request</strong>
      <p>{{ message() }}</p>
    </div>
  }`,
})
export class ErrorBox {
  message = input("");
}
@Component({
  selector: "app-page",
  standalone: true,
  imports: [CommonModule],
  template: `<div class="page-heading">
      <div>
        <div class="eyebrow">{{ section() }}</div>
        <h1>{{ title() }}</h1>
        <p>{{ subtitle() }}</p>
      </div>
      <div class="heading-actions"><ng-content select="[actions]" /></div>
    </div>
    <ng-content />`,
})
export class Page {
  section = input("WORKSPACE");
  title = input("");
  subtitle = input("");
}
