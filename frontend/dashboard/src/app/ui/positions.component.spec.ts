import { TestBed } from '@angular/core/testing';
import { PositionsComponent } from './positions.component';

// Pure view-logic of the staking table. Created via TestBed so the signal `input()`s have an
// injection context; detectChanges is never called, so these run fast and offline.
describe('PositionsComponent', () => {
  let c: PositionsComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [PositionsComponent] });
    c = TestBed.createComponent(PositionsComponent).componentInstance;
  });

  it('short() truncates a key to 10 chars + ellipsis, null-safe', () => {
    expect(c.short('0147ce053a742c')).toBe('0147ce053a…');
    expect(c.short(undefined)).toBe('');
  });

  it('fmt() groups thousands, caps decimals, dashes the unknown', () => {
    expect(c.fmt(2002.42)).toBe('2,002.42');
    expect(c.fmt(0)).toBe('0');
    expect(c.fmt(undefined)).toBe('—');
    expect(c.fmt(null)).toBe('—');
  });
});
