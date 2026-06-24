import { Component, input } from '@angular/core';

export interface Position {
  publicKey: string; feePercent: number; active: boolean; compliant: boolean;
  principalCspr: number; currentStakeCspr: number; rewardCspr: number; status: string; name?: string;
}
export interface Staking {
  positions: Position[];
  totalPrincipalCspr: number; totalCurrentStakeCspr: number; totalRewardCspr: number;
  freeBalanceCspr: number; bondCspr: number; totalUnderManagementCspr: number; stale: boolean;
}

/**
 * Where the vault delegated + what it earned. The vault stakes from a CONTRACT purse, so block
 * explorers keyed by a public key can't show its delegations or rewards — this is the canonical
 * view. Reward = current on-chain stake − directed principal (Casper auto-compounds rewards).
 * Green is spent only on the earned reward; red only on an off-policy commission.
 */
@Component({
  selector: 'app-positions',
  imports: [],
  template: `
    <section id="positions" class="mt-5">
      <div class="panel p-5">
        <h3 class="font-mono font-semibold text-body mb-1 flex flex-wrap items-baseline gap-x-2">
          Staking positions & rewards
          <small class="text-mute font-normal text-meta">what the vault delegated + earned · live from chain</small>
        </h3>
        <p class="text-mute text-meta mb-4 leading-relaxed max-w-[72ch]">
          The vault delegates from a contract purse, so explorers keyed by a public key don't show these
          positions or their rewards. Reward = current on-chain stake − directed principal (rewards auto-compound).
        </p>

        @if (data(); as d) {
          <div class="overflow-x-auto">
            <table class="w-full text-mini font-mono border-collapse">
              <thead>
                <tr class="text-mute text-meta uppercase tracking-wide text-left border-b border-line">
                  <th class="py-2 pr-3 font-normal">Validator</th>
                  <th class="py-2 px-3 font-normal">Commission</th>
                  <th class="py-2 px-3 font-normal">Status</th>
                  <th class="py-2 px-3 font-normal text-right">Principal</th>
                  <th class="py-2 px-3 font-normal text-right">Current stake</th>
                  <th class="py-2 pl-3 font-normal text-right">Rewards</th>
                </tr>
              </thead>
              <tbody>
                @for (p of d.positions; track p.publicKey) {
                  <tr class="border-b border-line last:border-0">
                    <td class="py-2.5 pr-3">
                      <a [href]="explorerBase() + '/validator/' + p.publicKey" target="_blank" rel="noopener"
                         class="hover:underline inline-flex items-baseline gap-1.5 min-w-0" [title]="p.publicKey">
                        @if (p.name) {
                          <span class="font-sans text-ink truncate">{{ p.name }}</span>
                          <span class="text-mute shrink-0">{{ short(p.publicKey) }}</span>
                        } @else {
                          <span class="text-steel hover:text-ink">{{ short(p.publicKey) }}</span>
                        }
                      </a>
                    </td>
                    <td class="py-2.5 px-3" [class]="p.compliant ? 'text-ink' : 'text-redtext'">{{ p.feePercent }}%</td>
                    <td class="py-2.5 px-3 text-mute">{{ p.status }}</td>
                    <td class="py-2.5 px-3 text-right text-ink tnum">{{ fmt(p.principalCspr) }}</td>
                    <td class="py-2.5 px-3 text-right text-ink tnum">{{ fmt(p.currentStakeCspr) }}</td>
                    <td class="py-2.5 pl-3 text-right tnum" [class]="p.rewardCspr > 0 ? 'text-green' : 'text-mute'">
                      {{ p.rewardCspr > 0 ? '+' + fmt(p.rewardCspr) : '—' }}
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="6" class="text-mute text-center py-6">No delegations yet — the agent hasn't deployed the treasury.</td></tr>
                }
              </tbody>
              @if (d.positions.length) {
                <tfoot>
                  <tr class="border-t border-line">
                    <td class="py-2.5 pr-3 text-mute" colspan="3">
                      {{ fmt(d.totalUnderManagementCspr) }} CSPR under management
                      <span class="text-mute/70">· {{ fmt(d.freeBalanceCspr) }} free · {{ fmt(d.bondCspr) }} bond</span>
                    </td>
                    <td class="py-2.5 px-3 text-right text-ink tnum">{{ fmt(d.totalPrincipalCspr) }}</td>
                    <td class="py-2.5 px-3 text-right text-ink tnum">{{ fmt(d.totalCurrentStakeCspr) }}</td>
                    <td class="py-2.5 pl-3 text-right tnum font-semibold" [class]="d.totalRewardCspr > 0 ? 'text-green' : 'text-mute'">
                      {{ d.totalRewardCspr > 0 ? '+' + fmt(d.totalRewardCspr) : '—' }}
                    </td>
                  </tr>
                </tfoot>
              }
            </table>
          </div>
          @if (d.stale) {
            <p class="text-amber text-meta mt-3">Showing the last known good read — a CSPR.cloud read just failed.</p>
          }
        } @else {
          <p class="text-mute text-note text-center py-6">Loading staking positions…</p>
        }
      </div>
    </section>
  `,
})
export class PositionsComponent {
  data = input<Staking | null>(null);
  explorerBase = input<string>('https://testnet.cspr.live');

  short(s?: string): string {
    if (!s) return '';
    return s.length <= 12 ? s : s.slice(0, 5) + '...' + s.slice(-5);
  }
  fmt(n?: number | null): string {
    return n === undefined || n === null ? '—'
      : new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(n);
  }
}
