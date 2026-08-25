# AP Automation — Delivery Plan for Sanmar (Phase 1)

**Prepared:** 19 August 2026

## How to read this document

**Scope: Sanmar only.** This is the first phase. Later vendors are added as configuration rather
than new code, and vendors 3–5 would be roughly a further 9 developer-days that are **not** in any
number below.

**Two tables, two different kinds of estimate. They cannot be added together.**

| Table | Estimate means | Whose time |
|---|---|---|
| **Prerequisites (P1–P16)** | **Lead time** — calendar days from raising the request to having the thing | Other people's |
| **Development (D1–D20)** | **Developer-days** — a day being about 5 focused hours, testing included | Yours |

**Prerequisite lead times are estimates.** There is no historical data for how quickly requests
are turned around here, so they are informed guesses and should be corrected as real experience
arrives.

**The prerequisite total is not a sum.** Those items are requested and delivered in parallel, so
adding their lead times would give a meaningless number. The total shown is the longest single
item.

**Development runs in parallel with the waiting.** Prerequisites are assumed to arrive staggered
over the first weeks, never all at once. 30 of the 52 developer-days need no mailbox access and 36
need no server, so several weeks of delay can pass before anyone is idle.

**These are developer days only.** Steps D13, D15, D17 and D20 also consume Accounting and IT
time — reviewing outcomes, signing off, and checking bills daily through the pilot. That time is
real and is not counted here.

**Nothing posts automatically.** The engine creates vendor bills *unposted* in a batch, and a
person posts them in Pace. This is deliberate: the software is built so that it physically cannot
post.

**Three business decisions are still open** (P9). They sit inside the matching logic, so they are
expensive to change after the fact. One of the three has a proposed answer awaiting Accounting's
confirmation.

**Where the detail comes from.** This document combines two others, which remain current:
*AP Automation — What Is Needed Before Development Can Start* and
*AP Automation — Development Steps*. Numbering is prefixed `P` and `D` here so the two lists do
not collide; the numbers themselves are unchanged.

---

# Table A — Prerequisites (P1–P16)

| # | What is needed | Who provides it | Estimated lead time |
|---|---|---|---|
| P1 | Development database | Network / IT | 1–2 weeks |
| P2 | Email access — test mailbox and Microsoft Graph | Network / IT | 2–3 weeks |
| P3 | Sanmar invoice samples | AP | 2–5 days |
| P4 | Pace test system | IT / Accounting | 1–2 weeks |
| P5 | Report content | AP / Accounting | 3–5 days |
| P6 | Server and service account | Diana / IT | 2–3 weeks |
| P7 | Scheduled task account and password | IT | 1–2 weeks, after P6 |
| P8 | File storage folders | Network | 1–2 weeks |
| P9 | Open business decisions | Accounting | 3–5 days |
| P10 | Production database | Network / IT | 1–2 weeks |
| P11 | Microsoft Graph access to the live AP mailbox | IT | 1–3 days if P2 used a group, otherwise 2–3 weeks |
| P12 | Pace production credentials | IT / Chase | 1–2 weeks |
| P13 | Production server and service account | Diana / IT | 2–3 weeks |
| P14 | Scheduled task account on the production server | IT | 1–2 weeks |
| P15 | File storage folders — production | Network | 1–2 weeks |
| P16 | Report distribution list for live running | AP | 2–5 days |
| | **TOTAL — the longest item, not the sum** | | **2–3 weeks** |

**Your own effort across all sixteen is about 2 days** — writing the requests, chasing them, and
recording the answers. The calendar cost is not your effort and is not within your control.

**Which of these actually hold up the start:**

| Prerequisite | Steps that need it | Developer-days that do **not** need it |
|---|---|---|
| P1 database | D3 | needed almost immediately |
| P3 Sanmar samples | D5 | needed by about day 5 |
| P4 Pace test system | D6 | needed by about week 3 |
| P2 email access | D4 only | **30 days** |
| P6 server and service account | D11 onward | **36 days** |

P2 and P6 take the longest to obtain but have the most slack, because only D4 needs the mailbox
and nothing before D11 needs the server. **The three that genuinely gate the start are P1, P3 and
P4** — all of them among the quicker items to obtain.

