# docupilot-ai-rag-mcp-dotnet

DocuPilot AI — Private .NET RAG + MCP Workflow Engine for Enterprise Document Management.

A proof-of-concept AI document management platform built with .NET, Angular, SQL, Docker, vector embeddings, RAG pipelines, and MCP-style tool orchestration. Designed to work with private LLM infrastructure (Ollama or vLLM-compatible APIs) for classification, semantic search, document-grounded question answering, and AI-assisted workflow recommendations.

## Repository Layout

```
code/
├── backend/     # .NET Web API + Worker + Clean Architecture projects (planned)
│   └── database/
└── frontend/    # Web UI
    ├── index.html
    ├── css/
    ├── js/
    └── assets/
```

> Only the `code/` folder is source controlled. Agent workflow files (`agents/`, `workflows/`, `tasks/`, `outputs/`) live in the parent working directory and are intentionally excluded.

## Tech Stack (Planned)

- **Backend:** .NET 8 Web API, EF Core, Clean Architecture
- **Frontend:** Angular + Tailwind CSS (current `frontend/` contains an early static prototype)
- **Database:** SQL Server or PostgreSQL
- **Vector DB:** Qdrant or pgvector
- **LLM Runtime:** Ollama or vLLM-compatible API
- **Container:** Docker Compose

## Current State

This initial commit contains the working prototype produced during early frontend prototyping:

- `frontend/index.html` + `css/styles.css` + `js/app.js` — standalone login UI (Bootstrap 5, vanilla JS, light/dark theme)
- `backend/database/` — placeholder folder (DB scripts to be added)

Subsequent phases will add the .NET backend, RAG pipeline, vector DB integration, MCP tool layer, and Docker Compose setup per `DocuPilot_AI_POC_Spec.md`.

## License

TBD.
