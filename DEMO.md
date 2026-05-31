# DocuPilot AI — Demo Script

A runnable, end-to-end walkthrough for showing DocuPilot AI to an employer or
interviewer. It follows the eight-step flow from the spec (§16) but is adapted
to the **actual application** — the real routes, the real queries, and the seed
dataset. Allow **10–15 minutes**; budget extra for CPU-only LLM latency.

> **Latency caveat (read first).** All LLM inference (classification, metadata
> extraction, RAG answers, workflow recommendations) runs **CPU-only** — no GPU
> is assumed. A single document can take **tens of seconds** to move through the
> pipeline, and an Ask-AI / recommend call can take **10–40 s** to return. This
> is expected, not a hang. Seed the docs a few minutes before you present so
> they are already `ReadyForSearch` when the demo starts.

---

## Step 0 — Bring up the stack and seed the demo data

```bash
# From code/ — a clean, reproducible start
docker compose down -v          # wipe any prior state (optional but recommended)
docker compose up -d --build    # build + start all 6 services
docker compose ps               # wait until all services are Up / healthy
```

First boot pulls several GB of images and **two Ollama models** (`llama3.2:3b`
chat + `nomic-embed-text` embeddings) — one-time. Wait until:

- `docker compose ps` shows all services `Up` (sqlserver/qdrant/ollama `healthy`),
- `curl http://localhost:5010/health` returns `{"status":"healthy",...}`,
- `docker compose exec ollama ollama list` shows **both** models.

**Seed the four demo documents** (manual, on-demand — keeps the demo predictable):

```powershell
# Windows (PowerShell) — the host default
cd code\backend\database\seed
.\seed-demo.ps1                        # uploads via http://localhost:4210
```
```bash
# macOS / Linux
cd code/backend/database/seed
./seed-demo.sh                         # or: ./seed-demo.sh http://localhost:5010
```

The script POSTs all four `.txt` files to `POST /api/documents/upload` (the
`files` multipart field). Now **open http://localhost:4210** and watch the
Library page: each doc walks `Queued → ExtractingText → Classifying →
GeneratingEmbeddings → ReadyForSearch`. Wait until all four are
**ReadyForSearch** before continuing.

The four seed documents:

| File | Becomes | Why it's in the demo |
| --- | --- | --- |
| `sample-contract.txt` | Contract | Master Service Agreement **expiring June 30, 2026** (renewal deadline May 1, 2026) — drives the "about to expire" search, the "legal review" RAG question, and the workflow task. |
| `sample-invoice.txt` | Invoice | Vendor, invoice number `INV-2026-00842`, $18,500, due May 31, 2026 — drives metadata extraction + a finance-approval recommendation. |
| `sample-employee-record.txt` | Employee/HR record | PII-bearing HR record — proves classification discriminates from contracts/invoices. |
| `sample-compliance-policy.txt` | Compliance/Policy | Data-retention policy — the 4th category; broadens the dashboard breakdown. |

---

## Step 1 — Explain the problem  ·  page: `/dashboard`

Open **Dashboard** (`http://localhost:4210/dashboard`). Talking point:

> "This POC simulates a document-management SaaS product where companies upload
> many business documents but need AI to classify, search, and route them
> efficiently — using a **private** LLM, so sensitive documents never leave the
> environment."

The dashboard shows the at-a-glance counts (total / pending / ready-for-search /
failed / pending workflow tasks) and a **classification breakdown** — after
seeding you should see four documents across Contract / Invoice / Employee /
Compliance categories.

## Step 2 — Show the uploaded documents  ·  page: `/library`

Open **Library** (`/library`). Show the table of the four uploaded documents
with their status, classification, and upload date. (If you want to show upload
live, use the **Upload** page at `/upload` — drag-and-drop — instead of the seed
script.)

> "Each upload kicks off a background pipeline: extract text → classify → extract
> metadata → chunk → embed into the vector store."

## Step 3 — Show automatic classification  ·  page: `/library` → `/documents/:id`

Point at the **Classification** column in the Library, then click **View** on
the contract to open its Detail page (`/documents/:id`).

> "After upload, the system extracts the text and uses a private LLM endpoint to
> classify each document — the contract as a Contract, the invoice as an Invoice,
> and so on — with a confidence score."

