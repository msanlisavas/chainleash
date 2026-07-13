import { fakeAsync, tick, flush } from '@angular/core/testing';
import { WalletService } from './wallet.service';

/**
 * A minimal stand-in for the CSPR.click SDK (`window.csprclick`). CSPR.click's
 * signIn()/switchAccount() BOTH return immediately — the active account only
 * settles later — so the mock lets the test flip the account mid-poll.
 */
class MockSdk {
  private acct: { public_key: string } | null = null;
  signInCount = 0;
  switchCount = 0;
  handlers: Record<string, (evt: any) => void> = {};

  getActiveAccount() { return this.acct; }
  signIn() { this.signInCount++; }
  switchAccount() { this.switchCount++; }
  on(name: string, cb: (evt: any) => void) { this.handlers[name] = cb; }

  /** Simulate the wallet reporting a (new) active account. */
  setAccount(pk: string | null) { this.acct = pk ? { public_key: pk } : null; }
}

describe('WalletService', () => {
  let svc: WalletService;
  let mock: MockSdk;

  beforeEach(() => {
    svc = new WalletService();
    mock = new MockSdk();
    (window as any).csprclick = mock;
  });

  afterEach(() => { delete (window as any).csprclick; });

  it('connect() is a no-op (null) until the SDK is ready', async () => {
    expect(await svc.connect()).toBeNull();
    expect(mock.signInCount).toBe(0);
  });

  it('connect() calls signIn() then waits until an account becomes active', fakeAsync(() => {
    svc.status.set('ready');
    let resolved: string | null | undefined;
    svc.connect().then(k => (resolved = k));

    expect(mock.signInCount).toBe(1); // signIn fired immediately
    tick(500);
    expect(resolved).toBeUndefined(); // no account yet → still waiting

    mock.setAccount('01aa');
    tick(500);
    expect(resolved).toBe('01aa'); // adopted once the wallet reports the account
  }));

  it('switchAccount() never returns the stale account — it waits for the change', fakeAsync(() => {
    // Already connected to the wrong account.
    mock.setAccount('01old');
    svc.activeKey.set('01old');
    svc.status.set('ready');

    let resolved: string | null | undefined;
    svc.switchAccount().then(k => (resolved = k));

    expect(mock.switchCount).toBe(1);
    tick(500);
    expect(resolved).toBeUndefined();      // did NOT resolve with the pre-switch key
    expect(svc.activeKey()).toBe('01old'); // and did not flip prematurely

    mock.setAccount('01new'); // user picks a different account
    tick(500);
    expect(resolved).toBe('01new');
    expect(svc.activeKey()).toBe('01new'); // app adopts (logs in) the switched account
  }));

  it('cancelConnect() ends the wait promptly', fakeAsync(() => {
    svc.status.set('ready');
    let resolved: string | null | undefined;
    svc.connect().then(k => (resolved = k));
    tick(500);
    svc.cancelConnect();
    flush();
    expect(resolved).toBeNull(); // aborted → resolves with the (still null) key
  }));

  it('switchAccount() gives up after the ~30s budget, resolving with the unchanged key', fakeAsync(() => {
    // A cancelled picker / same-account re-select never changes the key, so the poll
    // must terminate on its own budget rather than hang forever.
    mock.setAccount('01old');
    svc.activeKey.set('01old');
    svc.status.set('ready');
    let resolved: string | null | undefined;
    svc.switchAccount().then(k => (resolved = k));
    tick(60 * 500); // exhaust the 60-iteration budget without any account change
    expect(resolved).toBe('01old');       // resolves with the current key, no hang beyond budget
    expect(svc.activeKey()).toBe('01old');
  }));

  it('an account-change event settles an in-flight wait immediately (no poll lag)', fakeAsync(() => {
    mock.setAccount('01old');
    svc.activeKey.set('01old');
    svc.status.set('ready');
    (svc as any).wireEvents(); // so the event handlers call notifySettle()
    let resolved: string | null | undefined;
    svc.switchAccount().then(k => (resolved = k));

    // Fire the switched_account event WITHOUT advancing the 500ms poll timer.
    mock.setAccount('01new');
    mock.handlers['csprclick:switched_account']({ account: { public_key: '01new' } });
    flush();
    expect(resolved).toBe('01new'); // the event woke the wait rather than waiting for a poll tick
  }));

  it('event handlers adopt the account from evt.account.public_key', () => {
    (svc as any).wireEvents(); // registers handlers on the mock sdk
    mock.handlers['csprclick:signed_in']({ account: { public_key: '01signed' } });
    expect(svc.activeKey()).toBe('01signed');

    mock.handlers['csprclick:switched_account']({ account: { public_key: '01switched' } });
    expect(svc.activeKey()).toBe('01switched');

    mock.handlers['csprclick:signed_out']({});
    expect(svc.activeKey()).toBeNull();
  });
});
