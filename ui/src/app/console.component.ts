import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { catchError } from 'rxjs/operators';

@Component({
  selector: 'app-console',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="panel-h">LLMOps-консоль <span class="sub">over service API</span></div>

    <div class="tiles">
      <div class="tile">
        <div class="k">Вартість сьогодні</div>
        <div class="v">{{ money(cost?.today_usd) }} <span class="u">/ {{ money(cost?.budget_usd) }}</span></div>
      </div>
      <div class="tile"><div class="k">p95 latency</div><div class="v">{{ num(obs?.p95_ms) }}<span class="u">ms</span></div></div>
      <div class="tile"><div class="k">Запити</div><div class="v">{{ num(obs?.requests) }}</div></div>
      <div class="tile"><div class="k">Cache-hit</div><div class="v">{{ num(obs?.cache_hit_pct) }}<span class="u">%</span></div></div>
      <div class="tile"><div class="k">Error rate</div><div class="v">{{ num(obs?.error_rate_pct) }}<span class="u">%</span></div></div>
      <div class="tile"><div class="k">Fallback</div><div class="v">{{ num(obs?.fallback_events) }}</div></div>
    </div>

    <div class="card">
      <div class="ch">Prompt registry <span class="lens">L2</span></div>
      <ng-container *ngIf="prompts?.length; else emptyP">
        <div class="row" *ngFor="let p of prompts">
          <span class="mono">{{ p.name }}</span><span class="mono">{{ p.version }}</span>
          <span class="badge" [class.on]="p.active">{{ p.active ? 'active' : 'archived' }}</span>
        </div>
      </ng-container>
      <ng-template #emptyP><div class="empty">— очікує GET /prompts</div></ng-template>
    </div>

    <div class="card">
      <div class="ch">Надійність провайдерів <span class="lens">L7</span></div>
      <ng-container *ngIf="providers?.length; else emptyH">
        <div class="prov" *ngFor="let p of providers"><span class="dot" [class.down]="p.status !== 'ok'"></span>{{ p.name }} · {{ p.status }}</div>
      </ng-container>
      <ng-template #emptyH><div class="empty">— очікує GET /providers (providers[])</div></ng-template>
    </div>

    <div class="card">
      <div class="ch">Approvals · HITL <span class="lens">L8</span></div>
      <ng-container *ngIf="approvals?.length; else emptyA">
        <div class="row" *ngFor="let a of approvals"><span class="mono">{{ a.id }}</span> {{ a.action }}</div>
      </ng-container>
      <ng-template #emptyA><div class="empty">— черга порожня / очікує GET /approvals</div></ng-template>
    </div>

    <p class="foot">Read-only. Дані з API сервісу. Поки ендпоінти — заглушки, показано «—»; студент реалізує їх → консоль наповнюється.</p>
  `,
  styles: [
    `
      :host { display: block; }
      .panel-h { font-family: ui-monospace, monospace; font-size: 12px; text-transform: uppercase; letter-spacing: .1em; color: #8a90a0; margin-bottom: 12px; }
      .panel-h .sub { text-transform: none; letter-spacing: 0; }
      .tiles { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; margin-bottom: 12px; }
      .tile { background: #fff; border: 1px solid #e6e6ea; border-radius: 10px; padding: 12px 13px; }
      .tile .k { font-size: 11px; color: #8a90a0; text-transform: uppercase; letter-spacing: .04em; }
      .tile .v { font-family: ui-monospace, monospace; font-size: 20px; font-weight: 700; margin-top: 5px; }
      .tile .u { font-size: 12px; color: #8a90a0; font-weight: 600; }
      .card { background: #fff; border: 1px solid #e6e6ea; border-radius: 10px; padding: 14px; margin-bottom: 12px; }
      .ch { font-weight: 650; font-size: 14px; margin-bottom: 10px; display: flex; justify-content: space-between; align-items: center; }
      .lens { font-family: ui-monospace, monospace; font-size: 10px; color: #4038c4; background: #ecebfb; padding: 2px 7px; border-radius: 20px; }
      .row { display: flex; gap: 10px; align-items: center; font-size: 13px; padding: 3px 0; }
      .mono { font-family: ui-monospace, monospace; }
      .badge { margin-left: auto; font-family: ui-monospace, monospace; font-size: 10px; text-transform: uppercase; padding: 2px 7px; border-radius: 20px; background: #eee; color: #888; }
      .badge.on { background: #e6f2ea; color: #177245; }
      .prov { display: flex; align-items: center; font-size: 13px; padding: 3px 0; }
      .dot { width: 8px; height: 8px; border-radius: 50%; background: #177245; display: inline-block; margin-right: 7px; }
      .dot.down { background: #b3261e; }
      .empty { font-size: 13px; color: #999; }
      .foot { font-size: 12px; color: #999; margin: 6px 0 0; }
    `,
  ],
})
export class ConsoleComponent implements OnInit {
  obs: any = null;
  cost: any = null;
  prompts: any[] | null = null;
  providers: any[] | null = null;
  approvals: any[] | null = null;

  constructor(private http: HttpClient) {}

  ngOnInit(): void {
    const get = (u: string) => this.http.get<any>(u).pipe(catchError(() => of(null)));
    get('/api/observability').subscribe((d) => (this.obs = d));
    get('/api/cost').subscribe((d) => (this.cost = d));
    get('/api/prompts').subscribe((d) => (this.prompts = Array.isArray(d) ? d : d?.versions ?? null));
    get('/api/providers').subscribe((d) => (this.providers = d?.providers ?? null));
    get('/api/approvals').subscribe((d) => (this.approvals = d?.pending ?? null));
  }

  num(v: unknown): string | number {
    return typeof v === 'number' ? v : '—';
  }

  money(v: unknown): string {
    return typeof v === 'number' ? '$' + v.toFixed(2) : '—';
  }
}
