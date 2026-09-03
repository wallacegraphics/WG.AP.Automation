# The AP database

Reference for `WG.AP.Database` — what the tables are for, which guarantees are enforced where, and
the traps that are easy to walk back into.

## Why it exists

Before this, all state lived in files: the Graph delta link in one JSON file per mailbox, extracted
invoice fields in another (dead code that was never even registered in DI), and the audit trail in a
daily text log. **Nothing recorded that an email had been seen.** So the delta link was the only
memory of what had been processed, and an email could be decided twice — a message left in the Inbox
was re-decided on every single run, forever.

The database is now the system of record. The rule the owner asked for — *"the emails cannot be
processed again if it is deleted, duplicate, failed or already processed"* — is a set of constraints
rather than a convention.

## Deploying

A SQL Database Project (`Microsoft.Build.Sql` SDK), published from Visual Studio or the CLI.

```
dotnet build WG.AP.Database/WG.AP.Database.sqlproj
sqlpackage /Action:Publish /SourceFile:WG.AP.Database/bin/Debug/WG.AP.Database.dacpac ^
           /Profile:WG.AP.Database/WG.AP.Database.Dev.publish.xml
```

Seeds are `MERGE`/`IF NOT EXISTS` in the post-deployment script, so **re-publishing is the normal
case** — that is how a change reaches Dev and then Prod. A no-change publish should report **zero**
schema operations; if it reports any, something has drifted and the *Publish churn* section below
explains the usual causes.

`Microsoft.Build.Sql` must be **2.2.0 or later**: 1.x imports a NuGet targets file from a path the
.NET 10 SDK no longer uses and cannot restore.

The Prod profile differs from Dev in three ways, all pointing the same direction — a publish must
never be able to destroy invoice history: `DropObjectsNotInSource=False` (a stale checkout cannot drop
a live table), `BlockOnPossibleDataLoss=True`, and `GenerateSmartDefaults=False` (a new `NOT NULL`
column ships with an explicit default or not at all).

## The tables

Two schemas: `lkup` for lookups, `dbo` for everything else.

| Table | One row per | Notes |
|---|---|---|
| `lkup.Status` | status | The no-reprocess mechanism. See below |
| `dbo.MailboxSyncState` | mailbox | The Graph delta link |
| `dbo.MailMessage` | email, ever | The row that makes "never twice" true |
| `dbo.MailAttachment` | attachment | Path + SHA-256; bytes live on the share |
| `dbo.Invoice` | invoice | The ledger. Carries the duplicate-number constraint |
| `dbo.Client` | supplier sending invoices | `ClientId 0` is the Unknown sentinel |
| `dbo.InvoiceFormat` | client + PDF layout | `ExtractorKey` selects the extractor |
| `dbo.ExtractionPrompt` | prompt version | The Ollama prompt, as versioned data |
| `dbo.ProcessingRun` | process execution | `IsSuccessful IS NULL` = it crashed |
| `dbo.ApplicationLog` | log event | No foreign keys, deliberately |

Plus one function, `dbo.NormalizeInvoiceNumber`, which is the single definition of "are these two
invoice numbers the same invoice".

## The no-reprocess guarantee

`lkup.Status.IsFinal` is the whole mechanism. Two statements in
`MailMessageRepository.DiscoverAndClaimAsync` carry it:

1. An `INSERT ... WHERE NOT EXISTS`, made safe by `UQ_MailMessage_MessageKeyHash`. Graph re-delivers a
   batch whenever a run crashes before the delta link is committed, so this runs against
   already-recorded messages constantly and must be a no-op when it does. **It never touches
   `StatusId`** — which is why a hundred re-deliveries of a processed message change nothing but
   `ModifiedOn`.
2. An `UPDATE` that joins `lkup.Status` and gates on `IsFinal = 0`. The gate is a `WHERE` clause, not
   an `if` in C#, so it cannot be forgotten at a call site — and a new no-reprocess reason becomes a
   seed row rather than a code change.

`@@ROWCOUNT = 0` means the message is finished: do not parse it, do not move it.

| Owner's word | Status | Enforced by |
|---|---|---|
| already processed | `MailProcessed`, and `MailSkipped` / `MailNeedsReview` | `IsFinal = 1` + the claim gate |
| duplicate (same email) | `MailDuplicate` | `UQ_MailMessage_MessageKeyHash` |
| duplicate (same number) | `InvoiceDuplicate` | `UQ_Invoice_ClientNumber` |
| failed | `MailError` | `IsFinal = 1` |
| deleted | `MailDeleted` | Not reachable yet — see *Known gaps* |

