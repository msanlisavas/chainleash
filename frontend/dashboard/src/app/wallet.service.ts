import { Injectable, signal } from '@angular/core';

/** Public, secret-free config the dashboard fetches from the agent to drive co-sign. */
export interface AppConfig {
  chainName: string;
  packageHash: string;
  ownerPublicKey: string | null;
  csprClickAppId: string;
  walletCoSignEnabled: boolean;
  allowServerKeyCoSign: boolean;
  readOnly: boolean;
}

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

  private get sdk(): any { return (window as any).csprclick; }

  /** Load + initialize the CSPR.click client with the server-supplied appId. Idempotent. */
  async init(cfg: AppConfig): Promise<void> {
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
      this.status.set('unavailable'); // wallet/SDK not present — UI falls back gracefully
    }
  }

  /** Open the CSPR.click sign-in modal; resolves once an account is active (or null). */
  async connect(): Promise<string | null> {
    if (this.status() !== 'ready') return null;
    this.sdk.signIn();
    // signed_in fires via wireEvents(); also poll briefly as a fallback.
    for (let i = 0; i < 60 && !this.activeKey(); i++) {
      await new Promise(r => setTimeout(r, 500));
      this.refreshActiveKey();
    }
    return this.activeKey();
  }

  async disconnect(): Promise<void> {
    try { await this.sdk?.signOut?.(); } catch { /* ignore */ }
    this.activeKey.set(null);
  }

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
    this.sdk.on?.('csprclick:signed_in', (evt: any) => this.activeKey.set(evt?.account?.public_key ?? null));
    this.sdk.on?.('csprclick:switched_account', (evt: any) => this.activeKey.set(evt?.account?.public_key ?? null));
    this.sdk.on?.('csprclick:signed_out', () => this.activeKey.set(null));
    this.sdk.on?.('csprclick:disconnected', () => this.activeKey.set(null));
  }
}
