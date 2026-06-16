import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { WalletService, AppConfig } from './wallet.service';
import { NavBarComponent } from './ui/nav-bar.component';
import { HeroComponent } from './ui/hero.component';
import { HowItWorksComponent } from './ui/how-it-works.component';
import { GuaranteeComponent } from './ui/guarantee.component';
import { StatCardComponent } from './ui/stat-card.component';
import { DeployComponent } from './ui/deploy.component';
import { SiteFooterComponent } from './ui/site-footer.component';

interface AuditEvent {
  time: string; tick: number; kind: string; message: string;
  validator?: string; amountCspr?: number; txHash?: string; success?: boolean;
  iso?: string; // full ISO-8601 UTC timestamp (older persisted events may lack it)
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
  maxPerValidatorCspr: number; violations: number; stale: boolean;
  validators: ValidatorView[]; proposals: ProposalView[];
}

@Component({
    selector: 'app-root',
    imports: [
        NavBarComponent, HeroComponent, HowItWorksComponent,
        GuaranteeComponent, StatCardComponent, DeployComponent, SiteFooterComponent,
    ],
    templateUrl: './app.component.html',
    styleUrl: './app.component.scss'
})
export class AppComponent implements OnInit, OnDestroy {
  // Dev (ng serve :4200) talks to the agent host; in prod the dashboard is served from it.
  private readonly api = location.port === '4200' ? 'http://localhost:5179' : '';

  events = signal<AuditEvent[]>([]);
  state = signal<FeedState | null>(null);
  config = signal<AppConfig | null>(null);
  /** Honest connection status: the dashboard NEVER silently gives up reconnecting. */
  link = signal<'connecting' | 'live' | 'reconnecting' | 'offline'>('connecting');
  loadError = signal(false);
  coSigning = signal<number | null>(null);
  /** Which leg of the co-sign is in flight: wallet signature vs on-chain confirmation. */
  coSignPhase = signal<'wallet' | 'confirm' | null>(null);
  coSignError = signal<string | null>(null);
  coSignOk = signal<string | null>(null);

  private conn?: signalR.HubConnection;
  private destroyed = false;
  private retryTimer?: ReturnType<typeof setTimeout>;

  // `wallet` is public so the template reads wallet.activeKey() / wallet.status() directly.
  constructor(private http: HttpClient, public wallet: WalletService) {}

  ngOnInit(): void {
    this.loadState();
    this.loadConfig();

    this.conn = new signalR.HubConnectionBuilder()
      .withUrl(`${this.api}/hub/audit`)
      .withAutomaticReconnect()
      .build();

    this.conn.on('audit', (e: AuditEvent) => this.events.update(list => [e, ...list].slice(0, 200)));
    this.conn.on('state', (s: FeedState) => this.state.set(s));
    this.conn.onreconnecting(() => this.link.set('reconnecting'));
    this.conn.onreconnected(() => { this.link.set('live'); this.loadState(); }); // re-sync after an outage
    // withAutomaticReconnect gives up after ~4 attempts; onclose hands over to our own
    // endless retry loop so an outage never permanently kills the dashboard.
    this.conn.onclose(() => this.scheduleRestart());
    this.startSignalR(true);
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    clearTimeout(this.retryTimer);
    this.conn?.stop().catch(() => { /* already down */ });
  }

  private startSignalR(first = false): void {
    if (this.destroyed) return;
    if (!first) this.link.set('reconnecting');
    this.conn!.start()
      .then(() => { this.link.set('live'); if (!first) this.loadState(); })
      .catch(() => this.scheduleRestart());
  }

