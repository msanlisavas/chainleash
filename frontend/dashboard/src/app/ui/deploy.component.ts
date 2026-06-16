import { Component } from '@angular/core';

/**
 * "Run your own" — the self-host path. The leash is open source and runs anywhere
 * Docker does: watch the live demo read-only, or deploy your own vault and drive
 * the agent yourself. The terminal card shows the real commands (see RUNBOOK.md).
 */
@Component({
  selector: 'app-deploy',
  imports: [],
  template: `
    <section id="deploy" class="mx-auto max-w-[1180px] px-5 py-14 md:py-20 scroll-mt-16">
      <p class="eyebrow mb-3">Run your own</p>
      <h2 class="font-mono font-semibold text-[clamp(1.5rem,3vw,2rem)] tracking-[-0.01em] max-w-[24ch]">
        Self-host the leash — your keys, your vault.
      </h2>
      <p class="mt-4 text-steel text-lead leading-relaxed max-w-[52ch]">
        It’s open source and runs anywhere Docker does. Watch the live demo read-only, or deploy
        your own GovernedVault and drive the agent yourself — its agent key is yours, so the chain
        accepts its moves.
      </p>

      <div class="mt-10 grid md:grid-cols-[0.85fr_1.15fr] gap-8 md:gap-10 items-start">
        <ol class="grid gap-4">
          @for (s of steps; track s.n) {
            <li class="flex gap-4">
              <span class="tnum text-steel text-cap font-semibold pt-0.5 shrink-0">{{ s.n }}</span>
              <div>
                <h3 class="font-mono font-semibold text-note">{{ s.title }}</h3>
                <p class="text-steel text-note leading-relaxed mt-0.5">{{ s.body }}</p>
              </div>
            </li>
          }
        </ol>

        <!-- Real commands (RUNBOOK.md has the full walkthrough). Data-driven so line
             breaks don't depend on template whitespace handling. -->
        <div class="panel overflow-hidden">
          <div class="flex items-center gap-2 px-4 py-2.5 border-b border-line">
            <span class="eyebrow">terminal</span>
            <span class="pill ml-auto">casper-test</span>
          </div>
          <div class="font-mono text-mini leading-[1.7] px-4 py-4 overflow-x-auto" aria-label="Deploy commands">
            @for (l of lines; track $index) {
              @if (l.k === 'gap') {
                <div class="h-3" aria-hidden="true"></div>
              } @else if (l.k === 'c') {
                <div class="whitespace-pre text-mute">{{ l.t }}</div>
              } @else {
                <div class="whitespace-pre"><span class="text-steel">$ </span><span class="text-ink">{{ l.t }}</span></div>
              }
            }
          </div>
        </div>
      </div>

      <div class="mt-8 flex flex-wrap items-center gap-3">
        <a [href]="runbookUrl" target="_blank" rel="noopener" class="btn btn-primary">Full deploy guide <span aria-hidden="true">↗</span></a>
        <a [href]="repoUrl" target="_blank" rel="noopener" class="btn btn-ghost">View on GitHub <span aria-hidden="true">↗</span></a>
        <span class="text-mute text-meta font-mono ml-auto">Open source · MIT · Casper&nbsp;2.0 testnet</span>
      </div>
    </section>
  `,
})
export class DeployComponent {
  readonly repoUrl = 'https://github.com/msanlisavas/chainleash';
  readonly runbookUrl = 'https://github.com/msanlisavas/chainleash/blob/main/RUNBOOK.md';

  // k: 'c' = comment, '$' = command, 'gap' = blank spacer
  readonly lines = [
    { k: 'c', t: '# watch the live demo (read-only)' },
    { k: '$', t: 'cp .env.example .env' },
    { k: '$', t: 'docker compose up --build' },
    { k: 'gap', t: '' },
    { k: 'c', t: '# …or drive your own vault:' },
    { k: 'c', t: '# 1 · keys + faucet' },
    { k: '$', t: 'cd spike/ChainLeash.Spike' },
    { k: '$', t: 'dotnet run -- keygen' },
    { k: 'gap', t: '' },
    { k: 'c', t: '# 2 · deploy + arm (one command)' },
    { k: '$', t: './scripts/onboard.sh --cap 600 --validators <hex>,<hex> --deposit 1000 --bond 300' },
    { k: 'gap', t: '' },
    { k: 'c', t: '# 3 · run it, pointed at your vault' },
    { k: '$', t: 'docker compose up --build' },
  ];

  readonly steps = [
    {
      n: '01',
      title: 'Fund an account',
      body: 'Generate keys (dotnet run -- keygen) and top the agent up at the testnet faucet — a fresh vault needs ~600 CSPR (≈500 is one-time install gas).',
    },
    {
      n: '02',
      title: 'Deploy + arm your vault',
      body: 'One command (onboard.sh / onboard.ps1) deploys the GovernedVault and sets your cap, validator allowlist, deposit and slashable bond on-chain.',
    },
    {
      n: '03',
      title: 'Run the stack',
      body: 'docker compose up brings up the agent, dashboard and x402 provider — pointed at your vault. The owner co-signs material moves in their own wallet.',
    },
  ];
}
