import { Component } from '@angular/core';

@Component({
  selector: 'app-site-footer',
  imports: [],
  template: `
    <footer class="border-t border-line mt-8">
      <div class="mx-auto max-w-[1180px] px-5 py-10 flex flex-col sm:flex-row gap-6 sm:items-center justify-between">
        <div class="flex items-center gap-3">
          <img src="logo.webp" alt="" width="28" height="28" class="w-7 h-7" aria-hidden="true">
          <div>
            <p class="font-mono font-semibold tracking-[0.2em] text-cap">CHAINLEASH</p>
            <p class="text-mute text-mini">The chain-enforced leash for autonomous staking agents.</p>
          </div>
        </div>
        <p class="text-mute text-meta font-mono leading-relaxed sm:text-right">
          Built on Casper 2.0 · testnet<br>
          Every value is read live from chain.
        </p>
      </div>
    </footer>
  `,
})
export class SiteFooterComponent {}
