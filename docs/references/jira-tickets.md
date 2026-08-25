# Jira Tickets — AP Automation

Tracking of Jira tickets related to this project, kept separate from meeting notes for quick reference.

## How the records fit together

This project exists as **four separate Jira records in four different projects**, joined by **issue links only** — there is no parent/child relationship between any of them.

| Key | Project | Type | What it is |
|---|---|---|---|
| CHG-185 | `CHG` Change Management (service desk) | Request a change | The original change request. Still at **Triage**, **linked to nothing**. |
| **MIH-155** | `MIH` Making It Happen: What We're Building (business) | **Project** (workstream) | The **business / executive card**. Same body text as CHG-185, copied. |
| **SDP-153** | `SDP` Systems Development Team (software) | **Epic** | The **engineering epic**. Parent of SDP-154…SDP-179. |
| SD-385 | `SD` Systems Development Queue (service desk) | Submit a Request | The prerequisites request. In Progress. |

**MIH-155 `relates to` SDP-153.** That is the only connection between the business card and the engineering epic — a sideways link between two hierarchy-level-1 items in different projects. Nothing rolls up.

### The rule for every new ticket

A ticket appears on the MIH-155 card **only if it has its own direct link to MIH-155**. Being a child of SDP-153 does *not* put it there. So each new ticket needs:

1. **Type Task, `parent = SDP-153`** — puts it in the epic and on the SDP board
2. **Label `ap-automation`** — makes the JQL sweep find it
3. **An issue link to MIH-155**, type `has action item` — puts it on the business card

**Do not add an issue link to SDP-153.** The parent field already is epic membership in a team-managed project; a link on top of it shows the same relationship twice.

On 2026-08-21 the missing MIH-155 links were backfilled on **SDP-154…SDP-163** (10 tickets that had none and were invisible on the business card). SDP-164…SDP-175 use the older `relates to` type and were left as they are.

## Tickets

