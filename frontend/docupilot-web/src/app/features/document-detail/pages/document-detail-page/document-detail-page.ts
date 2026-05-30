import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { PageShell } from '@shared/ui/page-shell/page-shell';

import { DocumentDetailClient } from '../../data/document-detail.client';
import {
  AuditLogEntry,
  DocumentDetail,
  DocumentTextResponse,
  NON_TERMINAL_STATUSES,
} from '../../data/document-detail.models';

/** One rendered metadata field (a key + its display value + whether it was nested). */
interface MetadataRow {
  readonly key: string;
  readonly label: string;
  readonly value: string;
}

/**
 * Full literal Tailwind class strings per status — no runtime concatenation, so
 * Tailwind's Oxide engine never purges them (DA-011 §6.6). Keys cover the full
 * lifecycle incl. the Phase-3 `TextExtracted` success terminal and `Failed`.
 */
const STATUS_BADGE_CLASSES: Readonly<Record<string, string>> = {
  Uploaded: 'bg-sky-100 text-sky-800 ring-sky-600/20',
  Queued: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ExtractingText: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  TextExtracted: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  Classifying: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  GeneratingEmbeddings: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ReadyForSearch: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  Failed: 'bg-red-100 text-red-800 ring-red-600/20',
};
const STATUS_BADGE_FALLBACK = 'bg-slate-100 text-slate-700 ring-slate-500/20';

/** Human-readable labels for audit actions (DA-024/DA-032 set). Unknown values pass through. */
const AUDIT_ACTION_LABELS: Readonly<Record<string, string>> = {
  Queued: 'Queued for processing',
  ExtractionStarted: 'Text extraction started',
  ExtractionSucceeded: 'Text extraction succeeded',
  ExtractionFailed: 'Text extraction failed',
  ClassificationStarted: 'Classification started',
  ClassificationSucceeded: 'Classification succeeded',
  ClassificationFailed: 'Classification failed',
  ReprocessQueued: 'Re-queued for processing',
};

/**
 * Full literal Tailwind class strings per classification category — no runtime
 * concatenation so Tailwind's Oxide engine never purges them (DA-011 §6.6).
 * Keys are the spec §5.3 display strings (with spaces) the API returns verbatim.
 */
const CATEGORY_BADGE_CLASSES: Readonly<Record<string, string>> = {
  Contract: 'bg-indigo-100 text-indigo-800 ring-indigo-600/20',
  Invoice: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  'Employee Record': 'bg-sky-100 text-sky-800 ring-sky-600/20',
  'Legal Document': 'bg-purple-100 text-purple-800 ring-purple-600/20',
  'Compliance Document': 'bg-amber-100 text-amber-800 ring-amber-600/20',
  'Client Correspondence': 'bg-cyan-100 text-cyan-800 ring-cyan-600/20',
  'Policy Document': 'bg-teal-100 text-teal-800 ring-teal-600/20',
  Unknown: 'bg-slate-100 text-slate-700 ring-slate-500/20',
};
const CATEGORY_BADGE_FALLBACK = 'bg-slate-100 text-slate-700 ring-slate-500/20';

/** Re-poll cadence while the document is in a non-terminal state. */
const POLL_INTERVAL_MS = 5000;

@Component({
  selector: 'app-document-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink, DatePipe],
  templateUrl: './document-detail-page.html',
})
export class DocumentDetailPage {
  private readonly client = inject(DocumentDetailClient);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  /** Route param `:id`. Snapshot is fine — the slice is re-created per id. */
  protected readonly id = this.route.snapshot.paramMap.get('id') ?? '';

  // --- Detail (GET /{id}) ---
  protected readonly loading = signal<boolean>(true);
  protected readonly error = signal<string | null>(null);
  protected readonly notFound = signal<boolean>(false);
  protected readonly detail = signal<DocumentDetail | null>(null);

  // --- Audit timeline (GET /{id}/audit) ---
  protected readonly auditLoading = signal<boolean>(true);
  protected readonly auditError = signal<string | null>(null);
  protected readonly audit = signal<readonly AuditLogEntry[]>([]);

