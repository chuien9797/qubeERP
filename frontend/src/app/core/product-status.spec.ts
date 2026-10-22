import { TestBed } from "@angular/core/testing";
import { HttpErrorResponse, HttpResponse } from "@angular/common/http";
import { Api } from "./api";
import { ProductStatus } from "./product-status";
describe("Product status verification", () => {
  const api = { command: vi.fn(), product: vi.fn() };
  let service: ProductStatus;
  beforeEach(() => {
    api.command.mockReset();
    api.product.mockReset();
    TestBed.configureTestingModule({
      providers: [{ provide: Api, useValue: api }],
    });
    service = TestBed.inject(ProductStatus);
  });
  afterEach(() => TestBed.resetTestingModule());
  it("sends an explicit desired value and verifies it with a GET after 204", async () => {
    api.command.mockResolvedValue(new HttpResponse({ status: 204 }));
    api.product.mockResolvedValue({ id: "p", sku: "SKU", isActive: false });
    expect((await service.set("p", false)).outcome).toBe("verified");
    expect(api.command).toHaveBeenCalledWith(
      "products",
      "PATCH",
      "/api/products/p/status",
      { isActive: false },
    );
    expect(api.product).toHaveBeenCalledWith("p");
  });
  it("does not assume a lost response means failure or automatically replay the write", async () => {
    api.command.mockRejectedValue(new HttpErrorResponse({ status: 0 }));
    api.product.mockResolvedValue({ id: "p", sku: "SKU", isActive: false });
    const result = await service.set("p", false);
    expect(result.outcome).toBe("verified");
    expect(result.message).toContain("response was unavailable");
    expect(api.command).toHaveBeenCalledTimes(1);
  });
  it("reports a concurrent mismatch instead of claiming requested success", async () => {
    api.command.mockResolvedValue(new HttpResponse({ status: 204 }));
    api.product.mockResolvedValue({ id: "p", sku: "SKU", isActive: true });
    expect((await service.set("p", false)).outcome).toBe("conflict");
    expect(api.command).toHaveBeenCalledTimes(1);
  });
  it("leaves the outcome uncertain when read-back is unavailable", async () => {
    api.command.mockResolvedValue(new HttpResponse({ status: 204 }));
    api.product.mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    expect((await service.set("p", false)).outcome).toBe("uncertain");
  });
  it("shows a definite backend validation/not-found failure without fabricating success", async () => {
    api.command.mockRejectedValue(
      new HttpErrorResponse({
        status: 404,
        error: { title: "Product not found.", errorCode: "PRODUCT_NOT_FOUND" },
      }),
    );
    expect((await service.set("p", false)).outcome).toBe("failed");
    expect(api.product).not.toHaveBeenCalled();
  });
});
