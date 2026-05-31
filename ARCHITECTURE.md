# DocuPilot AI — Architecture

A concise, honest overview of how DocuPilot AI is built: the services, the
backend/frontend patterns, the document pipeline, the RAG flow, the audited
MCP-style tool layer, and the dual-store data model. It reflects what was
actually built across Phases 1–9 (not aspirational design).

> **What this is.** A private, AI-powered document-management proof of concept:
> upload business documents → a private LLM classifies them, extracts metadata,
> chunks + embeds them for semantic search and document-grounded (RAG) Q&A, and
> an audited tool layer turns AI recommendations into validated workflow tasks.
> Authentication, multi-tenancy, and production hardening are intentionally out
> of scope for the POC.

---

## 1. System overview — the six Docker services

The whole stack runs via `docker compose` on a single bridge network; services
address each other by service name (e.g. `http://docupilot-api:8080`).

```
                          Browser (http://localhost:4210)
                                     │
                                     ▼
                        ┌──────────────────────────┐
                        │  docupilot-web (NGINX)    │  serves the Angular SPA
                        │  - static SPA + index.html│  + reverse-proxies /api/*
                        └──────────────┬────────────┘
                                       │ /api/*  →  :8080
                                       ▼
        ┌────────────────────────────────────────────────────────┐
        │  docupilot-api (.NET 10 Web API)                         │
        │  controllers → services → repositories → EF Core         │
        │  search / ask (RAG) / workflow + tools / dashboard / audit│
        └───┬───────────────┬───────────────┬───────────────┬─────┘
            │               │               │               │
   ┌────────▼──────┐  ┌─────▼──────┐  ┌─────▼──────┐  ┌─────▼──────────┐
   │  sqlserver    │  │  qdrant    │  │  ollama    │  │  (shared files │
   │  (SQL 2022)   │  │  (vectors) │  │  (LLM +    │  │   volume)      │
   │  authoritative│  │            │  │   embed)   │  │                │
   └───────▲───────┘  └─────▲──────┘  └─────▲──────┘  └─────▲──────────┘
           │                │               │               │
        ┌──┴────────────────┴───────────────┴───────────────┴──┐
        │  docupilot-worker (.NET 10 Generic Host, no HTTP)     │
        │  polls SQL for queued docs and runs the pipeline:     │
        │  extract → classify → metadata → chunk → embed → ready│
        └───────────────────────────────────────────────────────┘
```

| Service | Image / stack | Role |
| --- | --- | --- |
| **docupilot-web** | Angular 21 SPA built, served by NGINX | Serves the SPA; reverse-proxies `/api/*` to the API so SPA + API share an origin. |
| **docupilot-api** | .NET 10 Web API | All HTTP endpoints: upload, library, detail, search, ask (RAG), workflow recommend + tools, dashboard stats, audit logs, `/health`, Swagger. |
| **docupilot-worker** | .NET 10 Generic Host (no HTTP) | The background document pipeline — polls SQL for work and drives each document through the processing state machine. |
| **sqlserver** | SQL Server 2022 (Linux) | The **authoritative** relational store: documents + classifications + metadata + chunks + workflow tasks + audit logs. |
| **qdrant** | Qdrant | The vector store: one `document_chunks` collection of chunk embeddings for semantic search + RAG retrieval. |
| **ollama** | Ollama runtime | The private LLM runtime: the chat model `llama3.2:3b` (classification / metadata / RAG answers / workflow recommendation) and the embedding model `nomic-embed-text` (768-dim). Both auto-pulled on first boot. |

Two one-shot helpers round out the compose file: `docupilot-files-init` (chowns
the shared uploads volume for the non-root app user) and `ollama-model-init`
(pulls both models before the API/Worker start). Stateful services use named
volumes that survive `down` but are wiped by `down -v`.

> **Port/adapter seams (config-swap, no code change).** The external services
> sit behind ports declared in the Services layer — `ILlmClient`,
> `IEmbeddingClient`, `IVectorStore`, `IFileStorage`. The Ollama clients speak
> the Ollama-native API but are configured for an OpenAI/vLLM-compatible style
> via `Llm__ApiStyle` / `Embedding__ApiStyle`, so moving to vLLM or a larger
> model (or MinIO for storage) is a configuration change, not a rewrite.

---

## 2. Backend — Repository Pattern (.NET 10)

Lean controllers, business logic in services, **all** database access isolated
behind repository interfaces. The reference direction is inward-only and acyclic:

```
  Api  ┐
       ├──►  Services  ──►  Repository  ──►  Models
  Worker┘        ▲
                 │ implements the external-service ports (ILlmClient,
                 │ IEmbeddingClient, IVectorStore, IFileStorage)
          Infrastructure  (EF Core DbContext, Qdrant/Ollama/file-storage clients)
```

- **DocuPilot.Api** — controllers (thin: bind → call a service → map to a DTO) + the composition root (DI, Serilog, CORS, Swagger, `/health`, startup DB migration).
- **DocuPilot.Services** — business logic + the external-service **port interfaces**. Services orchestrate; they never touch EF or HTTP directly.
- **DocuPilot.Repository** — repository interfaces + EF Core implementations; the ONLY place that issues queries.
- **DocuPilot.Models** — entities, DTO contracts, enums; zero dependencies.
- **DocuPilot.Infrastructure** — the EF Core `DbContext` + the concrete adapters that implement the Services ports (Qdrant, Ollama chat/embed, local file storage).
- **DocuPilot.Worker** — a Generic Host that owns the polling pipeline; shares the same Services/Repository/Infrastructure layers as the API.

This keeps controllers testable, swaps of an external service to a single class,
and the data layer behind a seam the rest of the app can't bypass.

## 3. Frontend — Vertical Slice Architecture (Angular 21)

Each of the eight product pages is a **self-contained slice** under `features/`
(its own route, page component, child components, and a slice-scoped data
client), lazy-loaded as an independent chunk. App-wide chrome (layout shell,
sidebar nav, health widget) lives in `core/`; reusable presentational primitives
in `shared/`.

| Route | Slice | Purpose |
| --- | --- | --- |
| `/dashboard` | dashboard | At-a-glance counts + classification breakdown. |
| `/upload` | document-upload | Drag-and-drop multi-file upload + progress. |
| `/library` | document-library | Table of documents (status, classification, date, filter). |
| `/documents/:id` | document-detail | Per-doc classification, metadata, chunks, related tasks. |
| `/search` | semantic-search | NL query → ranked docs + scores + matched text. |
| `/ask` | ask-ai | RAG Q&A with citations + a no-hallucination warning. |
| `/tasks` | workflow-tasks | AI-recommended tasks (team, priority, status, complete). |
| `/audit` | audit-logs | Newest-first timeline of every document/AI/tool/workflow event. |

The data client for each slice is registered in the route's `providers: []` (NOT
`providedIn: 'root'`), so it lives and dies with the slice — the vertical-slice
convention. Each client returns discriminated outcome types so the page can
render distinct loading / empty / unavailable / error states.

> **NGINX caching note (Phase-9 fix).** Hashed JS/CSS bundles are served
> `immutable` with a 1-year cache; `index.html` is served `no-cache` (revalidate
> every load) via an exact `location = /index.html` block, so a rebuilt SPA is
> picked up immediately and a stale entry document never points at deleted
> hashed bundles (the ChunkLoadError trap). The SPA deep-link fallback still
> serves `index.html` for client-side routes.

---

## 4. The document processing pipeline (the Worker state machine)

Upload only persists the file + a `Queued` row and returns immediately. The
Worker polls SQL on an interval, claims queued/eligible documents, and advances
each through the state machine in stages, persisting status after each stage so
the pipeline is resumable and observable:

```
  Queued
    └─► ExtractingText ─► TextExtracted
          └─► Classifying ─► Classified            (+ metadata extraction)
                └─► GeneratingEmbeddings ─► ReadyForSearch
                                                  
  any stage may transition ─────────────────────► Failed   (recorded + audited)
```

1. **Extract text** — read the stored file (`.txt` / `.pdf` / `.docx`) into plain text.
2. **Classify + extract metadata** — one private-LLM call classifies the document (with a confidence score), another extracts structured metadata (vendor, invoice number, contract expiry, department, …) as JSON.
3. **Chunk + embed + index** — split the text into overlapping chunks, embed each with `nomic-embed-text` (768-dim), **upsert the vectors into Qdrant first, then persist the chunk rows in SQL**, and mark the document `ReadyForSearch`.

The Worker has no HTTP surface; it self-heals failed claims via a stale-claim
sweep and a per-tick try/catch, and (Phase-9) gates its first poll tick on the
API having applied migrations so boot logs stay clean.

---

## 5. Semantic search & the RAG flow

Both are **read-only** API endpoints over the vectors the pipeline already
produced — they add no new model, image, or store.

- **Semantic search (`POST /api/search`)** — embed the NL query with the same
  embedding model → cosine search over Qdrant → map the top chunks back to their
  documents → return documents ranked by score with matched-text snippets. It
  finds meaning, not keywords ("about to expire" matches "valid until").

