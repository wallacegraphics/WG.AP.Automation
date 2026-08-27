# AP Automation — Development Steps

**Prepared:** 19 August 2026

**Scope:** first phase, Sanmar only. Later vendors are added as configuration.

Steps 1 and 2 depend on nothing and can start immediately. Steps 3–10 build the engine. Steps 11–16 test it on the development environment in two rounds. Steps 17–19 move it to production.

## At a glance

| # | Step | Depends on | Can start before prerequisites arrive? |
|---|---|---|---|
| 1 | Project set up per architecture, including the test project | — | **Yes** |
| 2 | GitHub setup | — | **Yes** |
| 3 | Database, tables, rules, invoice ledger | Development database | Partly — build locally first |
| 4 | Read emails using Microsoft Graph | Test mailbox, Graph access | No |
| 5 | Format parser engine | Sanmar sample files | Partly — sample files on disk first |
| 6 | Pace integration | Pace staging credentials | No |
| 7 | Matching and business logic | Steps 3, 5, 6 | No |
| 8 | Reports | Step 7 | No |
| 9 | Email sender | Step 8 | No |
| 10 | Tests across the whole pipeline | Steps 3–9 | No |
| 11 | Deploy to the development server | Server, service account | No |
| 12 | Start the scheduled task | Step 11 | No |
| 13 | **Testing round 1 — dry run, nothing written to Pace** | Step 12 | No |
| 14 | Enable writing to Pace staging | Step 13 signed off | No |
| 15 | **Testing round 2 — write test against Pace staging** | Step 14 | No |
| 16 | Sign-off: development ready for production | Step 15 | No |
| 17 | Confirm all production settings are in place | Production prerequisites | No |
| 18 | Move to production | Step 17 | No |
| 19 | Pilot in production | Step 18 | No |

---

# Part 1 — Work that depends on nothing

## 1. Project set up per architecture

**a.** Solution with the four class libraries, the console application, the database project and the test project.

**b. The test project is created now, not later**, with one real test that passes. Created at the end instead, the first time anyone finds out it does not build is after everything else is already sitting on top of it.

**c.** Configuration and logging in place, with credentials kept out of source control.

## 2. GitHub setup

**a.** Repository, branches, and branch protection.

**b.** Verified by cloning fresh onto a second machine: it opens, it builds, the tests run green. If a fresh clone does not build, this step is not finished.

---

# Part 2 — Building the engine

## 3. Database, tables, rules and connection to the project

**a.** Tables, lookup values and the stored procedures the application uses.

**b. The invoice ledger** — every invoice recorded the moment it arrives, before anything is attempted with it. This is what makes the daily count provable: what came in equals what was reported.

**c. The reconciliation view** — received, classified, and the count that must balance.

**d.** Verified by running the application once against a sample file: one invoice row appears, and running it a second time still gives one row, not two.

> The structure can be built against a local SQL instance before the development database is provisioned, so this step does not have to wait.

## 4. Read emails using Microsoft Graph

**a.** Connect to the mailbox and read new messages.

**b.** Save attachments to the file storage folder.

**c.** Record every message and attachment in the ledger on arrival.

**d.** Move handled mail into Processed or Errors, and send a notification when something cannot be read.

**e.** Verified by re-running: a second run must create no duplicates.

## 5. Format parser engine

**a.** Read a Sanmar PDF and pull out vendor, invoice number, invoice date, purchase order number and amount.

**b.** Read a Sanmar Excel workbook containing many invoices and separate it into one invoice each.

**c.** Ignore what is not an invoice — signature logos, and non-invoice email.

> **On "mostly no code":** that is true for *later* formats — once the engine exists, an easy vendor format is rows of configuration and no code at all. The *first* format still has to be written, and Sanmar's multi-invoice Excel workbook is the harder of the two shapes rather than the easier. The saving is real but it arrives at the second vendor, not the first.

> This step can begin against sample files kept on disk, before mailbox access exists.

## 6. Pace integration

**a.** Generate the client from the Pace specification rather than writing it by hand.

**b.** Working against Pace **staging**, confirm and record how Pace actually behaves before relying on it:

- does creating a bill line derive quantity and price from the receipt, or must both be supplied?
- does it consume the receipt?
- what does Pace reject — a closed accounting period, a closed job, a receipt already billed, more than was received?
- what do the error responses look like, and which are temporary rather than permanent?

**c.** Save every request and response. These become the test fixtures and the written record of Pace's behaviour, so the answers do not have to be rediscovered later.

**d.** The command that posts a bill batch is deliberately left out of the generated client, so the application physically cannot post. Posting stays a person's decision in Pace.

## 7. Matching and business logic

**a. Every invoice ends in exactly one of five outcomes:**

