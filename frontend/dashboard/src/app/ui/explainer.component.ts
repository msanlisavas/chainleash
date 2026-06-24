import { Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';

/**
 * The walkthrough video, as a privacy facade: the page shows a SELF-HOSTED poster
 * (served same-origin, under img-src 'self') and contacts YouTube ONLY when the
 * visitor presses play — then via the cookie-less youtube-nocookie.com host. So no
 * third-party request reaches Google until a click, the landing page carries no
 * tracking/iframe cost on load (fitting for a security product), and the embed sits
 * inside our own steel-panel design rather than a raw red iframe.
 *
 * CSP: poster is same-origin (img-src 'self'); the player is an https frame
 * (frame-src https:) — both already permitted, so no header change.
 */
@Component({
  selector: 'app-explainer',
  imports: [],
  template: `
    <section id="watch" class="mx-auto max-w-[1180px] px-5 py-14 md:py-20 scroll-mt-16">
      <p class="eyebrow mb-3">The walkthrough</p>
      <h2 class="font-mono font-semibold text-[clamp(1.5rem,3vw,2rem)] tracking-[-0.01em] max-w-[24ch]">
        An agent that can move money, but can’t steal it.
      </h2>
      <p class="mt-3 text-steel text-body leading-relaxed max-w-[62ch]">
        A walkthrough of the leash: the agent proposing real CSPR staking moves on testnet,
        Casper bounding every one on-chain, and a human co-signing the material ones.
      </p>

      <div class="panel mt-8 overflow-hidden">
        <div class="relative w-full aspect-video bg-graphite">
          @if (embedUrl()) {
            <iframe #player
              class="absolute inset-0 h-full w-full"
              [src]="embedUrl()"
              title="CHAINLEASH — an AI agent that can move money, but can’t steal it"
              loading="lazy"
              referrerpolicy="strict-origin-when-cross-origin"
              allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share"
              allowfullscreen></iframe>
          } @else {
            <button type="button" (click)="play()"
                    class="group absolute inset-0 h-full w-full cursor-pointer"
                    aria-label="Play the walkthrough — CHAINLEASH, 3:24">
              <img src="walkthrough-poster.webp" alt="" aria-hidden="true" loading="lazy"
                   class="absolute inset-0 h-full w-full object-cover opacity-70 transition-opacity duration-300 group-hover:opacity-95" />
              <span class="pointer-events-none absolute inset-0 bg-gradient-to-t from-graphite/85 via-graphite/25 to-transparent"></span>
              <span class="pointer-events-none relative flex h-full w-full items-center justify-center">
                <span class="flex items-center gap-3 rounded-full border border-line bg-graphite/80 px-5 py-3 backdrop-blur-md
                             transition-transform duration-200 group-hover:scale-105 group-focus-visible:scale-105">
                  <svg width="15" height="15" viewBox="0 0 16 16" aria-hidden="true" fill="currentColor" class="text-ink">
                    <path d="M3 1.6v12.8L14 8z" />
                  </svg>
                  <span class="font-mono font-semibold text-cap tracking-[0.04em] text-ink">Play the walkthrough</span>
                  <span class="font-mono text-meta text-mute">3:24</span>
                </span>
              </span>
            </button>
          }
        </div>
      </div>

      <p class="mt-3 font-mono text-meta text-mute">
        No YouTube connection until you press play ·
        <a [href]="watchUrl" target="_blank" rel="noopener" class="text-steel transition-colors hover:text-ink">
          watch on YouTube <span aria-hidden="true">↗</span>
        </a>
      </p>
    </section>
  `,
})
export class ExplainerComponent {
  private readonly sanitizer = inject(DomSanitizer);
  private readonly id = '6_j26_2XpYA';
  private readonly player = viewChild<ElementRef<HTMLIFrameElement>>('player');

  readonly watchUrl = `https://youtu.be/${this.id}`;
  /** null until the visitor presses play — that's what keeps YouTube uncontacted on load. */
  readonly embedUrl = signal<SafeResourceUrl | null>(null);

  constructor() {
    // Once play() swaps the button for the iframe, move focus onto the player so
    // keyboard / screen-reader users aren't silently dropped to <body> (WCAG 2.4.3).
    effect(() => this.player()?.nativeElement.focus());
  }

  play(): void {
    this.embedUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(
      `https://www.youtube-nocookie.com/embed/${this.id}?autoplay=1&rel=0`));
  }
}
