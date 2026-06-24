import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { AppComponent } from './app.component';

// Pure view-logic of the dashboard. ngOnInit (which opens HTTP + SignalR) is never
// triggered — we don't call detectChanges — so these run fast and offline.
describe('AppComponent', () => {
  let c: AppComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    c = TestBed.createComponent(AppComponent).componentInstance;
  });

  it('short() mid-truncates a key to first-5...last-5, and is null-safe', () => {
    expect(c.short('0106618e1493f73ee0')).toBe('01066...73ee0');
    expect(c.short('abcdef')).toBe('abcdef'); // short strings unchanged
    expect(c.short('')).toBe('');
    expect(c.short(undefined)).toBe('');
  });

  it('kindClass() maps an audit kind to its css class', () => {
    expect(c.kindClass('DELEGATE')).toBe('k-delegate');
    expect(c.kindClass('REJECT')).toBe('k-reject');
  });

  it('pendingProposals() returns only unresolved proposals', () => {
    c.state.set({
      proposals: [
        { id: 1, validator: 'v', amountCspr: 10, undelegate: false, txHash: 'h', resolved: false },
        { id: 2, validator: 'v', amountCspr: 20, undelegate: false, txHash: 'h', resolved: true },
      ],
    } as any);

    const pending = c.pendingProposals();
    expect(pending.length).toBe(1);
    expect(pending[0].id).toBe(1);
  });

  it('pendingProposals() is empty when there is no state', () => {
    c.state.set(null);
    expect(c.pendingProposals()).toEqual([]);
  });

  it('fmt() groups thousands, caps decimals, and renders unknown as a dash', () => {
    expect(c.fmt(2000)).toBe('2,000');
    expect(c.fmt(2.505)).toBe('2.51');
    expect(c.fmt(0)).toBe('0');
    expect(c.fmt(undefined)).toBe('—');
    expect(c.fmt(null)).toBe('—');
  });

  it('timeOf() prefers the iso timestamp and falls back to the legacy UTC string', () => {
    const iso = { time: '10:00:00', tick: 1, kind: 'HOLD', message: '', iso: '2026-06-10T10:00:00.000Z' };
    const legacy = { time: '10:00:00', tick: 1, kind: 'HOLD', message: '' };
    expect(c.timeOf(iso)).toBe(new Date(iso.iso!).toLocaleTimeString([], { hour12: false }));
    expect(c.timeOf(legacy)).toBe('10:00:00');
    expect(c.timeTitle(legacy)).toContain('UTC');
  });

  it('explorerBase() prefers the server-configured base, then the chain heuristic', () => {
    expect(c.explorerBase()).toBe('https://testnet.cspr.live'); // no config yet → testnet
    c.config.set({ chainName: 'casper' } as any);
    expect(c.explorerBase()).toBe('https://cspr.live');
    c.config.set({ chainName: 'casper-test', explorerBaseUrl: 'https://my.explorer' } as any);
    expect(c.explorerBase()).toBe('https://my.explorer'); // server value wins
    expect(c.txLink('abc')).toBe('https://my.explorer/transaction/abc');
    expect(c.txLink(undefined)).toBeNull();
  });

  it('coSignBlocked() flags loading, wrong account, and an unavailable SDK', () => {
    c.config.set({ ownerPublicKey: '01AA' } as any);
    expect(c.coSignBlocked()).toContain('loading'); // idle/loading SDK → explain, don't mislead
    c.wallet.status.set('ready');
    c.wallet.activeKey.set('01bb');
    expect(c.coSignBlocked()).toContain('owner account');
    c.wallet.activeKey.set('01aa'); // case-insensitive match
    expect(c.coSignBlocked()).toBeNull();
    c.wallet.status.set('unavailable');
    expect(c.coSignBlocked()).toContain('unavailable');
  });
});
