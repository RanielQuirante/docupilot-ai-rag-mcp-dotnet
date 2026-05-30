import {
  ChangeDetectionStrategy,
  Component,
  inject,
  signal,
} from '@angular/core';
import { HttpErrorResponse, HttpEventType } from '@angular/common/http';

import { PageShell } from '@shared/ui/page-shell/page-shell';

import { DocumentUploadClient } from '../../data/document-upload.client';
import {
  FailedDocument,
  UploadDocumentResponse,
  UploadedDocument,
} from '../../data/document-upload.models';
import {
  ACCEPT_ATTRIBUTE,
  formatBytes,
  MAX_FILE_BYTES,
  preValidateFile,
} from '../../data/upload-validation';

/** A file the user has selected/dropped, plus its client-side validation verdict. */
interface SelectedFile {
  readonly file: File;
  readonly valid: boolean;
  readonly error?: string;
}

/** Where the page is in the request lifecycle. */
type UploadPhase = 'idle' | 'uploading' | 'done' | 'error';

@Component({
  selector: 'app-document-upload-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell],
  templateUrl: './document-upload-page.html',
})
export class DocumentUploadPage {
  private readonly client = inject(DocumentUploadClient);

  /** Exposed to the template for the picker `accept` + the size hint. */
  protected readonly acceptAttribute = ACCEPT_ATTRIBUTE;
  protected readonly maxFileBytes = MAX_FILE_BYTES;
  protected readonly formatBytes = formatBytes;

  /** Files currently staged for upload. */
  protected readonly selectedFiles = signal<readonly SelectedFile[]>([]);

  /** Drag-over visual state for the drop zone. */
  protected readonly isDragging = signal(false);

  /** Lifecycle phase + 0–100 batch progress. */
  protected readonly phase = signal<UploadPhase>('idle');
  protected readonly progress = signal(0);

  /** Results from the last completed upload. */
  protected readonly uploaded = signal<readonly UploadedDocument[]>([]);
  protected readonly failed = signal<readonly FailedDocument[]>([]);

  /** Network / transport-level error message (distinct from per-file `failed[]`). */
  protected readonly requestError = signal<string | null>(null);

  // --- file selection -------------------------------------------------------

  protected onFilesPicked(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files) {
      this.addFiles(input.files);
    }
    // Reset so re-picking the same file fires `change` again.
    input.value = '';
  }

  protected onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.isDragging.set(true);
  }

  protected onDragLeave(event: DragEvent): void {
    event.preventDefault();
    this.isDragging.set(false);
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.isDragging.set(false);
    if (event.dataTransfer?.files) {
      this.addFiles(event.dataTransfer.files);
    }
  }

  protected removeFile(index: number): void {
    this.selectedFiles.update((files) => files.filter((_, i) => i !== index));
  }

  protected clearSelection(): void {
    this.selectedFiles.set([]);
  }

  private addFiles(fileList: FileList): void {
    const incoming: SelectedFile[] = Array.from(fileList).map((file) => {
      const result = preValidateFile(file);
      return { file, valid: result.valid, error: result.error };
    });
    this.selectedFiles.update((existing) => [...existing, ...incoming]);
  }

  // --- upload ---------------------------------------------------------------

  /** Only client-valid files are eligible to send. */
  protected validFiles(): readonly SelectedFile[] {
    return this.selectedFiles().filter((f) => f.valid);
  }

  protected canUpload(): boolean {
    return this.phase() !== 'uploading' && this.validFiles().length > 0;
  }

  protected upload(): void {
    const eligible = this.validFiles().map((f) => f.file);
    if (eligible.length === 0) {
      return;
    }

    this.phase.set('uploading');
    this.progress.set(0);
    this.uploaded.set([]);
    this.failed.set([]);
    this.requestError.set(null);

    this.client.upload(eligible).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress) {
          const total = event.total ?? 0;
          this.progress.set(total > 0 ? Math.round((event.loaded / total) * 100) : 0);
        } else if (event.type === HttpEventType.Response) {
          const body = event.body as UploadDocumentResponse | null;
          this.applyResult(body);
        }
      },
      error: (err: HttpErrorResponse) => this.handleError(err),
    });
  }

  private applyResult(body: UploadDocumentResponse | null): void {
    this.progress.set(100);
    this.uploaded.set(body?.uploaded ?? []);
    this.failed.set(body?.failed ?? []);
    this.phase.set('done');
    // Clear staged files that succeeded would be ambiguous on names; clear all
    // since the authoritative result is now shown below.
    this.selectedFiles.set([]);
  }

  private handleError(err: HttpErrorResponse): void {
    this.phase.set('error');
    this.progress.set(0);

    // The contract returns a `{ uploaded, failed }` body even on 400 — surface
    // per-file errors when present, otherwise a generic transport message.
    const body = err.error as UploadDocumentResponse | undefined;
    if (body && Array.isArray(body.failed)) {
      this.uploaded.set(body.uploaded ?? []);
      this.failed.set(body.failed);
      return;
    }

    this.requestError.set(this.describeError(err));
  }

  private describeError(err: HttpErrorResponse): string {
    if (err.status === 0) {
      return 'Could not reach the server. Check your connection and try again.';
    }
    if (err.status === 413) {
      return 'The upload was too large for the server to accept.';
    }
    if (err.status >= 500) {
      return `The server encountered an error (HTTP ${err.status}). Please try again.`;
    }
    return `Upload failed (HTTP ${err.status}). Please try again.`;
  }

  protected reset(): void {
    this.phase.set('idle');
    this.progress.set(0);
    this.uploaded.set([]);
    this.failed.set([]);
    this.requestError.set(null);
  }
}
