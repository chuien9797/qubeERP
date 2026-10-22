import {
  Component,
  inject,
  input,
  output,
  signal,
  effect,
  untracked,
} from "@angular/core";
import { FormControl, ReactiveFormsModule } from "@angular/forms";
import { Api, errorMessage } from "../core/api";
import { Product } from "../core/models";
import { ErrorBox } from "./ui";

@Component({
  selector: "app-product-select",
  standalone: true,
  imports: [ReactiveFormsModule, ErrorBox],
  template: ` <div class="product-picker">
      <div class="picker-search">
        <label
          >Find product<input
            aria-label="Search product SKU or UUID"
            [formControl]="term"
            maxlength="100"
            placeholder="SKU, name or UUID"
            (keydown.enter)="$event.preventDefault(); search()" /></label
        ><button
          type="button"
          class="secondary"
          [disabled]="loading()"
          (click)="search()"
        >
          {{ loading() ? "Searching…" : "Search" }}
        </button>
      </div>
      <label class="picker-options"
        >Product<select
          aria-label="Select product"
          [formControl]="selection"
          (change)="choose($event)"
        >
          <option value="">Select a product</option>
          @if (selectedId() && !selectedKnown()) {
            <option [value]="selectedId()">{{ selectedId() }}</option>
          }
          @for (p of options(); track p.id) {
            <option [value]="p.id">{{ p.sku }} · {{ p.name }}</option>
          }
        </select></label
      >
      @if (more()) {
        <p class="muted picker-help">
          Showing 50 matches. Refine your search to find another product.
        </p>
      }
      @if (!loading() && !options().length && !error()) {
        <p class="muted picker-help">
          No matching products. Try a different SKU or UUID.
        </p>
      }
    </div>
    <app-error [message]="error()" />`,
})
export class ProductSelect {
  api = inject(Api);
  selectedId = input("");
  selected = output<string>();
  term = new FormControl("", { nonNullable: true });
  selection = new FormControl('', {nonNullable:true});
  options = signal<Product[]>([]);
  loading = signal(false);
  error = signal("");
  more = signal(false);
  generation = 0;
  constructor() {
    void this.search();
    effect(() => {
      const id = this.selectedId();
      this.selection.setValue(id,{emitEvent:false});
      if (id) untracked(() => void this.ensureOption(id));
    });
  }
  async ensureOption(id: string) {
    if (this.options().some((p) => p.id === id)) return;
    try {
      const p = await this.api.product(id);
      if (this.selectedId() === id)
        this.options.update((rows) =>
          rows.some((r) => r.id === id) ? rows : [p, ...rows],
        );
    } catch {
      /* Keep the selected UUID usable even when ProductService is unavailable. */
    }
  }
  choose(event: Event) {
    this.selected.emit((event.target as HTMLSelectElement).value);
  }
  selectedKnown() {
    return this.options().some((p) => p.id === this.selectedId());
  }
  async search() {
    const g = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    const term = this.term.value.trim();
    try {
      const uuid =
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
          term,
        );
      const found = uuid
        ? [await this.api.product(term)]
        : await this.api.products(0, 51, term);
      if (g !== this.generation) return;
      this.options.set(found.slice(0, 50));
      this.more.set(found.length > 50);
      if (term && found.length === 1) this.selected.emit(found[0].id);
      else if (this.selectedId()) void this.ensureOption(this.selectedId());
    } catch (e) {
      if (g === this.generation) {
        this.error.set(errorMessage(e));
        this.options.set([]);
      }
    } finally {
      if (g === this.generation) this.loading.set(false);
    }
  }
}
