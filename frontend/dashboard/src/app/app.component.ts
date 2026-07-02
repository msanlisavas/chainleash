import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { WalletService, AppConfig } from './wallet.service';
import { NavBarComponent } from './ui/nav-bar.component';
import { HeroComponent } from './ui/hero.component';
import { HowItWorksComponent } from './ui/how-it-works.component';
import { GuaranteeComponent } from './ui/guarantee.component';
import { ExplainerComponent } from './ui/explainer.component';
import { StatCardComponent } from './ui/stat-card.component';
import { DeployComponent } from './ui/deploy.component';
import { SiteFooterComponent } from './ui/site-footer.component';
import { PositionsComponent, Staking } from './ui/positions.component';
import { InfoComponent } from './ui/info.component';

interface AuditEvent {
  time: string; tick: number; kind: string; message: string;
  validator?: string; amountCspr?: number; txHash?: string; success?: boolean;
  iso?: string; // full ISO-8601 UTC timestamp (older persisted events may lack it)
}
interface ValidatorView {
  publicKey: string; feePercent: number; active: boolean; compliant: boolean; delegatedCspr: number; note: string; name?: string; allowed?: boolean;
}
interface DirectoryValidator { publicKey: string; name?: string | null; feePercent: number; active: boolean; }
interface ProposalView {
  id: number; validator: string; amountCspr: number; undelegate: boolean; txHash: string; resolved: boolean;
}
interface FeedState {
  packageHash: string; capCspr: number; maxCommissionPercent: number;
  x402SpentCspr: number; actions: number; buys: number;
  // full leash state, all read from chain
  paused: boolean; bondCspr: number; freeBalanceCspr: number; totalBalanceCspr: number;
  maxPerValidatorCspr: number; violations: number; stale: boolean; lastCheckedIso?: string;
  actionIntervalMs?: number;
  validators: ValidatorView[]; proposals: ProposalView[];
}

