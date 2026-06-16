import { Component, input } from '@angular/core';

/**
 * One leash metric in the live console: a steel panel with a mono label, a large
 * tabular value, and a help tooltip. `accent` tints the value for the few metrics
 * that carry weight (a slashable bond, a recorded violation).
 */
@Component({
  selector: 'app-stat-card',
  imports: [],
  host: { class: 'block min-w-0' },
  template: `
    <div class="panel px-4 py-4 flex flex-col gap-2 h-full overflow-hidden" [title]="hint()">
      <span class="eyebrow flex items-center gap-2">
        @if (accent() !== 'steel') {
          <span class="inline-block w-1.5 h-1.5 rounded-full shrink-0" [class]="dotClass()" aria-hidden="true"></span>
        }
        {{ label() }}
      </span>
      <span class="tnum flex flex-wrap items-baseline gap-x-1.5 min-w-0 text-[clamp(0.9rem,4.6vw,1.7rem)] leading-none font-semibold" [class]="valueClass()">
        <span>{{ value() }}</span>@if (unit()) {<span class="text-mute text-mini font-normal">{{ unit() }}</span>}
      </span>
    </div>
  `,
})
export class StatCardComponent {
  label = input.required<string>();
  value = input.required<string>();
  unit = input<string>('');
  hint = input<string>('');
  accent = input<'steel' | 'red' | 'green' | 'amber'>('steel');

  dotClass(): string {
    return { red: 'bg-red', green: 'bg-green', amber: 'bg-amber' }[this.accent() as 'red' | 'green' | 'amber'] ?? 'bg-steel';
  }
  valueClass(): string {
    return this.accent() === 'red' ? 'text-redtext' : 'text-ink';
  }
}
