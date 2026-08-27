# SDP Backlog vs. Sanmar Delivery Plan — Crosswalk

**Prepared:** 19 August 2026
**Sources:** Jira board SDP (label `ap-automation`, SDP-153 epic + SDP-154…SDP-175) and
[delivery-plan-sanmar-2026-08-19.md](delivery-plan-sanmar-2026-08-19.md)

**Counts:** 38 Jira items (1 Epic, 31 Task, 4 Spike, 1 Risk) vs. 36 plan items
(16 prerequisites P1–P16, 20 development steps D1–D20). SDP-176 and SDP-177 were added on
20 August 2026 for D1 and D2, and **SDP-178 through SDP-190** on 21 August 2026 for D3, D4, D5, D6,
D9, D10, D11, D12, D13, D14+D15, D18, D19 and D20; the tables below otherwise describe the board as
of 19 August.

**Only two development steps remain unticketed: D16 and D17** — 2.5 of the 52 developer-days.
Every prerequisite gap in Table 2 is unchanged, and that is now where essentially all the untracked
risk sits.

---

## Table 1 — Jira ticket → plan item

| Jira | Type | Title | Plan item | Relation |
|---|---|---|---|---|
| SDP-153 | Epic | AP Invoice Automation | whole plan | **Same project, different scope** — epic covers all vendors in the AP mailbox; plan is Sanmar-only Phase 1 |
| SDP-154 | Task | Galina access to shared inbox | P2a | **Related, not the same** — Jira grants access to the *live* AP inbox; plan asks for a *dedicated test mailbox* and states development must not run against live |
| SDP-155 | Task | Programmatic access to AP mailbox | P2b–P2e, P11 | **Same** — plan adds detail Jira lacks: scoped application access policy as a *group*, immutable message IDs, client-secret expiry owner |
| SDP-156 | Spike | Centralized storage location for PDFs | P8, P15 | **Same** — plan adds backup confirmation and 7-year retention, and a separate production location |
| SDP-157 | Task | Retrieve & save invoice attachments | **D4b, D4c, D4e** | **Half of D4** — rescoped 2026-08-21 to the persistence side: save attachment bytes, write the message and attachment ledger rows, decide Processed vs Errors, handle statements. ~2.5 d. Pairs with SDP-178 |
| SDP-158 | Task | Parse PDFs — PO & invoice number formats per vendor | D5a | **Part of D5, left open unchanged** — covers PDF field extraction only. D5 gained its own ticket, SDP-180, on 2026-08-21, which adds the Excel workbook fan-out and non-invoice rejection. The two overlap on item a |
| SDP-159 | Task | Define Vendor Invoice Status values | D7a, P9a | **Same idea, conflicting content** — Jira has **6** statuses (adds `STATEMENT`), plan has **5** outcomes |
| SDP-160 | Task | Match to PACE | D6b, D7b | **Part of D6, left open unchanged** — carries the proven 3-call staging API sequence. D6 gained its own ticket, SDP-181, on 2026-08-21 |
| SDP-161 | Task | Create Bill in PACE | D6b, D7e | **Part of D6, left open unchanged** — both keep bills unposted; Jira adds duplicate-invoice HTTP 500 handling (proven on staging) and GL/job coding copied from the PO line. See SDP-181 |
| SDP-162 | Task | Find or create the bill batch | D6b (partial) | **Mostly new, left open unchanged** — GL period open/closed gate (T/O/F, verified across 195 periods on live) and one-batch-per-day reuse; the plan only lists "closed accounting period" as a rejection to discover. Answers part of D6b for SDP-181 |
| SDP-163 | Task | Month-end batch backdating rule | D7c | **Same** — 5-day rule, identical |
| SDP-164 | Task | Handle credit memos | D7d | **Same** — negative receipt, "CR" suffix, identical |
| SDP-165 | Task | Report — Vendor Invoices Created in PACE | D8a (report 1) | **Same** — Jira specifies the columns, which is what P5a asks for |
| SDP-166 | Task | Report — PO Not Received | D8a ("awaiting receipt") | **Same** |
| SDP-167 | Task | Report — No PO | D8a ("no purchase order") | **Same** |
| SDP-168 | Task | Report — Errors | — | **New** — plan's fourth report is *discrepancies*, not errors |
| SDP-169 | Task | Report — Statements | — | **New** — no statements concept anywhere in the plan |
| SDP-170 | Spike | Download POs from PACE per invoice? | — | **New** — no PO export/download in the plan |
| SDP-171 | Spike | Print vendor invoices as they arrive? | — | **New** — no printing/filing workflow in the plan |
| SDP-172 | Spike | Print the downloaded POs? | — | **New** — same gap |
| SDP-173 | Risk | Vendor name may not match PACE Vendor/Legal Name | — | **New** — plan assumes vendor resolution off the PO; this is the case where there is no PO |
| SDP-174 | Task | Daily reconciliation check | D3c, D8b | **Same** — reconciliation count; Jira's version sums 6 buckets, plan's sums 5 |
| SDP-175 | Task | Hourly run scheduling | D12a | **The schedule definition, left open unchanged** — 6am–6pm weekdays, ≤13 runs/day, nothing at weekends. More specific than the plan. D12 gained its own ticket, SDP-185, on 2026-08-21 for standing the task up and proving it runs unattended |
| SDP-176 | Task | Set up WG.AP.sln per the confirmed architecture | D1 | **Same** — created from the plan on 2026-08-20 |
| SDP-177 | Task | GitHub repository, branches and branch protection | D2 | **Same** — created from the plan on 2026-08-20 |
| SDP-178 | Task | Microsoft Graph mailbox adapter | **D4a, D4d** | **Half of D4** — created 2026-08-21 and rescoped the same day to the mailbox boundary: authenticate, read messages, immutable IDs, move mail, send. ~3.5 d. Touches no database and no file share. Pairs with SDP-157 |
| SDP-179 | Task | Database, tables, rules and connection to the project | D3 | **Same** — created from the plan on 2026-08-21, retitled and rewritten the same day after the original description truncated on save. Builds the reconciliation *view*; SDP-174 is the *check* that runs against it |
| SDP-180 | Task | Format parser engine | D5 | **Same** — created from the plan on 2026-08-21. Detect the format, parse it, persist the extracted fields. Carries the Excel workbook fan-out and non-invoice rejection that SDP-158 lacks |
| SDP-181 | Task | Pace integration | D6 | **Same** — created from the plan on 2026-08-21, priority Medium. Carries client generation from the specification, fixture capture, and the **excluded post-bill-batch command** — none of which appear on SDP-160/161/162. Cites what those three already proved so the discovery is not repeated |
| SDP-182 | Task | Email sender | D9 | **Same** — created from the plan on 2026-08-21, priority Medium. The only development step that had **no** prior coverage on the board. Builds no transport (`IMailSender` is SDP-178's); composes and routes the reports, the reconciliation line and the three notifications |
| SDP-183 | Task | Tests across the whole pipeline | D10 | **Same** — created 2026-08-21, priority Medium. No prior coverage |
| SDP-184 | Task | Deploy to the development server | D11 | **Same** — created 2026-08-21, priority Medium. No prior coverage |
| SDP-185 | Task | Start the scheduled task | D12 | **Same** — created 2026-08-21, priority Medium. Scoped to standing the task up and proving it runs unattended; SDP-175 keeps the schedule definition and is referenced, not restated. D12b (the "log on as a batch job" right) appeared on no ticket before this |
| SDP-186 | Task | Testing round 1 — dry run | D13 | **Same** — created 2026-08-21, priority Medium. No prior coverage |
| SDP-187 | Task | Enable writing to Pace staging and Testing | **D14 + D15** | **Same, two steps in one ticket** — created 2026-08-21, priority Medium, 3 d combined. Includes D15d, sign-off by Accounting and IT |
| SDP-188 | Task | Confirm all production settings are in place | D18 | **Same** — created 2026-08-21, priority Medium. Gated by P10–P16, none of which is ticketed |
| SDP-189 | Task | Move to Production | D19 | **Same** — created 2026-08-21, priority Medium. No prior coverage |
| SDP-190 | Task | Pilot in production | D20 | **Same** — created 2026-08-21, priority Medium. 4 d of effort across 2 weeks of calendar; the two-clean-week gate is a duration, not an amount of work |

---

## Table 2 — Plan items with no Jira ticket

### Prerequisites

| Plan | Item | Note |
|---|---|---|
| P1 | Development database | No ticket. Gates D3 |
| P3 | Sanmar invoice samples | No ticket. Largest single gap — one full day of unfiltered mail, each format, 10 named business scenarios, expected result per sample |
| P4 | Pace test system | No ticket. Staging credentials, copied live data, example POs, one worked manual example. SDP-160 *uses* staging but nothing asks for it |
| P5 | Report content and recipients | Partly answered by SDP-165–169 columns; recipients still unassigned |
| P6 | Server and service account | No ticket |
| P7 | Scheduled task account + "log on as a batch job" | No ticket. SDP-175 defines the schedule but not what runs it |
| P9b | Amount mismatch — create the bill and flag, or stop? | No ticket. Open decision inside matching logic |
| P9c | Report frequency | No ticket |
| P10 | Production database | No ticket |
| P12 | Pace production credentials | No ticket |
| P13 | Production server and service account | No ticket |
| P14 | Production scheduled task account | No ticket |
| P16 | Live report distribution list | No ticket |

### Development steps

| Plan | Item | Days |
|---|---|---|
| D16 | Fix issues found in testing | 2 |
| D17 | Sign-off: development ready for production | 0.5 |
| | **Total unticketed development** | **2.5 d of 52** |

> **Only D16 and D17 remain.** Every other development step is ticketed: D1 and D2 as **SDP-176**
> and **SDP-177** on 20 August 2026; D3, D4, D5, D6, D9, D10, D11, D12, D13, D14+D15, D18, D19 and
> D20 as **SDP-178 through SDP-190** on 21 August 2026.
>
> D5, D6 and D12 were already partly covered by SDP-158, SDP-160/161/162 and SDP-175 respectively,
> so they were never in this table. **D14 and D15 share one ticket** (SDP-187), which is why the
> count of tickets does not equal the count of steps.

Also unticketed within a covered step: D8's **discrepancies report** (the plan's fourth report).

