import { Component, signal } from "@angular/core";
import { RouterLink, RouterLinkActive, RouterOutlet } from "@angular/router";
@Component({
  selector: "app-root",
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: ` <aside [class.expanded]="menu()">
      <a class="brand" routerLink="/dashboard"
        ><span class="brand-mark">Q</span
        ><span
          >Qube<span class="brand-light">ERP</span
          ><small>STOCK OPERATIONS</small></span
        ></a
      >
      <div class="nav-caption">WORKSPACE</div>
      <nav aria-label="Main navigation">
        @for (n of navigation; track n.path) {
          <a
            [routerLink]="n.path"
            routerLinkActive="selected"
            (click)="menu.set(false)"
            ><svg
              class="nav-icon"
              aria-hidden="true"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="1.7"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path [attr.d]="n.icon" /></svg
            >{{ n.name }}</a
          >
        }
      </nav>
    </aside>
    <div class="workspace">
      <button
        class="mobile-menu secondary"
        (click)="menu.set(!menu())"
        aria-label="Toggle navigation"
      >
        ☰ Menu
      </button>
      <main><router-outlet /></main>
    </div>`,
})
export class App {
  menu = signal(false);
  navigation = [
    {
      path: "/dashboard",
      name: "Overview",
      icon: "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z",
    },
    {
      path: "/products",
      name: "Products",
      icon: "M12 3 3 8v8l9 5 9-5V8z M3 8l9 5 9-5 M12 13v8 M7.5 5.5l9 5",
    },
    {
      path: "/inventory",
      name: "Inventory",
      icon: "M3 5h18v5H3z M3 14h18v5H3z M7 7.5h3 M7 16.5h3",
    },
    {
      path: "/orders",
      name: "Orders",
      icon: "M6 3h9l3 3v15H6z M14 3v5h4 M9 12h6 M9 16h6",
    },
    {
      path: "/reservations",
      name: "Reservations",
      icon: "M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M12 7v5l3 2",
    },
    {
      path: "/movements",
      name: "Stock movements",
      icon: "M4 7h16m-4-4 4 4-4 4 M20 17H4m4-4-4 4 4 4",
    },
  ];
}
