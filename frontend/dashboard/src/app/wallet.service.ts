import { Injectable, signal } from '@angular/core';

/** Public, secret-free config the dashboard fetches from the agent to drive co-sign. */
export interface AppConfig {
  chainName: string;
  packageHash: string;
  explorerBaseUrl?: string; // server-configured block-explorer base (single source of truth)
  ownerPublicKey: string | null;
  csprClickAppId: string;
  walletCoSignEnabled: boolean;
  allowServerKeyCoSign: boolean;
  readOnly: boolean;
  x402Enabled?: boolean;
}

// Pinned, versioned SDK build. Subresource-integrity pinning was tried and REVERTED:
// SRI requires `crossorigin` + an Access-Control-Allow-Origin header, and cdn.cspr.click
// serves none — the browser then blocks the script entirely (verified empirically).
// Compensating control: the server's CSP restricts script-src to 'self' + this CDN.
const CSPRCLICK_CDN = 'https://cdn.cspr.click/ui/v2.0.0/csprclick-client-2.0.0.js';

/**
 * Thin wrapper over the CSPR.click vanilla SDK (integration per the official CSPR.click
 * agent skill). The owner co-signs material proposals by signing the agent-prepared,
 * UNSIGNED `approve_material` TransactionV1 in their own wallet — the owner key never
 * reaches the server. CSPR.click exposes `window.csprclick` after `csprclick:loaded`.
 */
@Injectable({ providedIn: 'root' })
export class WalletService {
  /** The connected owner's public key (hex), or null when not signed in. */
  activeKey = signal<string | null>(null);
  /** SDK readiness: 'idle' before init, 'loading', 'ready', or 'unavailable' on failure. */
  status = signal<'idle' | 'loading' | 'ready' | 'unavailable'>('idle');

  private cfg: AppConfig | null = null;
  private connectAborted = false;
  /** When a waitForKey() poll is sleeping, this wakes it the instant an account
   *  event lands (see wireEvents → notifySettle) so the UI settles without poll lag. */
  private settleSignal: (() => void) | null = null;

  private get sdk(): any { return (window as any).csprclick; }

  /** Load + initialize the CSPR.click client with the server-supplied appId. Idempotent. */
  async init(cfg: AppConfig): Promise<void> {
    this.cfg = cfg;
    if (this.status() === 'ready' || this.status() === 'loading') return;
    this.status.set('loading');

    // The CDN client reads these globals when it loads — set them BEFORE injecting the script.
    (window as any).clickSDKOptions = {
      appName: 'CHAINLEASH',
      appId: cfg.csprClickAppId || 'csprclick-template',
      providers: ['casper-wallet', 'casper-signer', 'ledger', 'metamask-snap'],
    };
    (window as any).clickUIOptions = {
      uiContainer: 'csprclick-ui',
      rootAppElement: '#app',
      defaultTheme: 'dark',
    };

    try {
      await this.loadScript(CSPRCLICK_CDN);
      await this.waitForLoaded();
      this.wireEvents();
      this.refreshActiveKey(); // pick up an already-connected session after a reload
      this.status.set('ready');
    } catch {
      this.status.set('unavailable'); // SDK didn't load (offline/blocked CDN) — retry() re-attempts
    }
  }

  /** Re-attempt a failed SDK load (e.g. the CDN was briefly unreachable). */
  async retry(): Promise<void> {
    if (this.status() !== 'unavailable' || !this.cfg) return;
    document.querySelector(`script[src="${CSPRCLICK_CDN}"]`)?.remove(); // a failed tag blocks loadScript
    this.status.set('idle');
    await this.init(this.cfg);
  }

  /** Open the CSPR.click sign-in modal; resolves once an account is active (or null). */
  async connect(): Promise<string | null> {
    if (this.status() !== 'ready') return null;
    this.connectAborted = false;
    this.sdk.signIn();
    // signIn() returns immediately (per CSPR.click); the signed_in event drives
    // activeKey and this poll is the fallback. Wait until an account is active.
    return this.waitForKey(k => !!k);
  }

  /** Abort a pending connect()/switchAccount() wait (the user closed/ignored the modal). */
  cancelConnect(): void { this.connectAborted = true; this.notifySettle(); }

  /** True when the last connect() wait ended because the user cancelled it. */
  wasConnectCancelled(): boolean { return this.connectAborted; }

  async disconnect(): Promise<void> {
    try { await this.sdk?.signOut?.(); } catch { /* ignore */ }
    this.activeKey.set(null);
  }

