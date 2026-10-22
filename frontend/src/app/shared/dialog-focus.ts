import {
  AfterViewInit,
  Directive,
  ElementRef,
  HostListener,
  OnDestroy,
  inject,
} from "@angular/core";
@Directive({ selector: "[appDialogFocus]", standalone: true })
export class DialogFocus implements AfterViewInit, OnDestroy {
  private element: ElementRef<HTMLElement> = inject(ElementRef);
  private previous = document.activeElement as HTMLElement | null;
  private controls() {
    return Array.from(
      this.element.nativeElement.querySelectorAll<HTMLElement>(
        "button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href]",
      ),
    );
  }
  ngAfterViewInit() {
    this.controls()[0]?.focus();
  }
  @HostListener("keydown", ["$event"]) trap(event: KeyboardEvent) {
    if (event.key !== "Tab") return;
    const nodes = this.controls();
    const first = nodes[0],
      last = nodes.at(-1);
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last?.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first?.focus();
    }
  }
  ngOnDestroy() {
    this.previous?.focus();
  }
}
