import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { PageShell } from '@shared/ui/page-shell/page-shell';

import { AuditLogsClient } from '../../data/audit-logs.client';
import {
  AUDIT_ACTIONS,
  AuditActionFilter,
  AuditLogListItem,
} from '../../data/audit-log.models';

/**
 * The page's lifecycle of states (mirrors the workflow-tasks
 * loading/list/empty/error split):
 *  - `loading` — the list request is in flight (initial load / page / filter change).
 *  - `entries` — 200 with ≥1 audit entry on this page.
 *  - `empty`   — 200 with `[]` (no entries match — NOT an error).
 *  - `error`   — generic transport/500 failure (incl. the never-sent 400).
 */
type AuditState = 'loading' | 'entries' | 'empty' | 'error';

const PAGE_SIZE = 50;

/**
 * Full literal Tailwind class strings per action badge (no runtime
 * concatenation — DA-011 §6.6, so Oxide keeps every utility). Colour-coded by
 * outcome family, consistent with the status palette elsewhere: *Succeeded =
 * emerald, *Failed = red, *Started/Queued/in-flight = amber, the AI-tool
 * actions get a distinct indigo/violet family so the "every AI action is
 * audited" entries stand out. Unknown/future actions fall back to slate.
 */
const ACTION_BADGE_CLASSES: Readonly<Record<string, string>> = {
  // Phase 3 — extraction
  Queued: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ExtractionStarted: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ExtractionSucceeded: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  ExtractionFailed: 'bg-red-100 text-red-800 ring-red-600/20',
  ReprocessQueued: 'bg-sky-100 text-sky-800 ring-sky-600/20',
  // Phase 4 — classification
  ClassificationStarted: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ClassificationSucceeded: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  ClassificationFailed: 'bg-red-100 text-red-800 ring-red-600/20',
  // Phase 5 — embeddings
  EmbeddingStarted: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  EmbeddingSucceeded: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  EmbeddingFailed: 'bg-red-100 text-red-800 ring-red-600/20',
  // Phase 8 — AI workflow tools (distinct, prominent family)
  ToolInvoked: 'bg-violet-100 text-violet-800 ring-violet-600/30',
  ToolSucceeded: 'bg-indigo-100 text-indigo-800 ring-indigo-600/30',
  ToolFailed: 'bg-rose-100 text-rose-800 ring-rose-600/30',
};
const ACTION_BADGE_FALLBACK = 'bg-slate-100 text-slate-700 ring-slate-500/20';

/** The set of AI-tool actions — flagged with an "AI" marker on the timeline. */
const TOOL_ACTIONS: ReadonlySet<string> = new Set(['ToolInvoked', 'ToolSucceeded', 'ToolFailed']);

@Component({
  selector: 'app-audit-logs-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink, DatePipe],
  templateUrl: './audit-logs-page.html',
})
export class AuditLogsPage {
  private readonly client = inject(AuditLogsClient);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly state = signal<AuditState>('loading');
  protected readonly entries = signal<readonly AuditLogListItem[]>([]);

  protected readonly page = signal<number>(1);
  protected readonly pageSize = signal<number>(PAGE_SIZE);
  protected readonly totalCount = signal<number>(0);
  protected readonly totalPages = signal<number>(0);

  /** Active action filter (`'All'` ⇒ unfiltered). */
  protected readonly filter = signal<'All' | AuditActionFilter>('All');

  /** Filter dropdown options (All + every valid AuditAction enum name). */
  protected readonly actionOptions: readonly string[] = ['All', ...AUDIT_ACTIONS];

  protected readonly isLoading = computed<boolean>(() => this.state() === 'loading');

  protected readonly canPrev = computed<boolean>(() => this.page() > 1);
  protected readonly canNext = computed<boolean>(() => this.page() < this.totalPages());

  constructor() {
    this.load();
  }

  /** Map the active filter to the contract's optional `action` param. */
  private actionParam(): AuditActionFilter {
    const f = this.filter();
    return f === 'All' ? undefined : f;
  }

  /** (Re)load the current page for the active filter. */
  protected load(): void {
    this.state.set('loading');
    this.client
      .list(this.page(), this.pageSize(), this.actionParam())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome) => {
          if (outcome.kind === 'page') {
            const r = outcome.result;
            this.entries.set(r.items);
            this.totalCount.set(r.totalCount);
            this.totalPages.set(r.totalPages);
            this.state.set(r.items.length > 0 ? 'entries' : 'empty');
          } else {
            this.entries.set([]);
            this.state.set('error');
          }
        },
        error: () => {
          this.entries.set([]);
          this.state.set('error');
        },
      });
  }

  /** Change the action filter, reset to page 1, and refetch. */
  protected onFilterChange(value: string): void {
    const next = value === 'All' ? 'All' : (value as AuditActionFilter);
    if (this.filter() === next) {
      return;
    }
    this.filter.set(next);
    this.page.set(1);
    this.load();
  }

  protected goToPage(target: number): void {
    if (target < 1 || (this.totalPages() > 0 && target > this.totalPages())) {
      return;
    }
    if (target === this.page()) {
      return;
    }
    this.page.set(target);
    this.load();
  }

  protected prevPage(): void {
    this.goToPage(this.page() - 1);
  }

  protected nextPage(): void {
    this.goToPage(this.page() + 1);
  }

  // --- View helpers ---

  protected actionBadgeClass(action: string): string {
    return ACTION_BADGE_CLASSES[action] ?? ACTION_BADGE_FALLBACK;
  }

  /** True for the Phase-8 AI-tool actions (gets an "AI" marker). */
  protected isToolAction(action: string): boolean {
    return TOOL_ACTIONS.has(action);
  }

  /** True when the entry links to a document detail page. */
  protected isDocumentEntity(entry: AuditLogListItem): boolean {
    return entry.entityName === 'Document' && !this.isEmptyGuid(entry.entityId);
  }

  private isEmptyGuid(id: string): boolean {
    return !id || id === '00000000-0000-0000-0000-000000000000';
  }

  /** Short, copy-friendly id suffix for display (full id on hover). */
  protected shortId(id: string): string {
    if (!id) {
      return '—';
    }
    return id.length > 8 ? `…${id.slice(-8)}` : id;
  }

  /**
   * Render `detailsJson` readably. Valid JSON objects are pretty-printed
   * (2-space indent); non-JSON or scalar payloads pass through as-is. Never
   * dumps raw, unformatted JSON. Returns `null` when there are no details.
   */
  protected formatDetails(detailsJson: string | null): string | null {
    if (!detailsJson) {
      return null;
    }
    const trimmed = detailsJson.trim();
    if (!trimmed) {
      return null;
    }
    try {
      const parsed: unknown = JSON.parse(trimmed);
      if (parsed !== null && typeof parsed === 'object') {
        return JSON.stringify(parsed, null, 2);
      }
      // Scalar JSON (string/number/bool) — show its plain value.
      return String(parsed);
    } catch {
      // Not JSON — show the raw string (already a readable note).
      return trimmed;
    }
  }
}
