# Copilot Instructions

## Project Guidelines
- User prefers changes to be introduced only when there is a concrete usage point in the code flow, not preemptive registration/injection without active use.
- For the AP Automation Pace integration, populate `PaceSubmission.RequestJson` from `Invoice.FieldsJson` as the source payload rather than an enriched/generated request envelope.
- In the AP Automation database design, avoid duplicating `Invoice.FieldsJson` into `intgr.PaceSubmission.RequestJson`. `RequestJson` should remain null until an actual Pace create/update payload is built.
- For database schema changes, follow normalization rules: lookup/status tables should have an ID primary key, and dependent tables should reference that ID instead of storing a status code as the foreign key.
- For Pace status normalization, the dependent table should use a column named `StatusCodeId`, not `PaceSubmissionStatusId`; `StatusCode` should exist only in the lookup/status table as the readable business code.