# AP Invoice Automation — Engine Architecture & Delivery Plan

## At a glance

**What gets built:** a .NET 8 console app, run hourly by Task Scheduler, that reads the AP mailbox, parses invoices, matches them to PO lines in Pace, creates unposted vendor bills in a daily batch, and emails four reports plus a reconciliation proof. A website can be added later as a separate project over the same libraries.

**Effort:** ~46 developer-days for the engine (~55 including vendors 3–5). Elapsed time ~3.5 months — the gap is waiting on access and decisions, not coding.

> **Companion documents (2026-08-19)** — this plan is the internal, detailed version. Two plain-language derivatives exist for everyone else, and they are the ones that get sent out:
> - [prerequisites-before-development-2026-08-19.md](./prerequisites-before-development-2026-08-19.md) — items 1–16, what IT, Network, Accounting and AP must provide, test and production
> - [development-steps-2026-08-19.md](./development-steps-2026-08-19.md) — steps 1–19, the build sequence in business language
>
> Where they disagree with this file, they are newer. The numbering deliberately differs: this plan keeps its phases, they use a flat 1–N sequence.

### To start day one, you need only these five

| # | Item | Owner | Unblocks |
|---|---|---|---|
| 14 | GitHub repo + push access | IT / you | P1 — project setup |
| 8 | `APAutomationDev` SQL database | IT | P2 — schema + ledger |
| 6 | Pace **staging** API credentials | Chase / IT | S2 — Pace integration |
| 13 | Pace staging has live data copied | Chase / IT | S2 |
| 15 | Real Sanmar invoice files | Client / AP | P4 — parsing |

Everything else in the 20-item P0 chart can be requested now and arrive later without stalling work — **except #1 (Graph app registration)**, which gates P3 and is the worst-timed dependency in the project: it lands early, when there is nothing else to switch to.

### Phase flow

```
P0  Prerequisites & environments ......... 2d   (mostly other people's work)
     │
     ├─ S2  Pace integration ............. 3d   ← discovery + client, staging already exists
     ├─ P1  Project + GitHub ............. 2d
     │       └─ P2  Database + ledger .... 3d
     │              └─ P3  Read email ..... 6d
     │                     └─ P4  Parse invoices (Sanmar +1) .... 7d
     │                            └─ P5  CATEGORIES 1/2/3 + DRY RUN ★ .... 6d
     │                                   └─ P6  Four reports + reconciliation .... 4d
     │                                          └─ P7  Full test pass ............ 5d
     │                                                 └─ P8  Prod + scheduled task .. 4d
     │                                                        └─ P9  Pilot ......... 4d
     └─ P4b Vendors 3–5 (parallel, after P5) .......... 9d
```

**★ P5 is the milestone that matters** — the full pipeline runs against real mail and real Pace *reads*, computing and logging every bill it *would* create, writing nothing. It is the first trustworthy demo and it is reviewable by AP before a single real payable exists.

## Context

Wallace Graphics processes up to ~75 vendor invoices/day by hand out of one shared mailbox, matching each against a PO in the Pace ERP. The goal is an unattended **Engine** that does the straightforward ones automatically and tells Accounting which of the rest need attention and why.

The `APAutomation` repo currently holds only docs — no code, no git. This plan defines what to build and in what order, starting with the core. A website is explicitly **out of scope for now** but must be cheap to add later.

Source documents reviewed: `AP_Invoice_Automation_Project_Plan-Draft02-Reconciled-08-17-2026.docx`, the Draft01 variants with Elizabeth's review comments, `Account Payable.docx` (AP shadowing notes), `ShortSummaryAPAutomation.txt`, [kickoff notes](../meeting-notes/2026-08-12-kickoff-meeting.md), [prerequisites-checklist.md](./prerequisites-checklist.md).

### Decisions made in this planning session

| Decision | Choice |
|---|---|
| Solution home | Standalone `WG.AP.sln` in the `APAutomation` repo. **Not** inside `WGS.Web.Portal`. |
| Pace client | Regenerate a slim NSwag client from the on-disk SDK `swagger.json`. Do **not** reference the 19.9 MB `EPaceGeneratedNSwagClient.cs`, and do **not** hand-write the calls. |
| Project structure | **CONFIRMED** — 7 projects: 4 class libraries (`Core`, `DataAccess`, `Extraction`, `Integrations`) + `Engine.Host` console app + `WG.AP.Database` .sqlproj + `WG.AP.Tests`. See the table under *Architecture* for exact contents. Not the 2-library variant, not the 6-library variant. |
| Run model | Console Exe + Windows Task Scheduler. No Quartz, no Windows Service. |
| Database | New dedicated `APAutomationDev` / `APAutomation` SQL Server DB with its own `.sqlproj`. |
| Extraction | Deterministic data-driven rules engine now; fallback (LLM/OCR) left as a pluggable seam, decided after real samples arrive. |
| First chunk | P1/P2 — Foundations, schema, invoice ledger. |

### Why the Pace client is generated, not hand-written — settled, do not re-litigate

Generation reads EFI's own contract; a hand-written record encodes a *belief* about it, and beliefs drift silently. It moves a class of error from runtime to compile time, which matters because the dangerous failure in AP is not "the call errored" but **"the call succeeded and billed the wrong amount to the wrong job"** — that lands in a real batch and looks fine on the report.

The specific landmines hand-writing gets wrong: `Vendor.Id` is a **string** while most `Id` fields are `int`; every money field is `double`, not `decimal`; ~15 fields per object each nullable or not; and Pace's date formatting needed a dedicated `PaceDateTimeOffsetConverter` in the portal. On a Pace upgrade, regeneration makes the compiler list every break.

The 19.9 MB file in `WGS.Web.Portal` was an *ergonomics* problem, never a reliability one — filtering the spec to ~16 operations removes that objection without giving up the reliability. So slim-generated is not a compromise; it is the reliable option with the downside removed.

**What generation does not cover** (and why S2 exists): Pace's *business* rules — whether `createBillLine` auto-derives quantity/price from the receipt, whether it consumes the receipt, what Pace rejects (closed period, closed job, already-billed receipt, over-receipt), and what its error payloads look like. No client style helps there; only working against staging does.

### What the exploration changed

Two findings materially reduced risk and cost:

1. **Pace publishes a live REST spec, and the SDK is also on disk.** Every operation needed exists: `readVendor`, `readPurchaseOrder`, `readPurchaseOrderLine`, `readPurchaseOrderReceipt`, `createBill`, `createBillBatch`, `createBillLine`, `AttachmentService/addAttachment`. **Prerequisite #4 ("Pace integration method undocumented") is closed.** See *Pace spec source* below.
2. **`WG.MarcomOrderProcessor_2026-04-19\Services\PaceService.cs` is a working standalone .NET 8 precedent** — plain `HttpClient` + Basic auth against `{BaseUrl}/CreateObject/createX`. That is the pattern to copy, not the portal's `EPaceAPIClient` (which contains zero bill/PO methods and hard-depends on `sec.TenantAppSetting` multi-tenancy).

Also confirmed: **no Graph/Exchange precedent exists in any repo** — mailbox access is genuinely new work. And the claimed "we created a test bill in Pace" has **no code artifact anywhere on this machine**; treat Pace bill creation as unproven.

---

## Architecture

### The mental model

**The engine is a set of .NET 8 class libraries (`.dll`) — it is not a runnable program.** A library cannot run by itself; something has to host it. The console app is that host. The website, when it arrives, is a *second* host over the same libraries. Both hosts are thin shells containing no business rules.

```
┌─────────────────────────────────────────────┐
│   THE ENGINE = class libraries (.dll)       │
│   Core · Data · Extraction · Integrations   │
└─────────────────────────────────────────────┘
              ▲                    ▲
    ┌─────────┘                    └──────────┐
┌───────────────────┐              ┌──────────────────────┐
│ Engine.Host       │              │ WG.AP.Web  (LATER)   │
│ Console App .exe  │              │ ASP.NET Core, IIS    │
│ Task Scheduler    │              │ separate project     │
└───────────────────┘              └──────────────────────┘
```

