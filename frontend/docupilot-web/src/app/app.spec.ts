import { HttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { App } from './app';
import { HealthResponse, HealthService } from './core/api/health.service';

describe('App', () => {
  beforeEach(async () => {
    const stubHealth: Pick<HealthService, 'getHealth'> = {
      getHealth: () =>
        of<HealthResponse>({
          status: 'healthy',
          service: 'DocuPilot.Api',
          version: '0.1.0',
          timestamp: '2026-05-20T00:00:00.000Z',
        }),
    };

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        { provide: HealthService, useValue: stubHealth },
        { provide: HttpClient, useValue: { get: () => of(null) } },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render the Health Check heading', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Health Check');
  });
});
