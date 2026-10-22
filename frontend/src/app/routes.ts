import { Routes } from "@angular/router";
export const routes: Routes = [
  {
    path: "dashboard",
    loadComponent: () => import("./pages/overview").then((m) => m.Overview),
  },
  {
    path: "products",
    loadComponent: () => import("./pages/products").then((m) => m.Products),
  },
  {
    path: "inventory",
    loadComponent: () => import("./pages/inventory").then((m) => m.Inventory),
  },
  {
    path: "orders",
    loadComponent: () => import("./pages/orders").then((m) => m.Orders),
  },
  {
    path: "orders/:id",
    loadComponent: () =>
      import("./pages/order-detail").then((m) => m.OrderDetail),
  },
  {
    path: "reservations",
    loadComponent: () => import("./pages/records").then((m) => m.Reservations),
  },
  {
    path: "movements",
    loadComponent: () => import("./pages/records").then((m) => m.Movements),
  },
  { path: "", pathMatch: "full", redirectTo: "dashboard" },
  {
    path: "**",
    loadComponent: () => import("./pages/overview").then((m) => m.NotFound),
  },
];
