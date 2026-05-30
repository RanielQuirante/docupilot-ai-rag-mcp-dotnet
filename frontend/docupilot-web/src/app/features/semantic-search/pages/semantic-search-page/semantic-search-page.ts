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

import { SemanticSearchClient } from '../../data/semantic-search.client';
import { SearchResult } from '../../data/search.models';

/**
 * The page's lifecycle of states (mirrors `document-library`'s
 * loading/error/empty split, extended with `idle` for the pre-search prompt and
 * `unavailable` for the contract's 503):
 *  - `idle`        — before the first search (friendly prompt).
 *  - `loading`     — request in flight (embedding the query takes a moment on CPU).
 *  - `results`     — 200 with ≥1 result.
 *  - `empty`       — 200 with `results: []` (no matches — NOT an error).
 *  - `unavailable` — 503 (embedder/Qdrant starting or down — retryable).
 *  - `error`       — generic transport/500 failure.
 */
type SearchState = 'idle' | 'loading' | 'results' | 'empty' | 'unavailable' | 'error';

/**
 * Full literal Tailwind class strings per category chip — copied from
 * `document-library` per the per-slice literal-class convention (DA-011 §6.6 /
 * ADR §5: literal strings so Tailwind Oxide can statically see every utility;
 * duplicated, not shared, to keep the map a literal). Keyed by the spec §5.3
 * display strings. An unknown category falls back to a neutral slate chip.
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

/**
 * The spec §5.3 classification taxonomy, surfaced as an optional filter
 * dropdown. Empty value = no filter (search all categories).
 */
const CATEGORIES: readonly string[] = [
  'Contract',
  'Invoice',
  'Employee Record',
  'Legal Document',
  'Compliance Document',
  'Client Correspondence',
  'Policy Document',
];

@Component({
  selector: 'app-semantic-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink],
  templateUrl: './semantic-search-page.html',
})
export class SemanticSearchPage {
  private readonly client = inject(SemanticSearchClient);
  private readonly destroyRef = inject(DestroyRef);

  /** Two-way-ish bound input text (the NL query). */
  protected readonly query = signal<string>('');

  /** Optional category filter (empty = all categories). */
  protected readonly category = signal<string>('');

  /** The query string of the most recent successful/attempted search (for messaging). */
  protected readonly lastQuery = signal<string>('');

  protected readonly state = signal<SearchState>('idle');
  protected readonly results = signal<readonly SearchResult[]>([]);

  protected readonly categories = CATEGORIES;

  /** True while a request is in flight (disables the form). */
  protected readonly isLoading = computed<boolean>(() => this.state() === 'loading');

  /** The search button is disabled for a blank query or while loading. */
  protected readonly canSearch = computed<boolean>(
    () => this.query().trim().length > 0 && !this.isLoading(),
  );

  protected onQueryInput(value: string): void {
    this.query.set(value);
  }

  protected onCategoryChange(value: string): void {
    this.category.set(value);
  }

  /**
   * Run the search. Blank query is guarded client-side (no request, no 400).
   * Sets `loading`, then maps the discriminated outcome to the final state.
   */
  protected search(): void {
    const term = this.query().trim();
    if (term.length === 0 || this.isLoading()) {
      return;
    }
    const cat = this.category().trim();

    this.state.set('loading');
    this.lastQuery.set(term);

    this.client
      .search(term, cat || undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome) => {
          switch (outcome.kind) {
            case 'results':
              this.results.set(outcome.response.results);
              this.state.set(outcome.response.results.length > 0 ? 'results' : 'empty');
              break;
            case 'unavailable':
              this.results.set([]);
              this.state.set('unavailable');
              break;
            case 'error':
              this.results.set([]);
              this.state.set('error');
              break;
          }
        },
        error: () => {
          this.results.set([]);
          this.state.set('error');
        },
      });
  }

  /** Full literal Tailwind chip classes for a given category string. */
  protected categoryChipClass(category: string): string {
    return CATEGORY_CHIP_CLASSES[category] ?? CATEGORY_CHIP_FALLBACK;
  }

  /**
   * Format the Qdrant cosine score as a percentage (e.g. 0.89 → "89%").
   * Clamped to [0, 100] for display safety.
   */
  protected formatScore(score: number): string {
    const pct = Math.round(Math.max(0, Math.min(1, score)) * 100);
    return `${pct}%`;
  }
}
