# AP Automation — Kickoff Meeting Notes

**Date & Time:** 2026-08-12 11:13:07
**Location:** [Insert Location]
**Customer:** [Insert Customer]
**Source:** Notes from Chase Kammer

## Overview

The client aims to automate accounts payable (AP) invoice processing. Currently, all vendor invoices arrive via a single global email account, creating significant manual workload—up to seventy-five invoices in one day. The goal is to build a system that reads invoice attachments from emails, matches them to purchase orders (POs) in the Pace system, and categorizes them for processing. The system should identify invoices with matched and received POs, invoices with matched POs not yet received, and invoices with no PO. This automation seeks to reduce manual data entry, streamline discrepancy handling, and deliver clear daily reports for the AP team to manage exceptions.

## Background

The AP process is heavily manual and centered on a single global email account. The AP team, including Jessica, manually checks each invoice against a PO in Pace. Steps include opening attachments, finding POs, checking receipts, and following up with team members like Javier, Angela, Matt, or purchasing (Coley, Janet) to resolve discrepancies, unreceived orders, or missing POs. Invoice volume can reach seventy or more per day. An estimated seventy percent of invoices have a corresponding PO, supporting automation. A major technical challenge is the variety of invoice formats across vendors.

## Pain Points

### 1. Entirely manual, inefficient invoice workflow

All invoices funnel into one global email account, which the team must sort and process, causing delays.

- **Impact:** Creates a bottleneck, slows payment cycles, and consumes time that could be spent on higher-value tasks.
- **Current Situation:** The team manually reviews each email, opens attachments, finds the PO in Pace, checks goods receipt, and decides next actions. For invoices with POs awaiting receipt, they manually wait (e.g., six days) and follow up with the PO creator. For invoices without a PO, they contact the relevant department (e.g., production) to create one.
- **Quantitative Metrics:** The team can receive fifty, sixty, seventy, or up to seventy-five invoices at a time, especially overnight—representing substantial daily manual work.
- **Examples:**
  - An invoice arrives with a PO, but goods haven't been marked received in Pace; Jessica holds the invoice, tracks it, and reaches out to Javier to update receipt status.
  - An invoice for a purchase by Matt in production without a PO, requiring AP to chase him to create one.
- **Stakeholders:** AP team (Jessica and others), PO creators (Javier, Angela, Matt), and purchasing (Coley, Janet).

### 2. Wide variety of vendor invoice formats

A major technical hurdle. The system must accurately extract key information (PO number, amount) from many layouts.

- **Impact:** Failure to read formats correctly would make automation ineffective, causing processing failures or incorrect data extraction and requiring manual correction.
- **Current Situation:** Humans can adapt to varying layouts; programs require specific logic per format. No automated system exists today.
- **Context:** The central email account holds historical invoice formats. The top five vendors—led by Sanmar—account for about sixty-eight percent of workload, offering a starting point.
- **Stakeholders:** Development team (Galena) must build format logic; AP workload depends on success.

### 3. Discrepancies between invoices and POs

E.g., price differences—require manual intervention. The client wants matching automated but needs a process to flag and manage exceptions without halting the workflow.

- **Impact:** Poor handling could block valid payments or cause incorrect payments; manual resolution is time-consuming.
- **Current Situation:** When a discrepancy occurs (e.g., ten cent difference), AP often consults purchasing (e.g., Coley). Larger issues may lead to removing the invoice from the payment batch for investigation.
- **Context:** Nothing will be posted automatically. The goal is to create a bill batch and have a report highlight discrepancies so AP can review and act. The invoice remains in the batch with the discrepancy noted in the report.
- **Stakeholders:** AP and purchasing teams.

## Expectations

### Automated categorization (3 categories)

The client expects an automated system that scans the central AP inbox, processes attached invoices, and sorts them into three categories for action.

| Category | Description | Action |
|---|---|---|
| 1 — Match & Post | Invoice matches an open, **received** PO in Pace | Add to bill batch for posting |
| 2 — Match, Not Received | Invoice matches a PO **not yet marked received** | Flag for follow-up (currently a six-day wait) |
| 3 — No PO | No corresponding PO found | Flag for manual PO creation |

- **Success Metrics:** Reduce manual time spent processing invoices. Successfully categorize the majority of the seventy percent of invoices already tied to POs. Generate a daily report detailing posted items, pending receipt items, no-PO items, and any discrepancies.
- **Stakeholders:** AP team as primary users; development team builds the system.

### Scheduled processing & reporting

- **Specific Goals:** Process emails and generate reports regularly. Reports must clearly show processed invoices, discrepancies, pending receipts, and items requiring POs.
- **Timeframe:** Tentative processing schedule: once an hour to avoid overwhelming Pace. Reports could be generated twice daily; schedule is flexible.
- **Resources Required:** SQL database for logging and reporting; a service account for independent deployment.
- **Stakeholders:** AP team uses reports daily; development team builds reporting.

### Phased implementation

- **Specific Goals:** Start with a small set of high-volume vendors—the top five vendors representing sixty-eight percent of invoice volume, with Sanmar a primary target.
- **Timeframe:** No firm deadline; project will take more than a month and be iterative. The client wants visible progress within the first couple of weeks.
- **Success Metrics:** Successful processing for initial vendors in Phase 1.
- **Stakeholders:** All parties (client and consultant team) agreed to the iterative approach.

## Other Information

- Access to Microsoft Exchange is required to read AP inbox emails. Credentials and permissions must be provided.
- A SQL database is required to log processed invoices and generate reports.
- A dedicated service account is needed so the application runs independently of user accounts.
- Logic must be built to handle new vendors and invoice formats as they are onboarded.
- Invoices the system cannot process (unknown format, errors) will be moved to an "errors" folder, with email notifications sent to users—preferred over leaving emails as unread.
- The client initially asked about auto-printing matched invoices; this is lower priority to avoid complicating initial scope.

## To-Do List

- [ ] **[Client]** Create a Jira ticket with detailed business requirements for the automation.
- [ ] **[Client]** Submit an IT ticket to grant Galena (developer) permissions to the AP email inbox on Exchange.
- [ ] **[Consultant/Diana]** Coordinate with IT to set up a service account for application deployment.
- [ ] **[Consultant]** Schedule a session for Galena to shadow the AP team's current workflow next week (by 2026-08-22).
- [ ] **[Client]** Provide all known invoice formats to the development team.
- [ ] **[Consultant/Galena]** Provide an estimated timeline/hours after reviewing requirements and observing the workflow; target discussion by mid-next week (around 2026-08-20).
- [ ] **[Client]** Set up a recurring 15-minute weekly check-in for the next four weeks to track progress.

## AI Suggestions

The AI has identified the biggest pain point as the entirely manual, time-consuming, and inefficient process of handling a high volume of invoices from a single inbox. Consider:

- **Phased Rollout with Intelligent OCR:** Develop core logic using Intelligent OCR, starting with the top 5 vendors covering 68% of volume. Train models on these formats to deliver quick value and iteratively add more vendors.
- **Rules-Based Workflow Engine:** Implement a workflow engine encoding the three categories: if PO present and received, route to batch creation; if PO present but not received, place in a pending queue with automated follow-up timers; if no PO, route to an exception queue for manual review.
- **Centralized Exception Dashboard:** Augment email reports with a simple web dashboard for real-time visibility across "processed," "pending receipt," "discrepancies," and "no PO"—a more robust control center than error folders and emails.
- **Pre-processing Email Rules:** Set Exchange rules to filter non-invoice emails and pre-sort messages from top vendors into subfolders, simplifying initial processing.
