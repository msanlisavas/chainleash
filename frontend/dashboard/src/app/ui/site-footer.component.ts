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

        <nav class="flex flex-wrap items-center gap-x-5 gap-y-2 text-meta font-mono text-steel" aria-label="Project links">
          <a href="https://github.com/msanlisavas/chainleash" target="_blank" rel="noopener"
             class="inline-flex items-center gap-1.5 hover:text-ink transition-colors">
            <svg class="w-4 h-4" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
              <path d="M12 .5A11.5 11.5 0 0 0 .5 12a11.5 11.5 0 0 0 7.86 10.92c.58.11.79-.25.79-.55v-2.17c-3.2.7-3.87-1.36-3.87-1.36-.53-1.32-1.28-1.68-1.28-1.68-1.05-.71.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.76 2.7 1.25 3.36.96.1-.75.4-1.26.72-1.55-2.55-.29-5.23-1.28-5.23-5.68 0-1.26.45-2.28 1.19-3.09-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.18 1.18a11.05 11.05 0 0 1 5.78 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.81 1.19 1.83 1.19 3.09 0 4.41-2.69 5.38-5.25 5.67.41.35.77 1.05.77 2.12v3.15c0 .3.2.66.8.55A11.5 11.5 0 0 0 23.5 12 11.5 11.5 0 0 0 12 .5Z"/>
            </svg>
            GitHub
          </a>
          <a href="https://youtu.be/6_j26_2XpYA" target="_blank" rel="noopener"
             class="inline-flex items-center gap-1.5 hover:text-ink transition-colors">
            <svg class="w-4 h-4" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
              <path d="M23.5 7.2a3 3 0 0 0-2.1-2.1C19.5 4.6 12 4.6 12 4.6s-7.5 0-9.4.5A3 3 0 0 0 .5 7.2 31.3 31.3 0 0 0 0 12a31.3 31.3 0 0 0 .5 4.8 3 3 0 0 0 2.1 2.1c1.9.5 9.4.5 9.4.5s7.5 0 9.4-.5a3 3 0 0 0 2.1-2.1A31.3 31.3 0 0 0 24 12a31.3 31.3 0 0 0-.5-4.8ZM9.6 15.6V8.4L15.8 12l-6.2 3.6Z"/>
            </svg>
            Walkthrough
          </a>
          <a href="mailto:muratsanlisavas@gmail.com" class="inline-flex items-center gap-1.5 hover:text-ink transition-colors">
            <svg class="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
                 stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <rect width="20" height="16" x="2" y="4" rx="2"/><path d="m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7"/>
            </svg>
            Contact
          </a>
        </nav>

        <p class="text-mute text-meta font-mono leading-relaxed sm:text-right">
          Built on Casper 2.0 · testnet — every value read live from chain.<br>
          Casper Agentic Buildathon 2026 · Final Round<br>
          MIT · © 2026
        </p>
      </div>
    </footer>
  `,
})
export class SiteFooterComponent {}
