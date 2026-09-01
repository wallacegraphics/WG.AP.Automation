/*
    Post-deployment seed.

    Runs on every publish, so every script it includes must be re-runnable: MERGE or
    IF NOT EXISTS, never a bare INSERT. Re-publishing is how this schema reaches Dev and
    then Prod, so idempotency is the deployment mechanism rather than a nicety.

    Order matters - InvoiceFormat resolves a ClientId, and ExtractionPrompt resolves an
    InvoiceFormatId.
*/

PRINT '--- WG.AP.Database post-deployment seed ---';
GO

:r .\Seed\Status.sql
GO

:r .\Seed\Client.sql
GO

:r .\Seed\InvoiceFormat.sql
GO

:r .\Seed\ExtractionPrompt.sql
GO

PRINT '--- post-deployment seed complete ---';
GO