### Solution layout — `c:\Users\galina.zezetko\source\repos\APAutomation\`

| Project | VS template | Produces | Contains |
|---|---|---|---|
| `src\WG.AP.Core` | Class Library | `.dll` | domain, **all interfaces**, pipeline orchestration, `PoLineMatcher`, report models, `AddApEngine()` |
| `src\WG.AP.DataAccess` | Class Library | `.dll` | Dapper over stored procs, `Microsoft.Data.SqlClient` |
| `src\WG.AP.Extraction` | Class Library | `.dll` | PDF/Excel readers, format catalog, detection scorer, field extractors |
| `src\WG.AP.Integrations` | Class Library | `.dll` | `Pace\` (slim NSwag client) + `Graph\` (mail read/move, SMTP send) |
| `src\WG.AP.Engine.Host` | **Console App** | **`.exe`** | thin shell: build services, run pipeline, exit. **No business logic, ever** |
| `src\WG.AP.Database` | SQL Server Database Project | `.dacpac` | `dbo\ intgr\ lkup\ mgt\ rpt\`, each with `Tables\` + `Stored Procedures\` |
| `tests\WG.AP.Tests` | xUnit Test Project | `.dll` | unit tests over Core + Extraction |

**This layout is confirmed and settled.** Seven projects. All `net8.0`, `ImplicitUsings` + `Nullable` enabled. Splitting a library further later is cheap (moving files between projects); un-tangling a grab-bag is not — so `Core` stays strict.

Rejected alternatives, for the record: a 2-library variant (`Core` + one `Infrastructure` grab-bag) was simpler but would let data, extraction, Pace and Graph concerns tangle; a 6-library variant (separate `Integration.Pace`, `Integration.Microsoft365`, `Composition`) isolated dependencies more cleanly but added daily solution noise for a single developer. `AddApEngine()` lives in `Core` rather than its own `Composition` project.

### Pace spec source — VERIFIED WORKING

**Canonical spec (use this one):**

```
https://pacestaging.wallacegraphics.com/static/swaggerUI/swagger.json
```

Confirmed reachable 2026-08-18. Basic auth, same credentials as the API. The browsable UI is at `https://pacestaging.wallacegraphics.com/static/swaggerUI/index.html`.

Prefer this over the SDK copy at `C:\Users\galina.zezetko\Documents\EPace-sdk\REST\rest-openapi-schema\swagger.json` (4.1 MB) — the live spec is generated by the Pace build actually running on staging, whereas the SDK download may be a different release. **Do once:** save both and diff them; the diff shows how far the SDK copy has drifted, which matters before trusting any of its docs. Staging wins on conflict.

