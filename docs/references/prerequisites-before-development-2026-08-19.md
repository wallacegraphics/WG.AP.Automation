# AP Automation — What Is Needed Before Development Can Start

**Prepared:** 19 August 2026

**Scope:** first phase, **Sanmar only.** Later vendors are added as configuration and will need nothing from this list except additional sample files.

Items 1–9 are needed to build and test. Items 10–16 are for the live system — not needed immediately, but listed now because they involve the same people and have the longest lead times.

---

# Part 1 — Test and development environment

## At a glance

| # | What is needed | Who provides it | Without it we cannot |
|---|---|---|---|
| 1 | Development database | Network / IT | Log invoices, produce reports, prove the daily count reconciles |
| 2 | Email access — test mailbox and Microsoft Graph | Network / IT | Read invoices from email |
| 3 | Sanmar invoice samples | AP | Read the contents of an invoice |
| 4 | Pace test system | IT / Accounting | Match invoices to purchase orders |
| 5 | Report content | AP / Accounting | Know what to build, or who receives it |
| 6 | Server and service account | Diana / IT | Run without a person logged in |
| 7 | Scheduled task account and password | IT | Start the hourly run automatically |
| 8 | File storage folders | Network | Save invoice attachments anywhere |
| 9 | Open business decisions | Accounting | Build the matching rules correctly |

Items 1, 2 and 3 are needed immediately. The rest have long lead times and should be requested now even though they are used later.

---

## 1. Development database (Network / IT)

A new database, used to log every processed invoice and generate the reports. Please provide:

**a.** Server / instance name

**b.** Database name

**c.** Authentication method

**d.** Permission for the developer to publish the database schema from Visual Studio

A database on its own is not enough. Without **d**, the structure has to be built by hand, which cannot then be repeated reliably when the live database is created.

## 2. Email access — test mailbox and Microsoft Graph (Network / IT)

These items replace the earlier general request for "access to Microsoft Exchange". Personal mailbox access, already granted, is enough for a person to read mail by hand but **not** for an application running unattended. The application needs **read and write, plus send** — read-only permission will not work, because it moves handled mail into subfolders and emails the reports.

**a. Test mailbox** — a dedicated test inbox with real sample mail copied into it, and the developer granted access. Development must not run against the live AP inbox: the process moves messages into a Processed or Errors folder, which would disturb the AP team's daily work.

**b. Application registration** — `Mail.ReadWrite` and `Mail.Send`, granted as **application** permissions, with administrator consent.

**c. Application access policy** — scoped to a mail-enabled security group containing **both** the test mailbox and the live AP mailbox. Without a policy the registration can read every mailbox in the company. Scoped to the live mailbox alone, the test mailbox is unreachable and development cannot proceed. Setting this up as a group means adding the live mailbox later needs no new request — see item 11.

**d. Immutable message IDs enabled** — the application must be able to request identifiers that do not change. By default a message's identifier changes when it is moved between folders, and this process moves mail into a Processed or Errors folder. Without this the system cannot tell it has already handled a message, and the same invoices would be processed over and over.

**e. Client secret** — with the **expiry date recorded and an owner assigned to renew it.** Typically valid 24 months. When it lapses the application stops working with no warning.

> Mail folders do not need to be created by hand. The application creates its own Processed, Errors and Needs-Review folders on first run, in both the test and live mailboxes.

## 3. Sanmar invoice samples (AP)

Two different things are needed here, and one does not replace the other.

**a. One complete day of mailbox traffic** — every message received on one normal business day, **including non-invoice mail and junk, unfiltered.** This gives the true daily mix and is the only way to verify that the count reported each day reconciles to what actually arrived. Filtering it first removes exactly what needs testing.

**b. Sanmar invoice formats — including any that did not arrive on that day.** A single day will not contain every format Sanmar sends, so these have to be gathered separately.

An email from Sanmar arrives with **either a PDF or an Excel attachment.** Both forms occur, they are not sent as a pair, and neither is contained inside the other. A real example of each of the following is needed:

| Format wanted | Why |
|---|---|
| Excel workbook containing many invoices in one file | One of the two ways Sanmar sends invoices |
| PDF with selectable text | The other. Later vendors are expected to be mostly PDF, so this reader carries straight over to them |
| Scanned / image-only PDF, if any exist | Determines whether text recognition (OCR) is needed at all — a large difference in cost and time |
| An email with several attachments | Confirms how multiple invoices in one email are separated |
| An email whose attachments include a signature logo image | These must be recognised as not-an-invoice |

**c. Business scenarios — one real example of each.** These are rare enough that a single day will almost certainly not contain them, so they need to be picked deliberately:

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

**d. The expected result for each sample** — the purchase order number and what the correct outcome should be. This becomes the checklist the first demonstration is measured against, and it is almost free to produce while the files are being gathered.

## 4. Pace test system (IT / Accounting)

**a. Pace staging API credentials (IT)**

