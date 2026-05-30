/**
 * Client-side pre-validation that MIRRORS the server allow-list/size rules
 * (ADR §3.1). This is UX-only — the backend remains the source of truth and
 * re-validates every file. We surface obvious rejections early so users do not
 * wait for a round-trip to learn a 30 MB `.exe` was never going to be accepted.
 */

/** Maximum file size accepted by the server: 25 MB. */
export const MAX_FILE_BYTES = 25 * 1024 * 1024;

/** Allow-listed extensions (lower-case, including the dot). */
export const ALLOWED_EXTENSIONS = ['.pdf', '.doc', '.docx', '.txt'] as const;

/** Human-readable allow-list for the file-picker `accept` attribute + hints. */
export const ACCEPT_ATTRIBUTE = ALLOWED_EXTENSIONS.join(',');

/** Returns the lower-cased extension (including dot), or '' if none. */
function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot >= 0 ? fileName.slice(dot).toLowerCase() : '';
}

/** Result of validating a single file before upload. */
export interface PreValidationResult {
  readonly valid: boolean;
  /** Present only when `valid` is false. */
  readonly error?: string;
}

/**
 * Validate a single file against the mirrored server rules. Extension-based
 * (MIME types are unreliable across browsers; the server checks both).
 */
export function preValidateFile(file: File): PreValidationResult {
  if (file.size === 0) {
    return { valid: false, error: 'File is empty (0 bytes).' };
  }
  if (file.size > MAX_FILE_BYTES) {
    return { valid: false, error: 'Exceeds the 25 MB limit.' };
  }
  const ext = extensionOf(file.name);
  if (!ALLOWED_EXTENSIONS.includes(ext as (typeof ALLOWED_EXTENSIONS)[number])) {
    return { valid: false, error: 'Unsupported file type (allowed: .pdf, .doc, .docx, .txt).' };
  }
  return { valid: true };
}

/** Format a byte count as a short human-readable string (e.g. "1.4 MB"). */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unitIndex]}`;
}
