import { Component, input } from '@angular/core';

/**
 * Sticky top nav. The live link status + vault link live here so the chain
 * connection is visible from anywhere on the page. Presentational: every value
 * is passed in by AppComponent.
 */
@Component({
  selector: 'app-nav-bar',
  imports: [],
  template: `
    <header class="sticky top-0 z-50 border-b border-line bg-graphite/85 backdrop-blur-md">
      <div class="mx-auto max-w-[1180px] px-5 h-14 flex items-center justify-between gap-3">
        <a href="#top" class="flex items-center gap-2.5 min-w-0" aria-label="CHAINLEASH — top of page">
          <img src="logo.webp" alt="" width="26" height="26" class="w-[26px] h-[26px] shrink-0" aria-hidden="true">
          <span class="font-mono font-semibold tracking-[0.2em] text-[0.9rem] truncate">CHAINLEASH</span>
        </a>

        <nav class="hidden md:flex items-center gap-6 text-cap text-steel font-mono" aria-label="Sections">
          <a href="#how" class="hover:text-ink transition-colors">How it works</a>
          <a href="#who" class="hover:text-ink transition-colors">Who it's for</a>
          <a href="#watch" class="hidden lg:inline hover:text-ink transition-colors">Watch</a>
          <a href="#console" class="hover:text-ink transition-colors">Live console</a>
          <a href="#deploy" class="hover:text-ink transition-colors">Deploy</a>
        </nav>

        <div class="flex items-center gap-2.5 text-mini text-mute shrink-0">
          @if (pending() > 0) {
            <a href="#console" class="pill pill-warn anim-pulse-ring" [attr.aria-label]="pending() + ' moves awaiting co-sign'">
              <span aria-hidden="true">⚠</span> {{ pending() }}<span class="hidden sm:inline">&nbsp;co-sign</span>
            </a>
          }
          @if (readOnly()) {
            <span class="pill" title="This agent has no signing key — it streams the vault but never acts.">observer</span>
          }
          <span class="flex items-center gap-1.5">
            <span class="inline-block w-2 h-2 rounded-full" [class]="dotClass()" aria-hidden="true"></span>
            <span class="hidden sm:inline" aria-hidden="true">{{ linkLabel() }}</span>
          </span>
          <span class="sr-only" role="status">Connection status: {{ linkLabel() }}</span>
          @if (pkgShort()) {
            <a [href]="pkgUrl()" target="_blank" rel="noopener"
               class="hidden lg:inline font-mono text-steel hover:text-ink transition-colors"
               title="The GovernedVault contract package on the block explorer">vault {{ pkgShort() }} ↗</a>
          }
        </div>
      </div>
    </header>
  `,
})
export class NavBarComponent {
  linkState = input<'connecting' | 'live' | 'reconnecting' | 'offline'>('connecting');
  linkLabel = input<string>('connecting…');
  pkgShort = input<string>('');
  pkgUrl = input<string>('');
  pending = input<number>(0);
  readOnly = input<boolean>(false);

  dotClass(): string {
    switch (this.linkState()) {
      case 'live': return 'bg-green anim-breathe';
      case 'reconnecting': return 'bg-amber';
      case 'offline': return 'bg-mute';     // infra down, not enforcement — red stays reserved
      default: return 'bg-mute';
    }
  }
}