Repo-level (outside `src\`):

```
docs\pace\swagger.full.json     committed copy of the LIVE staging spec (~4 MB) + the date fetched
docs\pace\fixtures\             captured request/response JSON from S2
tools\pace-filter\              filters full spec -> src\WG.AP.Integrations\pace.slim.json (both committed)
```

Committing the full spec is deliberate: `WGS.Web.Portal` has a 19.9 MB generated Pace client with **no `swagger.json` and no `.nswag` beside it**, so it can never be regenerated. Not repeating that mistake is most of the value here.

Production spec is presumably the same path on `efipace.wallacegraphics.com` — confirm before go-live and re-diff, since a version skew between staging and production would be a nasty surprise in C8.

### Reference graph — the one rule that matters

```
Core          -> nothing but Microsoft.Extensions.{Logging,Configuration,DependencyInjection}.Abstractions
Data          -> Core
Extraction    -> Core
Integrations  -> Core
Engine.Host   -> Core, Data, Extraction, Integrations   (+ NLog.Extensions.Logging)
```

**`WG.AP.Core` references no edge technology.** Every adapter points inward at it. That single rule is what makes the website free later, and it is the thing to protect during code review.

`AddApEngine(IConfiguration)` lives in `WG.AP.Core` and is the single wiring entry point both hosts call. (It registers implementations by interface; the host projects supply the adapter assemblies via project reference.)

### How the website gets added later

In Visual Studio: right-click solution → Add → New Project → **ASP.NET Core Web App (MVC)** → `src\WG.AP.Web`. Add project references to the same four libraries. Then in `Program.cs`:

```csharp
builder.Services.AddApEngine(builder.Configuration);   // the identical line Engine.Host already has
builder.Services.AddControllersWithViews();
```

**You touch neither the console app nor any engine library.** The console keeps running the hourly schedule exactly as before — the website is an addition, not a replacement. Three capabilities come free: a read-only exception dashboard (same views the email reports use), "reprocess this invoice" (writes a queue row the console app drains on its next run), and CRUD over the format config tables — so **onboarding a vendor format becomes a web form**.

### Run model

Single console Exe, run by Task Scheduler. **The scheduled task fires hourly and never needs reconfiguring** — the app itself decides whether to work, by reading `lkup.ProcessingCalendar` and `dbo.ProcessingWindow`. This is necessary regardless of host, because the engine must *compute* the business date to title the bill batch, group reports, and roll weekend mail into Monday. Cron can't do that.

CLI switches: `--run-once ingest|process|reports`, `--business-date yyyy-MM-dd` (replay a day), `--dry-run` (skip all Pace writes and mail moves).

Single-instance safety: named mutex `Global\WG.AP.Engine.{Env}` plus `UQ_mgt_ProcessingRun_Slot` on `(BusinessDate, RunSequenceInDay)`.

### Key interfaces (all in `WG.AP.Core\Abstractions\`)

| Seam | Interface | Note |
|---|---|---|
| Mail | `IMailSource`, `IMailSender` | `MoveMessageAsync` **returns the new id** — Graph's `message.id` changes on folder move |
| Document type | `IDocumentProbe`, `IDocumentReader`, `IDocumentSplitter` | all yield one `DocumentPayload` (pages/tables/text/coords) so extractors never see a PDF or workbook |
| Format | `IFormatCatalog`, `IFormatDetector`, `IInvoiceFieldExtractor` | resolved by `ExtractorKey` from DB config |
| Pace | `IPaceReader`, `IPaceWriter` | split by direction: reads may go direct-SQL, writes always REST |
| Reports | `IReportBuilder`, `IReportRenderer`, `IReportDelivery` | one column/row `ReportModel` covers all reports |

All Pace calls return `PaceResult<T>` with `ErrorFound`/`Successful`/`NotFound`/`IsTransient` — **never throw**, matching the house `BaseAPIClient.CallAPI` convention.

`PoLineMatcher` in `WG.AP.Core\Matching\` is a **pure function** — line-grain by construction, unit-testable with no Pace at all:
- `QtyReceived > 0` **and** a receipt exists with `BilledQuantity < Quantity` → `BILL CREATED`, bill line tied to that receipt id
- `QtyReceived == 0`, or no unbilled receipt while `QtyReceived < QtyOrdered` → `PO NOT RECEIVED`
- all receipts fully billed, or `InvoiceComplete` set, or a Pace `Bill` already exists for `(vendor, invoiceNumber)` → `ALREADY ENTERED`

PO header status is a corroborating signal only, **never the decision**.

---

## Database design

New DB, schemas `dbo` (domain), `intgr` (Pace/Graph mirrors), `lkup` (enums), `mgt` (runs/monitoring), `rpt` (reporting). `READ_COMMITTED_SNAPSHOT ON`. Stored-proc-only access via Dapper; the app login gets `EXECUTE` on procs, not `INSERT`/`UPDATE` on tables.

### The three-level ledger

Counts don't line up 1:1, so three grains are required:

| Table | Grain | Example: Sanmar email with `invoices.xlsx` (40 invoices) + `logo.png` |
|---|---|---|
| `intgr.GraphMailMessage` | one email | 1 row |
| `intgr.GraphMailAttachment` | one attachment | 2 rows (logo.png flagged ignorable) |
| `dbo.InvoiceDocument` | one real document | 1 row |
| `dbo.Invoice` | one invoice | 40 rows, PENDING |

**The rule that makes reconciliation provable: every `InvoiceDocument` must yield at least one `Invoice` row.** A file that cannot be opened still produces one placeholder `Invoice` with outcome `ERROR`. There is no path by which work enters the system and leaves the count. *(Needs Accounting sign-off — see open items.)*

### Core tables

| Table | Purpose |
|---|---|
| `dbo.Invoice` | the ledger row. Flattened decided values + `CurrentInvoiceOutcomeID`, `BusinessDate`, `InvoiceNumberNormalized`, `AccountingPeriodKey` |
| `dbo.InvoiceExtractedField` | the *evidence* — raw value, page/cell reference, which rule fired, confidence. Answers "why did it read PO 44551?" |
| `dbo.InvoiceOutcomeHistory` | every state change. `uq...OneCurrent` filtered unique index on `(InvoiceID) WHERE IsCurrent = 1` makes "exactly one outcome" a **database guarantee** |
| `dbo.Vendor`, `dbo.VendorEmailIdentity` | local reference to Pace vendors + no-PO defaults + sender-matching patterns |
| `dbo.InvoiceFormat` | 1..n per vendor. Versioned via `FormatVersion` + `EffectiveFrom/To`; `uq...ActiveVersion` allows only one ACTIVE per `FormatCode` |
| `dbo.InvoiceFormatField` | field → locator mapping, typed columns + narrow `ExtraSettings` JSON escape hatch |
| `dbo.InvoiceFormatDetectionRule` | weighted signals; `dbo.InvoiceDocumentFormatDetection` records **every candidate's score**, not just the winner |
| `intgr.PaceBillBatch` | keyed `(AccountingPeriodKey, BatchDate, BatchSequence)` — **month-end's N-open-batches falls out of the key, with no code branch** |
| `intgr.PaceReceiptClaim` | local mirror of "one receipt, one bill". `uq...LiveReceipt` filtered unique on `(PacePurchaseOrderReceiptID) WHERE ReleasedDateTime IS NULL` |
| `intgr.PaceBill`, `intgr.PaceWriteIntent` | write intent before the call, result after. `CK...SucceededHasID` forbids SUCCEEDED without a Pace id |
| `mgt.ProcessingRun` | one row per run, with heartbeat for crash detection |
| `rpt.InvoiceOutcomeSnapshot` | per-run frozen snapshot with descriptive values as **text** |
| `rpt.ReconciliationCheck` | the proof, as data, with `IsBalanced` as a persisted computed column |

Copy near-verbatim from `WGS.Database`: `mgt.APIIntegrationLog` + `up_insertAPIIntegrationLog`, the `SystemTaskQueueMessage` retry columns, the `lkup` table template, and the proc style (`SET NOCOUNT ON`, explicit transaction, `TRY/CATCH` with `While @@tranCount <> 0 Rollback`, `EXECUTE up_RaiseErrorInfo`).

### When to write to the database

**Two hard rules:** (1) no transaction ever encloses a Graph or Pace HTTP call; (2) intent is committed *before* the side effect, result *after*.

| # | Step | Transaction boundary | Committed |
|---|---|---|---|
| T0 | Run start | 1 txn | `mgt.ProcessingRun` STARTED — before any I/O |
| T1 | Mail discovered | 1 txn **per message** | message + all attachment rows, **before any file is written** |
| T2 | Attachment saved | 1 txn per attachment, **after** the file write | `StoredFilePath`, `ContentSHA256`. File first, then DB — an orphan file is harmless, a DB row pointing at nothing is not |
| T3 | Format detected | 1 txn per document | all candidate scores + the winner |
| **T4** | **Log on arrival** | **1 txn per document** | **all N `Invoice` rows + N history rows at PENDING.** All-or-nothing, so a document's count is never partial |
| T5 | Extraction | 1 txn per invoice | evidence rows + decided values. **Before any Pace call** — the duplicate check depends on a DB unique index, which can't help while the invoice number is only in a C# variable |
| T6 | Pace PO/receipt read | 1 txn per invoice | snapshots + resolved line/receipt ids, or outcome NO PO / PO NOT RECEIVED |
| T7 | Batch reserve → confirm | 2 txns, `UPDLOCK, HOLDLOCK` on reserve | shell row, then `PaceBillBatchID` |
| **T8a** | **Bill pre-write** | **1 txn, committed BEFORE the HTTP call** | receipt claim (or unique index rejects → ALREADY ENTERED), write intent IN FLIGHT, bill shell |
| **T8b** | **Bill post-write** | **1 txn, after the HTTP calls** | `PaceBillID`, bill line, discrepancies, outcome → BILL CREATED. One txn, so an invoice can never be BILL CREATED without a bill id |
| T9 | Run close | 1 txn (all local) | snapshot + reconciliation rows + run counters |

**Why extraction is saved before the Pace lookup:** the duplicate constraint is a database index, and if Pace is down for the day you still have complete reportable extraction and won't re-parse a single PDF next run. Re-parsing is the expensive non-deterministic step; it should happen exactly once.

**The one genuinely dangerous operation:** `createBill` is not idempotent and Pace offers no idempotency key. Three layers — pre-probe Pace for an existing bill; commit a write intent stamped with `Invoice.InvoiceGUID` before the call; never blind-retry an intent left at IN FLIGHT (re-probe Pace first, by `Bill.Reference` = the GUID, then by receipt id, then by vendor+invoice number). The real safety net is organisational and already in scope: **bills are created but never posted.** Automating posting later reopens this risk.

### Duplicate prevention — five layers, all constraints not conventions

1. Mail — unique on `SHA256(MailboxAddress|GraphMessageID)`. **Requires Graph immutable IDs** (`Prefer: IdType="ImmutableId"`) — the default id changes on folder move, and the design moves mail to an Errors folder, so without this the same mail reprocesses forever.
2. Attachment — unique `(GraphMailMessageID, AttachmentKeyHash)`
3. Document slot — unique `(InvoiceDocumentID, SourceSequence)` — re-running a split is idempotent
4. **Invoice — unique `(PaceVendorID, InvoiceNumberNormalized, IsCreditMemo)`** where number is not null. This is why the invoice number is mandatory: it is half the duplicate key
5. **Receipt — unique live claim per Pace receipt id**, acquired *before* the HTTP call

`InvoiceNumberNormalized` upper-cases and strips spaces/hyphens but **deliberately keeps leading zeros** — `0012345` and `12345` are different invoices at some vendors, and collapsing them blocks real payables.

### Reconciliation

Day-scoped proof, from `dbo.Invoice WHERE BusinessDate = @d`:

```
COUNT(*) = SUM(outcome 1..5)  AND  PENDING count = 0   ->  IsBalanced
```

`PENDING` is a deliberate sixth bucket that must read zero — it is the "we lost one mid-run" signal. **The system is permitted to be uncertain; it is not permitted to be silently uncertain.** Note run-scoped ≠ day-scoped: a 10am run also re-touches invoices received last Tuesday, so `OutcomesAssigned` for a run is not `InvoicesReceived`.

A mismatch surfaces in five places, and **retention procs refuse to purge logs for any run whose reconciliation is unbalanced** — the forensic trail for a broken day survives indefinitely.

### Reporting

Reports read a **per-run frozen snapshot** (`rpt.InvoiceOutcomeSnapshot`), not live joins. Vendor name, format code/version, GL account, job number are stored as **text** at run close. Re-running October's report next July produces byte-identical output. A live view (`rpt.vReconciliationDay`) is kept as an independent cross-check and disagreement is recorded — so a snapshot bug shows up as a reconciliation failure rather than as quietly wrong reports.

---

## Vendor & format extensibility — CONFIRMED approach

A stated hard requirement: a vendor may have 1..n formats, and vendors/formats must be easy to add or remove. Three tiers:

**Tier 1 — pure data. No code, no rebuild, no deploy.** The common case: a single-invoice text PDF with labeled fields, or flat Excel where one row = one invoice. Insert rows into four tables; the engine reloads the format catalog every run, so the next run picks it up.

```sql
INSERT dbo.Vendor              (VendorShortCode, VendorName, PaceVendorID, IsAutomationEnabled, PhaseNumber) ...
INSERT dbo.VendorEmailIdentity (VendorID, IdentityType, Pattern, Priority)          -- 'DOMAIN', 'acme\.com$'
INSERT dbo.InvoiceFormat       (VendorID, FormatCode, FormatVersion, DocumentTypeID,
                                ExtractorKey, SplitStrategyCode, InvoiceFormatStatusID, EffectiveFromDate)
INSERT dbo.InvoiceFormatDetectionRule (InvoiceFormatID, DetectionMethod, Target, Pattern, IsRequired, Weight)
INSERT dbo.InvoiceFormatField  (InvoiceFormatID, ExtractionFieldTypeID, ExtractionMethodID,
                                Sort, IsRequired, LocatorExpression, ValidationRegex, DateFormatString)
INSERT dbo.InvoiceFormatFieldTest (InvoiceFormatFieldID, SampleText, ExpectedValue)  -- regression test AS DATA
```

Then flip status DRAFT → ACTIVE. `InvoiceFormatFieldTest` matters more than it looks: one xUnit `[Theory]` loads every row, so **each new format ships its own regression coverage as data** — the only way test coverage survives dozens of onboardings by one person.

**A vendor with three formats is just three `InvoiceFormat` rows sharing a `VendorID`** — nothing is special-cased. Sanmar: `SANMAR-XLSX-BATCH` (multi, `EXCEL_GROUP_BY_COLUMN`), `SANMAR-PDF-SINGLE`, `SANMAR-PDF-CREDIT`.

**Removing** = one UPDATE: `IsAutomationEnabled = 0`, or format status → RETIRED with `EffectiveToDate`. **Never DELETE** — historical invoices point at the format that parsed them and reports must stay accurate. Config tables are append-and-retire only.

**Vendor changes their layout** — insert `FormatVersion = 2` as DRAFT (a filtered index excludes DRAFT from live detection), test against samples, then cut over in one transaction: v1 → RETIRED + `SupersededByInvoiceFormatID`, v2 → ACTIVE. A filtered unique index makes two-active-versions impossible. October invoices still resolve to v1, and October's report still *says* v1 because the snapshot froze the name as text.

**The maintenance loop that makes this work in practice.** An unrecognised file ends `ERROR` with the scoring trace saved **for every candidate**, not just the winner:

```
SELECT * FROM rpt.vFormatDetectionMisses
-> "SANMAR-XLSX-BATCH scored 0.62 (threshold 0.70); missed TEXT_CONTAINS 'Remit To: SanMar'"
```

Reweight one row, then **reprocess the already-ingested document** — no mailbox re-read, no redeploy, no debugger. That same view grouped by sender domain and ranked by volume tells you which format to build next; it is the highest-value view in the system.

**Tier 2 — data plus one small tested class** (rebuild + publish, but nothing else changes). Required when the *algorithm* is new, not the vendor: multi-row-per-invoice Excel (expected Sanmar — group rows while invoice number is blank, sum lines, reconcile to a header total); multi-invoice PDFs needing anchor-scanned page ranges; cross-field arithmetic; credit memos. The registry resolves `IInvoiceFieldExtractor` by `ExtractorKey` via assembly scan, so **there is no switch statement to edit**.

**Tier 3 — never.** Adding a vendor must never touch matching logic, the five outcomes, reporting, or the Pace write path. That constraint is the acceptance test of this design, and it is the exit criterion for P4b.

**Explicit anti-recommendation:** do not build a scripting engine, expression DSL, or Roslyn runtime compilation to collapse Tier 2 into Tier 1. It trades a ten-minute publish for years of undebuggable production behaviour, and a one-developer team should not own a language runtime. Target: *an easy format is data; a hard format is one small tested class.*

**The real bottleneck is samples, not code.** Onboarding is only easy with 5–10 real invoices in hand — writing locators without a real file is guesswork you discover at 6am. Sample availability, not code structure, sets onboarding speed. Once the website exists, Tier 1 stops being SQL and becomes a web form.

## P0 — Prerequisites: the start gate

**Nothing in P1+ starts until the rows marked ⛔ are closed.** Almost all of this is other people's work, so it must be requested now and tracked in Jira epic `APA-E9`. This is a phase, not a footnote.

### P0.1 Access, accounts & credentials

| # | What is needed | Detail | Owner | ⛔ Blocks |
|---|---|---|---|---|
| 1 | **Exchange / Graph app registration** | `Mail.ReadWrite` + `Mail.Send` **application** permissions, admin consent, an `ApplicationAccessPolicy` scoping the app to `accountspayable@` only (else it can read every mailbox in the company), **and immutable-ID support enabled** | IT | ⛔ P3 |
| 2 | Graph client secret | Note the expiry date — typically 24 months, and it will silently kill the engine when it lapses | IT | ⛔ P3 |
| 3 | **Service account** | Confirmed created, with a documented list of what it can reach: SQL, file shares, Pace, the app server. Status is "UNKNOWN" in the reconciled plan | Diana / IT | ⛔ P8 |
| 4 | **Scheduled-task run-as account + password** | The account Task Scheduler runs the exe as. Needs the **"Log on as a batch job"** right on the app server, and a password that either does not expire or has a documented rotation process | IT | ⛔ P8 |
| 5 | Remote access to the app server | Whether you can RDP to create/edit the scheduled task yourself, or must raise a ticket each time | IT | ⛔ P8 |
| 6 | **Pace staging API credentials** | For the Swagger UI and the client. *(Server + spec already confirmed reachable.)* | Chase / IT | ⛔ S2 |
| 7 | Pace production API credentials | Held until P8; do not use earlier | Chase / IT | ⛔ P8 |

### P0.2 Environments — Dev/Test **and** Production, both specified up front

| # | What is needed | Detail | Owner | ⛔ Blocks |
|---|---|---|---|---|
| 8 | **SQL: `APAutomationDev`** | Instance name, auth method, your rights to publish from Visual Studio | IT | ⛔ P2 |
| 9 | **SQL: `APAutomation` (prod)** | Created up front so the schema is only ever published, never hand-built. Plus a backup schedule | IT | ⛔ P8 |
| 10 | **Test mailbox** | A copy or dedicated test inbox with real sample mail. **Do not develop against the live AP inbox** — moving mail to an Errors folder is destructive | IT / AP | ⛔ P3 |
| 11 | **Test file-share folders** | Talk to the network team. Service account needs write access; must be backed up | Network | ⛔ P3 |
| 12 | **Production file-share folders** | Same structure, separate root, agreed at the same time so P8 is not a surprise | Network | ⛔ P8 |
| 13 | Pace staging has live data copied | A Phase-0 blocker in the reconciled plan; without real POs the matching work cannot be validated | Chase / IT | ⛔ S2 |
| 14 | GitHub repository | Repo created under the org, your push access, branch protection on `dev` | IT / you | ⛔ P1 |

### P0.3 Folders to be created — specify both environments

**File-share folders** (one root per environment, identical structure beneath):

```
\\<server>\APAutomation-TEST\          and   \\<server>\APAutomation\
    Inbound\yyyy\MM\dd\        attachments saved straight off the email
    Split\yyyy\MM\dd\          individual invoices carved out of multi-invoice files
    Processed\yyyy\MM\         successfully handled
    Errors\yyyy\MM\            unreadable or unmatched
    Reports\yyyy\MM\           generated report copies
    _staging\                  in-flight writes, swept nightly
    Logs\                      NLog output
```

**Exchange mail folders** — a separate thing from file folders, and both are needed:

```
Inbox                  polled hourly
Inbox\Processed        moved after successful handling
Inbox\Errors           moved on failure, + notification email
Inbox\NeedsReview      optional: parsed but low confidence
```

Confirm with the network team: UNC paths, service-account write rights, **whether the share is backed up** (the DB will reference files AP may be legally obliged to produce for 7 years), and the retention policy.

### P0.4 Business inputs

| # | What is needed | Detail | Owner | ⛔ Blocks |
|---|---|---|---|---|
| 15 | **Real Sanmar files** | 🟩 **Excel-vs-PDF RESOLVED 08-19: it is both.** An email from Sanmar arrives with *either* a PDF *or* an Excel attachment — not paired, neither embedded in the other. Both readers are in scope. Still needed: **one full unedited day of mailbox traffic** (junk included, for the reconciliation count) **plus** format examples that did not happen to arrive that day, and one example per business scenario with its expected outcome | Client / AP | ⛔ P4 |
| 16 | **Second vendor named + samples** | Enough to prove the format registry works for more than one vendor | Client / AP | ⛔ P4 |
| 17 | Vendors 3–5 named + samples | Never identified anywhere in any document | Client / AP | ⛔ P4b |
| 18 | Report content sign-off | Which columns AP actually wants on each of the four reports, and the distribution list | AP | ⛔ P6 |
| 19 | ~~**"Hold" semantics**~~ 🟩 **RESOLVED 08-18** | **A PO line with a receipt → create the bill. No receipt on that line → error.** The PO need not be fully received; the *lines* must be, in order to apply | Accounting | ✅ |
| 20 | ~~**Month-end rule**~~ 🟩 **RESOLVED 08-18** | **The 5-day rule.** Invoice arriving in the first 5 days of a month and dated to the prior month → bill batch GL period = the **prior** month. After day 5 → current GL period, Accounting adjusts if needed | Accounting + Chase | ✅ |
| 🟩 21 | 🟩 **The four sample reports** | The 08-18 notes state four sample reports already exist, "built from real invoices." Request them — they are the closest thing to a report spec and would de-risk #18 | Chase | ⛔ P6 |

**P0 effort for you: ~2 days** of writing tickets, chasing, and documenting answers. The *calendar* cost is entirely outside your control, which is exactly why it is its own phase.

<div class="added">

🟩 **ADDED FROM 08-18 NOTES** — three P0 blockers are now answered, which materially de-risks the plan: **#19 (hold semantics)** and **#20 (month-end)** were the two most expensive items to get wrong, and **credit memos** now have a mechanism. Full detail and the exact wording in *[Added from the 08-18 notes](#-added-from-the-08-18-notes)* at the end of this document. Two of them confirm the design as built; none require rework.

</div>

## Delivery chunks

Ordering principle: P0 items are requested on day one and chased in parallel with everything else. Pace integration (S2) needs only staging credentials, so it can start before the mailbox or the database exist. Sample invoices gate only P4, so P4 is deliberately kept off the critical path.

| # | Phase | Demoable at the end | Blocked by | Days |
|---|---|---|---|---|
| **P0** | **Prerequisites & environments** | The start-gate chart above, all rows answered and dated | — | **2** |
| **P1** | **Project & GitHub setup** | `git clone` on a second machine, open `WG.AP.sln`, build succeeds, `dotnet test` runs green with one placeholder test | #14 | **2** |
| **P2** | **Database + invoice ledger** | Publish the `.sqlproj` to Dev; run the exe on a local test file → row in `dbo.Invoice`; reconciliation view says `1 received, 1 unclassified` | #8 | **3** |
| **P3** | **Read email from Exchange** | Point at the **test** inbox: N emails read, attachments landed in `Inbound\`, N ledger rows created, one malformed email moved to `Errors\` + notification sent. Re-run creates zero duplicates | #1, #2, #10, #11 | **6** |
| **S2** | **Pace integration** | Against staging: read a real PO → line → receipt, then `createBillBatch` → `createBill` → `createBillLine` tied to that receipt, via the generated slim client. Every request/response captured to `docs\pace\fixtures\` | #6, #13, P1 | **3** |
| **P4** | **Parse invoices — Sanmar + 1 more** | Drop real files → table of extracted vendor / invoice no. / date / **PO number** / **amount**, including one Excel workbook fanned out into many invoices | #15, #16 | **7** |
| **P5** | **Categories 1/2/3 + DRY RUN ★** | Real day's mail classified: *"62 received = 41 Cat-1 + 9 Cat-2 + 7 Cat-3 + 3 already entered + 2 error"*, walked line by line with Jessica. **Nothing written to Pace** | P3, P4, S2, #19 | **6** |
| **P6** | **Four reports + reconciliation** | The actual emails AP will receive, built from dry-run data, reviewed by AP before a single real bill exists | P5, #18 | **4** |
| **P7** | **Full test pass (test env)** | `WriteEnabled=true` against Pace **staging**: created bills match P5's predicted payloads exactly. Month-end simulation with two batches open. Killed-mid-run recovery demonstrated | P6, #20 | **5** |
| **P8** | **Production deploy + scheduled task** | Running unattended on the real server as the service account, hourly 6am–6pm weekdays; monitoring query shows the last 20 runs | #3, #4, #5, #7, #9, #12 | **4** |
| P4b | Vendors 3–5 | Each added as registry rows + one extractor class — **no changes to matching, reporting or the write path** | #17 | 9 |
| P9 | Pilot & stabilization | Two consecutive weeks of daily reports, AP sign-off, zero reconciliation failures | P8 | 4 |

### The three specific goals map to P5

P5 is where the business requirement actually lands. All three categories are one classifier with one shared PO-line lookup, which is why they are a single phase rather than three:

| Category | Condition | Action | Outcome |
|---|---|---|---|
| **1 — Match & Post** | PO found, **the invoice's PO line** received, receipt unbilled | create bill + bill line tied to the receipt, in the day's batch, **unposted** | `BILL CREATED` |
| **2 — Match, Not Received** | PO found, that line not received | flag for follow-up; report shows days waiting against the 6-day window | `PO NOT RECEIVED` |
| **3 — No PO** | no PO on the invoice, or not found in Pace | flag for manual PO creation | `NO PO` |

Plus the two outcomes the categories don't cover, which is why there are five: `ALREADY ENTERED` (receipt already consumed, or a duplicate invoice number) and `ERROR` (unreadable, unknown format, missing invoice number).

**Note on "Post" in Category 1:** the engine creates the bill and stops. Posting stays a human step in Pace, per the kickoff commitment that nothing posts automatically. `postBillBatchTrn` is deliberately excluded from the generated client so the engine physically cannot post.

### Why P5 is the milestone worth aiming for

With `Pace:WriteEnabled=false` the whole pipeline runs against the real mailbox and real Pace *reads*, storing the exact bill JSON it *would* have posted. Nothing is written. It is the first trustworthy demo, it is reviewable by AP before any real payable exists, and it is what earns the trust that P7 and P8 spend.

**S2 answers the Pace questions no design can settle** — decided 2026-08-19: there is **no separate browser-only spike phase**. Pace staging already exists, so discovery happens inside S2 rather than as a phase of its own. The questions still have to be answered before anything relies on the answers:

- does `createBillLine` auto-derive quantity and price from `purchaseOrderReceipt`, or must both be supplied?
- does creating the bill line consume or decrement the receipt?
- what does Pace reject — closed accounting period, closed job, already-billed receipt, over-receipt?
- what shape are the error payloads, and which are transient vs deterministic?
- is `Posted = false` the default, and is it safe?

**Staging only, and commit every request/response.** Those files are simultaneously the S2 acceptance criteria, the unit-test fixtures, and the only written record of Pace's real behaviour. S2 also settles whether the consultant's unverified "we created a test bill" claim holds; there is no code artifact for it anywhere on this machine.

**Parallelism** is mostly *"don't let a blocked phase stop you"*: S2 ∥ P1 ∥ P2 (different blockers); P4 can start against synthetic fixtures while real samples are chased, then be validated against real files; P6 drafted during P5; P4b ∥ P7/P8.

### Estimate — Engine only, website excluded

Assumes one developer, a dev-day = ~5 focused hours, testing included, prior-art reuse credited (Pace REST pattern: large credit; queue schema: moderate; Graph/PDF/Excel: none — all new).

| | Effort | Throughput | Calendar |
|---|---|---|---|
| Best | 34 d | 4 d/wk | **~9 weeks** |
| **Expected** | **54 d** | **3.5 d/wk** | **~15–16 weeks (~3.5 months)** |
| Worst | 85 d | 3 d/wk | **~28 weeks** |

Engine core (P0–P9 including Pace integration, excluding P4b) is **~46 days expected**: P0 2 · P1 2 · P2 3 · P3 6 · S2 3 · P4 7 · P5 6 · P6 4 · P7 5 · P8 4 · P9 4. Adding P4b (vendors 3–5) brings Phase 1 to **~55 days**.

**P0's 2 days are your effort only.** The calendar cost of waiting on IT, Network and Accounting is not in this number and is not under your control — which is the single biggest reason the elapsed time is ~3.5 months while the coding is ~11 weeks.

**How blockers move the date without adding effort:** the **Graph app registration (#1) is the worst-timed risk** — P3 comes early, when there is little else to swap to, so every week of delay is close to a 1:1 calendar slip. **Sample invoices (#15/#16) arriving late** (week 8+) compresses P4/P4b against P7/P8 for a 2–4 week slip. **Month-end (#20)** deciding late risks 1–2 weeks plus real rework. Guessing **"hold" semantics (#19)** wrong costs 3–5 rework days in the highest-risk phase. The **scheduled-task account (#4)** and **production folders (#12)** only bite at P8, but they are the classic items nobody requests until the day they are needed — request them in P0 anyway.

**Framing for Elizabeth and Chase:** *"Roughly three and a half months elapsed, of which about eleven weeks is my hands on the keyboard. The gap is waiting on decisions and access — here's the list and who owns each item."*

---

## Jira structure

**Recommendation: a new team-managed software project, key `APA`.** `SD` is a service-desk project whose only issue type is "Submit a Request" — it gives no epic hierarchy, no board, no rollup, and a multi-month build inside it becomes a flat list nobody reads as progress.

**CHG-185 stays where it is** as the business change record ("what did we agree to"); `APA` becomes "where is it." Link them, and comment on CHG-185 that it predates the 8/17 reconciliation.

**Epics:** `APA-E1` Foundation & Environments · `E2` Pace Integration · `E3` Email Ingestion & Ledger · `E4` Formats & Extraction · `E5` Matching & Outcomes · `E6` Bill & Batch Creation · `E7` Reporting & Reconciliation · `E8` Scheduling, Deploy & Ops · **`E9` Business Decisions & Blockers** · `E10` Pilot & Go-Live.

`APA-E9` is the important one — every ticket business-owned, with a due date, naming the chunk it blocks. Every item in [prerequisites-checklist.md](./prerequisites-checklist.md) becomes a ticket here.

**Four business-visible summary tickets** (read, not worked; updated weekly; written in AP's language) — this is the "business overview" ask:
1. `APA-1` Status Overview & Weekly Progress — a live table: chunk, plain-English goal, status, last demo, next demo
2. `APA-2` Open Business Decisions Awaiting Answers — makes "we're blocked on you" visible without a conversation
3. `APA-3` Dry Run Results — created at P5; outcome counts, reconciliation proof, accuracy vs. AP's manual judgement
4. `APA-4` Phase 1 Scope: Vendors & Formats Covered — answers "how much of my workload is automated now"

**Components:** `Ingestion`, `Extraction`, `Matching`, `PaceWrite`, `Reporting`, `Scheduling`, `Database`, `Ops`.
**Custom fields — three, no more:** `Business Owner`, `Answer Needed By`, `Delivery Phase (P0–P9)`.
**Dashboard "AP Automation — Business View":** the four summary tickets pinned; open blockers sorted by `Answer Needed By`; `Delivery Chunk` × status two-dimensional; issues by component; created-vs-resolved.
**Convention to enforce:** a stalled dev ticket gets blocked-by-linked to its `E9` decision ticket and moved to `Blocked`. Then the dashboard tells the true story on its own.

---

## P1 — Project & GitHub setup (2 days)

In `c:\Users\galina.zezetko\source\repos\APAutomation\` (currently docs only, **no git at all**):

1. **`git init`**, `.gitignore` (VS + .NET template), `.editorconfig`. Create the GitHub repo under the org, add the remote, push.
2. **Branches:** `main` (production) and `dev` (base for work), `feature/...` per change, PR-reviewed — matching `WGS.Web.Portal\docs\DEVELOPER_ONBOARDING.md`. Branch protection on `main` and `dev`.
3. **`WG.AP.sln`** with the seven projects — 4 class libraries, 1 console app, 1 `.sqlproj`, 1 test project. All `net8.0`, `ImplicitUsings` + `Nullable` enabled.
4. **`WG.AP.Engine.Host`** — generic host + DI + `appsettings.{Env}.json` + `nlog.{Env}.config`, copying the bootstrap from `WGS.ShopifyProcessor\Program.cs`. **Credentials go in user secrets / a protected config, never in `appsettings.json`** — `WG.MarcomOrderProcessor`'s committed `sa` password is the pattern to avoid.
5. **`WG.AP.Tests`** wired up with one real (not skipped) placeholder test, so "tests run" is proven now rather than discovered to be broken in P7.
6. Commit `docs\pace\swagger.full.json` from the live staging URL, with the fetch date.

**Verification:** clone the repo fresh on a second machine → `WG.AP.sln` opens → builds with zero errors → `dotnet test` green. If a fresh clone doesn't build, P1 isn't done.

## P2 — Database + invoice ledger (3 days)

1. **`WG.AP.Database`** — `lkup` seed tables (`InvoiceOutcome`, `InvoiceProcessingStatus`, `DocumentType`, `ExtractionFieldType`, `ExtractionMethod`, `ProcessingRunStatus`, `ProcessingCalendar`), the ledger tables (`GraphMailMessage`, `GraphMailAttachment`, `InvoiceDocument`, `Invoice`, `InvoiceOutcomeHistory`), `mgt.ProcessingRun`, `mgt.APIIntegrationLog`, `rpt.ReconciliationCheck`, `rpt.vReconciliationDay`.
2. **Procs:** `mgt.up_ProcessingRunStart` / `End`, `dbo.up_InvoiceLogOnArrival` (TVP), **`dbo.up_InvoiceOutcomeSet`** (the *only* object permitted to write outcome state), `rpt.up_ReconciliationCheckBuild`, `rpt.up_ReconciliationRetrieveDay`.
3. **A `FileDropMailSource`** implementing `IMailSource` over a local folder — so the ledger is exercisable before Graph exists, and fixtures drive tests forever after.

### Verification for P2

```
1. Publish WG.AP.Database to APAutomationDev via Schema Compare -> clean, no drift on re-publish.
2. Drop a test PDF in .\_fixtures\inbound\
3. dotnet run --project src\WG.AP.Engine.Host -- --run-once ingest --business-date 2026-08-18
4. SELECT * FROM dbo.Invoice                 -> 1 row, CurrentInvoiceOutcomeID = 0 (PENDING)
   SELECT * FROM dbo.InvoiceOutcomeHistory   -> 1 row, IsCurrent = 1
   EXEC rpt.up_ReconciliationRetrieveDay '2026-08-18'
                                             -> InvoicesReceivedCount = 1, PendingCount = 1, IsBalanced = 0
5. Re-run step 3 verbatim -> STILL 1 row (idempotency via the document-slot unique index)
6. Delete the Invoice row, re-run -> row reappears (recovery path works)
7. Drop a deliberately corrupt PDF -> exactly 1 Invoice row, outcome ERROR, run does not crash
8. dotnet test -> green
```

Steps 5 and 7 are the ones that matter — they prove idempotency and the "no work leaves the count" rule, which everything downstream depends on.

---

## Open items that gate later chunks

Chase in this order. Full list in [prerequisites-checklist.md](./prerequisites-checklist.md).

The access/environment/input items are the numbered rows in **P0** above. The remaining items are *business decisions* rather than provisioning:

| Blocks | Decision needed | Owner |
|---|---|---|
| ~~**P5**~~ | ~~What does "hold" mean for an unreceived PO line?~~ 🟩 **RESOLVED 08-18** — PO line with a receipt → bill it; no receipt → error. PO need not be fully received | Accounting |
| P5 | Does "invoices received" include one ERROR row per unreadable document? *(sign off, or every reconciliation report gets argued with)* | Accounting |
| P5/P6 | 🟩 Duplicate = `ALREADY ENTERED` or `ERROR`? **The 08-18 notes say AP expects the "error report"** — this plan classifies it `ALREADY ENTERED`. Reconcile the terminology. Report cadence — hourly or twice daily? | AP |
| ~~**P7**~~ | ~~Month-end rule — three conflicting numbers~~ 🟩 **RESOLVED 08-18** — the 5-day rule, with a worked example. Open-vs-closed job status at EOM is still unaddressed | Accounting + Chase |
| ~~P7~~ | ~~Credit memos — do they consume a PO receipt?~~ 🟩 **RESOLVED 08-18** — yes, a **negative** receipt on the PO; or the PO number with `CR` appended | Accounting |
| S2 | Any artifact from the claimed "we created a test bill" — request it before rediscovering the sequence by hand | Chase |
| 🟩 P5 | 🟩 Is an **amount match a gate or a flag**? The 08-18 checkpoint note says *"if a line is received **and match the total amount due**… it should create the invoice"*, but §2.2 says amounts follow the invoice and discrepancies are flagged while the bill is still created. These cannot both be true. **Proposed 08-19, awaiting Accounting's confirmation: create the bill and flag the difference on the report** — i.e. a flag, not a gate. Recorded the same way in the prerequisites document | Accounting |
| 🟩 P8+ | 🟩 The notes' Phase 8 says *"start posting without review."* This plan's engine **cannot post** — `postBillBatchTrn` is excluded from the generated client by design. Confirm posting stays human, or treat it as a separate future project with its own risk review | Chase / Accounting |

Deliberately deferrable without stalling anything: the 6-day escalation (manual off the report until asked otherwise), and the print-ready PO+invoice package (still unanswered in the 08-18 notes — report first, print later).

## Definition of done for the Engine

**Functional** — every arriving invoice logged before processing; every logged invoice in exactly one of five outcomes; reconciliation holds for 30 consecutive days with zero unclassified; bills created **unposted** in a batch keyed to the invoice date's period, with N batches open at month end; all five Phase-1 vendors process end to end including one multi-invoice file; unprocessable email → Errors folder + notification, run continues.

**Non-functional** — runs unattended as the service account, weekdays 06:00–18:00, never weekends/holidays; a killed process loses nothing and double-creates nothing; one bad invoice cannot abort the batch; no credentials in source-controlled config; a monitoring query answers "did it run, what did it do, what failed" without a developer; the runbook exists and someone else has followed it once.

**Business** — Jessica has reviewed a dry-run day and a test-write day at ≥95% agreement on outcome and 100% on invoice number/total; Accounting has signed off on month-end and "hold" behaviour **as implemented**; Elizabeth or Chase signed go-live.

**Pilot gate** — live against production mail with a **vendor allow-list** (Sanmar + one other); `WriteEnabled=true` but **every bill left unposted** for daily AP review; per-invoice audit trail from source email to bill id; two consecutive weeks with zero reconciliation failures before widening one vendor at a time. Documented rollback: empty the allow-list or set `WriteEnabled=false`, and AP resumes manually with no data loss — the ledger recorded everything regardless.

---

## 🟩 Added from the 08-18 notes

<div class="added">

Source: `AP_Invoice_Automation_Project_Plan-Draft02-08-18-2026-Notes.docx` (2026-08-18 12:25). Everything in this section is **added, not edited** — no pre-existing plan content was changed except marking resolved items as resolved. Quotes are verbatim from the notes, typos and all.

### A. "Hold" semantics — RESOLVED *(was P0 #19, the highest-cost item to get wrong)*

> "We spoke on this and received conformation that if the PO line has a receipt we can apply and create the bill if not then it will go to error the Po does not need to be fully received but the PO lines do in order to apply."

**The rule:** receipt on the invoice's PO line → create the bill. No receipt on that line → error. **The PO does not need to be fully received; the line does.**

**Impact: none — this confirms the design as built.** `PoLineMatcher` in `WG.AP.Core\Matching\` already decides at line grain and treats PO header status as corroborating only. This closes the disagreement between the earlier draft ("the invoice is held") and Elizabeth's reading ("we post per line, we do not hold"). Elizabeth was right.

**One terminology gap:** the note says an unreceived line goes "to error," but this plan has `PO NOT RECEIVED` as its own outcome, distinct from `ERROR`. The notes use "error report" loosely throughout. Confirm which is meant — it changes which report the invoice appears on, and the five-outcome reconciliation depends on the distinction.

### B. Month-end — RESOLVED *(was P0 #20; three conflicting numbers are now one)*

> "We spoke on this if a vendor Invoice comes of the first 5 days of the month and they are date to the prior month then a bill back needs to be created for that GL period. Example. Invoice A Comes in on 8/02/2026 and the vendor invoice is date 7/31/2026 then the Bill batch GL period should be 07-2026"

> "After the 5 days the Bill batch will be the current GL Period Accounting will review and make any adjustments if needed."

**The rule — 5 days, not 3:**

| Invoice arrives | Invoice dated | Bill batch GL period |
|---|---|---|
| Days 1–5 of the month | prior month | **prior month** |
| Days 1–5 of the month | current month | current month |
| Day 6 onward | prior month | **current month** — Accounting adjusts manually if needed |

**Impact: none structurally.** `intgr.PaceBillBatch` is already keyed `(AccountingPeriodKey, BatchDate, BatchSequence)`, so N open batches at month end falls out of the key with no code branch. The 5-day cutover becomes a row in `ap.PeriodCutoverRule`, not an `if`. The "after day 5 → current period" half is *simpler* than what was designed for — no unbounded prior-period window.

**Still unaddressed:** Elizabeth's point that open-vs-closed **job** status matters at EOM, not just the invoice date. Also confirm behaviour when the target Pace `GLAccountingPeriod` is already closed — recommend `ERROR` with reason `PERIOD_CLOSED` rather than silently landing in the current period.

### C. Credit memos — RESOLVED *(mechanism now exists)*

> "We discussed and understand that if there is a negative receipt with the PO that listed on the vendor invoice then we can grab it and tie the Bill Line with the Po Receipt if the vendor bill has PO number with the CR on it then we can use that."

**The rule:** a credit ties to a **negative PO receipt**, matched either by the PO number on the invoice or by that PO number with `CR` appended.

**Impact: a welcome simplification.** Credits go down the *same* path as debits — find the receipt, tie the bill line to it. The schema question of whether `CK_dbo_Invoice_BillCreatedRequiresReceipt` needed an `IsCreditMemo` carve-out is answered: **no carve-out** — credits consume a receipt too, just a negative one. `dbo.Vendor.CreditMemoPOSuffix` (default `CR`) and `IsCreditMemo` already exist in the design.

**Confirm:** does the receipt lookup need to search *both* the bare PO number and the `CR`-suffixed variant, or does the vendor always print one form? And can a credit legitimately arrive with no negative receipt yet — in which case it is `PO NOT RECEIVED`, not `ERROR`?

### D. New asset to request — four sample reports

> "There are also four sample reports for Accounting to look at, built from real invoices."

Added as **P0 #21**. These have never been handed over and are not on this machine. They are effectively a report specification built from real data, so obtaining them would substantially de-risk P6 and shortcut #18 (report content sign-off). Ask Chase.

### E. New conflict to resolve — Phase 8 "posting without review"

The notes' phase table ends with: *"8. Rollout — Add the remaining vendors once the pilot is trusted, and **start posting without review**."*

This directly contradicts a deliberate design decision: `InvokeProcess/postBillBatchTrn` is **excluded from the generated Pace client**, so the engine physically cannot post a batch. That exclusion is a safety property, not an oversight — it is what makes the residual `createBill` non-idempotency risk survivable, since a human reviews every batch before it reaches the GL.

**Recommendation:** keep posting human for Phase 1 and treat "automated posting" as a separate future project requiring its own risk review. Automating it reopens the duplicate-bill exposure documented under *Database design*. Added to the open decisions table.

### F. Unchanged conflicts worth restating

- **§5 "What Has Already Been Proven"** still asserts a test bill was created in Pace and that every sample invoice is a clean text PDF. The **test bill remains unverified** — no code artifact exists anywhere on this machine; S2 covers it. The **PDF claim is now settled and was wrong as an absolute**: Sanmar sends *either* a PDF *or* an Excel workbook (resolved 08-19), so both readers are in scope. P0 #15 covers the samples.
- **The printing question** ("So we want to print the POs from Pace and print the invoices?") is still unanswered in the 08-18 notes — it has now gone two revisions without a reply. Still deferrable.
- **Duplicate terminology:** the notes confirm AP expects duplicates on "an error report," while this plan classifies them `ALREADY ENTERED`. Added to the open decisions table.

### G. Net effect on the plan

| | Before 08-18 notes | After |
|---|---|---|
| P0 blockers outstanding | 20 | **18** (#19, #20 closed; #21 added) |
| Business decisions blocking P5 | hold semantics **+** 2 others | 2 others + amount-match question |
| Business decisions blocking P7 | month-end **+** credits | none — both resolved |
| Design rework required | — | **none** |

**No phase estimate changes.** All three resolutions confirm or simplify what was already designed, which is the outcome you want from a review — the design absorbed the answers rather than needing to be rebuilt around them.

</div>

## Persisting these decisions

Three places, deliberately, because they serve different readers.

### 1. This plan file
`C:\Users\galina.zezetko\.claude\plans\i-need-prerequizite-before-clever-pie.md` — the working document. Full detail, edited as decisions change.

### 2. Copy into the repo — `docs\references\project-plan.md`
The plan belongs alongside the other project docs so the team and the consultant can read it without access to a Claude session, and so it is version-controlled with the code once P1 creates the git repo. Sits beside the existing [prerequisites-checklist.md](./prerequisites-checklist.md) and [jira-tickets.md](./jira-tickets.md).

Note the relationship: **prerequisites-checklist.md is the *questions*; P0 of this plan is the *work items* those questions became.** Keep both — the checklist is what was asked, P0 is what is tracked.

### 3. Memory files — for future sessions
Written to `C:\Users\galina.zezetko\.claude\projects\c--Users-galina-zezetko-source-repos-APAutomation\memory\`. These load automatically in a new session; the plan file does not.

**`ap_automation_architecture.md`** *(type: project)* — the confirmed technical decisions:
- Standalone `WG.AP.sln` in the `APAutomation` repo — **not** inside `WGS.Web.Portal`
- **7 projects**: 4 class libraries (`Core`, `DataAccess`, `Extraction`, `Integrations`) + `Engine.Host` console app + `WG.AP.Database` `.sqlproj` + `WG.AP.Tests`. Rejected: a 2-library and a 6-library variant
- The engine is **class libraries**; the console app is a thin host; a future website is a **second host** over the same libraries, not a replacement
- **Console Exe + Windows Task Scheduler** — explicitly not Quartz, not a Windows Service. The business window and holiday calendar live in DB tables, so the scheduled task is configured once
- Dedicated `APAutomationDev` / `APAutomation` SQL DB, stored-proc access via Dapper, NLog
- Pace spec: `https://pacestaging.wallacegraphics.com/static/swaggerUI/swagger.json` (verified working 2026-08-18)
- Three-tier vendor/format model: easy format = data only; hard format = one small tested class; adding a vendor never touches matching, reporting or the write path
- `postBillBatchTrn` deliberately excluded from the generated client so the engine physically cannot post a batch
- Links: [[pace_client_generated_not_handwritten]], [[project_overview]], [[jira_reference]]

**`pace_client_generated_not_handwritten.md`** *(type: feedback)* — generate a slim NSwag client from the filtered SDK swagger; do not hand-write the calls and do not reference the portal's 19.9 MB `EPaceGeneratedNSwagClient.cs`.
**Why:** generation reads EFI's contract; hand-written records encode a belief that drifts. It moves type errors from runtime to compile time, which matters because the dangerous AP failure is not "the call errored" but "the call succeeded and billed the wrong amount to the wrong job." Known landmines: `Vendor.Id` is a string while most Ids are int; money fields are `double` not `decimal`; per-field nullability; Pace's date formatting needed a dedicated converter in the portal.
**How to apply:** treat as settled — do not reopen. Commit the full spec plus the filter tool so the client stays regenerable (the portal's is orphaned from its source and cannot be regenerated).

**`MEMORY.md`** — add one pointer line per file above.

**`project_overview.md`** — needs updating, not replacing: its "Open items as of 2026-08-17" list is now stale. Prerequisite #4 (Pace integration method undocumented) is **closed** — the SDK, the live spec, and a working in-house precedent all exist. Several other entries are superseded by the P0 start gate.

<div class="added">

### 🟩 Regeneration needed after the 08-18 additions

Three derived artefacts are now **stale** and must be regenerated from this file:

1. `source\repos\APAutomation\docs\references\project-plan.md` — re-copy, rewriting the two relative link prefixes (`../meeting-notes/` → `../meeting-notes/`, `.../references/` → `./`). Use explicit UTF-8 (`[System.IO.File]::ReadAllText/WriteAllText`) — a plain `Get-Content`/`Set-Content` round-trip on PowerShell 5.1 mangles every em-dash.
2. `Documents\APAutomation\AP-Automation-Project-Plan.html` — regenerate via pandoc, **and add `.added` styling to the stylesheet** so the additions render green rather than as plain text:
   ```css
   div.added { background:#e9f7ec; border-left:4px solid #2e7d32; padding:.9rem 1.2rem; margin:1.2rem 0; }
   div.added h3, div.added h4 { color:#1b5e20; margin-top:.6rem; }
   ```
3. `Documents\APAutomation\AP-Automation-Project-Plan.docx` — regenerate. Note the `<div class="added">` wrappers are dropped in Word output, so the 🟩 markers and **bold labels** are what make additions visible there.

Also update memory: `project_overview.md`'s "Still blocking" list should drop hold semantics, month-end and credit memos, and gain the four-sample-reports request and the posting-without-review conflict.

</div>
