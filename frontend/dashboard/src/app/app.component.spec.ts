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

  it('short() truncates a hash to 10 chars + ellipsis, and is null-safe', () => {
    expect(c.short('0106618e1493f73ee0')).toBe('0106618e14…');
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
});
