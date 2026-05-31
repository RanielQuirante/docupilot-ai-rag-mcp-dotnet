import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { PageShell } from '@shared/ui/page-shell/page-shell';

import { DashboardClient } from '../../data/dashboard.client';
import { DashboardStats } from '../../data/dashboard.models';

/**
 * The page's lifecycle of states (mirrors the slice-wide loading/loaded/error
 * split — there is no "empty" surface because the stats endpoint always returns
 * a payload; an empty database simply renders all-zero cards):
 *  - `loading` — the stats request is in flight (initial load / manual refresh).
 *  - `loaded`  — 200 with the aggregate metrics.
 *  - `error`   — any transport/500 failure.
 */
type DashboardState = 'loading' | 'loaded' | 'error';

/**
 * View model for a single stat card. `accent` selects a full literal Tailwind
 * class bundle from {@link STAT_CARD_ACCENTS} (no runtime concatenation, so
 * Oxide never purges the utilities — DA-011 §6.6 / ADR §5).
 */
interface StatCard {
  readonly label: string;
  readonly value: number;
  readonly accent: StatAccent;
  /** Route to navigate to on click (optional — keeps the cards useful). */
  readonly link: string | null;
  /** Inline SVG path data for the card's icon. */
  readonly iconPath: string;
}

type StatAccent = 'slate' | 'amber' | 'emerald' | 'red' | 'indigo';

/**
 * Full literal Tailwind class bundles per accent (icon tile bg/text + value
 * colour). Consistent with the status-badge palette used in document-library /
 * workflow-tasks (failed = red, ready = emerald, pending = amber).
 */
const STAT_CARD_ACCENTS: Readonly<
  Record<StatAccent, { readonly tile: string; readonly value: string }>
> = {
  slate: { tile: 'bg-slate-100 text-slate-600', value: 'text-slate-900' },
  amber: { tile: 'bg-amber-100 text-amber-700', value: 'text-amber-700' },
  emerald: { tile: 'bg-emerald-100 text-emerald-700', value: 'text-emerald-700' },
  red: { tile: 'bg-red-100 text-red-700', value: 'text-red-700' },
  indigo: { tile: 'bg-indigo-100 text-indigo-700', value: 'text-indigo-700' },
};

/**
 * Full literal Tailwind chip/bar classes per category — keyed by the spec §5.3
 * display strings the backend emits (DA-058 maps via `DocumentCategoryNames`).
 * Mirrors the document-library category palette (DA-035) so the same category
 * reads the same colour everywhere. Unknown/future categories fall back to
 * neutral slate.
 */
const CATEGORY_CHIP_CLASSES: Readonly<Record<string, string>> = {
  Contract: 'bg-indigo-100 text-indigo-800 ring-indigo-600/20',
  Invoice: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  'Employee Record': 'bg-sky-100 text-sky-800 ring-sky-600/20',
  'Legal Document': 'bg-purple-100 text-purple-800 ring-purple-600/20',
  'Compliance Document': 'bg-rose-100 text-rose-800 ring-rose-600/20',
  'Client Correspondence': 'bg-teal-100 text-teal-800 ring-teal-600/20',
  'Policy Document': 'bg-amber-100 text-amber-800 ring-amber-600/20',
  Unknown: 'bg-slate-100 text-slate-600 ring-slate-500/20',
};
const CATEGORY_CHIP_FALLBACK = 'bg-slate-100 text-slate-600 ring-slate-500/20';

/** Matching bar-fill colours for the breakdown bars (literal, by category). */
const CATEGORY_BAR_CLASSES: Readonly<Record<string, string>> = {
  Contract: 'bg-indigo-500',
  Invoice: 'bg-emerald-500',
  'Employee Record': 'bg-sky-500',
  'Legal Document': 'bg-purple-500',
  'Compliance Document': 'bg-rose-500',
  'Client Correspondence': 'bg-teal-500',
  'Policy Document': 'bg-amber-500',
  Unknown: 'bg-slate-400',
};
const CATEGORY_BAR_FALLBACK = 'bg-slate-400';

// Inline SVG path data (Heroicons-style, 24x24, stroke). Literal so no asset hop.
const ICON_DOCUMENTS =
  'M19.5 14.25v-2.625a3.375 3.375 0 0 0-3.375-3.375h-1.5A1.125 1.125 0 0 1 13.5 7.125v-1.5a3.375 3.375 0 0 0-3.375-3.375H8.25m2.25 0H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 0 0-9-9Z';
