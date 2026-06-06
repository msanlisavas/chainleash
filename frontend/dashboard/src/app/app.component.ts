import { Component, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { WalletService, AppConfig } from './wallet.service';

interface AuditEvent {
  time: string; tick: number; kind: string; message: string;
  validator?: string; amountCspr?: number; txHash?: string; txUrl?: string; success?: boolean;
}
interface ValidatorView {
  publicKey: string; feePercent: number; active: boolean; compliant: boolean; delegatedCspr: number; note: string;
}
interface ProposalView {
  id: number; validator: string; amountCspr: number; undelegate: boolean; txHash: string; resolved: boolean;
}
interface FeedState {
  packageHash: string; capCspr: number; maxCommissionPercent: number;
  x402SpentCspr: number; actions: number; buys: number;
  // full leash state, all read from chain
  paused: boolean; bondCspr: number; freeBalanceCspr: number; totalBalanceCspr: number;
  maxPerValidatorCspr: number; violations: number;
  validators: ValidatorView[]; proposals: ProposalView[];
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit {
  // Dev (ng serve :4200) talks to the agent host; in prod the dashboard is served from it.
  private readonly api = location.port === '4200' ? 'http://localhost:5179' : '';

  events = signal<AuditEvent[]>([]);
  state = signal<FeedState | null>(null);
  config = signal<AppConfig | null>(null);
  connected = signal(false);
  loadError = signal(false);
  coSigning = signal<number | null>(null);
  coSignError = signal<string | null>(null);

  // `wallet` is public so the template reads wallet.activeKey() / wallet.status() directly.
  constructor(private http: HttpClient, public wallet: WalletService) {}

  ngOnInit(): void {
    this.loadState();
    this.loadConfig();

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(`${this.api}/hub/audit`)
      .withAutomaticReconnect()
      .build();

    conn.on('audit', (e: AuditEvent) => this.events.update(list => [e, ...list].slice(0, 200)));
    conn.on('state', (s: FeedState) => this.state.set(s));
    conn.onreconnected(() => { this.connected.set(true); this.loadState(); }); // re-sync after an outage
    conn.onclose(() => this.connected.set(false));
    conn.start().then(() => this.connected.set(true)).catch(() => this.connected.set(false));
  }

  /** (Re)fetch the snapshot — used on load and on SignalR reconnect. */
  private loadState(): void {
    this.http.get<{ state: FeedState; events: AuditEvent[] }>(`${this.api}/api/state`).subscribe({
      next: r => { this.state.set(r.state); if (r.events?.length) this.events.set(r.events); this.loadError.set(false); },
      error: () => this.loadError.set(true)
    });
  }

  /** Fetch public config + boot the CSPR.click wallet SDK (so the owner can co-sign in-wallet). */
  private loadConfig(): void {
    this.http.get<AppConfig>(`${this.api}/api/config`).subscribe({
      next: cfg => { this.config.set(cfg); if (cfg.walletCoSignEnabled) this.wallet.init(cfg); },
      error: () => { /* config optional; co-sign UI degrades to disabled */ }
    });
  }

  async connectWallet(): Promise<void> {
    this.coSignError.set(null);
    try { await this.wallet.connect(); }
    catch (e: any) { this.coSignError.set(e?.message ?? 'connect failed'); }
  }

  /**
   * Owner co-signs a material proposal IN THEIR OWN WALLET:
   * server builds the unsigned approve_material tx → wallet signs + sends → server
   * confirms the result on-chain. The owner key never touches the server.
   */
  async coSignWallet(p: ProposalView): Promise<void> {
    this.coSigning.set(p.id);
    this.coSignError.set(null);
    try {
      const cfg = this.config();
      if (!cfg?.ownerPublicKey) throw new Error('no owner account configured');

      let key = this.wallet.activeKey();
      if (!key) key = await this.wallet.connect();
      if (!key) throw new Error('connect the owner wallet first');
      if (key.toLowerCase() !== cfg.ownerPublicKey.toLowerCase())
        throw new Error('connected wallet is not the owner account');

      const prep = await firstValueFrom(
        this.http.get<{ transactionJson: unknown; ownerPublicKey: string }>(`${this.api}/api/approve/${p.id}/prepare`));
      const hash = await this.wallet.send(prep.transactionJson, prep.ownerPublicKey);
      const res = await firstValueFrom(
        this.http.post<{ success: boolean; hash: string; error?: string }>(
          `${this.api}/api/approve/${p.id}/confirm`, { txHash: hash }));
      if (!res.success) this.coSignError.set(res.error ?? 'on-chain confirm failed');
    } catch (e: any) {
      this.coSignError.set(e?.message ?? 'co-sign failed');
    } finally {
      this.coSigning.set(null);
    }
  }

  /** Dev fallback: server-key co-sign (only available when the agent enables it). */
  approveServerKey(p: ProposalView): void {
    this.coSigning.set(p.id);
    this.coSignError.set(null);
    this.http.post<{ success: boolean; error?: string }>(`${this.api}/api/approve/${p.id}`, {}).subscribe({
      next: r => { this.coSigning.set(null); if (!r.success) this.coSignError.set(r.error ?? 'co-sign failed'); },
      error: () => { this.coSigning.set(null); this.coSignError.set('co-sign request failed'); }
    });
  }

  pendingProposals(): ProposalView[] {
    return (this.state()?.proposals ?? []).filter(p => !p.resolved);
  }

  short(s?: string): string { return s ? s.slice(0, 10) + '…' : ''; }
  kindClass(k: string): string { return 'k-' + k.toLowerCase(); }
}