  // --- Text preview (GET /{id}/text) — lazy: fetched only when expanded ---
  protected readonly textExpanded = signal<boolean>(false);
  protected readonly textLoading = signal<boolean>(false);
  /** Set to a friendly message when text isn't available yet (404) or fails. */
  protected readonly textUnavailable = signal<string | null>(null);
  protected readonly text = signal<DocumentTextResponse | null>(null);

  // --- Re-process (POST /{id}/process) ---
  protected readonly processing = signal<boolean>(false);
  protected readonly processMessage = signal<string | null>(null);
  protected readonly processIsError = signal<boolean>(false);

  /** True while the document is still being worked (Queued / ExtractingText). */
  protected readonly isNonTerminal = computed<boolean>(() => {
    const s = this.detail()?.status;
    return s != null && (NON_TERMINAL_STATUSES as readonly string[]).includes(s);
  });

  protected readonly isFailed = computed<boolean>(() => this.detail()?.status === 'Failed');

  /** True once classification has completed (the doc reached the `Classified` terminal). */
  protected readonly isClassified = computed<boolean>(() => this.detail()?.status === 'Classified');

  /** True while classification is actively running (between TextExtracted and Classified). */
  protected readonly isClassifying = computed<boolean>(() => this.detail()?.status === 'Classifying');

  /**
   * The parsed metadata object flattened into displayable rows. Nested objects /
   * arrays are JSON-stringified (readable, no fixed-schema assumption); empty
   * `{}` yields `[]` (template shows "No metadata extracted"). Null → [].
   */
  protected readonly metadataRows = computed<readonly MetadataRow[]>(() => {
    const meta = this.detail()?.metadata;
    if (meta == null) {
      return [];
    }
    return Object.keys(meta).map((key) => ({
      key,
      label: this.humanizeKey(key),
      value: this.formatMetaValue(meta[key]),
    }));
  });

  /**
   * Re-process is allowed only from a terminal/idle state (Failed / Uploaded /
   * TextExtracted) per the contract; disabled while Queued/ExtractingText (the
   * backend would 409) or while a request is in flight.
   */
  protected readonly canReprocess = computed<boolean>(() => {
    const s = this.detail()?.status;
    if (s == null) {
      return false;
    }
    return !this.processing() && !(NON_TERMINAL_STATUSES as readonly string[]).includes(s);
  });