const ICON_CLOCK =
  'M12 6v6h4.5m4.5 0a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z';
const ICON_CHECK =
  'M9 12.75 11.25 15 15 9.75M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z';
const ICON_WARNING =
  'M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z';
const ICON_TASKS =
  'M9 12h3.75M9 15h3.75M9 18h3.75m3 .75H18a2.25 2.25 0 0 0 2.25-2.25V6.108c0-1.135-.845-2.098-1.976-2.192a48.424 48.424 0 0 0-1.123-.08m-5.801 0c-.065.21-.1.433-.1.664 0 .414.336.75.75.75h4.5a.75.75 0 0 0 .75-.75 2.25 2.25 0 0 0-.1-.664m-5.8 0A2.251 2.251 0 0 1 13.5 2.25H15c1.012 0 1.867.668 2.15 1.586m-5.8 0c-.376.023-.75.05-1.124.08C9.095 4.01 8.25 4.973 8.25 6.108V8.25m0 0H4.875c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125h9.75c.621 0 1.125-.504 1.125-1.125V9.375c0-.621-.504-1.125-1.125-1.125H8.25Z';

@Component({
  selector: 'app-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink],
  templateUrl: './dashboard-page.html',
})
export class DashboardPage {
  private readonly client = inject(DashboardClient);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly state = signal<DashboardState>('loading');
  protected readonly stats = signal<DashboardStats | null>(null);

  protected readonly isLoading = computed<boolean>(() => this.state() === 'loading');

  /** The stat cards derived from the loaded stats (empty until loaded). */
  protected readonly cards = computed<readonly StatCard[]>(() => {
    const s = this.stats();
    if (!s) {
      return [];
    }
    return [
      {
        label: 'Total Documents',
        value: s.totalDocuments,
        accent: 'indigo',
        link: '/library',
        iconPath: ICON_DOCUMENTS,
      },
      {
        label: 'Pending Processing',
        value: s.pendingProcessing,
        accent: 'amber',
        link: '/library',
        iconPath: ICON_CLOCK,
      },
      {
        label: 'Ready for Search',
        value: s.readyForSearch,
        accent: 'emerald',
        link: '/search',
        iconPath: ICON_CHECK,
      },
      {
        label: 'Failed',
        value: s.failed,
        accent: 'red',
        link: '/library',
        iconPath: ICON_WARNING,
      },
      {
        label: 'Pending Workflow Tasks',
        value: s.pendingWorkflowTasks,
        accent: 'slate',
        link: '/tasks',
        iconPath: ICON_TASKS,
      },
    ];
  });

  /** The classification breakdown rows (count DESC; empty when none). */
  protected readonly breakdown = computed(() => this.stats()?.classificationBreakdown ?? []);

  /** Largest category count — drives the relative bar widths. */
  protected readonly breakdownMax = computed<number>(() => {
    const rows = this.breakdown();
    return rows.reduce((max, r) => (r.count > max ? r.count : max), 0);
  });

  constructor() {
    this.load();
  }

  /** (Re)load the dashboard stats. */
  protected load(): void {
    this.state.set('loading');
    this.client
      .getStats()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome) => {
          if (outcome.kind === 'stats') {
            this.stats.set(outcome.stats);
            this.state.set('loaded');
          } else {
            this.stats.set(null);
            this.state.set('error');
          }
        },
        error: () => {
          this.stats.set(null);
          this.state.set('error');
        },
      });
  }

  // --- View helpers ---

  protected accentTile(accent: StatAccent): string {
    return STAT_CARD_ACCENTS[accent].tile;
  }

  protected accentValue(accent: StatAccent): string {
    return STAT_CARD_ACCENTS[accent].value;
  }

  protected categoryChipClass(category: string): string {
    return CATEGORY_CHIP_CLASSES[category] ?? CATEGORY_CHIP_FALLBACK;
  }

  protected categoryBarClass(category: string): string {
    return CATEGORY_BAR_CLASSES[category] ?? CATEGORY_BAR_FALLBACK;
  }

  /** Bar width as an integer percentage of the largest category (min 6% so a 1-count row is visible). */
  protected barWidthPercent(count: number): number {
    const max = this.breakdownMax();
    if (max <= 0) {
      return 0;
    }
    return Math.max(6, Math.round((count / max) * 100));
  }
}