**`MailDeleted` is why this table has to exist.** A message the mailbox has already removed is left
where it is — there's nothing to move it into — so no folder move records the decision. The row is the
only thing stopping it being re-classified on every run. (`MailSkipped` used to be the example here; it
now routes to `NeedsReview` like everything else that needs a human look, so it no longer stays in the
Inbox.)

All of it depends on Graph immutable ids (`Prefer: IdType="ImmutableId"`, applied by
`GraphMailboxProcessor.ApplyImmutableId`). Without that header a message's id changes when it is moved
to `Processed`/`Errors`/`NeedsReview`, and every filed message would re-enter as new work forever.

### Transient failures have no status

A transient failure is represented by **the absence of a final state**, not by a status. The extractor
rethrows `HttpRequestException`/`TaskCanceledException`, nothing final commits, the delta link is not
advanced, and the row stays claimable — so the next run re-delivers it. Retry works exactly as it did
before this table existed, through the absence of a committed delta link.

`AttemptCount` and `LastAttemptOn` exist so "Ollama has been down for three runs" is a query rather
than a grep. At `Database:MaxAttempts` (3) the code routes the message to `MailNeedsReview` — never
`MailError`, because nobody should silently give up on a payable.

## Invoice identity

`UQ_Invoice_ClientNumber` on `dbo.Invoice.InvoiceDuplicateHash`. The code attempts the insert and
catches error 2601/2627: **the constraint decides, the code records.** A check-then-insert has a race
that a unique index does not.

The normalization rule (in `dbo.NormalizeInvoiceNumber`):

- **Leading zeros are kept.** `0012345` and `12345` are different invoices at some clients, and
  collapsing them would reject a real payable as a duplicate.
- Space, hyphen, **tab and NBSP** are stripped — PDF extraction produces the latter two, and
  `SanmarPdfHeaderExtractor`'s `\S+` captures include them.
- Upper-cased, so case cannot make a second invoice.
- A number that normalizes away to nothing (`'---'`) becomes `NULL`, i.e. "no usable number".

Two exclusions are built **into the key** rather than into a filter, and the reason is worth knowing
before anyone tries to simplify it:

- `ClientId = 0` (Unknown) is excluded, and this matters *more* as clients are added. Two **different**
  clients' identical invoice numbers would otherwise collide and one real payable would be rejected as
  already entered. Unknown-client invoices go to `InvoiceNeedsReview` instead.
- Rows with no usable number are excluded.

They cannot be expressed as a filtered index, because **a filtered index cannot reference a computed
column** (Msg 10609). Filtering on the raw `InvoiceNumber` is not equivalent: a SQL Server unique index
permits only **one** NULL, so a second unusable number would be rejected as a duplicate of the first —
the same false-duplicate failure the `ClientId` exclusion exists to prevent. So excluded rows get a
sentinel built from their own `InvoiceId`, which is unique by construction.

The key is a **hash**, not text, so uniqueness does not depend on the server's collation at all. Under
the default accent-insensitive collation, a text key would treat `INVA001` and `INVÁ001` as one
invoice. (A collated `nvarchar` key also reintroduced rebuild-on-every-publish — see below.)

## "Extracted" means complete

`CK_Invoice_ExtractedIsComplete` refuses an `InvoiceExtracted` row unless all five required fields are
present: client, invoice number, invoice date, customer PO and a **positive** total.
`APProcessor.Classify` applies the same rule, so the two cannot drift into disagreement — marking an
incomplete row as extracted fails the insert rather than quietly storing something that claims to be
complete.

Note the constraint needs **both** `[Total] IS NOT NULL AND [Total] > 0`. A CHECK rejects only on
FALSE, and `NULL > 0` is UNKNOWN, so `> 0` alone lets a null total through — which is the one value
most likely to appear when extraction quietly failed. `Tests/constraints.sql` covers this case
because the first version of the constraint had exactly that bug.

## The Ollama prompt

`dbo.ExtractionPrompt` holds prompt text, response schema and an optional model pin as **one versioned
row**, because the schema's `required` list is part of the prompt's contract with
`InvoiceFieldsJsonParser`, and the prompt was tuned against a specific model. Splitting them would let
one deploy without the other.