  private pollTimer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    if (!this.id) {
      this.loading.set(false);
      this.notFound.set(true);
      return;
    }
    this.loadDetail();
    this.loadAudit();
    this.destroyRef.onDestroy(() => this.stopPolling());
  }

  /** (Re)load the document detail. `refresh` keeps the page chrome during polling. */
  protected loadDetail(refresh = false): void {
    if (!refresh) {
      this.loading.set(true);
    }
    this.error.set(null);
    this.client
      .getDetail(this.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (d) => {
          this.detail.set(d);
          this.loading.set(false);
          this.syncPolling();
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          if (err.status === 404) {
            this.notFound.set(true);
          } else {
            this.error.set('Could not load this document. The API may be unreachable.');
          }
          this.stopPolling();
        },
      });
  }

  protected loadAudit(): void {
    this.auditLoading.set(true);
    this.auditError.set(null);
    this.client
      .getAudit(this.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (entries) => {
          this.audit.set(entries);
          this.auditLoading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.auditLoading.set(false);
          // 404 here means the document itself is missing — detail load reports that.
          if (err.status !== 404) {
            this.auditError.set('Could not load the activity history.');
          }
        },
      });
  }

  /** Toggle the extracted-text panel; lazily fetch the LOB on first expand. */
  protected toggleText(): void {
    const next = !this.textExpanded();
    this.textExpanded.set(next);
    if (next && this.text() === null && this.textUnavailable() === null && !this.textLoading()) {
      this.loadText();
    }
  }

  private loadText(): void {
    this.textLoading.set(true);
    this.textUnavailable.set(null);
    this.client
      .getText(this.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (t) => {
          this.text.set(t);
          this.textLoading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.textLoading.set(false);
          this.textUnavailable.set(
            err.status === 404
              ? 'Text not available yet — this document has not been extracted.'
              : 'Could not load the extracted text. Please try again.',
          );
        },
      });
  }

  /** POST /{id}/process and reflect 202 / 404 / 409 outcomes. */
  protected reprocess(): void {
    if (!this.canReprocess()) {
      return;
    }
    this.processing.set(true);
    this.processMessage.set(null);
    this.processIsError.set(false);
    this.client
      .process(this.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          // 202 Accepted — backend re-queued the document.
          this.processing.set(false);
          this.processIsError.set(false);
          this.processMessage.set('Re-processing queued. Status will update shortly.');
          // Reset the lazily-loaded text so the next expand re-fetches fresh output.
          this.text.set(null);
          this.textUnavailable.set(null);
          this.textExpanded.set(false);
          this.loadDetail(true);
          this.loadAudit();
        },
        error: (err: HttpErrorResponse) => {
          this.processing.set(false);
          this.processIsError.set(true);
          if (err.status === 409) {
            this.processMessage.set(
              'This document is already being processed — re-processing is not available right now.',
            );
            this.loadDetail(true);
          } else if (err.status === 404) {
            this.processMessage.set('This document no longer exists.');
          } else {
            this.processMessage.set('Could not start re-processing. Please try again.');
          }
        },
      });
  }

  /** Start/stop polling so the displayed status tracks the live pipeline. */
  private syncPolling(): void {
    if (this.isNonTerminal()) {
      this.startPolling();
    } else {
      this.stopPolling();
    }
  }

  private startPolling(): void {
    if (this.pollTimer !== null) {
      return;
    }
    this.pollTimer = setInterval(() => {
      this.loadDetail(true);
      this.loadAudit();
    }, POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  // --- View helpers ---

  protected statusBadgeClass(status: string): string {
    return STATUS_BADGE_CLASSES[status] ?? STATUS_BADGE_FALLBACK;
  }

  protected auditLabel(action: string): string {
    return AUDIT_ACTION_LABELS[action] ?? action;
  }

  /** Human-readable file size (e.g. 184320 → "180 KB"). */
  protected formatSize(bytes: number): string {
    if (bytes <= 0) {
      return '0 B';
    }
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    const value = bytes / Math.pow(1024, exponent);
    const rounded = exponent === 0 ? value : Math.round(value * 10) / 10;
    return `${rounded} ${units[exponent]}`;
  }

  protected formatCharCount(count: number): string {
    return count.toLocaleString();
  }

  protected categoryBadgeClass(category: string): string {
    return CATEGORY_BADGE_CLASSES[category] ?? CATEGORY_BADGE_FALLBACK;
  }

  /** 0..1 confidence → integer percentage clamped to [0,100]. */
  protected confidencePercent(confidence: number): number {
    if (!Number.isFinite(confidence)) {
      return 0;
    }
    return Math.max(0, Math.min(100, Math.round(confidence * 100)));
  }

  /** Turn a JSON key (e.g. `invoiceNumber`, `due_date`) into a readable label. */
  private humanizeKey(key: string): string {
    const spaced = key
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .trim();
    if (spaced.length === 0) {
      return key;
    }
    return spaced.charAt(0).toUpperCase() + spaced.slice(1);
  }

  /** Render a schemaless metadata value as a readable string (handles nested/array/null). */
  private formatMetaValue(value: unknown): string {
    if (value === null || value === undefined) {
      return '—';
    }
    if (typeof value === 'string') {
      return value.length === 0 ? '—' : value;
    }
    if (typeof value === 'number' || typeof value === 'boolean') {
      return String(value);
    }
    // Nested object / array — show compact JSON so no field is silently dropped.
    try {
      return JSON.stringify(value);
    } catch {
      return String(value);
    }
  }
}