---

## Table 3 — Direct conflicts to settle

| # | Conflict | Jira says | Plan says |
|---|---|---|---|
| 1 | **Scope** | All vendors arriving in the AP mailbox (S&S Activewear, GPAHOL, Family Folders cited) | Sanmar only for Phase 1; vendors 3–5 a further ~9 developer-days, not in the 52 |
| 2 | **Outcome count** | 6 statuses incl. `STATEMENT` | 5 outcomes, no statements |
| 3 | **Report set** | 5 reports: Created, PO Not Received, No PO, Errors, Statements | 4 reports: processed, discrepancies, awaiting receipt, no PO |
| 4 | **Discrepancies** | A `Difference`/`Variance` column on every report | Its own report |
| 5 | **Mailbox used for development** | Live shared inbox (SDP-154) | Test mailbox; live inbox explicitly excluded so AP's folders are not disturbed |
| 6 | **Attachment formats** | PDF only (SDP-158) | PDF *and* Sanmar multi-invoice Excel — the harder of the two shapes. **Ticketed 2026-08-21 as SDP-180**; SDP-158 stays PDF-only, so the two overlap on PDF extraction |
| 7 | **Printing / filing** | Three open spikes (SDP-170/171/172) | Absent entirely — unestimated scope |
| 8 | **Vendor resolution without a PO** | Named risk (SDP-173) | Assumed resolved from the PO |

