import { Injectable, inject } from "@angular/core";
import {
  HttpClient,
  HttpErrorResponse,
  HttpParams,
} from "@angular/common/http";
import { firstValueFrom, timeout } from "rxjs";
import {
  Movement,
  Order,
  Product,
  Problem,
  Reservation,
  Service,
  Stock,
} from "./models";

@Injectable({ providedIn: "root" })
export class Api {
  private http = inject(HttpClient);
  get<T>(
    service: Service,
    path: string,
    query: Record<string, string | number | boolean> = {},
  ): Promise<T> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query))
      params = params.set(key, value);
    return firstValueFrom(
      this.http
        .get<T>(`/gateway/${service}${path}`, { params })
        .pipe(timeout(12000)),
    );
  }
  command(
    service: Service,
    method: "POST" | "PUT",
    path: string,
    body: unknown,
    key?: string,
  ) {
    return firstValueFrom(
      this.http
        .request<any>(method, `/gateway/${service}${path}`, {
          body,
          observe: "response",
          headers: key ? { "Idempotency-Key": key } : {},
        })
        .pipe(timeout(18000)),
    );
  }
  products(offset = 0, limit = 21, search = "", active = "") {
    return this.get<Product[]>("products", "/api/products", {
      offset,
      limit,
      ...(search ? { search } : {}),
      ...(active ? { isActive: active } : {}),
    });
  }
  product(id: string) {
    return this.get<Product>(
      "products",
      `/api/products/${encodeURIComponent(id)}`,
    );
  }
  stock(id: string) {
    return this.get<Stock>(
      "inventory",
      `/api/inventory/${encodeURIComponent(id)}`,
    );
  }
  movements(id: string, offset = 0, limit = 21) {
    return this.get<Movement[]>(
      "inventory",
      `/api/inventory/${encodeURIComponent(id)}/movements`,
      { offset, limit },
    );
  }
  orders(offset = 0, limit = 21, status = "") {
    return this.get<Order[]>("orders", "/api/orders", {
      offset,
      limit,
      newestFirst: true,
      ...(status ? { status } : {}),
    });
  }
  order(id: string) {
    return this.get<Order>("orders", `/api/orders/${encodeURIComponent(id)}`);
  }
  reservation(id: string) {
    return this.get<Reservation>(
      "inventory",
      `/api/inventory/reservations/${encodeURIComponent(id)}`,
    );
  }
  health(service: Service) {
    return firstValueFrom(
      this.http
        .get(`/gateway/${service}/health`, { responseType: "text" })
        .pipe(timeout(6000)),
    );
  }
}
export function unknownOutcome(error: unknown): boolean {
  return (
    !(error instanceof HttpErrorResponse) ||
    error.status === 0 ||
    error.status >= 500 ||
    error.status === 408
  );
}
export function problemOf(error: unknown): Problem {
  return error instanceof HttpErrorResponse &&
    error.error &&
    typeof error.error === "object"
    ? error.error
    : {};
}
export function errorMessage(error: unknown): string {
  const p = problemOf(error);
  if (p.errors) return Object.values(p.errors).flat().join(" ");
  if (p.title)
    return `${p.title}${p.errorCode ? " · " + p.errorCode : ""}${p.detail ? " " + p.detail : ""}`;
  if (error instanceof HttpErrorResponse && error.status === 404)
    return "No record exists for this ID.";
  return "The service could not be reached or the response was lost. A submitted command may already have completed.";
}
export async function boundedMap<T, R>(
  values: T[],
  action: (value: T) => Promise<R>,
  concurrency = 4,
): Promise<R[]> {
  const result = new Array<R>(values.length);
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(concurrency, values.length) }, async () => {
      while (next < values.length) {
        const i = next++;
        result[i] = await action(values[i]);
      }
    }),
  );
  return result;
}
