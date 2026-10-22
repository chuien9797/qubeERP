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
            ><span class="nav-icon" aria-hidden="true">{{ n.icon }}</span
            >{{ n.name }}</a
          >
        }
      </nav>
      <div class="sidebar-bottom">
        <span class="live-dot"></span> Local workspace<small
          >Product · Inventory · Order</small
        >
      </div>
    </aside>
    <div class="workspace">
      <header class="topbar">
        <button
          class="mobile-menu secondary"
          (click)="menu.set(!menu())"
          aria-label="Toggle navigation"
        >
          ☰</button
        ><span>Operations workspace</span>
        <div>
          <span class="timezone">MYT · UTC+08:00</span
          ><span class="workspace-tag">LOCAL</span>
        </div>
      </header>
      <main><router-outlet /></main>
      <footer>
        QubeERP
        <span
          >Backend-verified stock operations · All dates in Malaysia time</span
        >
      </footer>
    </div>`,
})
export class App {
  menu = signal(false);
  navigation = [
    { path: "/dashboard", name: "Overview", icon: "▦" },
    { path: "/products", name: "Products", icon: "◇" },
    { path: "/inventory", name: "Inventory", icon: "▤" },
    { path: "/orders", name: "Orders", icon: "▧" },
    { path: "/reservations", name: "Reservations", icon: "◷" },
    { path: "/movements", name: "Stock movements", icon: "⇄" },
    { path: "/status", name: "System status", icon: "◉" },
  ];
}