Rows are **inserted, never edited**. `dbo.Invoice.ExtractionPromptId` pins the version that produced
an extraction, so an invoice read in October is still explainable next July.
`UQ_ExtractionPrompt_OneActive` guarantees exactly one active version per format, so the per-run
lookup cannot fan out.

**Edit prompts in `Scripts/Seed/ExtractionPrompt.sql`, not in SSMS.** That file is the readable,
diffable, reviewable master copy, and publishing deploys it. An `UPDATE` in SSMS would quietly break
the insert-a-new-version rule, and SSMS's grid flattens a multi-line string onto one line anyway.
`SeededPromptTests` reads that file directly, so the tests and the deployed prompt cannot drift.

Newlines: the template is stored **CRLF** so it reads correctly in SSMS and Notepad, and normalized to
LF in `ExtractionPromptRepository` and again for the document text in
`PdfInvoiceFieldExtractor.BuildPrompt`. Without that, the exact bytes sent to the model would depend
on how git checked the seed file out and on which OS ran the extraction — and replaying a stored
extraction would not reproduce it.

### Onboarding client #2

Three `INSERT`s and no deploy: a `dbo.Client` row with its email domain, a `dbo.InvoiceFormat` row, and
a `dbo.ExtractionPrompt` row with `IsActive = 1`. The engine reloads the catalog every run.

`InvoiceFormat.ExtractorKey` is functional, not descriptive: `PdfInvoiceFieldExtractor` runs the SanMar
regex tier **only** when the key is `SANMAR_PDF_HEADER_V1`. With one client it did not matter that
those regexes ran against every PDF, because every PDF was SanMar's. With several they would be
pointed at another client's invoice — and although `TryExtract` is all-or-nothing, "every SanMar
pattern happens to match someone else's layout" is a silent wrong-data path rather than a crash.

## Application log

Three sinks, and the file one is **not** redundant: it is the only one that still works when the
database is what is broken, and the only one that cannot vanish with a rolled-back transaction. The
database sink defaults to `Warning` while the file stays at `Information`, so the file keeps the full
narrative of one run and the database keeps what is worth querying across runs.

`ProcessingRunId` and `MailMessageId` have **no foreign keys**, deliberately. An FK would make the log
insert fail precisely when the referenced row was rolled back — exactly when the line mattered. They
are correlation ids, not relationships, and this is the one place the schema gives up referential
integrity on purpose.

No columns for structured template properties, `EventId` or scopes, because `FileLogger` discards all
three today (`BeginScope` returns `null`). Adding them is a code change first.

Retention is **1 year**. Note the file log is configured at **60 days**, so the two differ
deliberately rather than by accident.

## Client requirements

`dbo.MailMessage` and `dbo.Invoice` carry indexes on persisted computed columns, so SQL Server refuses
any `INSERT`/`UPDATE` against them unless **`QUOTED_IDENTIFIER` and `ANSI_NULLS` are both ON**
(Msg 1934). `Microsoft.Data.SqlClient` sets both when it opens a connection, so the application is
fine. **SQLCMD defaults `QUOTED_IDENTIFIER` to OFF** — pass `-I`, or set it in the script.

Dapper cannot bind `DateOnly` parameters without help, and `InvoiceFields` uses `DateOnly?` for both
dates — so without the handlers in `DapperTypeHandlers` the first real invoice fails on a type the
compiler was perfectly happy with. They are registered from `SqlConnectionFactory`'s static
constructor, because every database call in `WG.AP.DataAccess` opens its connection there.

## Publish churn

A no-change publish should do **zero** schema operations. Two things break that, and both were hit
while building this:

**A computed column whose type SQL Server has to infer** (a bare function call, a bare `CASE`) does not
round-trip through the dacpac model, so sqlpackage decides the column changed and schedules a full
**table rebuild** — dropping and recreating every constraint on the table, every time. Wrap computed
columns in an explicit `CONVERT`. A computed column carrying a **non-default collation** has the same
effect, which is one reason the invoice duplicate key is a hash.

**`BETWEEN` and `IN` in CHECK constraints.** SQL Server rewrites both when it stores them (`IN` even
reorders the values alphabetically), the model keeps what was written, and they never match — so every
publish drops and recreates the constraint. Write them as SQL Server stores them: `>= x AND <= y`, and
ORed equalities in alphabetical order.

## Testing