---

# Table B — Development steps (D1–D20)

| # | Step | Depends on | Estimate |
|---|---|---|---|
| D1 | Project set up per architecture, including the test project | — | 1.5 d |
| D2 | GitHub setup | — | 0.5 d |
| D3 | Database, tables, rules, invoice ledger | P1 | 3 d |
| D4 | Read emails using Microsoft Graph | P2 | 6 d |
| D5 | Format parser engine | P3 | 7 d |
| D6 | Pace integration | P4 | 5 d |
| D7 | Matching and business logic | D3, D5, D6 | 7 d |
| D8 | Reports | D7 | 3 d |
| D9 | Email sender | D8 | 1 d |
| D10 | Tests across the whole pipeline | D3–D9 | 2 d |
| D11 | Deploy to the development server | P6 | 1 d |
| D12 | Start the scheduled task | D11, P7 | 0.5 d |
| D13 | **Testing round 1 — dry run, nothing written to Pace** | D12 | 2 d |
| D14 | Enable writing to Pace staging | D13 | 0.5 d |
| D15 | **Testing round 2 — write test against Pace staging** | D14 | 2.5 d |
| D16 | Fix issues found in testing | D15 | 2 d |
| D17 | Sign-off: development ready for production | D16 | 0.5 d |
| D18 | Confirm all production settings are in place | P10–P16 | 1.5 d |
| D19 | Move to production | D18 | 1.5 d |
| D20 | Pilot in production | D19 | 4 d |
| | **TOTAL** | | **52 developer-days** |

---

# Total estimate

## What the 52 days consist of

| Category | Steps | Days | Share |
|---|---|---|---|
| **Coding and testing** | D1–D10, D13, D15, D16 | **42.5 d** | 82% |
| **Deployment, sign-off and pilot** | D11, D12, D14, D17, D18, D19, D20 | **9.5 d** | 18% |
| | | **52 d** | 100% |

Within the coding and testing figure:

| | Steps | Days |
|---|---|---|
| Writing the engine | D1–D9 | 34 d |
| Testing and fixing | D10, D13, D15, D16 | 8.5 d |

## How that becomes elapsed time

At **4 productive days per week**, allowing for interruptions:

- **D1–D19:** 48 developer-days ÷ 4 = **12 weeks**
- **D20, the pilot:** **2 weeks of calendar**, using only 4 of those developer-days. The gate is
  two consecutive weeks with no reconciliation failures — a duration, not an amount of work, so it
  cannot be shortened by working harder.
- **Minimum elapsed, with no idle time at all: 14 weeks**

## The two scenarios

Neither assumes the prerequisites arrive together. Both assume requests are raised across the
first week or two. What separates them is whether **P1, P3 and P4** come back promptly, since
those three are what actually gate the start.

| | Shorter | Longer |
|---|---|---|
| Requests raised | across the first 1–2 weeks | staggered over 2–3 weeks |
| P1, P3, P4 arrive | within 2–3 weeks | 4 weeks or more |
| P2 and P6 arrive | any time within about 7 weeks | later than about 7 weeks |
| Idle waiting | none — parallel work absorbs it | 2–3 weeks |
| **Developer effort** | **52 days** | **52 days** |
| Build, D1–D19 | 12 weeks | 12 weeks |
| Pilot floor, D20 | 2 weeks | 2 weeks |
| **ELAPSED** | **14 weeks — about 3.5 months** | **16–17 weeks — about 4 months** |

**The developer effort is the same in both.** Delay does not add work; it adds waiting. That
distinction matters when reading the two numbers: what this costs and when it lands are separate
questions, and only the second is affected by how quickly requests are answered.

---

---

# Part 1 — Prerequisites in detail

## P1. Development database (Network / IT)

A new database, used to log every processed invoice and generate the reports. Please provide:

**a.** Server / instance name

**b.** Database name

**c.** Authentication method

**d.** Permission for the developer to publish the database schema from Visual Studio

A database on its own is not enough. Without **d**, the structure has to be built by hand, which
cannot then be repeated reliably when the live database is created.

## P2. Email access — test mailbox and Microsoft Graph (Network / IT)

