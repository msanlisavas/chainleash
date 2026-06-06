import { Component, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import * as signalR from '@microsoft/signalr';

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
  connected = signal(false);
  loadError = signal(false);
  approving = signal<number | null>(null);
  approveError = signal<string | null>(null);

  constructor(private http: HttpClient) {}

  ngOnInit(): void {
    this.loadState();

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

  approve(p: ProposalView): void {
    this.approving.set(p.id);
    this.approveError.set(null);
    this.http.post<{ success: boolean; error?: string }>(`${this.api}/api/approve/${p.id}`, {}).subscribe({
      next: r => { this.approving.set(null); if (!r.success) this.approveError.set(r.error ?? 'co-sign failed'); },
      error: () => { this.approving.set(null); this.approveError.set('co-sign request failed'); }
    });
  }

  pendingProposals(): ProposalView[] {
    return (this.state()?.proposals ?? []).filter(p => !p.resolved);
  }

  short(s?: string): string { return s ? s.slice(0, 10) + '…' : ''; }
  kindClass(k: string): string { return 'k-' + k.toLowerCase(); }
}
