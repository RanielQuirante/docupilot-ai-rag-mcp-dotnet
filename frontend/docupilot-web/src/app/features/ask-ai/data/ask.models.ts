/**
 * TypeScript contracts for the Ask AI (RAG Q&A) slice.
 *
 * These mirror the FROZEN Phase-7 backend contract for `POST /api/ask`
 * (Tech Lead ADR "## Phase 7 — RAG Question Answering" §2 / backend DA-049).
 * Field names are camelCase on the wire and 1:1 with the Contracts DTOs
 * (`AskRequest` / `AskResponse` / `Citation`).
 *
 * Each ask is a stateless single-turn request: the answer is grounded ONLY in
 * the chunks retrieved for THIS question (no server-side conversation state).
 */

/**
 * Request body for `POST /api/ask`. Only `question` is required; `topK` and
 * `category` are optional (the minimal page sends just `question`).
 */
export interface AskRequest {
  /** Natural-language question. Empty/whitespace is rejected client-side and server-side (400). */
  readonly question: string;
  /** How many chunks to retrieve as context; server defaults to 6 and clamps to [1, 12] when omitted. */
  readonly topK?: number;
  /** Optional exact-match filter on the classification display string (e.g. "Contract"). */
  readonly category?: string;
}

/**
 * One source citation — a retrieved chunk that grounded the answer. Mirrors
 * spec §5.10 (Document / Page / Chunk). `page` is ALWAYS `null` in the POC
 * (chunks don't persist page numbers — ADR §2/§3), kept in the shape for
 * spec-fidelity; the UI renders "Chunk N" as the locator.
 */
export interface Citation {
  /** Source document id (UUIDv7) — links to `/documents/:documentId`. */
  readonly documentId: string;
  /** Original uploaded file name. */
  readonly fileName: string;
  /** 0-based index of the cited chunk within the document. */
  readonly chunkIndex: number;
  /** Always `null` in the POC (no persisted page numbers); documented N/A. */
  readonly page: number | null;
  /** Qdrant cosine relevance of the chunk (~0..1; higher = more relevant). */
  readonly score: number;
  /** The relevant text passage (authoritative chunk content, server-trimmed). */
  readonly snippet: string;
}

/**
 * `200 OK` body for `POST /api/ask` — for BOTH a grounded answer AND the
 * not-found case (a successful ask that found nothing is a 200 with
 * `answerFound: false` + the canned string + empty `citations`, NOT a 404).
 */
export interface AskResponse {
  /** Echo of the submitted question. */
  readonly question: string;
  /**
   * The LLM-generated grounded answer (prose), OR — when `answerFound` is
   * false — the canned "I could not find enough information in the uploaded
   * documents." string.
   */
  readonly answer: string;
  /** `false` ⇒ the not-found path (canned answer, empty citations); `true` ⇒ a real grounded answer. */
  readonly answerFound: boolean;
  /** The retrieved chunks the answer was grounded in (empty when `answerFound` is false). */
  readonly citations: readonly Citation[];
}