These items replace the earlier general request for "access to Microsoft Exchange". Personal
mailbox access, already granted, is enough for a person to read mail by hand but **not** for an
application running unattended. The application needs **read and write, plus send** — read-only
permission will not work, because it moves handled mail into subfolders and emails the reports.

**a. Test mailbox** — a dedicated test inbox with real sample mail copied into it, and the
developer granted access. Development must not run against the live AP inbox: the process moves
messages into a Processed or Errors folder, which would disturb the AP team's daily work.

**b. Application registration** — `Mail.ReadWrite` and `Mail.Send`, granted as **application**
permissions, with administrator consent.

**c. Application access policy** — scoped to a mail-enabled security group containing **both** the
test mailbox and the live AP mailbox. Without a policy the registration can read every mailbox in
the company. Scoped to the live mailbox alone, the test mailbox is unreachable and development
cannot proceed. Setting this up as a group means adding the live mailbox later needs no new
request — see P11.

**d. Immutable message IDs enabled** — the application must be able to request identifiers that do
not change. By default a message's identifier changes when it is moved between folders, and this
process moves mail into a Processed or Errors folder. Without this the system cannot tell it has
already handled a message, and the same invoices would be processed over and over.

**e. Client secret** — with the **expiry date recorded and an owner assigned to renew it.**
Typically valid 24 months. When it lapses the application stops working with no warning.

> Mail folders do not need to be created by hand. The application creates its own Processed,
> Errors and Needs-Review folders on first run, in both the test and live mailboxes.

## P3. Sanmar invoice samples (AP)

Two different things are needed here, and one does not replace the other.

**a. One complete day of mailbox traffic** — every message received on one normal business day,
**including non-invoice mail and junk, unfiltered.** This gives the true daily mix and is the only
way to verify that the count reported each day reconciles to what actually arrived. Filtering it
first removes exactly what needs testing.

**b. Sanmar invoice formats — including any that did not arrive on that day.** A single day will
not contain every format Sanmar sends, so these have to be gathered separately.

An email from Sanmar arrives with **either a PDF or an Excel attachment.** Both forms occur, they
are not sent as a pair, and neither is contained inside the other. A real example of each of the
following is needed:

| Format wanted | Why |
|---|---|
| Excel workbook containing many invoices in one file | One of the two ways Sanmar sends invoices |
| PDF with selectable text | The other. Later vendors are expected to be mostly PDF, so this reader carries straight over to them |
| Scanned / image-only PDF, if any exist | Determines whether text recognition (OCR) is needed at all — a large difference in cost and time |
| An email with several attachments | Confirms how multiple invoices in one email are separated |
| An email whose attachments include a signature logo image | These must be recognised as not-an-invoice |

**c. Business scenarios — one real example of each.** These are rare enough that a single day will
almost certainly not contain them, so they need to be picked deliberately:

| Scenario | What it proves |
|---|---|
| Purchase order matched, line received | Invoice can be turned into a bill |
| Purchase order matched, line not yet received | Goes to the follow-up list |
| No purchase order on the invoice | Flagged for someone to create one |
| Credit memo — both the plain PO number and the PO number with "CR" | Credits match to a negative receipt |
| Month-end — dated to the prior month, arriving in the first days of the next | The prior-period rule works |
| The same invoice sent twice | Reported as already entered, not as an error |
| Invoice amount differs from the purchase order | The discrepancy is surfaced |
| Partial receipt on a purchase order with several lines | Matching happens line by line |
| Unreadable or corrupt attachment | Moved to the errors folder and notified |
| Invoice in the body of the email with no attachment | Confirms whether this happens at all |

**d. The expected result for each sample** — the purchase order number and what the correct
outcome should be. This becomes the checklist the first demonstration is measured against, and it
is almost free to produce while the files are being gathered.

## P4. Pace test system (IT / Accounting)

**a. Pace staging API credentials (IT)**

**b. Pace staging loaded with copied live data (Accounting)** — without real purchase orders in
the test system, the matching cannot be validated at all.

