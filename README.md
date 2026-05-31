# DocuPilot AI

![.NET](https://img.shields.io/badge/.NET-10-blue)
![Angular](https://img.shields.io/badge/Angular-Frontend-red)
![Docker](https://img.shields.io/badge/Docker-Containerized-blue)
![RAG](https://img.shields.io/badge/RAG-Semantic_Search-green)
![Vector DB](https://img.shields.io/badge/Vector_DB-Qdrant-purple)
![LLM](https://img.shields.io/badge/LLM-Ollama%20%7C%20vLLM-orange)

DocuPilot AI is a private AI-powered document management proof of concept built with .NET, Angular, SQL, Docker, vector embeddings, RAG pipelines, and MCP-style tool orchestration.

The project demonstrates how enterprise SaaS products can use private LLM infrastructure such as Ollama or vLLM-compatible APIs to classify documents, extract metadata, perform semantic search, answer document-grounded questions, and automate workflow recommendations.

This POC is designed for document management, records management, legal operations, HR records, compliance files, invoices, and enterprise content management use cases.

---

## Prerequisites

| Tool | Notes |
| --- | --- |
| **Docker Desktop** | The whole stack runs in containers. Engine 24+ with Compose v2 (bundled). Start Docker Desktop and confirm `docker info` reports a running daemon before you begin. |
| **Git** | To clone the repository. |
| **~10 GB free disk** | First run pulls large base images (SQL Server 2022 ~1.5 GB, Ollama ~1.5 GB, .NET SDK ~1 GB, Qdrant + Node + NGINX). Docker Desktop stores these in its WSL2 data disk — make sure the drive backing that disk has the headroom. |

No local .NET SDK, Node, or Angular CLI is required to *run* the stack — everything builds inside Docker. (You only need those installed if you want to develop a service outside its container.)

---

## Quick Start

```bash
# 1. Clone
git clone https://github.com/RanielQuirante/docupilot-ai-rag-mcp-dotnet.git
cd docupilot-ai-rag-mcp-dotnet

# 2. Create your local env file from the template
copy .env.example .env      # Windows (PowerShell / cmd)
cp   .env.example .env      # macOS / Linux

# 3. Build and start the whole stack
docker compose up --build
```

> **First run is slow.** Docker pulls several GB of base images (notably SQL Server and Ollama). This is a one-time cost — subsequent `docker compose up` runs reuse the cached images and start in seconds.

Add `-d` (`docker compose up -d --build`) to run detached. Useful follow-ups:

```bash
docker compose ps        # see all six services and their state
docker compose logs -f   # tail logs from every service
docker compose down      # stop the stack, KEEP volume data
docker compose down -v   # stop the stack AND wipe the named volumes
```

---

## Service URLs

Once the stack is up, these endpoints are published on `localhost` (ports are overridable in `.env`):

| Service | URL | Purpose |
| --- | --- | --- |
| API | http://localhost:5010 | .NET 10 Web API |
| API Swagger | http://localhost:5010/swagger | OpenAPI UI (Development only) |
| API health | http://localhost:5010/health | Liveness JSON (`status: healthy`) |
| API search | http://localhost:5010/api/search | Semantic search (`POST`, NL query → ranked docs; Phase 6) |
| API ask | http://localhost:5010/api/ask | RAG question-answering (`POST`, NL question → grounded answer + citations; Phase 7) |
| API workflows | http://localhost:5010/api/workflows/recommend | AI workflow recommendation + controlled tools (`/api/workflow-tasks`, `/api/tools`, `/api/agent/recommend-and-create`; Phase 8) |
| Web | http://localhost:4210 | Angular SPA (served by NGINX) |
| SQL Server | localhost:1433 | Relational metadata store (SA login) |
| Qdrant HTTP | http://localhost:6333 | Vector DB REST API + dashboard |
| Qdrant gRPC | localhost:6334 | Vector DB gRPC API |
| Ollama | http://localhost:11434 | Local LLM runtime |

The web container also reverse-proxies `/api/*` to the API service internally, so the SPA and API share an origin in the browser.

---

## Verifying the install

1. Wait for `docker compose ps` to show all six services as running. SQL Server takes ~30–60 s to become ready on first boot — this is normal.
2. Open **http://localhost:4210** in a browser. You should see the DocuPilot AI app shell — a sidebar with the eight feature sections and a health-status widget.
3. The health-status widget calls the API's `/health` endpoint. When the API is reachable it shows **`status: healthy`** (the API is up). You can also hit the endpoint directly:

   ```bash
   curl http://localhost:5010/health
   # {"status":"healthy","service":"DocuPilot.Api","version":"0.1.0","timestamp":"..."}
   ```

---

## Repository Layout

```
code/
├── docker-compose.yml          # 6-service stack: api, worker, web, sqlserver, qdrant, ollama
├── .env.example                # SA_PASSWORD + overridable ports (copy to .env)
│
├── backend/                    # .NET 10 solution — Repository Pattern
│   ├── DocuPilot.sln
│   ├── global.json             # SDK pin 10.0.x
│   ├── Directory.Build.props   # Nullable, ImplicitUsings, TreatWarningsAsErrors, net10.0
│   ├── Directory.Packages.props# Central Package Management
│   ├── database/               # placeholder for Phase 2 DB init scripts
│   ├── src/
│   │   ├── DocuPilot.Api/           # ASP.NET Core Web API — controllers + composition root (/health, Swagger, Serilog, CORS) + Dockerfile
│   │   ├── DocuPilot.Services/      # business logic + external-service ports (ILlmClient/IVectorStore/IFileStorage)
│   │   ├── DocuPilot.Repository/    # ALL DB access — repository interfaces + implementations
│   │   ├── DocuPilot.Models/        # entities + DTO contracts + enums (no dependencies)
│   │   ├── DocuPilot.Infrastructure/# EF Core DbContext + Qdrant/Ollama/file-storage clients
│   │   └── DocuPilot.Worker/        # background pipeline host (Generic Host) + Dockerfile
│   └── tests/
│       ├── DocuPilot.UnitTests/
│       └── DocuPilot.IntegrationTests/
│
└── frontend/
    └── docupilot-web/          # Angular 21 + Tailwind 4.3 SPA — Vertical Slice Architecture
        ├── Dockerfile          # node build -> nginx serve
        ├── nginx.conf          # SPA fallback + /api reverse-proxy
        └── src/app/
            ├── app.routes.ts   # shell route + 8 lazy feature slices + 404
            ├── core/           # layout shell, sidebar nav, health-status widget
            ├── shared/         # reusable dumb UI (page-shell)
            └── features/       # one self-contained slice per page:
                                #   dashboard, document-upload, document-library,
                                #   document-detail, semantic-search, ask-ai,
                                #   workflow-tasks, audit-logs
```

> Only the `code/` folder is source controlled. Agent workflow files (`agents/`, `workflows/`, `tasks/`, `outputs/`) live in the parent working directory and are intentionally excluded.

### Architecture at a glance

- **Backend — Repository Pattern.** Lean controllers call services; services orchestrate business logic; all database access is isolated behind repository interfaces in `DocuPilot.Repository`. EF Core 10 backs the repositories via a `DbContext` owned by `DocuPilot.Infrastructure`. Reference direction is inward-only and acyclic: `Api`/`Worker` → `Services` → `Repository` → `Models`; `Infrastructure` implements the external-service ports declared in `Services`.
- **Frontend — Vertical Slice Architecture.** Each of the eight product pages is a self-contained folder under `features/` (its own route, page, components, and data layer) that is lazy-loaded as an independent chunk. App-wide chrome (layout shell, sidebar nav, health widget) lives in `core/`; reusable presentational primitives live in `shared/`.

---

## Tech Stack

- **Backend:** .NET 10 (LTS) Web API + Worker, EF Core 10, Repository Pattern, Serilog, Swashbuckle/Swagger
- **Frontend:** Angular 21 (standalone, signals) + Tailwind CSS 4.3, served via NGINX
- **Database:** SQL Server 2022 (Linux container)
- **Vector DB:** Qdrant
- **LLM Runtime:** Ollama (private LLM; vLLM-compatible API is a documented alternative)
- **Orchestration:** Docker Compose

---

## Phase 1 scope

Phase 1 is the **runnable foundation** — the goal is "clone, `docker compose up`, see a healthy app shell in the browser." It establishes the project structure, container images, and the end-to-end wire (browser → Angular → API).

**Implemented in Phase 1:**

- Six-service Docker Compose stack (API, Worker, Web, SQL Server, Qdrant, Ollama)
- `GET /health` endpoint + Swagger UI on the API
- Angular SPA with the layout shell, sidebar navigation, and all eight feature pages as navigable "coming soon" placeholders
- Repository-Pattern .NET solution skeleton (projects, references, DI wiring) — build is green with `TreatWarningsAsErrors`
- EF Core packages registered (no entities or migrations yet)

**NOT yet implemented (deferred to later phases):**

- Document upload, text extraction
- LLM classification + metadata extraction
- Chunking, embeddings, and Qdrant integration
- Semantic search and RAG question-answering with citations
- MCP-style tool orchestration / workflow tasks
- EF Core entities, migrations, and real persistence
- Authentication / authorization (intentionally out of scope for the POC)
- Production hardening (HTTPS, secrets management), container healthchecks, OpenTelemetry
- An Ollama model is **not** pulled in Phase 1 — only the runtime container is started (the model layer is multi-GB and lands when classification arrives)

---

## Troubleshooting

**Port 1433 (or any published port) is already in use.**
If you already run SQL Server natively, the `sqlserver` container's `1433:1433` mapping will collide. Every published port is overridable in `.env` via the `${VAR:-default}` syntax — set a free host port and re-run:

```env
SQL_PORT=14333
```

The same applies to `API_PORT`, `WEB_PORT`, `QDRANT_HTTP_PORT`, `QDRANT_GRPC_PORT`, and `OLLAMA_PORT`.

**SQL Server is slow to start the first time / API can't reach the DB at boot.**
The SQL Server container needs ~30–60 s to initialize its system databases on a fresh volume before it accepts connections. `docker compose ps` may show it as starting for a while. This is expected; Phase 1 uses `depends_on:` for start ordering only (not readiness) and the API does not open a DB connection at startup, so the app shell and `/health` come up regardless.

**SQL Server container exits / restarts immediately.**
Usually the SA password fails SQL Server's complexity policy. `SA_PASSWORD` in `.env` must be ≥ 8 characters and contain at least three of: uppercase, lowercase, digit, symbol. Fix it, then `docker compose down -v` (to clear the half-initialized volume) and `docker compose up` again.

**Ollama / the LLM model (Phase 4 — auto-pulled).**
As of Phase 4 the stack **auto-pulls the classification model**. A one-shot
`ollama-model-init` service waits for Ollama to become healthy, then runs
`ollama pull ${LLM_MODEL}` (default **`llama3.2:3b`**, ~2 GB) into the
`ollama-models` named volume and exits. The `docupilot-api` and
`docupilot-worker` services gate on it
(`depends_on: condition: service_completed_successfully`), so they never start
against a model-less Ollama. On a **fresh** run the first `up` blocks while the
~2 GB model downloads (one-time); the model then persists in the
`ollama-models` volume across `docker compose down` (but not `down -v`), so
later `up` runs find it already present and `ollama-model-init` is a fast no-op.

Confirm the model is present:

```bash
docker compose exec ollama ollama list      # should list llama3.2:3b
```

**Manual fallback** — if the init was skipped or you want a different model,
pull into the running container directly:

```bash
docker compose exec ollama ollama pull llama3.2:3b
```

**Cheaper / faster gate:** set `LLM_MODEL=llama3.2:1b` in `.env` (~1.3 GB) for a
smaller model — faster but lower extraction quality. The model name is a single
config value (`Llm__Model`), so swapping is zero-code.

> **LLM latency:** inference runs **CPU-only** (no GPU assumed), so a single
> classification takes **seconds to tens of seconds**. A slow first
> classification after upload is normal — it is **not** a hang. Per-call timeout
> is `Llm__TimeoutSeconds` (default 120 s).

The full LLM configuration (`Llm__BaseUrl`, `Llm__Model`, `Llm__ApiStyle`,
`Llm__TimeoutSeconds`, `Llm__MaxAttempts`, `Llm__MaxInputChars`,
`Llm__Temperature`) is wired to both the API and Worker and is overridable via
the `LLM_*` variables documented in `.env.example`.

**Embeddings & Qdrant (Phase 5 — second auto-pulled model + vector search).**
Phase 5 chunks each classified document, embeds the chunks with a **second**
Ollama model, and stores the vectors in **Qdrant** for semantic search.

- **Second model auto-pull.** The same one-shot `ollama-model-init` service now
  pulls **both** models into the `ollama-models` volume before api/worker start:
  the chat model `${LLM_MODEL}` (default `llama3.2:3b`, ~2 GB) **and** the
  embedding model `${EMBEDDING_MODEL}` (default **`nomic-embed-text`**, ~274 MB).
  It runs `ollama pull llama3.2:3b && ollama pull nomic-embed-text`, so api/worker
  (gated on `service_completed_successfully`) never start with either model
  absent. Both persist across `down`/`up` and are re-pulled only after `down -v`.

  ```bash
  docker compose exec ollama ollama list      # should list BOTH llama3.2:3b and nomic-embed-text
  # manual fallback if init was skipped:
  docker compose exec ollama ollama pull nomic-embed-text
  ```

- **Qdrant readiness.** `qdrant` now has a **healthcheck** (it previously had
  none) and `docupilot-api`/`docupilot-worker` gate on
  `depends_on: qdrant: condition: service_healthy`. The `qdrant/qdrant` image
  ships no `curl`/`wget`, so the probe uses **bash's `/dev/tcp`** built-in to
  send an HTTP `GET /readyz` to Qdrant's own readiness endpoint on the
  in-container port `6333` and asserts a `200 OK`. Check it from the host:

  ```bash
  curl http://localhost:6333/readyz      # -> "all shards are ready" / HTTP 200
  docker compose ps                       # qdrant shows (healthy)
  ```

- **The `document_chunks` collection is created by the APP on startup**
  (at `Embedding__Dimensions`), **not** by compose — nothing in the stack
  pre-creates it, and a startup bootstrap validates an existing collection's
  size against the configured dimension.

- **Dimension-match rule (important when swapping the embedding model).**
  `Embedding__Dimensions` MUST equal the embedding model's true output dimension
  (`nomic-embed-text` = **768**). If you change `EMBEDDING_MODEL` (e.g.
  `all-minilm` = 384, `mxbai-embed-large` = 1024) you **must** set
  `EMBEDDING_DIMENSIONS` to the new model's dim **and** start on a fresh Qdrant
  volume (`docker compose down -v`) so the collection re-bootstraps at the new
  size. The app's dim-validate **refuses** a collection whose size mismatches the
  configured dimension (this guards the silent-break trap where a mismatch would
  quietly break upserts and search).

The full Phase-5 configuration (`Chunking__MaxChars`, `Chunking__OverlapChars`,
`Chunking__MaxChunksPerDocument`, `Embedding__BaseUrl`, `Embedding__Model`,
`Embedding__Dimensions`, `Embedding__ApiStyle`, `Embedding__TimeoutSeconds`,
`Embedding__MaxAttempts`, `Qdrant__Host`, `Qdrant__GrpcPort`,
`Qdrant__CollectionName`, `Qdrant__UseTls`) is wired to both the API and Worker
and is overridable via the `CHUNKING_*` / `EMBEDDING_*` / `QDRANT_*` variables
documented in `.env.example`.

**Semantic search (Phase 6 — read-only `POST /api/search`).**
Phase 6 adds a read-only semantic-search endpoint **on the API only**
(`POST /api/search`): it embeds the natural-language query with the same Phase-5
embedding model, runs a cosine search over the Qdrant vectors, and returns the
best-matching documents ranked by score. It adds **no new image, model, port, or
Qdrant/SQL change** — it reads the vectors Phase 5 already produced. Its tuning
keys (`Search__DefaultLimit`, `Search__MaxLimit`, `Search__ChunkOverFetchFactor`,
`Search__MaxChunkFetch`, `Search__MatchedTextMaxChars`, `Search__MinScore`) are
**all code-defaulted, so search works out-of-the-box with zero `.env` changes**;
they are wired to `docupilot-api` (not the Worker) and are optionally overridable
via the `SEARCH_*` variables documented in `.env.example`.

**RAG question-answering (Phase 7 — `POST /api/ask`).**
Phase 7 adds a document-grounded question-answering endpoint **on the API only**
(`POST /api/ask`): it embeds the natural-language question with the same Phase-5
embedding model, retrieves the most relevant chunks from the Qdrant vectors, and
asks the same Phase-4 chat LLM (`llama3.2:3b`) to answer **only** from that
context — returning the grounded answer plus ranked source citations. Grounding
and the not-found behavior are **built in**: if nothing relevant is found, the
endpoint returns `200` with `answerFound: false` and a canned "I could not find
enough information…" answer (no fabricated content). It adds **no new image,
model, port, or Qdrant/SQL change** — it reuses the Phase-4 chat LLM, the
Phase-5 embedder, and the Phase-5/6 Qdrant vectors. Its tuning keys (`Rag__TopK`,
`Rag__MaxTopK`, `Rag__ContextMaxChars`, `Rag__PerChunkMaxChars`,
`Rag__SnippetMaxChars`, `Rag__MinScore`) are **all code-defaulted, so the
endpoint works out-of-the-box with zero `.env` changes**; they are wired to
`docupilot-api` (not the Worker) and are optionally overridable via the `RAG_*`
variables documented in `.env.example`.

**MCP-style workflow tools (Phase 8 — `POST /api/workflows/recommend`, `/api/workflow-tasks`, `GET /api/tools`, `POST /api/agent/recommend-and-create`).**
Phase 8 adds an AI workflow-recommendation plus a controlled, audited MCP-style
tool layer **on the API only**. `POST /api/workflows/recommend` makes one
JSON-mode call to the same Phase-4 chat LLM (`llama3.2:3b`) over a document's
classification/metadata/text to suggest a workflow, next step, priority, and
reason. The AI **never touches the database directly** — the only mutation path
is the validated, audited `create_workflow_task` tool (`POST /api/workflow-tasks`),
which is also driven by the fixed `recommend → create` pipeline at
`POST /api/agent/recommend-and-create`; `GET /api/tools` introspects the tool
catalogue, and `GET /api/workflow-tasks` / `POST /api/workflow-tasks/{id}/complete`
list and close tasks. It adds **no new image, model, port, or volume** — it
reuses the Phase-4 chat LLM, the embedder, and Qdrant; the new `WorkflowTasks`
table is created via the **existing API startup migrate path** (no compose
change). Its tuning keys (`Workflow__DefaultPriority`,
`Workflow__RecommendTextMaxChars`, `Workflow__AllowDuplicateTasks`) are **all
code-defaulted, so the endpoints work out-of-the-box with zero `.env` changes**;
they are wired to `docupilot-api` (not the Worker) and are optionally overridable
via the `WORKFLOW_*` variables documented in `.env.example`.

**First `docker compose up --build` looks hung.**
It is almost certainly pulling base images (SQL Server and Ollama are the big ones). Run `docker compose logs -f` or watch Docker Desktop to confirm download progress. The pull is a one-time cost.

---

## License

TBD.