  /**
   * Switch the connected account. Casper Wallet only shares its ACTIVE account, so a
   * plain signIn() (provider picker) can't change it — CSPR.click's `switchAccount()`
   * opens the account picker instead. Falls back to sign-out + sign-in on older SDKs.
   */
  async switchAccount(): Promise<string | null> {
    if (this.status() !== 'ready') return this.activeKey();
    this.connectAborted = false;
    const previous = this.activeKey();
    if (typeof this.sdk?.switchAccount === 'function') {
      try { this.sdk.switchAccount(); } catch { /* SDK rejected the request */ }
      // switchAccount() ALSO returns immediately — reading getActiveAccount() now
      // would return the stale, pre-switch account (the reported bug). The
      // switched_account/signed_in events drive activeKey; wait until it actually
      // changes so the app adopts (logs in) whatever account the user selected.
      return this.waitForKey(k => !!k && k !== previous);
    }
    await this.disconnect();
    return this.connect();
  }

  /**
   * Wait until getActiveAccount() satisfies `settled`, the user cancels, or the ~30s
   * budget elapses. Both signIn() and switchAccount() return BEFORE the user has chosen,
   * so we can't read state synchronously. The signed_in/switched_account events are the
   * REAL adoption path (wireEvents) — they update activeKey even after this returns — and
   * they also wake this loop instantly via notifySettle(); the 500ms poll is the fallback.
   * CSPR.click emits no "picker closed/cancelled" event, so a cancelled or same-account
   * pick can't settle the predicate — the caller shows a Cancel control for that path.
   */
  private async waitForKey(settled: (k: string | null) => boolean): Promise<string | null> {
    for (let i = 0; i < 60 && !this.connectAborted; i++) {
      this.refreshActiveKey();
      if (settled(this.activeKey())) break;
      await new Promise<void>(resolve => {
        let timer: ReturnType<typeof setTimeout>;
        const finish = () => { clearTimeout(timer); if (this.settleSignal === finish) this.settleSignal = null; resolve(); };
        timer = setTimeout(finish, 500);
        this.settleSignal = finish; // an account event fires finish() early (no 500ms lag)
      });
    }
    return this.activeKey();
  }

  /** Wake an in-flight waitForKey() poll — an account event just updated activeKey. */
  private notifySettle(): void { const s = this.settleSignal; this.settleSignal = null; s?.(); }

  /**
   * Sign + send a prepared, unsigned TransactionV1 JSON via the active wallet.
   * `transactionJson` is the server's {"transaction":{"Version1":{…}}} (already the shape
   * CSPR.click `send()` expects). Returns the on-chain tx hash for server-side confirm.
   */
  async send(transactionJson: unknown, ownerPublicKey: string,
             onStatus?: (status: string, data: any) => void): Promise<string> {
    if (this.status() !== 'ready') throw new Error('wallet not ready');
    const res: any = await this.sdk.send(transactionJson, ownerPublicKey.toLowerCase(), onStatus);
    if (!res || res.cancelled) throw new Error('signing cancelled');
    if (res.error) throw new Error(res.error);
    const hash = res.transactionHash ?? res.deployHash ?? res.hash;
    if (!hash) throw new Error('wallet returned no transaction hash');
    return hash;
  }

  // --- internals ---

  private refreshActiveKey(): void {
    try {
      const acct = this.sdk?.getActiveAccount?.();
      this.activeKey.set(acct?.public_key ?? null);
    } catch { /* not signed in */ }
  }

  private loadScript(src: string): Promise<void> {
    return new Promise((resolve, reject) => {
      if (document.querySelector(`script[src="${src}"]`)) return resolve();
      const s = document.createElement('script');
      s.src = src; s.defer = true;
      s.onload = () => resolve();
      s.onerror = () => reject(new Error('failed to load CSPR.click'));
      document.head.appendChild(s);
    });
  }

  /** Resolve when `csprclick:loaded` fires (or if the global is already present). */
  private waitForLoaded(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (this.sdk) return resolve();
      const onLoaded = () => { window.removeEventListener('csprclick:loaded', onLoaded); resolve(); };
      window.addEventListener('csprclick:loaded', onLoaded);
      setTimeout(() => { if (this.sdk) resolve(); else reject(new Error('csprclick load timeout')); }, 15000);
    });
  }

  private wireEvents(): void {
    // Every handler nudges notifySettle() so an in-flight connect()/switchAccount() wait
    // resolves the instant the account settles (rather than on the next 500ms poll tick).
    const adopt = (evt: any) => { this.activeKey.set(evt?.account?.public_key ?? null); this.notifySettle(); };
    this.sdk.on?.('csprclick:signed_in', adopt);
    this.sdk.on?.('csprclick:switched_account', adopt);
    // fired when the user switches the active account inside the wallet extension itself
    this.sdk.on?.('csprclick:unsolicited_account_change', () => { this.refreshActiveKey(); this.notifySettle(); });
    this.sdk.on?.('csprclick:signed_out', () => { this.activeKey.set(null); this.notifySettle(); });
    this.sdk.on?.('csprclick:disconnected', () => { this.activeKey.set(null); this.notifySettle(); });
  }
}