**c. Example purchase orders created in Pace staging (Accounting)** — specifically, purchase
orders matching the sample invoices from P3, with the correct receipt status for each scenario. If
these do not exist, a genuine matching failure cannot be told apart from missing test data, and
every test result becomes unreadable.

**d. One complete worked example (Accounting)** — a single transaction done manually in the test
system, from purchase order through receipt to bill, so the automated version can be compared
against a known-correct result.

## P5. Report content (AP / Accounting)

**a. The columns wanted on each report** — for the processed, discrepancy, pending-receipt and
no-purchase-order reports.

**b. Who receives each report.**

**c. The four sample reports that already exist.** These are said to have been built from real
invoices. They are the closest thing to a specification that exists and would answer most of **a**
immediately, so please share them before writing anything new.

## P6. Server and service account (Diana / IT)

**a. Which server the application will run on** — not yet named in any document. P7 cannot be
actioned until this is answered.

**b. A service account**, so the application runs independently of any person's account, with a
written list of what it can reach: the database, the file storage, Pace, and the application
server.

## P7. Scheduled task account and password (IT)

**a. The username and password** the scheduled task runs as.

**b. The "log on as a batch job" right** for that account on the application server. Without it
the task fails silently — it appears configured and simply never runs.

**c. Either a non-expiring password, or a documented renewal process with a named owner.**

## P8. File storage folders — test (Network)

**a. The folder path.**

**b. Write access for the service account.**

**c. Confirmation that the location is backed up.**

**d. A retention policy.** The system stores invoice files that may need to be produced for up to
seven years.

## P9. Open business decisions (Accounting)

Not access requests, but they determine how the matching is built. Changing them afterwards means
reworking the core logic.

**a. Wording of outcomes** — should "purchase order not received", "already entered" and "error"
be reported as three separate outcomes, or all grouped together as errors? The daily
reconciliation depends on the distinction.

**b. When the invoice amount does not match the purchase order** — should the bill still be
created with the difference flagged, or should the mismatch stop it? Current documents say both.

*Proposed answer, awaiting Accounting's confirmation: create the bill and show the difference as a
flag on the report, rather than stopping it.*

**c. How often reports should be sent** — invoice processing runs hourly, which is settled. Report
frequency is not: the kickoff notes suggest twice daily and describe the schedule as flexible.
Please confirm what AP actually wants.

---

## Live (production) prerequisites

Not needed to begin development, but requested now because these involve the same people and have
the longest lead times.

**Two items from above are not repeated here:**

- **No test mailbox** — the live AP mailbox is used directly.
- **No sample invoices** — real invoices already arrive in the live mailbox.

## P10. Production database (Network / IT)

**a. Created up front**, at the same time as P1 — so the structure is only ever copied from the
tested version, never rebuilt by hand at go-live.

**b. A confirmed backup schedule.**

## P11. Microsoft Graph access to the live AP mailbox (IT)

**a.** The same application registration and client secret from P2 are used. The only change
needed is that the **live AP mailbox is included in the access policy group** from P2c.

If that group is created correctly at the start, **this item costs nothing later — no new request,
no new approval.** That is the entire reason for asking for a group rather than a single mailbox
address.

## P12. Pace production credentials (IT / Chase)

**a.** Held until the system has been proven against the test environment. Not to be issued or
used before then.

## P13. Production server and service account (Diana / IT)

**a. Whether production runs on the same server as P6** or a separate one.

**b. Whether the developer can create and edit the scheduled task directly**, or must raise a
request for each change. This determines how much friction every future update carries.

**c. Service account rights extended to production** — the production database, production file
storage, and Pace production.

## P14. Scheduled task account on the production server (IT)

**a. The username and password** for the production server.

**b. The "log on as a batch job" right**, as in P7b.

**c. A password renewal owner**, as in P7c.

## P15. File storage folders — production (Network)

**a. The same folder structure as P8**, in a separate location, agreed at the same time so go-live
is not a surprise.

**b. Backup confirmed and the seven-year retention policy applied.**

## P16. Report distribution list for live running (AP)

**a.** Who actually receives each report once the system is live. This may differ from who reviews
them during testing, and is often the last thing anyone thinks of.

---

---

# Part 2 — Development steps in detail

## Work that depends on nothing

