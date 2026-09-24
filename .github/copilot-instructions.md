# Copilot Instructions

## Project Guidelines
- User prefers changes to be introduced only when there is a concrete usage point in the code flow, not preemptive registration/injection without active use.
- In the AP Automation database design, avoid duplicating `Invoice.FieldsJson` into `intgr.PaceSubmission`; do not add a Pace request-payload column until an actual Pace create/update payload is built.
- For database schema changes, follow normalization rules: lookup/status tables should have an ID primary key, and dependent tables should reference that ID instead of storing a status code as the foreign key.
- For Pace status normalization, the dependent table should use a column named `StatusCodeId`, not `PaceSubmissionStatusId`; `StatusCode` should exist only in the lookup/status table as the readable business code.
- For Pace AP automation, after duplicate lookup, find or create one daily `AUTO M-D-YY` bill batch for all invoices, dated by the processing date (today). The batch must exist before the Bill is created.
- Resolve the Pace GL accounting period from the invoice date and proceed only when its status is `T` or `O`; a closed period (`F`) routes the invoice to the Errors report.
- For PO invoices, create the Bill with the PO vendor and tie each bill line to the matched PO receipt.
- For no-PO invoices, read the Pace Vendor by primary key using `Client.Code` (e.g. `SANMAR`) via `ReadVendorAsync`, code one bill line with the Pace vendor default GL account/department (never from config), and route the invoice to NeedsReview.
- A `createBill` HTTP 500 for a duplicate invoice number routes the invoice to the Errors report.
- The Pace client omits null properties from outgoing JSON so Pace does not treat a null primary key as id 0.

## Mailbox Sync Guidelines
- Avoid implementing automatic destructive recovery for stale Graph mailbox delta state. Do not clear/retry mailbox sync state automatically without explicit operator action; prefer failing safely and requiring manual intervention.