@Component({
    selector: 'app-root',
    imports: [
        NavBarComponent, HeroComponent, HowItWorksComponent,
        GuaranteeComponent, ExplainerComponent, StatCardComponent, PositionsComponent, InfoComponent, DeployComponent, SiteFooterComponent,
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
  staking = signal<Staking | null>(null);
  /** A 1s ticking clock so the "last checked Ns ago" heartbeat counts up live between agent ticks. */
  private clock = signal(Date.now());
  private clockTimer?: ReturnType<typeof setInterval>;
  /** Honest connection status: the dashboard NEVER silently gives up reconnecting. */
  link = signal<'connecting' | 'live' | 'reconnecting' | 'offline'>('connecting');
  loadError = signal(false);
  coSigning = signal<number | null>(null);
  /** Which leg of the co-sign is in flight: wallet signature vs on-chain confirmation. */
  coSignPhase = signal<'wallet' | 'confirm' | null>(null);
  coSignError = signal<string | null>(null);
  coSignOk = signal<string | null>(null);

  // Owner-direct controls (kill-switch, recall staked, withdraw) — owner signs in their wallet.
  ownerBusy = signal<string | null>(null);   // label of the in-flight owner action
  ownerError = signal<string | null>(null);
  ownerOk = signal<string | null>(null);
  // Owner policy-panel inputs (per-action cap raise, per-validator cap, cooldown seconds, add validator).
  capInput = signal<number | null>(null);
  maxValInput = signal<number | null>(null);
  cooldownInput = signal<number | null>(null);
  commissionInput = signal<number | null>(null);
  validatorSearch = signal<string>('');
  availableValidators = signal<DirectoryValidator[]>([]);

  private conn?: signalR.HubConnection;
  private destroyed = false;
  private retryTimer?: ReturnType<typeof setTimeout>;

  // `wallet` is public so the template reads wallet.activeKey() / wallet.status() directly.
  constructor(private http: HttpClient, public wallet: WalletService) {}

  ngOnInit(): void {
    this.loadState();
    this.loadConfig();
    this.loadStaking();
    this.loadValidators();

    this.conn = new signalR.HubConnectionBuilder()
      .withUrl(`${this.api}/hub/audit`)
      .withAutomaticReconnect()
      .build();

    this.conn.on('audit', (e: AuditEvent) => this.events.update(list => [e, ...list].slice(0, 200)));
    this.conn.on('state', (s: FeedState) => this.state.set(s));
    this.conn.onreconnecting(() => this.link.set('reconnecting'));
    this.conn.onreconnected(() => { this.link.set('live'); this.loadState(); this.loadStaking(); }); // re-sync after an outage
    // withAutomaticReconnect gives up after ~4 attempts; onclose hands over to our own
    // endless retry loop so an outage never permanently kills the dashboard.
    this.conn.onclose(() => this.scheduleRestart());
    this.startSignalR(true);
    this.clockTimer = setInterval(() => this.clock.set(Date.now()), 1000);
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    clearTimeout(this.retryTimer);
    clearInterval(this.clockTimer);
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

  /** Fetch the vault's staking positions + rewards (per-era data; reloads on connect). */
  private loadStaking(): void {
    this.http.get<Staking>(`${this.api}/api/staking`).subscribe({
      next: s => this.staking.set(s),
      error: () => { /* staking view optional; degrades to a loading line */ }
    });
  }

  /** Fetch the validator directory for the owner's "add validator" search. */
  private loadValidators(): void {
    this.http.get<DirectoryValidator[]>(`${this.api}/api/validators`).subscribe({
      next: vs => this.availableValidators.set(vs ?? []),
      error: () => { /* directory optional; the add-search stays empty */ }
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

  /** Wrong account connected — open CSPR.click's account picker so the owner can select
   *  the right account (signIn only shows the provider picker, not an account list). */
  async switchAccount(): Promise<void> {
    this.coSignError.set(null);
    this.walletConnecting.set(true);
    try {
      await this.wallet.switchAccount();
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

  /** Stake actually delegated on-chain for a validator right now (from the staking view), not just
   *  what the agent DIRECTED. These diverge while a redelegation is settling (~7 eras): committed
   *  shows the move, but there's no undelegatable delegation yet — recalling it reverts on-chain. */
  /** Actual on-chain stake for v from the staking view, or null if that view hasn't loaded yet. */
  private actualStake(v: ValidatorView): number | null {
    const pos = (this.staking()?.positions ?? []).find(p => p.publicKey.toLowerCase() === v.publicKey.toLowerCase());
    return pos ? pos.currentStakeCspr : null;
  }

  recallableCspr(v: ValidatorView): number {
    const a = this.actualStake(v);
    return a !== null ? a : v.delegatedCspr; // fall back to committed until the staking view loads
  }

  /** True when the agent has DIRECTED stake here but there's no active on-chain delegation yet —
   *  a redelegation still settling through Casper's ~7-era unbonding queue (committed>0, current=0). */
  isSettling(v: ValidatorView): boolean {
    return v.delegatedCspr > 0 && this.actualStake(v) === 0;
  }

  /** The backend-computed status of this validator's position (authoritative), or '' if unloaded. */
  private positionStatus(v: ValidatorView): string {
    return (this.staking()?.positions ?? [])
      .find(p => p.publicKey.toLowerCase() === v.publicKey.toLowerCase())?.status ?? '';
  }

  /** True when a directed position can never bond (principal ≤ the network minimum) — stranded /
   *  phantom committed that needs owner reconciliation, NOT a normal ~7-era settle. */
  isStranded(v: ValidatorView): boolean {
    return this.positionStatus(v).includes('needs owner action');
  }

  /** Validators the owner can actually recall NOW (a real on-chain delegation exists). */
  committedValidators(): ValidatorView[] {
    return (this.state()?.validators ?? []).filter(v => this.recallableCspr(v) > 0);
  }

  /** True while any owner action OR co-sign is mid-flight — serialize wallet use. */
  walletBusy(): boolean { return this.ownerBusy() !== null || this.coSigning() !== null; }

  /**
   * Run an owner-direct action: server builds the UNSIGNED owner tx → owner signs it in their
   * own wallet → server confirms it on-chain. Same non-custodial flow as the material co-sign;
   * every entry point is owner-gated on-chain, so the server never holds the owner key.
   */
  async ownerAction(action: string, body: Record<string, unknown>, label: string): Promise<void> {
    this.ownerError.set(null); this.ownerOk.set(null);
    if (!this.walletIsOwner()) { this.ownerError.set('Connect the owner account first.'); return; }
    this.ownerBusy.set(label);
    try {
      const prep = await firstValueFrom(this.http.post<{ transactionJson: unknown; ownerPublicKey: string }>(
        `${this.api}/api/owner/prepare`, { action, ...body }));
      const hash = await this.wallet.send(prep.transactionJson, prep.ownerPublicKey);
      const res = await firstValueFrom(this.http.post<{ success: boolean; hash: string; error?: string }>(
        `${this.api}/api/owner/confirm`, { action, txHash: hash, ...body }));
      if (res.success) this.ownerOk.set(`${label} — confirmed on-chain.`);
      else this.ownerError.set(res.error ?? 'On-chain confirmation failed.');
    } catch (e: any) {
      this.ownerError.set(e?.message ?? 'Action failed.');
    } finally {
      this.ownerBusy.set(null);
    }
  }

  stopAgent(): void { this.ownerAction('pause', {}, 'Stop agent'); }
  resumeAgent(): void { this.ownerAction('unpause', {}, 'Resume agent'); }
  recallLabel(v: ValidatorView): string { return `Recall ${this.short(v.publicKey)}`; }
  recall(v: ValidatorView): void {
    this.ownerAction('undelegate', { validator: v.publicKey, amountCspr: this.recallableCspr(v) }, this.recallLabel(v));
  }
  withdrawFree(): void {
    this.ownerAction('withdraw', { amountCspr: this.state()?.freeBalanceCspr ?? 0 }, 'Withdraw to wallet');
  }
  rejectProposal(p: ProposalView): void { this.ownerAction('reject', { id: p.id }, `Reject #${p.id}`); }

  // --- Owner POLICY controls (wallet-signed, owner-gated on-chain). The agent reads each from
  //     chain, so the change takes effect on its next tick. ---
  raiseCap(): void {
    const c = this.capInput(), cur = this.state()?.capCspr ?? 0;
    if (c === null || isNaN(c) || c <= 0) { this.ownerError.set('Enter the new per-action cap in CSPR.'); return; }
    if (c <= cur) { this.ownerError.set(`The new cap must be HIGHER than the current ${this.fmt(cur)} CSPR — the agent can only tighten the cap; the owner raises it. To lower it, the agent does so itself.`); return; }
    if (c > 100_000_000) { this.ownerError.set('That cap is unreasonably large. Enter a sane value.'); return; }
    this.ownerAction('raisecap', { amountCspr: c }, 'Raise cap');
  }
  setMaxVal(): void {
    const m = this.maxValInput(), cap = this.state()?.capCspr ?? 0;
    if (m === null || isNaN(m) || m < 0) { this.ownerError.set('Enter a per-validator cap in CSPR (0 = unlimited).'); return; }
    if (m > 0 && m < cap) { this.ownerError.set(`A per-validator cap below the per-action cap (${this.fmt(cap)} CSPR) would stop the agent completing even one move. Use 0 for unlimited, or a value ≥ the per-action cap.`); return; }
    if (m > 1_000_000_000) { this.ownerError.set('That cap is unreasonably large. Enter a sane value (or 0 = unlimited).'); return; }
    this.ownerAction('setmaxval', { amountCspr: m }, 'Set per-validator cap');
  }
  setCooldown(): void {
    const s = this.cooldownInput();
    if (s === null || isNaN(s) || s < 0) { this.ownerError.set('Enter a cooldown in whole seconds (0 = off).'); return; }
    if (!Number.isInteger(s)) { this.ownerError.set('Cooldown must be a whole number of seconds.'); return; }
    if (s > 86400) { this.ownerError.set('A cooldown over 24h would effectively freeze the agent. Enter a smaller value.'); return; }
    this.ownerAction('setcooldown', { intervalSeconds: s }, 'Set cooldown');
  }
  setCommission(): void {
    const p = this.commissionInput();
    if (p === null || isNaN(p) || !Number.isInteger(p) || p < 0 || p > 100) { this.ownerError.set('Enter a commission threshold as a whole number from 0 to 100 (%).'); return; }
    this.ownerAction('setcommission', { percent: p }, 'Set commission threshold');
  }
  /** A Casper validator public key: ed25519 (01 + 64 hex) or secp256k1 (02 + 66 hex). */
  private isValidPubKey(k: string): boolean { return /^(01[0-9a-f]{64}|02[0-9a-f]{66})$/.test(k); }
  /** Validators NOT already on the agent's watch-list, filtered by the search box (name or key). */
  filteredValidators(): DirectoryValidator[] {
    const q = this.validatorSearch().trim().toLowerCase();
    const watched = new Set((this.state()?.validators ?? []).map(v => v.publicKey.toLowerCase()));
    return this.availableValidators()
      .filter(dv => !watched.has(dv.publicKey.toLowerCase()))
      .filter(dv => !q || dv.publicKey.toLowerCase().includes(q) || (dv.name ?? '').toLowerCase().includes(q))
      .slice(0, 12);
  }
  /** Add a validator the owner picked from the search list (signs set_validator(true)). */
  addValidatorByKey(key: string): void {
    const k = (key || '').trim().toLowerCase();
    if (!this.isValidPubKey(k)) { this.ownerError.set('That is not a valid Casper validator public key.'); return; }
    if ((this.state()?.validators ?? []).some(v => v.publicKey.toLowerCase() === k)) { this.ownerError.set("That validator is already on the agent's watch-list."); return; }
    this.validatorSearch.set('');
    this.ownerAction('setvalidator', { validator: k, allowed: true }, `Add ${this.short(k)}`);
  }
  /** Current on-chain cooldown in seconds (for the policy panel's placeholder/label). */
  cooldownSeconds(): number { return Math.round((this.state()?.actionIntervalMs ?? 0) / 1000); }

  /** Toggle a validator on/off the on-chain allowlist. Disallowing one makes the agent treat it
   *  as off-policy — it stops deploying there and redelegates any stake to an allowed validator. */
  toggleAllow(v: ValidatorView): void {
    const newAllowed = v.allowed === false; // currently disallowed → allow; else disallow
    const label = `${newAllowed ? 'Allow' : 'Disallow'} ${this.short(v.publicKey)}`;
    this.ownerAction('setvalidator', { validator: v.publicKey, allowed: newAllowed }, label);
  }
  /** Read a number input's value, or null when empty/invalid (so 0 stays 0, blank stays null). */
  numOrNull(e: Event): number | null { const v = (e.target as HTMLInputElement).valueAsNumber; return isNaN(v) ? null : v; }

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

  /** Mid-truncate a key/hash to first-5…last-5 for readability, e.g. 012d5...4f1f2. */
  short(s?: string): string {
    if (!s) return '';
    return s.length <= 12 ? s : s.slice(0, 5) + '...' + s.slice(-5);
  }
  kindClass(k: string): string { return 'k-' + k.toLowerCase(); }

  /** Seconds since the agent last evaluated the vault (live "watching" heartbeat). */
  secondsAgo(): number | null {
    const iso = this.state()?.lastCheckedIso;
    if (!iso) return null;
    const ms = this.clock() - new Date(iso).getTime();
    return ms >= 0 ? Math.floor(ms / 1000) : 0;
  }
  /** Human "checked Ns ago" / "Nm Ns ago" for the heartbeat. */
  checkedAgo(): string {
    const s = this.secondsAgo();
    if (s === null) return '—';
    return s < 60 ? s + 's ago' : Math.floor(s / 60) + 'm ' + (s % 60) + 's ago';
  }
}