## D1. Project set up per architecture — 1.5 days

**a.** Solution with the four class libraries, the console application, the database project and
the test project.

**b. The test project is created now, not later**, with one real test that passes. Created at the
end instead, the first time anyone finds out it does not build is after everything else is already
sitting on top of it.

**c.** Configuration and logging in place, with credentials kept out of source control.

## D2. GitHub setup — 0.5 days

**a.** Repository, branches, and branch protection.

**b.** Verified by cloning fresh onto a second machine: it opens, it builds, the tests run green.
If a fresh clone does not build, this step is not finished.

---

## Building the engine

## D3. Database, tables, rules and connection to the project — 3 days

**a.** Tables, lookup values and the stored procedures the application uses.

**b. The invoice ledger** — every invoice recorded the moment it arrives, before anything is
attempted with it. This is what makes the daily count provable: what came in equals what was
reported.

**c. The reconciliation view** — received, classified, and the count that must balance.

**d.** Verified by running the application once against a sample file: one invoice row appears,
and running it a second time still gives one row, not two.

> The structure can be built against a local SQL instance before the development database (P1) is
> provisioned, so this step does not have to wait.

## D4. Read emails using Microsoft Graph — 6 days

**a.** Connect to the mailbox and read new messages.

**b.** Save attachments to the file storage folder.

**c.** Record every message and attachment in the ledger on arrival.

**d.** Move handled mail into Processed or Errors, and send a notification when something cannot
be read.

**e.** Verified by re-running: a second run must create no duplicates.

## D5. Format parser engine — 7 days

**a.** Read a Sanmar PDF and pull out vendor, invoice number, invoice date, purchase order number
and amount.

**b.** Read a Sanmar Excel workbook containing many invoices and separate it into one invoice each.

**c.** Ignore what is not an invoice — signature logos, and non-invoice email.

> **On "mostly no code":** that is true for *later* formats — once the engine exists, an easy
> vendor format is rows of configuration and no code at all. The *first* format still has to be
> written, and Sanmar's multi-invoice Excel workbook is the harder of the two shapes rather than
> the easier. The saving is real but it arrives at the second vendor, not the first.

> This step can begin against sample files kept on disk, before mailbox access (P2) exists.

## D6. Pace integration — 5 days

**a.** Generate the client from the Pace specification rather than writing it by hand.

**b.** Working against Pace **staging**, confirm and record how Pace actually behaves before
relying on it:

- does creating a bill line derive quantity and price from the receipt, or must both be supplied?
- does it consume the receipt?
- what does Pace reject — a closed accounting period, a closed job, a receipt already billed, more
  than was received?
- what do the error responses look like, and which are temporary rather than permanent?

**c.** Save every request and response. These become the test fixtures and the written record of
Pace's behaviour, so the answers do not have to be rediscovered later.

**d.** The command that posts a bill batch is deliberately left out of the generated client, so the
application physically cannot post. Posting stays a person's decision in Pace.

## D7. Matching and business logic — 7 days

**a. Every invoice ends in exactly one of five outcomes:**

| Outcome | When |
|---|---|
| Bill created | Purchase order found, the invoice's line has a receipt, that receipt is not already billed |
| Purchase order not received | Purchase order found, but that line has no receipt yet — goes to the follow-up list |
| No purchase order | No purchase order on the invoice, or it is not in Pace — flagged so someone can create one |
| Already entered | The receipt has already been billed, or the invoice number has been seen before |
| Error | Unreadable, unknown format, or missing an invoice number |

**b. Matching is per line, not per purchase order.** The purchase order does not need to be fully
received — the line on the invoice does.

**c. Month-end — the 5-day rule.** An invoice arriving in the first five days of a month and dated
to the prior month goes to the prior month's accounting period. After the fifth day it goes to the
current period and Accounting adjusts if needed.

**d. Credit memos** match to a negative receipt, found by the purchase order number on the credit or
that number with "CR" added. They follow the same path as ordinary invoices.

**e. Bills are created unposted**, in the day's batch. A person reviews and posts them in Pace.

## D8. Reports — 3 days

**a.** The four reports — processed, discrepancies, awaiting receipt, and no purchase order.

