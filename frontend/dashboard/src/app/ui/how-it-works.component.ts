import { Component } from '@angular/core';

/**
 * The lifecycle of one agent action — a real sequence, so it earns numbered
 * steps. The middle step (on-chain enforcement) is the heart of the product and
 * carries the section's one red accent.
 */
@Component({
  selector: 'app-how-it-works',
  imports: [],
  template: `
    <section id="how" class="mx-auto max-w-[1180px] px-5 py-14 md:py-20 scroll-mt-16">
      <p class="eyebrow mb-3">How the leash works</p>
      <h2 class="font-mono font-semibold text-[clamp(1.5rem,3vw,2rem)] tracking-[-0.01em] max-w-[28ch]">
        Every move the agent makes runs the same three-step gauntlet.
      </h2>

      <ol class="mt-10 grid md:grid-cols-3 gap-5">
        @for (s of steps; track s.n) {
          <li class="panel p-6 flex flex-col" [class.enforce]="s.accent">
            <div class="flex items-baseline gap-3 mb-4">
              <span class="tnum text-cap font-semibold" [class.text-red]="s.accent" [class.text-steel]="!s.accent">{{ s.n }}</span>
              <span class="eyebrow">{{ s.tag }}</span>
            </div>
            <h3 class="font-mono font-semibold text-lead mb-2">{{ s.title }}</h3>
            <p class="text-steel text-body leading-relaxed">{{ s.body }}</p>
          </li>
        }
      </ol>
    </section>
  `,
  styles: [`
    .enforce { border-top-color: var(--color-red); }
  `],
})
export class HowItWorksComponent {
  readonly steps = [
    {
      n: '01', tag: 'Propose', accent: false,
      title: 'The agent proposes a move',
      body: 'It reads live validator metrics, then proposes a delegate, undelegate, or redelegate — paying for each premium risk read over x402, a real Casper transfer per decision.',
    },
    {
      n: '02', tag: 'Enforce', accent: true,
      title: 'Casper bounds it',
      body: 'The GovernedVault contract checks every move: within the per-action cap, to an allowlisted validator, never beyond what is committed. Off-policy moves revert on-chain, and the agent forfeits its bond on a violation.',
    },
    {
      n: '03', tag: 'Co-sign', accent: false,
      title: 'A human signs the big ones',
      body: 'Material moves wait until the owner co-signs in their own wallet — the owner key never touches the server. The owner can pause the agent at any time; the kill-switch is enforced by the contract.',
    },
  ];
}
