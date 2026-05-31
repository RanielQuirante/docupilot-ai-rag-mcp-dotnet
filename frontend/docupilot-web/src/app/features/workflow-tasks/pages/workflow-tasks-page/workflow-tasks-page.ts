import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe, LowerCasePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { PageShell } from '@shared/ui/page-shell/page-shell';

import { WorkflowTasksClient } from '../../data/workflow-tasks.client';
import { WorkflowTask, WorkflowTaskStatusFilter } from '../../data/workflow-tasks.models';

/**
 * The page's lifecycle of states (mirrors `semantic-search`'s
 * loading/error/empty split, adapted for a list-on-load surface):
 *  - `loading`     — the list request is in flight (initial load / filter change).
 *  - `tasks`       — 200 with ≥1 task.
 *  - `empty`       — 200 with `[]` (no workflow tasks yet — NOT an error).
 *  - `unavailable` — 503 (a dependent service starting or down — retryable).
 *  - `error`       — generic transport/500 failure.
 */
type TasksState = 'loading' | 'tasks' | 'empty' | 'unavailable' | 'error';

/** The status-filter options surfaced as a segmented control. */
type FilterOption = 'All' | 'Open' | 'Completed';

/**
 * Full literal Tailwind class strings per priority chip — no runtime
 * concatenation, so Tailwind's Oxide engine never purges them (DA-011 §6.6 /
 * ADR §5: literal strings; duplicated, not shared, to keep the map a literal).
 * An unknown/off-enum priority falls back to a neutral slate chip.
 */
const PRIORITY_CHIP_CLASSES: Readonly<Record<string, string>> = {
  Low: 'bg-slate-100 text-slate-700 ring-slate-500/20',
  Normal: 'bg-sky-100 text-sky-800 ring-sky-600/20',
  High: 'bg-rose-100 text-rose-800 ring-rose-600/20',
};
const PRIORITY_CHIP_FALLBACK = 'bg-slate-100 text-slate-600 ring-slate-500/20';

/**
 * Full literal Tailwind class strings per status badge. `Open` is actionable
 * (amber), `Completed` is done (emerald).
 */
const STATUS_BADGE_CLASSES: Readonly<Record<string, string>> = {
  Open: 'bg-amber-100 text-amber-800 ring-amber-600/20',
  Completed: 'bg-emerald-100 text-emerald-800 ring-emerald-600/20',
};
const STATUS_BADGE_FALLBACK = 'bg-slate-100 text-slate-700 ring-slate-500/20';

@Component({
  selector: 'app-workflow-tasks-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink, DatePipe, LowerCasePipe],
  templateUrl: './workflow-tasks-page.html',
})
export class WorkflowTasksPage {
  private readonly client = inject(WorkflowTasksClient);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly state = signal<TasksState>('loading');
  protected readonly tasks = signal<readonly WorkflowTask[]>([]);

  /** The active status filter (segmented control). Default = All. */
  protected readonly filter = signal<FilterOption>('All');

  protected readonly filterOptions: readonly FilterOption[] = ['All', 'Open', 'Completed'];

  /** Set of task ids whose Complete request is currently in flight (disables that row's button). */
  protected readonly completing = signal<ReadonlySet<string>>(new Set());

  /**
   * A transient, non-blocking message shown after a Complete attempt that the
   * list reconciled (e.g. the task was already completed elsewhere). Cleared on
   * the next filter change / refetch.
   */
  protected readonly notice = signal<string | null>(null);

  protected readonly isLoading = computed<boolean>(() => this.state() === 'loading');

  constructor() {
    this.load();
  }

  /** Map the active filter option to the contract's optional `status` param. */
  private statusParam(): WorkflowTaskStatusFilter {
    const f = this.filter();
    return f === 'All' ? undefined : f;
  }

  /** (Re)load the task list for the active filter. */
  protected load(): void {
    this.state.set('loading');
    this.client
      .listTasks(this.statusParam())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome) => {
          switch (outcome.kind) {
            case 'tasks':
              this.tasks.set(outcome.tasks);
              this.state.set(outcome.tasks.length > 0 ? 'tasks' : 'empty');
              break;
            case 'unavailable':
              this.tasks.set([]);
              this.state.set('unavailable');
              break;
            case 'error':
              this.tasks.set([]);
              this.state.set('error');
              break;
          }
        },
        error: () => {
          this.tasks.set([]);
          this.state.set('error');
        },
      });
  }

  /** Change the status filter and refetch. */
  protected setFilter(option: FilterOption): void {
    if (this.filter() === option) {
      return;
    }
    this.filter.set(option);
    this.notice.set(null);
    this.load();
  }

  /** True while the given task's Complete request is in flight. */
  protected isCompleting(id: string): boolean {
    return this.completing().has(id);
  }

  /**
   * Complete an Open task. On success, optimistically flip the row to Completed
   * (and re-evaluate the list state); on 409/404, refetch to reconcile (the task
   * was already completed/removed elsewhere). 503/error surface a transient
   * notice and refetch.
   */
  protected complete(task: WorkflowTask): void {
    if (task.status !== 'Open' || this.isCompleting(task.id)) {
      return;
    }
    this.markCompleting(task.id, true);
    this.notice.set(null);

    this.client
      .completeTask(task.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (outcome) => {
          this.markCompleting(task.id, false);
          switch (outcome.kind) {
            case 'completed':
              this.applyCompleted(outcome.task);
              break;
            case 'alreadyCompleted':
              this.notice.set('That task was already completed. The list has been refreshed.');
              this.load();
              break;
            case 'notFound':
              this.notice.set('That task no longer exists. The list has been refreshed.');
              this.load();
              break;
            case 'unavailable':
              this.notice.set('The service is temporarily unavailable — please try again.');
              break;
            case 'error':
              this.notice.set('Could not complete the task. Please try again.');
              break;
          }
        },
        error: () => {
          this.markCompleting(task.id, false);
          this.notice.set('Could not complete the task. Please try again.');
        },
      });
  }

  /**
   * Replace a task in the list with its completed version, then re-derive the
   * displayed state. If the active filter is `Open`, the now-Completed task
   * drops out of view (it no longer matches), so we filter it locally to keep
   * the list consistent without a round-trip.
   */
  private applyCompleted(updated: WorkflowTask): void {
    const filter = this.filter();
    let next = this.tasks().map((t) => (t.id === updated.id ? updated : t));
    if (filter === 'Open') {
      next = next.filter((t) => t.status === 'Open');
    }
    this.tasks.set(next);
    this.state.set(next.length > 0 ? 'tasks' : 'empty');
  }

  private markCompleting(id: string, value: boolean): void {
    const next = new Set(this.completing());
    if (value) {
      next.add(id);
    } else {
      next.delete(id);
    }
    this.completing.set(next);
  }

  // --- View helpers ---

  protected priorityChipClass(priority: string): string {
    return PRIORITY_CHIP_CLASSES[priority] ?? PRIORITY_CHIP_FALLBACK;
  }

  protected statusBadgeClass(status: string): string {
    return STATUS_BADGE_CLASSES[status] ?? STATUS_BADGE_FALLBACK;
  }
}