`Tests/constraints.sql` — 40 assertions, run by hand against a published scratch database. Each one
asserts the **specific error number**, because the point is not that an error happened but that the
constraint protecting that case fired:

```
sqlcmd -S "(localdb)\MSSQLLocalDB" -d WG_AP_SchemaCheck -E -I -i WG.AP.Database/Tests/constraints.sql
```

The `-I` is not optional: it turns on `QUOTED_IDENTIFIER`, which SQLCMD defaults to OFF and which
these tables reject (Msg 1934). Without it every insert fails and the run looks like a schema fault.
The script rolls back everything it does, so it is re-runnable.

`WG.AP.Tests/DataAccess/SqlRepositoryTests.cs` exercises the Dapper repositories against a real
server. Opt-in — set `AP_TEST_DB_CONNECTION` to a published scratch database:

```
$env:AP_TEST_DB_CONNECTION = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=WG_AP_SchemaCheck;Integrated Security=True;Encrypt=False"
dotnet test WG.AP.Tests/WG.AP.Tests.csproj
```

**The summary line distinguishes ran-and-passed from could-not-run**, and it is worth reading:

| Output | Meaning |
|---|---|
| `Passed: 90, Skipped: 0` | The database tests ran (and local invoice fixtures were present) |
| `Passed: 82, Skipped: 8` | No connection string — the database layer was **not** covered |
| `Passed: 81, Skipped: 9` | What CI shows: no connection string and no local invoice fixtures |

That distinction exists because these tests use `Skip.If` (via `Xunit.SkippableFact`) rather than
returning early. An early return reports as *Passed*, so a run with no database looked identical to a
verified one — and these are the tests that caught the two bugs the compiler could not: a CHECK
constraint that let `NULL` through, and Dapper being unable to bind `DateOnly`. A green light covering
none of that is the most misleading place in the suite to have one. The package becomes removable once
the tracked `xunit` → `xunit.v3` migration lands, since v3 has `Assert.Skip` built in.

These are integration tests rather than unit tests on purpose. The guarantees this system rests on are
enforced by the database, not by C# — "a message is never claimed twice" is a unique index plus a
`WHERE` clause. A fake would just be a second, wrong implementation of the rule.

## Known gaps

- **`MailDeleted` is unreachable.** `GraphMailboxProcessor.GetInboxDeltaAsync` drops every `@removed`
  tombstone, so the database cannot learn that a human deleted a message. Surfacing them needs a
  removed-id list on `MailboxDeltaResult`. One subtlety first: **our own `MoveMessageAsync` also
  produces a tombstone**, because a folder-scoped delta emits one for anything that left the folder,
  and Graph's `reason` does not say who moved it. Disambiguate from our own state — a tombstone for a
  row that is already final is our move (ignore it); one for a row at `MailNew` is a human.
- **`internetMessageId` is not captured.** The immutable id identifies a *mailbox item*, not an
  *email*: it does not survive a mailbox restore, a PST re-import, or a human *copying* mail back into
  the Inbox, and each of those produces a new immutable id carrying the same `internetMessageId`. That
  is the honest gap in duplicate coverage. Adding it needs a deliberate deployment step, because
  **`$select` is baked into the returned `deltaLink`** — the column stays NULL until a one-time full
  resync.
- **No Pace outbox.** Deferred to the Pace ticket. One note for whoever picks it up: `createBill` is
  not idempotent and Pace offers no idempotency key, so the outbox row's GUID must be minted and
  committed *before* the first HTTP call and stamped into `Bill.Reference`, so a crash can be recovered
  by probing rather than guessing. `dbo.Client.PaceVendorId` is the seam.
- **No status history**, so "how long was this in NeedsReview" is not answerable. A message sees at
  most two transitions today and `ApplicationLog` carries the trail.
- **No scored format detection.** A client with two enabled formats is a configuration error rather
  than a detection problem: `ClientRepository` logs it and uses the first. That is the point at which
  detection-rule tables become necessary.
- **`MailNeedsReview` and `MailError` are final.** A human who fixes one re-opens it with
  `UPDATE dbo.MailMessage SET StatusId = 10 WHERE ...`. Moving the mail back into the Inbox by hand
  will *not* work — delta re-delivers it, the row is final, and it is skipped.
- **The UNC share has no assigned backup owner.** The database is useless without the files it points
  at, and 7-year retention makes a share failure a compliance event rather than an inconvenience.