**b. Pace staging loaded with copied live data (Accounting)** — without real purchase orders in the test system, the matching cannot be validated at all.

**c. Example purchase orders created in Pace staging (Accounting)** — specifically, purchase orders matching the sample invoices from item 3, with the correct receipt status for each scenario. If these do not exist, a genuine matching failure cannot be told apart from missing test data, and every test result becomes unreadable.

**d. One complete worked example (Accounting)** — a single transaction done manually in the test system, from purchase order through receipt to bill, so the automated version can be compared against a known-correct result.

## 5. Report content (AP / Accounting)

**a. The columns wanted on each report** — for the processed, discrepancy, pending-receipt and no-purchase-order reports.

**b. Who receives each report.**

**c. The four sample reports that already exist.** These are said to have been built from real invoices. They are the closest thing to a specification that exists and would answer most of **a** immediately, so please share them before writing anything new.

## 6. Server and service account (Diana / IT)

**a. Which server the application will run on** — not yet named in any document. Item 7 cannot be actioned until this is answered.

**b. A service account**, so the application runs independently of any person's account, with a written list of what it can reach: the database, the file storage, Pace, and the application server.

## 7. Scheduled task account and password (IT)

**a. The username and password** the scheduled task runs as.

**b. The "log on as a batch job" right** for that account on the application server. Without it the task fails silently — it appears configured and simply never runs.

**c. Either a non-expiring password, or a documented renewal process with a named owner.**

## 8. File storage folders — test (Network)

**a. The folder path.**

**b. Write access for the service account.**

**c. Confirmation that the location is backed up.**

**d. A retention policy.** The system stores invoice files that may need to be produced for up to seven years.

## 9. Open business decisions (Accounting)

Not access requests, but they determine how the matching is built. Changing them afterwards means reworking the core logic.

**a. Wording of outcomes** — should "purchase order not received", "already entered" and "error" be reported as three separate outcomes, or all grouped together as errors? The daily reconciliation depends on the distinction.

**b. When the invoice amount does not match the purchase order** — should the bill still be created with the difference flagged, or should the mismatch stop it? Current documents say both.

*Proposed answer, awaiting Accounting's confirmation: create the bill and show the difference as a flag on the report, rather than stopping it.*

**c. How often reports should be sent** — invoice processing runs hourly, which is settled. Report frequency is not: the kickoff notes suggest twice daily and describe the schedule as flexible. Please confirm what AP actually wants.

---

# Part 2 — Live (production) environment

Not needed to begin development, but requested now because these involve the same people and have the longest lead times.

**Two items from Part 1 are not repeated here:**

- **No test mailbox** — the live AP mailbox is used directly.
- **No sample invoices** — real invoices already arrive in the live mailbox.

## At a glance

| # | What is needed | Who provides it | Note |
|---|---|---|---|
| 10 | Production database | Network / IT | Created up front so the structure is only ever copied, never hand-built |
| 11 | Microsoft Graph access to the live AP mailbox | IT | Needs no new registration or secret if item 2c is set up as a group |
| 12 | Pace production credentials | IT / Chase | Held until the system is proven in test |
| 13 | Production server and service account | Diana / IT | May or may not be the same server as item 6 |
| 14 | Scheduled task account on the production server | IT | Same batch-job right as item 7 |
| 15 | File storage folders — production | Network | Same structure as item 8, separate location |
| 16 | Report distribution list for live running | AP | May differ from who reviews during testing |

---

## 10. Production database (Network / IT)

**a. Created up front**, at the same time as item 1 — so the structure is only ever copied from the tested version, never rebuilt by hand at go-live.

**b. A confirmed backup schedule.**

## 11. Microsoft Graph access to the live AP mailbox (IT)

**a.** The same application registration and client secret from item 2 are used. The only change needed is that the **live AP mailbox is included in the access policy group** from item 2c.

If that group is created correctly at the start, **this item costs nothing later — no new request, no new approval.** That is the entire reason for asking for a group rather than a single mailbox address.

## 12. Pace production credentials (IT / Chase)

**a.** Held until the system has been proven against the test environment. Not to be issued or used before then.

## 13. Production server and service account (Diana / IT)

**a. Whether production runs on the same server as item 6** or a separate one.

**b. Whether the developer can create and edit the scheduled task directly**, or must raise a request for each change. This determines how much friction every future update carries.

**c. Service account rights extended to production** — the production database, production file storage, and Pace production.

## 14. Scheduled task account on the production server (IT)

**a. The username and password** for the production server.

**b. The "log on as a batch job" right**, as in item 7b.

**c. A password renewal owner**, as in item 7c.

## 15. File storage folders — production (Network)

**a. The same folder structure as item 8**, in a separate location, agreed at the same time so go-live is not a surprise.

**b. Backup confirmed and the seven-year retention policy applied.**

## 16. Report distribution list for live running (AP)

**a.** Who actually receives each report once the system is live. This may differ from who reviews them during testing, and is often the last thing anyone thinks of.