**b. The reconciliation count** — invoices received equals the sum of the five outcomes, with
nothing unclassified. This is the number that makes the rest believable.

**c.** Built from real dry-run data, so AP reviews the actual reports before a single real bill
exists.

## D9. Email sender — 1 day

**a.** Send the reports.

**b.** Send the notifications: success, problem, and nothing-to-process.

**c.** Sent to the distribution list agreed in P5, not to hard-coded addresses.

## D10. Tests across the whole pipeline — 2 days

**a.** The Pace requests and responses captured in D6, used as fixtures.

**b.** Sample invoices of each format.

**c.** The cases that matter most: running twice creates no duplicates, a stopped run loses nothing
and repeats nothing, and one bad invoice cannot bring down the batch.

---

## Testing on the development environment

## D11. Deploy to the development server — 1 day

**a.** Running as the service account, not as a person.

**b.** Pointing at the development database, the test mailbox and Pace staging.

## D12. Start the scheduled task — 0.5 days

**a.** Hourly, weekdays, within business hours.

**b.** Confirm it runs unattended with nobody logged in — this is where a missing "log on as a batch
job" right (P7b) shows up.

## D13. Testing round 1 — dry run, nothing written to Pace — 2 days

**Writing to Pace is switched off.** The whole pipeline runs against real mail and real Pace
*reads*, and stores the exact bill it *would* have created. Nothing is written anywhere in Pace.

**a.** A real day's mail classified, walked through line by line with Accounting: *"62 invoices
received — 41 billed, 9 awaiting receipt, 7 with no purchase order, 3 already entered, 2 errors."*

**b.** Reports produced from that data and reviewed by AP.

**c.** Reconciliation proved: what was reported equals what arrived.

**d.** Mail seen moving into Processed and Errors, and the notification emails received.

> **This is the first demonstration anyone can trust, because no real payable can exist yet.** It is
> worth taking time over — it earns the confidence that D19 and D20 spend.

## D14. Enable writing to Pace staging — 0.5 days

**a.** Writing switched on, against **staging only.**

## D15. Testing round 2 — write test against Pace staging — 2.5 days

**a.** The bills created must match exactly what round 1 predicted. Any difference is a genuine
finding.

**b.** Month-end tested with two batches open at once.

**c.** Recovery tested by stopping the run midway and restarting it — nothing lost, nothing
duplicated.

**d.** Signed off by Accounting and IT.

## D16. Fix issues found in testing — 2 days

**a.** Reserved time for the defects that D13 and D15 will produce.

**b.** Kept as its own step rather than hidden inside the testing estimates, so that a normal
amount of rework does not read as a test that overran.

## D17. Sign-off: development ready for production — 0.5 days

**a.** Accounting agrees the outcomes match their own judgement.

**b.** IT agrees the scheduled task, service account and logging are sound.

**c.** A named person signs go-live.

---

## Production

## D18. Confirm all production settings are in place — 1.5 days

**a.** Production database created and backed up (P10).

**b.** Mailbox access covering the live AP mailbox (P11).

**c.** Pace production credentials (P12).

**d.** Production file storage, backed up, with the retention policy applied (P15).

**e.** Service account and scheduled task on the production server (P13, P14).

**f.** No credentials in any file kept in source control.

## D19. Move to production — 1.5 days

**a.** Deploy and start the scheduled task.

**b.** A monitoring query that answers "did it run, what did it do, what failed" without needing a
developer.

**c.** A written runbook that somebody other than the developer has followed once.

## D20. Pilot in production — 4 days of effort, 2 weeks of calendar

Not a switch-on. A supervised period.

**a. One vendor only at first** — Sanmar. Everything else continues to be handled by hand.

**b. Every bill left unposted** for AP to review daily.

**c.** A full trail from the original email to the bill, for any invoice.

**d. Two consecutive weeks with no reconciliation failures** before adding a second vendor, then one
vendor at a time. This is why the pilot takes two weeks regardless of how much effort is available.

**e. Rollback, written down:** remove the vendor from the list or switch writing off, and AP resumes
manually with nothing lost — every invoice was recorded on arrival regardless of what happened to
it afterwards.
