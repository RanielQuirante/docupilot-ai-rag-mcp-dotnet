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

import { AskAiClient } from '../../data/ask-ai.client';
import { Citation } from '../../data/ask.models';

/**
 * The page's lifecycle of states (mirrors `semantic-search`'s
 * idle/loading/unavailable/error split, with `answered` + `notFound` replacing
 * the search slice's results/empty — the RAG-specific outcomes):
 *  - `idle`        — before the first question (friendly chat-style prompt).
 *  - `loading`     — request in flight. The CPU chat LLM is SLOW (tens of
 *                    seconds); the UI shows a persistent "Thinking…" affordance.
 *  - `answered`    — 200 with `answerFound: true` (a real grounded answer +
 *                    citations).
 *  - `notFound`    — 200 with `answerFound: false` (the canned not-found string,
 *                    no citations) — surfaced as a distinct WARNING (the key
 *                    "AI found nothing grounded" trust signal, spec §11.6).
 *  - `unavailable` — 503 (embedder/Qdrant/LLM starting or down — retryable).
 *  - `error`       — generic transport/500 failure.
 */
type AskState = 'idle' | 'loading' | 'answered' | 'notFound' | 'unavailable' | 'error';

/**
 * One completed Q&A turn in the client-side session transcript. Pure display —
 * each new question still hits the API as an independent, context-free request
 * (stateless single-turn; ADR §1). The transcript is in-memory only and is
 * cleared on reload.
 */
interface Turn {
  readonly id: number;
  readonly question: string;
  /** The grounded answer prose, or the canned not-found string. */
  readonly answer: string;
  /** `false` ⇒ render this turn as the not-found warning (no citations). */
  readonly answerFound: boolean;
  readonly citations: readonly Citation[];
}

/**
 * The spec §5.3 classification taxonomy, surfaced as an optional scope filter
 * (only ask over a given category of docs). Empty value = no filter (all docs).
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
  selector: 'app-ask-ai-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink],
  templateUrl: './ask-ai-page.html',
})
export class AskAiPage {
  private readonly client = inject(AskAiClient);
  private readonly destroyRef = inject(DestroyRef);

  /** Bound input text (the NL question). */
  protected readonly question = signal<string>('');

  /** Optional category scope (empty = all documents). */
  protected readonly category = signal<string>('');

  /** The question of the in-flight / latest ask (shown while "Thinking…"). */
  protected readonly pendingQuestion = signal<string>('');

  protected readonly state = signal<AskState>('idle');

  /** Client-side session transcript of completed Q&A turns (oldest → newest). */
  protected readonly transcript = signal<readonly Turn[]>([]);

  protected readonly categories = CATEGORIES;

  private nextTurnId = 1;

  /** True while a request is in flight (disables the form). */
  protected readonly isLoading = computed<boolean>(() => this.state() === 'loading');

  /** The Ask button is disabled for a blank question or while loading. */
  protected readonly canAsk = computed<boolean>(
    () => this.question().trim().length > 0 && !this.isLoading(),
  );

  protected onQuestionInput(value: string): void {
    this.question.set(value);
  }

  protected onCategoryChange(value: string): void {
    this.category.set(value);
  }

  /**
   * Submit on Enter (without Shift). Shift+Enter inserts a newline in the
   * textarea, so we only intercept a bare Enter.
   */
  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.ask();
    }
  }

  /**
   * Ask the question. Blank question is guarded client-side (no request, no
   * 400). Sets `loading`, then maps the discriminated outcome to the final
   * state — `answer` splits into `answered` / `notFound` on `answerFound`.
   */
  protected ask(): void {
    const q = this.question().trim();
    if (q.length === 0 || this.isLoading()) {
      return;
    }
    const cat = this.category().trim();

    this.pendingQuestion.set(q);
    this.state.set('loading');
    // Clear the input so the next question starts fresh while this one resolves.
    this.question.set('');

    this.client
      .ask(q, undefined, cat || undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome) => {
          switch (outcome.kind) {
            case 'answer': {
              const res = outcome.response;
              this.appendTurn({
                id: this.nextTurnId++,
                question: q,
                answer: res.answer,
                answerFound: res.answerFound,
                citations: res.answerFound ? res.citations : [],
              });
              this.state.set(res.answerFound ? 'answered' : 'notFound');
              break;
            }
            case 'unavailable':
              this.state.set('unavailable');
              break;
            case 'error':
              this.state.set('error');
              break;
          }
        },
        error: () => {
          this.state.set('error');
        },
      });
  }

  /** Retry the last question (used by the unavailable / error states). */
  protected retry(): void {
    const q = this.pendingQuestion().trim();
    if (q.length === 0 || this.isLoading()) {
      return;
    }
    this.question.set(q);
    this.ask();
  }

  private appendTurn(turn: Turn): void {
    this.transcript.update((turns) => [...turns, turn]);
  }

  /**
   * Format the Qdrant cosine score as a percentage (e.g. 0.89 → "89%").
   * Clamped to [0, 100] for display safety. Matches the search slice.
   */
  protected formatScore(score: number): string {
    const pct = Math.round(Math.max(0, Math.min(1, score)) * 100);
    return `${pct}%`;
  }
}
