import { Component } from '@angular/core';

/**
 * The trust section: who the leash is for, then the hard guarantee — the three
 * things the contract makes impossible. Red is spent only on the punitive one
 * (the slashable bond).
 */
@Component({
  selector: 'app-guarantee',
  imports: [],
  template: `
    <section id="who" class="mx-auto max-w-[1180px] px-5 py-14 md:py-20 scroll-mt-16">
      <div class="grid md:grid-cols-[0.9fr_1.1fr] gap-10 md:gap-14 items-start">
        <div>
          <p class="eyebrow mb-3">Who it's for</p>
          <h2 class="font-mono font-semibold text-[clamp(1.5rem,3vw,2rem)] tracking-[-0.01em] leading-tight">
            Built for the desks that stake at scale.
          </h2>
          <p class="mt-5 text-steel text-lead leading-relaxed max-w-[44ch]">
            Exchanges, custodians, and treasuries hold large CSPR and stake it for their users.
            They need rebalancing they can bound, audit to the transaction, and halt in one click —
            not an agent they have to trust.
          </p>
        </div>

        <div>
          <p class="eyebrow mb-4">What the agent cannot do</p>
          <ul class="grid gap-3">
            @for (g of guarantees; track g.title) {
              <li class="panel p-5 flex gap-4 items-start" [class.punitive]="g.red">
                <span class="mt-0.5 shrink-0 grid place-items-center w-7 h-7 rounded-md border text-[0.85rem]"
                      [class]="g.red ? 'border-red/40 text-red bg-red/10' : 'border-line text-steel bg-graphite/40'"
                      aria-hidden="true">{{ g.glyph }}</span>
                <div>
                  <h3 class="font-mono font-semibold text-body">{{ g.title }}</h3>
                  <p class="text-steel text-note leading-relaxed mt-1">{{ g.body }}</p>
                </div>
              </li>
            }
          </ul>
          <p class="mt-5 text-mute text-cap leading-relaxed">
            The leash is enforced by the Casper contract — not by this server, and not by trust.
          </p>
        </div>
      </div>
    </section>
  `,
  styles: [`
    .punitive { border-top-color: var(--color-red); }
  `],
})
export class GuaranteeComponent {
  readonly guarantees = [
    {
      glyph: '⊘', red: false,
      title: 'Cannot withdraw',
      body: 'It rebalances stake within the vault, but the contract never lets it move funds out to itself or anywhere else.',
    },
    {
      glyph: '⊘', red: false,
      title: 'Cannot raise its own authority',
      body: 'It can’t lift its cap, add a validator, or widen the allowlist. Only the owner can change the leash.',
    },
    {
      glyph: '◆', red: true,
      title: 'Forfeits its bond on a violation',
      body: 'The operator escrows slashable collateral. An off-policy move costs real money — misbehavior is expensive by design.',
    },
  ];
}
