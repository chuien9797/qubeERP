export interface Product {
  id: string;
  sku: string;
  name: string;
  description: string | null;
  unitPrice: number;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface Stock {
  productId: string;
  onHand: number;
  reserved: number;
  availableQuantity: number;
}
export interface Movement {
  id: number;
  operationId: string;
  productId: string;
  movementType: string;
  onHandDelta: number;
  reservedDelta: number;
  onHandAfter: number;
  reservedAfter: number;
  reason: string | null;
  createdAtUtc: string;
}
export interface Item {
  productId: string;
  quantity: number;
}
export interface Shortfall {
  productId: string;
  requested: number;
  available: number;
}
export interface Reservation {
  reservationId: string;
  status: string;
  failureCode: string | null;
  shortfalls: Shortfall[];
  items: Item[];
}
export interface OrderLine extends Item {
  sku: string;
  name: string;
  unitPrice: number;
  lineTotal: number;
}
export interface Order {
  orderId: string;
  status: string;
  totalAmount: number;
  lines: OrderLine[];
  failureCode: string | null;
  shortfalls: Shortfall[];
  createdAtUtc: string;
  updatedAtUtc: string;
  lastAttemptAtUtc: string | null;
  cancellationReason: string | null;
}
export interface Problem {
  title?: string;
  detail?: string;
  errorCode?: string;
  status?: number;
  orderId?: string;
  orderStatus?: string;
  reservationId?: string;
  shortfalls?: Shortfall[];
  errors?: Record<string, string[]>;
  traceId?: string;
}
export const orderStatuses = [
  "Pending",
  "Confirmed",
  "Rejected",
  "RequiresReview",
  "CancellationPending",
  "Cancelled",
  "Fulfilled",
] as const;
export type Service = "products" | "inventory" | "orders";
export interface Readiness {
  service: Service;
  state: "checking" | "ready" | "unavailable";
  checkedAt?: string;
}