## Step 4 — Show metadata extraction  ·  page: `/documents/:id` (the contract & invoice)

On the **contract** Detail page, show the extracted metadata (parties, term,
**expiration date June 30, 2026**). Open the **invoice** Detail page and show
its metadata (vendor *Brightline Cloud Solutions*, invoice number
`INV-2026-00842`, amount `$18,500`, due date `May 31, 2026`).

> "The system converts unstructured document text into structured metadata —
> vendor name, invoice number, contract expiry date, assigned department — that
> downstream automation can act on."

## Step 5 — Show semantic search  ·  page: `/search`

Open **Search** (`/search`) and run:

```txt
Find agreements that are about to expire.
```

The **contract** should rank at the top (with a relevance score and the matched
text snippet, linking to its Detail page).

> "This uses embeddings and vector search, so it finds related *meaning* even
> when the exact keywords don't match — the contract says 'valid until' and
> 'renewal deadline', never the word 'expire', yet it's the top hit."

## Step 6 — Ask a RAG question  ·  page: `/ask`

Open **Ask AI** (`/ask`) and ask:

```txt
Which contracts should legal review this month?
```

Wait for the grounded answer (CPU latency — give it 10–40 s).

> "The system retrieves the most relevant document chunks first, then asks the
> LLM to answer **only** from those chunks — so the answer is grounded in our
> documents, not the model's training data."

> **Honesty note:** the demo runs a small `llama3.2:3b` model, so phrasing can
> vary run-to-run. If an answer looks thin, re-ask, or swap to a larger model
> (`LLM_MODEL` in `.env`). The contract's near-term expiry/renewal content is
> what makes this question land.

## Step 7 — Show source citations  ·  page: `/ask` (same answer)

On the same answer, show the **citations / sources** panel linking back to the
contract document.

> "Every answer includes source references, so a user can click through and
> verify exactly where the AI got the information — no black box. If nothing
> relevant is found, the system says so instead of fabricating an answer."

(To show the no-hallucination guard, ask something the corpus can't answer,
e.g. *"What is our refund policy for retail customers?"* — it returns a canned
"I could not find enough information…" with no made-up content.)

## Step 8 — Create a workflow task  ·  page: `/documents/:id` (contract) → `/tasks`

On the **contract** Detail page, use the workflow **recommend → create** action
(the AI recommends a `LegalReview` task for the expiring contract). Confirm it,
then open **Tasks** (`/tasks`) to see the newly created task (team, priority,
status, complete button).

> "This is MCP-style tool orchestration. The AI does **not** write to the
> database directly. It calls a controlled backend tool — `create_workflow_task`
> — that validates the request, creates the task, and writes an audit record.
> The AI proposes; the validated tool disposes."

## Closer — Dashboard + Audit Logs  ·  pages: `/dashboard`, `/audit`

Return to **Dashboard** (`/dashboard`): the counts now reflect the run (ready
docs, the pending workflow task). Then open **Audit Logs** (`/audit`):

> "Here's the full audited trail — every extraction, classification, embedding,
> and every AI **tool** call is recorded newest-first. This is the safety story:
> the AI can only act through validated, audited tools, and you can see exactly
> what it did and when."

Point to the `create_workflow_task` tool entry (the `ToolInvoked` / `ToolSucceeded`
pair) from Step 8 as the concrete proof.

---

## Teardown

```bash
docker compose down       # stop, keep volumes (fast restart, models cached)
docker compose down -v    # stop AND wipe all data (next run re-seeds clean)
```

## Troubleshooting during a demo

- **A doc is stuck in a pending state** → it's CPU LLM latency, not a hang; wait.
  Check `docker compose logs -f docupilot-worker`. A doc may legitimately land as
  `Unknown` classification — that's fine.
- **Search/Ask returns nothing** → confirm the docs reached **ReadyForSearch**
  (embeddings must exist before search/ask work).
- **`seed-demo` upload fails** → confirm the stack is up (`docker compose ps`)
  and that `http://localhost:4210` loads; pass `http://localhost:5010` to hit the
  API directly if nginx isn't ready yet.
- See `README.md` (Troubleshooting) and `ARCHITECTURE.md` for deeper detail.
