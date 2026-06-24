import { Component, input, signal } from '@angular/core';

/**
 * A small "?" help badge with a styled popover (shown on hover, keyboard focus, or tap) — a
 * reliable replacement for the native `title` tooltip, which is delayed and easy to miss.
 */
@Component({
  selector: 'app-info',
  imports: [],
  template: `
    <span class="relative inline-flex align-middle">
      <button type="button" aria-label="More information"
              class="w-4 h-4 rounded-full border border-steel/50 text-steel/90 grid place-items-center cursor-help
                     text-[0.62rem] leading-none font-sans hover:border-ink hover:text-ink focus:border-ink focus:text-ink outline-none"
              (mouseenter)="show.set(true)" (mouseleave)="show.set(false)"
              (focus)="show.set(true)" (blur)="show.set(false)" (click)="show.set(!show())">?</button>
      @if (show()) {
        <span role="tooltip"
              class="absolute z-50 top-full right-0 mt-1.5 w-60 max-w-[72vw] panel px-3 py-2 text-cap text-steel
                     leading-relaxed normal-case tracking-normal font-sans shadow-xl pointer-events-none">
          {{ text() }}
        </span>
      }
    </span>
  `,
})
export class InfoComponent {
  text = input.required<string>();
  show = signal(false);
}