| Ticket | Title | Notes |
|---|---|---|
| [CHG-185](https://wallacegraphics.atlassian.net/browse/CHG-185) | AP Automation Deliveriable expectations | Created following the 2026-08-12 kickoff meeting. Captures the deliverable/business requirements scope discussed with Chase Kammer — see [2026-08-12 kickoff meeting notes](../meeting-notes/2026-08-12-kickoff-meeting.md) for full context. Still at status **Triage** in the Change Management service desk and **linked to nothing**; its content now also lives in MIH-155. |
| [MIH-155](https://wallacegraphics.atlassian.net/browse/MIH-155) | AP Automation Deliverable expectations | The business/executive card, type **Project** (workstream), status 📋 In Planning. Same body as CHG-185. Links out to SDP-153, SD-385 and every AP Automation work ticket. **This is the card to keep in sync** — see the rule above. |
| [SDP-153](https://wallacegraphics.atlassian.net/browse/SDP-153) | AP Invoice Automation (Epic) | Parent epic on the SDP board. All AP Automation items carry the label `ap-automation`. Children SDP-154…SDP-175 were created before the delivery plan — see [backlog-vs-delivery-plan-crosswalk.md](backlog-vs-delivery-plan-crosswalk.md) for how they map to plan items and the conflicts still to settle. |
| [SDP-176](https://wallacegraphics.atlassian.net/browse/SDP-176) | Set up WG.AP.sln per the confirmed architecture | Created 2026-08-20. Covers step D1 in [development-steps-2026-08-19.md](development-steps-2026-08-19.md) — 1.5 d, depends on nothing, can start immediately. Priority High. |
| [SDP-177](https://wallacegraphics.atlassian.net/browse/SDP-177) | GitHub repository, branches and branch protection for APAutomation | Created 2026-08-20. Covers step D2 in [development-steps-2026-08-19.md](development-steps-2026-08-19.md) — 0.5 d, depends on nothing. Priority Medium. |
| [SDP-157](https://wallacegraphics.atlassian.net/browse/SDP-157) | Retrieve & save invoice attachments — persist files and log every message on arrival | Pre-dates the delivery plan; **rescoped 2026-08-21** as the **persistence half of D4** (D4b, D4c and the no-duplicates gate) — ~2.5 d. Saves attachment bytes, writes the message and attachment ledger rows, decides Processed vs Errors, handles statements. Priority High. Blocked by SDP-178, SDP-179 and SDP-156. |
| [SDP-178](https://wallacegraphics.atlassian.net/browse/SDP-178) | Microsoft Graph mailbox adapter — read messages, move mail, send notifications | Created 2026-08-21, **rescoped the same day** to the **mailbox half of D4** (D4a, the move mechanics of D4d, immutable IDs) — ~3.5 d, gated by P2. Priority High. Blocked by SDP-155 only: it touches no database and writes no files, so it can be built the moment mailbox access lands. |
| [SDP-179](https://wallacegraphics.atlassian.net/browse/SDP-179) | Database, tables, rules and connection to the project | Created 2026-08-21, **retitled and description rewritten the same day** — the original save silently truncated, dropping items c and d. Covers step D3 in [development-steps-2026-08-19.md](development-steps-2026-08-19.md) — 3 d, nominally gated by P1 but buildable against a local SQL instance first, so it need not wait. Priority High. Blocks SDP-157 and SDP-180; relates to SDP-174 and SDP-159. |
| [SDP-180](https://wallacegraphics.atlassian.net/browse/SDP-180) | Format parser engine | Created 2026-08-21. Covers step D5 in [development-steps-2026-08-19.md](development-steps-2026-08-19.md) — 7 d, the largest single step, gated by P3 (Sanmar samples) but startable against sample files on disk. Detect the format, parse it, persist the extracted fields. Priority High. Blocked by SDP-157 and SDP-179; relates to SDP-158, which stays open and covers item a for PDFs only. |
| [SDP-181](https://wallacegraphics.atlassian.net/browse/SDP-181) | Pace integration | Created 2026-08-21. Covers step D6 in [development-steps-2026-08-19.md](development-steps-2026-08-19.md) — 5 d, gated by P4 (Pace staging with copied data and example POs). Generate the client from the specification, discover and record Pace's real behaviour, capture every request/response as fixtures, and **exclude the post-bill-batch command so the application physically cannot post**. **Priority Medium** — P4 has not arrived and much of the discovery is already done. Blocked by SDP-176; relates to SDP-160, SDP-161 and SDP-162, all unchanged. |
| [SDP-182](https://wallacegraphics.atlassian.net/browse/SDP-182) | Email sender | Created 2026-08-21. Covers step D9 in [development-steps-2026-08-19.md](development-steps-2026-08-19.md) — 1 d, depends on D8. **The first D-step with no pre-existing coverage at all.** Builds no transport: `IMailSender` comes from SDP-178, and this composes and routes the reports, the reconciliation summary line and the three notifications. **Priority Medium** — blocked on P5b (who receives each report, still unassigned) and P9c (report frequency, still undecided). Blocked by SDP-178; relates to SDP-165…169 and SDP-174, all unchanged. |
| [SDP-183](https://wallacegraphics.atlassian.net/browse/SDP-183) | Tests across the whole pipeline | Created 2026-08-21. Covers step D10 — 2 d, depends on D3–D9. Priority Medium. Blocked by SDP-180, SDP-181 and SDP-182; blocks SDP-184. |
| [SDP-184](https://wallacegraphics.atlassian.net/browse/SDP-184) | Deploy to the development server | Created 2026-08-21. Covers step D11 — 1 d, gated by P6 (server and service account). Priority Medium. **Titled from the plan's own D11 heading**, not the broader "Testing on the development environment" section heading, since the content is deployment only. Blocked by SDP-183; blocks SDP-185. |
| [SDP-185](https://wallacegraphics.atlassian.net/browse/SDP-185) | Start the scheduled task | Created 2026-08-21. Covers step D12 — 0.5 d, gated by P7. Priority Medium. **Scoped to standing the task up and proving it runs unattended** — the schedule itself is already defined in SDP-175, which is referenced rather than restated. Blocked by SDP-184; blocks SDP-186; relates to SDP-175. |
| [SDP-186](https://wallacegraphics.atlassian.net/browse/SDP-186) | Testing round 1 — dry run, nothing written to Pace | Created 2026-08-21. Covers step D13 — 2 d. Priority Medium. The first trustworthy demonstration, because no real payable can exist yet. Blocked by SDP-185; blocks SDP-187. |
| [SDP-187](https://wallacegraphics.atlassian.net/browse/SDP-187) | Enable writing to Pace staging and Testing | Created 2026-08-21. **Covers steps D14 and D15 together** — 3 d combined (0.5 + 2.5). Priority Medium. Item d (sign-off by Accounting and IT) restored from the plan. Blocked by SDP-186; blocks SDP-188. |
| [SDP-188](https://wallacegraphics.atlassian.net/browse/SDP-188) | Confirm all production settings are in place | Created 2026-08-21. Covers step D18 — 1.5 d, gated by P10–P16, **none of which has a ticket of its own**. Priority Medium. Blocked by SDP-187. |
| [SDP-189](https://wallacegraphics.atlassian.net/browse/SDP-189) | Move to Production | Created 2026-08-21. Covers step D19 — 1.5 d. Priority Medium. Deploy and start the task on production, a monitoring query anyone can run, and a runbook **somebody other than the developer has followed once**. Blocked by SDP-188; blocks SDP-190. |
| [SDP-190](https://wallacegraphics.atlassian.net/browse/SDP-190) | Pilot in production | Created 2026-08-21. Covers step D20 — **4 d of effort but 2 weeks of calendar**, because the gate is two consecutive clean weeks, a duration rather than an amount of work. Priority Medium. Sanmar only, bills left unposted, full email-to-bill trail, written rollback. Blocked by SDP-189; relates to SDP-181. |
