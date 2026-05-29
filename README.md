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

**Ollama responds but "has no model" / AI features do nothing.**
Phase 1 starts only the Ollama runtime — no model is pulled. That's intentional; AI features arrive in a later phase. When needed, a model can be pulled into the running container, e.g.:

```bash
docker compose exec ollama ollama pull nomic-embed-text
```

The `ollama-models` named volume persists pulled models across `docker compose down` (but not `down -v`).

**First `docker compose up --build` looks hung.**
It is almost certainly pulling base images (SQL Server and Ollama are the big ones). Run `docker compose logs -f` or watch Docker Desktop to confirm download progress. The pull is a one-time cost.

---

## License

TBD.
