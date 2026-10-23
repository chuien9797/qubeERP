import { Injectable, inject } from "@angular/core";
import { Api, errorMessage, unknownOutcome } from "./api";
import { Product } from "./models";

export interface ProductStatusResult {
  outcome: "verified" | "conflict" | "uncertain" | "failed";
  product?: Product;
  message: string;
}
@Injectable({ providedIn: "root" })
export class ProductStatus {
  private api = inject(Api);
  async set(id: string, isActive: boolean): Promise<ProductStatusResult> {
    let acknowledged = false;
    try {
      await this.api.command(
        "products",
        "PATCH",
        `/api/products/${encodeURIComponent(id)}/status`,
        { isActive },
      );
      acknowledged = true;
    } catch (error) {
      if (!unknownOutcome(error))
        return { outcome: "failed", message: errorMessage(error) };
    }
    try {
      const product = await this.api.product(id);
      const current = product.isActive ? "active" : "inactive";
      if (product.isActive === isActive)
        return {
          outcome: "verified",
          product,
          message: acknowledged
            ? `${product.sku} is now ${current}.`
            : `The request response was unavailable. ${product.sku} is currently ${current}.`,
        };
      return {
        outcome: acknowledged ? "conflict" : "uncertain",
        product,
        message: acknowledged
          ? `${product.sku} is currently ${current}. The status has changed. Refresh before trying again.`
          : `The request outcome is not confirmed. ${product.sku} is currently ${current}; the original request may still complete. Refresh before retrying.`,
      };
    } catch {
      return {
        outcome: "uncertain",
        message:
          "Unable to verify the current product status. The update may already have completed. Refresh the catalogue before trying again.",
      };
    }
  }
}