  private scheduleRestart(): void {
    if (this.destroyed) return;
    this.link.set('offline');
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => this.startSignalR(), 5000);
  }

  /** (Re)fetch the snapshot — used on load and on SignalR (re)connect. */
  private loadState(): void {
    this.http.get<{ state: FeedState; events: AuditEvent[] }>(`${this.api}/api/state`).subscribe({
      next: r => { this.state.set(r.state); this.mergeEvents(r.events ?? []); this.loadError.set(false); },
      error: () => this.loadError.set(true)
    });
  }

  /** Adopt the snapshot's backlog without re-creating rows the user already has — a full
   *  replace would re-render all 200 <li>s and make aria-live re-announce the backlog on
   *  every reconnect. Events are newest-first; `iso` orders the delta. */
  private mergeEvents(snapshot: AuditEvent[]): void {
    if (!snapshot.length) return;
    const current = this.events();
    const headIso = current[0]?.iso;
    if (!current.length || !headIso) { this.events.set(snapshot); return; }
    const fresh = snapshot.filter(e => e.iso && e.iso > headIso);
    if (fresh.length) this.events.update(list => [...fresh, ...list].slice(0, 200));
  }

  /** Fetch public config + boot the CSPR.click wallet SDK (so the owner can co-sign in-wallet). */
  private loadConfig(): void {
    this.http.get<AppConfig>(`${this.api}/api/config`).subscribe({
      next: cfg => { this.config.set(cfg); if (cfg.walletCoSignEnabled) this.wallet.init(cfg); },
      error: () => { /* config optional; co-sign UI degrades to disabled */ }
    });
  }

  /** True while a connect() wait is in flight (sign-in modal open). */
  walletConnecting = signal(false);

  async connectWallet(): Promise<void> {
    this.coSignError.set(null);
    this.walletConnecting.set(true);
    try { await this.wallet.connect(); }
    catch (e: any) { this.coSignError.set(e?.message ?? 'connect failed'); }
    finally { this.walletConnecting.set(false); }
  }

  /** The user dismissed/abandoned the sign-in modal — stop the wait immediately. */
  cancelConnect(): void { this.wallet.cancelConnect(); }

  retryWallet(): void { this.wallet.retry(); }

  /** True only when the connected wallet IS the vault owner (case-insensitive). */
  walletIsOwner(): boolean {
    const k = this.wallet.activeKey();
    const o = this.config()?.ownerPublicKey;
    return !!k && !!o && k.toLowerCase() === o.toLowerCase();
  }

  /** Wrong account connected — sign out and reopen sign-in so the owner can pick the
   *  right account (Casper Wallet shows its account list in the CSPR.click modal). */
  async switchAccount(): Promise<void> {
    this.coSignError.set(null);
    this.walletConnecting.set(true);
    try {
      await this.wallet.disconnect();
      await this.wallet.connect();
    } catch (e: any) {
      this.coSignError.set(e?.message ?? 'could not switch account');
    } finally {
      this.walletConnecting.set(false);
    }
  }

  /**
   * Owner co-signs a material proposal IN THEIR OWN WALLET:
   * server builds the unsigned approve_material tx → wallet signs + sends → server
   * confirms the result on-chain. The owner key never touches the server.
   */
  async coSignWallet(p: ProposalView): Promise<void> {
    this.coSigning.set(p.id);
    this.coSignPhase.set('wallet');
    this.coSignError.set(null);
    this.coSignOk.set(null);
    try {
      const cfg = this.config();
      if (!cfg?.ownerPublicKey) throw new Error('no owner account configured');

      let key = this.wallet.activeKey();
      if (!key) {
        this.walletConnecting.set(true);
        try { key = await this.wallet.connect(); }
        finally { this.walletConnecting.set(false); }
      }
      if (!key) throw new Error(this.wallet.wasConnectCancelled() ? 'sign-in cancelled' : 'connect the owner wallet first');
      if (key.toLowerCase() !== cfg.ownerPublicKey.toLowerCase())
        throw new Error('connected wallet is not the owner account');

      const prep = await firstValueFrom(
        this.http.get<{ transactionJson: unknown; ownerPublicKey: string }>(`${this.api}/api/approve/${p.id}/prepare`));
      const hash = await this.wallet.send(prep.transactionJson, prep.ownerPublicKey);
      this.coSignPhase.set('confirm'); // signed — now the chain has to execute it
      const res = await firstValueFrom(
        this.http.post<{ success: boolean; hash: string; error?: string }>(
          `${this.api}/api/approve/${p.id}/confirm`, { txHash: hash }));
      if (res.success) this.coSignOk.set(`Proposal #${p.id} co-signed — executed on-chain.`);
      else this.coSignError.set(res.error ?? 'on-chain confirm failed');
    } catch (e: any) {
      this.coSignError.set(e?.message ?? 'co-sign failed');
    } finally {
      this.coSigning.set(null);
      this.coSignPhase.set(null);
    }
  }

  /** Dev fallback: server-key co-sign (only available when the agent enables it). */
  approveServerKey(p: ProposalView): void {
    this.coSigning.set(p.id);
    this.coSignError.set(null);
    this.coSignOk.set(null);
    this.http.post<{ success: boolean; error?: string }>(`${this.api}/api/approve/${p.id}`, {}).subscribe({
      next: r => {
        this.coSigning.set(null);
        if (r.success) this.coSignOk.set(`Proposal #${p.id} co-signed (server key) — executed on-chain.`);
        else this.coSignError.set(r.error ?? 'co-sign failed');
      },
      error: () => { this.coSigning.set(null); this.coSignError.set('co-sign request failed'); }
    });
  }

  pendingProposals(): ProposalView[] {
    return (this.state()?.proposals ?? []).filter(p => !p.resolved);
  }

  /** Why the in-wallet co-sign button is unusable right now (null = usable). */
  coSignBlocked(): string | null {
    const status = this.wallet.status();
    if (status === 'unavailable') return 'wallet SDK unavailable';
    if (status === 'idle' || status === 'loading') return 'wallet SDK still loading…';
    const key = this.wallet.activeKey();
    const owner = this.config()?.ownerPublicKey;
    if (key && owner && key.toLowerCase() !== owner.toLowerCase()) return 'switch to the owner account in your wallet';
    return null;
  }

  /** Block-explorer base: the server's configured value (single source of truth),
   *  falling back to a chain-name heuristic until config loads. */
  explorerBase(): string {
    const cfg = this.config();
    if (cfg?.explorerBaseUrl) return cfg.explorerBaseUrl;
    return cfg?.chainName === 'casper' ? 'https://cspr.live' : 'https://testnet.cspr.live';
  }
  txLink(hash?: string): string | null { return hash ? `${this.explorerBase()}/transaction/${hash}` : null; }

  /** CSPR amounts: grouped thousands, at most 2 decimals — '—' when unknown. */
  fmt(n?: number | null): string {
    return n === undefined || n === null ? '—'
      : new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(n);
  }

  /** Event time in the VIEWER's timezone (full timestamp in the tooltip); falls back to
   *  the legacy UTC HH:mm:ss string for events persisted before `iso` existed. */
  timeOf(e: AuditEvent): string {
    return e.iso ? new Date(e.iso).toLocaleTimeString([], { hour12: false }) : e.time;
  }
  timeTitle(e: AuditEvent): string {
    return e.iso ? new Date(e.iso).toLocaleString() : `${e.time} UTC`;
  }

  linkLabel(): string {
    switch (this.link()) {
      case 'live': return 'live';
      case 'reconnecting': return 'reconnecting…';
      case 'offline': return 'offline — retrying';
      default: return 'connecting…';
    }
  }

  short(s?: string): string { return s ? s.slice(0, 10) + '…' : ''; }
  kindClass(k: string): string { return 'k-' + k.toLowerCase(); }
}