> **Resolved 2026-08-21 — the former conflict #9.** SDP-157 and SDP-178 briefly both claimed all of D4.
> They were split along the natural seam instead: **SDP-178** is the mailbox adapter (connect, read,
> immutable IDs, move, send — no database, no file share) and **SDP-157** is what the caller does with
> what it hands over (save the bytes, write the ledger rows, choose Processed or Errors, handle
> statements). D4's 6 days divide roughly 3.5 / 2.5 and are neither doubled nor lost.

---

## Summary

- **11 tickets are the same work** as a plan item: SDP-155, 157, 158, 159, 160, 161, 163, 164, 165, 166, 167, 174, 175 (13 counting 159 and 174 whose content differs slightly).
- **3 tickets are related but not identical:** SDP-153 (scope), SDP-154 (live vs test mailbox), SDP-156 (missing backup/retention).
- **1 ticket is mostly new:** SDP-162 (GL period gate, daily batch reuse).
- **6 tickets are entirely new to the plan:** SDP-168, 169, 170, 171, 172, 173.
- **15 plan items have no ticket** — just 2.5 of the 52 developer-days (D16, D17) plus **13 prerequisites, including every one that actually gates the start (P1, P3, P4)**. D1 and D2 were ticketed on 20 August 2026 as SDP-176 and SDP-177; D3–D6, D9–D15 and D18–D20 on 21 August 2026 as SDP-178 through SDP-190.
- **The gap has moved entirely.** Development is now ticketed end to end, from solution setup through to the production pilot, with the dependency chain modelled as blocking links. What remains untracked is **the prerequisites** — other people's lead time rather than developer effort, and precisely what holds up the start.

The two documents are complementary rather than duplicated: Jira holds the *proven Pace mechanics and report columns*, the plan holds the *access requests, environments, estimates, testing gates and go-live*. Neither is a superset of the other.