| Outcome | When |
|---|---|
| Bill created | Purchase order found, the invoice's line has a receipt, that receipt is not already billed |
| Purchase order not received | Purchase order found, but that line has no receipt yet — goes to the follow-up list |
| No purchase order | No purchase order on the invoice, or it is not in Pace — flagged so someone can create one |
| Already entered | The receipt has already been billed, or the invoice number has been seen before |
| Error | Unreadable, unknown format, or missing an invoice number |

**b. Matching is per line, not per purchase order.** The purchase order does not need to be fully received — the line on the invoice does.

**c. Month-end — the 5-day rule.** An invoice arriving in the first five days of a month and dated to the prior month goes to the prior month's accounting period. After the fifth day it goes to the current period and Accounting adjusts if needed.

**d. Credit memos** match to a negative receipt, found by the purchase order number on the credit or that number with "CR" added. They follow the same path as ordinary invoices.

**e. Bills are created unposted**, in the day's batch. A person reviews and posts them in Pace.

## 8. Reports

**a.** The four reports — processed, discrepancies, awaiting receipt, and no purchase order.

**b. The reconciliation count** — invoices received equals the sum of the five outcomes, with nothing unclassified. This is the number that makes the rest believable.

**c.** Built from real dry-run data, so AP reviews the actual reports before a single real bill exists.

## 9. Email sender

**a.** Send the reports.

**b.** Send the notifications: success, problem, and nothing-to-process.

**c.** Sent to the distribution list agreed in the prerequisites, not to hard-coded addresses.

## 10. Tests across the whole pipeline

**a.** The Pace requests and responses captured in step 6, used as fixtures.

**b.** Sample invoices of each format.

**c.** The cases that matter most: running twice creates no duplicates, a stopped run loses nothing and repeats nothing, and one bad invoice cannot bring down the batch.

---

# Part 3 — Testing on the development environment

## 11. Deploy to the development server

**a.** Running as the service account, not as a person.

**b.** Pointing at the development database, the test mailbox and Pace staging.

## 12. Start the scheduled task

**a.** Hourly, weekdays, within business hours.

**b.** Confirm it runs unattended with nobody logged in — this is where a missing "log on as a batch job" right shows up.

## 13. Testing round 1 — dry run, nothing written to Pace

**Writing to Pace is switched off.** The whole pipeline runs against real mail and real Pace *reads*, and stores the exact bill it *would* have created. Nothing is written anywhere in Pace.

**a.** A real day's mail classified, walked through line by line with Accounting: *"62 invoices received — 41 billed, 9 awaiting receipt, 7 with no purchase order, 3 already entered, 2 errors."*

**b.** Reports produced from that data and reviewed by AP.

**c.** Reconciliation proved: what was reported equals what arrived.

**d.** Mail seen moving into Processed and Errors, and the notification emails received.

> **This is the first demonstration anyone can trust, because no real payable can exist yet.** It is worth taking time over — it earns the confidence that steps 18 and 19 spend.

## 14. Enable writing to Pace staging

**a.** Writing switched on, against **staging only.**

## 15. Testing round 2 — write test against Pace staging

**a.** The bills created must match exactly what round 1 predicted. Any difference is a genuine finding.

**b.** Month-end tested with two batches open at once.

**c.** Recovery tested by stopping the run midway and restarting it — nothing lost, nothing duplicated.

**d.** Signed off by Accounting and IT.

## 16. Sign-off: development ready for production

**a.** Accounting agrees the outcomes match their own judgement.

**b.** IT agrees the scheduled task, service account and logging are sound.

**c.** A named person signs go-live.

---

# Part 4 — Production

## 17. Confirm all production settings are in place

**a.** Production database created and backed up.

**b.** Mailbox access covering the live AP mailbox.

**c.** Pace production credentials.

**d.** Production file storage, backed up, with the retention policy applied.

**e.** Service account and scheduled task on the production server.

**f.** No credentials in any file kept in source control.

## 18. Move to production

**a.** Deploy and start the scheduled task.

**b.** A monitoring query that answers "did it run, what did it do, what failed" without needing a developer.

**c.** A written runbook that somebody other than the developer has followed once.

## 19. Pilot in production

Not a switch-on. A supervised period.

**a. One vendor only at first** — Sanmar. Everything else continues to be handled by hand.

**b. Every bill left unposted** for AP to review daily.

**c.** A full trail from the original email to the bill, for any invoice.

**d. Two consecutive weeks with no reconciliation failures** before adding a second vendor, then one vendor at a time.

**e. Rollback, written down:** remove the vendor from the list or switch writing off, and AP resumes manually with nothing lost — every invoice was recorded on arrival regardless of what happened to it afterwards.
