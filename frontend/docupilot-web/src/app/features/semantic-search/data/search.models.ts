/**
 * TypeScript contracts for the Semantic Search slice.
 *
 * These mirror the FROZEN Phase-6 backend contract for `POST /api/search`
 * (Tech Lead ADR "## Phase 6 — Semantic Search" §2 / backend DA-045). Field
 * names are camelCase on the wire and 1:1 with the Contracts DTOs
 * (`SearchRequest` / `SearchResponse` / `SearchResult`). The result rows are
 * document-level: one row per document (the best-matching chunk wins), ranked
 * by `score` descending.
 */

/**
 * Request body for `POST /api/search`. Only `query` is required; `limit` and
 * `category` are optional (the minimal page sends just `query`).
 */
export interface SearchRequest {
  /** Natural-language search text. Empty/whitespace is rejected client-side and server-side (400). */
  readonly query: string;
  /** Max documents to return; server defaults to 10 and clamps to [1, 50] when omitted. */
  readonly limit?: number;
  /** Optional exact-match filter on the classification display string (e.g. "Contract"). */
  readonly category?: string;
}

/**
 * One ranked search result — a single document (the best-matching chunk per
 * document, collapsed). Mirrors spec §5.8 verbatim plus the additive
 * `chunkIndex`.
 */
export interface SearchResult {
  /** Source document id (UUIDv7) — links to `/documents/:documentId`. */
  readonly documentId: string;
  /** Original uploaded file name. */
  readonly fileName: string;
  /** Classification display string (e.g. "Contract"); `null` if unclassified. */
  readonly classification: string | null;
  /** Qdrant cosine similarity of the best-matching chunk (~0..1; higher = more relevant). */
  readonly score: number;
  /** The matched passage (authoritative chunk text, server-trimmed to ~300 chars). */
  readonly matchedText: string;
  /** Index of the matched chunk within the document (additive; for link/debug). */
  readonly chunkIndex: number;
}

/** `200 OK` body for `POST /api/search`. `results` is `[]` when nothing matched (NOT a 404). */
export interface SearchResponse {
  /** Echo of the submitted query. */
  readonly query: string;
  /** Ranked results, score-descending, one row per document. */
  readonly results: readonly SearchResult[];
}
