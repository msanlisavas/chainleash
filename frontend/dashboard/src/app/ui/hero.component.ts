import { Component, computed, input } from '@angular/core';

/**
 * Hero + signature element: the "leash instrument" — the logo framed as a live
 * control-room readout whose header reflects real chain state (ARMED / HALTED /
 * AWAITING CO-SIGN), with the five constraints shown as a simultaneous set.
 */
@Component({
  selector: 'app-hero',
  imports: [],
  template: `
    <section id="top" class="mx-auto max-w-[1180px] px-5 pt-14 pb-12 md:pt-20 md:pb-16
                             grid md:grid-cols-[1.05fr_0.95fr] gap-10 md:gap-12 items-center">
      <div>
        <p class="eyebrow mb-4">Bonded autonomy · Casper</p>
        <h1 class="font-mono font-semibold tracking-[-0.02em] leading-[1.08]
                   text-[clamp(2rem,4.6vw,3rem)]">
          The chain-enforced <span class="leash">leash</span> for autonomous money-moving agents.
        </h1>
        <p class="mt-5 text-steel max-w-[46ch] text-lead leading-relaxed">
          An AI agent rebalances CSPR delegation under a per-action cap, a validator allowlist,
          a slashable bond, and human co-sign on material moves — every limit enforced by a
          Casper contract, not by trust.
        </p>
        <div class="mt-8 flex flex-wrap gap-3">
          <a href="#console" class="btn btn-primary">Watch it live <span aria-hidden="true">↓</span></a>
          <a href="#how" class="btn btn-ghost">How the leash works</a>
        </div>
      </div>

      <!-- The leash instrument -->
      <div class="instrument panel p-5 md:p-6">
        <div class="flex items-center justify-between mb-5">
          <span class="eyebrow">Leash</span>
          <span class="pill" [class]="statusPill()" role="status" aria-live="polite">
            <span class="inline-block w-1.5 h-1.5 rounded-full" [class]="statusDot()" aria-hidden="true"></span>
            {{ statusLabel() }}
          </span>
        </div>

        <div class="mark relative mx-auto w-[min(72%,260px)] aspect-square">
          <img src="logo.webp" alt="The CHAINLEASH mark: a ring of chain links with one broken red link"
               class="w-full h-full object-contain relative z-10 rounded-full"
               [class.anim-pulse-red]="paused()" />
          <span class="sheen" aria-hidden="true"></span>
        </div>

        <p class="text-center text-mute text-meta font-mono mt-3 mb-5">
          Five limits, enforced on-chain — not by trust.
        </p>

        <ul class="grid grid-cols-2 gap-2 text-meta">
          @for (c of constraints; track c.key) {
            <li class="flex items-center gap-2 font-mono text-steel
                       border border-line rounded-lg px-2.5 py-2 bg-graphite/40">
              <svg class="w-4 h-4 shrink-0 text-steel" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                   stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                @switch (c.key) {
                  @case ('cap')       { <path d="M3.34 19a10 10 0 1 1 17.32 0" /><path d="m12 14 4-4" /> }
                  @case ('allowlist') { <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z" /><path d="m9 12 2 2 4-4" /> }
                  @case ('bond')      { <circle cx="8" cy="8" r="6" /><path d="M18.09 10.37A6 6 0 1 1 10.34 18" /><path d="M7 6h1v4" /><path d="m16.71 13.88.7.71-2.82 2.82" /> }
                  @case ('kill')      { <path d="M12 2v10" /><path d="M18.36 6.64a9 9 0 1 1-12.73 0" /> }
                  @case ('cosign')    { <path d="M12 20h9" /><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z" /> }
                }
              </svg>
              {{ c.t }}
            </li>
          }
        </ul>
      </div>
    </section>
  `,
  styles: [`
    /* The one deliberate exception to "red = enforcement only": the signature word
       carries the brand red, echoing the broken red link in the logo. */
    .leash {
      white-space: nowrap;
      border-bottom: 3px solid var(--color-red);
      padding-bottom: 1px;
    }
    /* corner ticks make the instrument read as an instrument */
    .instrument { position: relative; }
    .instrument::before, .instrument::after {
      content: ""; position: absolute; width: 12px; height: 12px; pointer-events: none;
    }
    .instrument::before { top: 8px; left: 8px; border-top: 2px solid var(--color-line); border-left: 2px solid var(--color-line); }
    .instrument::after  { bottom: 8px; right: 8px; border-bottom: 2px solid var(--color-line); border-right: 2px solid var(--color-line); }

    .mark .sheen {
      position: absolute; inset: 0; z-index: 20; pointer-events: none; border-radius: 50%;
      background: linear-gradient(105deg, transparent 35%, rgba(231,236,243,0.16) 50%, transparent 65%);
      background-size: 250% 100%;
      animation: cl-sheen 6s ease-in-out infinite;
      mix-blend-mode: screen;
    }
    @media (prefers-reduced-motion: reduce) { .mark .sheen { animation: none; opacity: 0; } }
  `],
})
export class HeroComponent {
  paused = input<boolean>(false);
  awaiting = input<number>(0);
  live = input<boolean>(false);

  readonly constraints = [
    { key: 'cap', t: 'per-action cap' },
    { key: 'allowlist', t: 'validator allowlist' },
    { key: 'bond', t: 'slashable bond' },
    { key: 'kill', t: 'kill-switch' },
    { key: 'cosign', t: 'human co-sign' },
  ];

  private status = computed<'halted' | 'awaiting' | 'armed' | 'idle'>(() => {
    if (this.paused()) return 'halted';
    if (this.awaiting() > 0) return 'awaiting';
    return this.live() ? 'armed' : 'idle';
  });

  statusLabel(): string {
    return { halted: 'HALTED', awaiting: 'AWAITING CO-SIGN', armed: 'ARMED', idle: 'CONNECTING' }[this.status()];
  }
  statusPill(): string {
    return { halted: 'pill-bad', awaiting: 'pill-warn', armed: 'pill-ok', idle: '' }[this.status()];
  }
  statusDot(): string {
    return { halted: 'bg-red anim-pulse-red', awaiting: 'bg-amber', armed: 'bg-green anim-breathe', idle: 'bg-mute' }[this.status()];
  }
}
