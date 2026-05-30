import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DestroyRef } from '@angular/core';

import { PageShell } from '@shared/ui/page-shell/page-shell';

import { DocumentLibraryClient } from '../../data/document-library.client';
import { DocumentListItem } from '../../data/document.models';

/** Full literal Tailwind class strings per status (no runtime concatenation — DA-011 §6.6). */
const STATUS_BADGE_CLASSES: Readonly<Record<string, string>> = {
  Uploaded: 'bg-sky-100 text-sky-800 ring-sky-600/20',
  Queued: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ExtractingText: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  Classifying: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  GeneratingEmbeddings: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  ReadyForSearch: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
  Failed: 'bg-red-100 text-red-800 ring-red-600/20',
};
const STATUS_BADGE_FALLBACK = 'bg-slate-100 text-slate-700 ring-slate-500/20';

const PAGE_SIZE = 20;

@Component({
  selector: 'app-document-library-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink, DatePipe],
  templateUrl: './document-library-page.html',
})
export class DocumentLibraryPage {
  private readonly client = inject(DocumentLibraryClient);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly loading = signal<boolean>(true);
  protected readonly error = signal<string | null>(null);

  protected readonly page = signal<number>(1);
  protected readonly pageSize = signal<number>(PAGE_SIZE);
  protected readonly totalCount = signal<number>(0);
  protected readonly totalPages = signal<number>(0);

  /** All items for the current page (unfiltered). */
  private readonly items = signal<readonly DocumentListItem[]>([]);

  /** Client-side filter text (Phase 2: filters the current page only). */
  protected readonly filter = signal<string>('');

  /** Items after applying the client-side filter to the current page. */
  protected readonly filteredItems = computed<readonly DocumentListItem[]>(() => {
    const term = this.filter().trim().toLowerCase();
    const all = this.items();
    if (!term) {
      return all;
    }
    return all.filter((d) => d.fileName.toLowerCase().includes(term));
  });

  /** True only when the API succeeded and returned zero documents overall. */
  protected readonly isEmpty = computed<boolean>(
    () => !this.loading() && !this.error() && this.totalCount() === 0,
  );

  protected readonly canPrev = computed<boolean>(() => this.page() > 1);
  protected readonly canNext = computed<boolean>(() => this.page() < this.totalPages());

  constructor() {
    this.load();
  }

  /** (Re)load the current page from the API. */
  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.client
      .list(this.page(), this.pageSize())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.items.set(result.items);
          this.totalCount.set(result.totalCount);
          this.totalPages.set(result.totalPages);
          this.loading.set(false);
        },
        error: () => {
          this.items.set([]);
          this.error.set(
            'Could not load documents. The API may be unreachable — please try again.',
          );
          this.loading.set(false);
        },
      });
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

  protected onFilterInput(value: string): void {
    this.filter.set(value);
  }

  /** Human-readable file size from a byte count (e.g. 184320 → "180 KB"). */
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

  /** Full literal Tailwind badge classes for a given status string. */
  protected statusBadgeClass(status: string): string {
    return STATUS_BADGE_CLASSES[status] ?? STATUS_BADGE_FALLBACK;
  }
}