- **RAG question-answering (`POST /api/ask`)**:

  ```
  question ─► embed ─► Qdrant top-k chunks ─► assemble grounded context
            ─► LLM answers ONLY from that context ─► answer + ranked citations
  ```

  Grounding is enforced two ways: a **pre-LLM relevance floor** (`Rag__MinScore`)
  short-circuits to a canned "I could not find enough information…" answer when
  no chunk is relevant enough (no LLM call, no fabrication), and the prompt
  constrains the model to the retrieved context. Every answer carries
  **citations** back to the source documents so a user can verify the claim.

---

## 6. The MCP-style audited tool layer (the safety story)

AI recommendations are advisory; **the AI never writes to the database
directly.** The only mutation path is a controlled, validated, audited tool.

```
  POST /api/workflows/recommend   → LLM proposes {taskType, team, priority, reason}
                                    (a SUGGESTION — writes nothing)

  create_workflow_task tool       → validate inputs → execute → ALWAYS audit
        ▲                            (ToolInvoked → ToolSucceeded | ToolFailed)
        │
  POST /api/workflow-tasks         (the tool's HTTP surface)
  POST /api/agent/recommend-and-create  (the fixed recommend → create pipeline)
  GET  /api/tools                  (introspect the tool catalogue)
```

A tool registry/dispatcher validates every call before execution; **invalid
calls are rejected with no database write** and an audit record of the failure.
Whether a call succeeds or fails, an `AuditLog` row is written — so the
`/audit` page shows the complete, tamper-evident trail of what the AI did. This
is the deterministic, always-impressive part of the demo: the AI proposes, the
validated tool disposes, and everything is audited.

---

## 7. Data model — SQL (authoritative) + Qdrant (vectors)

A **dual store**: SQL Server is the system of record for all relational data;
Qdrant holds the chunk embeddings for vector search. On the embed pass the
vector is written to **Qdrant first, then** the chunk row to SQL, so a SQL chunk
row always has a backing vector.

```
  Documents (Id, FileName, ContentType, FilePath, Status, UploadedAt, ProcessedAt)
     ├─1:N─► DocumentChunks          (ChunkIndex, Content, TokenEstimate)   ──┐
     ├─1:N─► DocumentClassifications (Classification, Confidence, Reason)     │ each chunk
     ├─1:1─► ExtractedMetadata       (MetadataJson)                          │ has a vector
     └─1:N─► WorkflowTasks           (TaskType, AssignedTeam, Priority,       │ in Qdrant's
                                      Status, Reason, CompletedAt)            │ document_chunks
                                                                              ▼ collection
  AuditLogs (EntityName, EntityId, Action, DetailsJson, CreatedAt)   Qdrant: vector + payload
     - FK-LESS by design: EntityId is a plain indexed GUID, not a foreign
       key, so an audit record outlives the entity it describes and can
       reference any entity type (Document / WorkflowTask / tool call).
```

The child tables cascade from `Documents`; `AuditLogs` is deliberately
**not** an FK child (it must survive and reference any entity). The schema is
created by the API's startup migration (no separate DB-init step).

---

## 8. Known limitations & scale-up notes (honest POC trade-offs)

These are deliberate POC choices, documented as the upgrade path — not bugs.

- **CPU-only LLM latency.** All inference runs on CPU; classification and RAG
  answers take seconds to tens of seconds. Mitigation/upgrade: a GPU host and/or
  a vLLM backend (`Llm__ApiStyle`).
- **Small chat model phrasing sensitivity.** `llama3.2:3b` is small, so RAG /
  recommendation phrasing varies run-to-run. Upgrade path: set `LLM_MODEL` to a
  larger model (zero code change — it's one config value).
- **Search relevance floor.** `Search__MinScore` defaults to `0` (maximum recall
  on a small seed corpus, so search never returns empty). For a larger corpus,
  raise the floor to suppress weak off-topic matches. (RAG already enforces its
  own `Rag__MinScore` grounding floor.)
- **Scanned/image PDFs.** Text extraction reads embedded text; image-only
  (scanned) PDFs would need OCR (a future extractor — the extractor seam is a
  one-class addition).
- **Global audit-log sort.** The unfiltered newest-first audit query is a
  scan+sort (the composite index leads with `EntityId`); fine at POC scale. A
  dedicated `IX_AuditLogs_CreatedAt` index is the scale-up fix when volume grows.
- **No auth / multi-tenancy / production hardening.** Out of scope for the POC.

---

See **README.md** for setup and run instructions and **DEMO.md** for the
end-to-end demo walkthrough.
