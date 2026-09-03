/*
    lkup.Status seed.

    StatusId values are contract, not arbitrary - WG.AP.Core mirrors them as an enum and
    dbo.MailMessage / dbo.Invoice constrain themselves to their band. Never renumber.

    Mail statuses map 1:1 onto the routing tree in APProcessor:
        no PDF attachments (incl. Excel-only mail)   -> MailSkipped     -> NeedsReview
        PDF unparseable, or Total <= 0               -> MailError
        Client/InvoiceDate/InvoiceNumber/CustomerPO
            missing, duplicate number, 3rd attempt   -> MailNeedsReview
        all five required fields present             -> MailProcessed

    IsFinal = 1 means the claim UPDATE will never pick the row up again.
    MailFolder NULL means "leave it in the Inbox" (e.g. MailDeleted, which the mailbox has
    already removed the message from).
*/
SET NOCOUNT ON;

DECLARE @Status TABLE
(
    StatusId   INT           NOT NULL PRIMARY KEY,
    Code       VARCHAR(30)   NOT NULL,
    Name       NVARCHAR(60)  NOT NULL,
    IsFinal    BIT           NOT NULL,
    MailFolder NVARCHAR(50)  NULL
);

INSERT INTO @Status (StatusId, Code, Name, IsFinal, MailFolder)
VALUES
    -- Mail: 10-19
    (10, 'MailNew',            N'New',                    0, NULL),
    (11, 'MailProcessed',      N'Processed',               1, N'Processed'),
    (12, 'MailNeedsReview',    N'Needs review',            1, N'NeedsReview'),
    (13, 'MailError',          N'Error',                   1, N'Errors'),
    (14, 'MailSkipped',        N'Skipped - not an invoice', 1, N'NeedsReview'),
    (15, 'MailDuplicate',      N'Duplicate email',         1, N'Errors'),
    (16, 'MailDeleted',        N'Deleted in mailbox',      1, NULL),
    -- Invoice: 20-29
    (20, 'InvoiceNew',         N'New',                     0, NULL),
    (21, 'InvoiceExtracted',   N'Extracted',               1, NULL),
    (22, 'InvoiceNeedsReview', N'Needs review',            1, NULL),
    (23, 'InvoiceError',       N'Error',                   1, NULL),
    (24, 'InvoiceDuplicate',   N'Duplicate invoice number', 1, NULL);

MERGE [lkup].[Status] AS target
USING @Status AS source
    ON target.[StatusId] = source.[StatusId]
WHEN MATCHED AND (target.[Code]    <> source.[Code]
               OR target.[Name]    <> source.[Name]
               OR target.[IsFinal] <> source.[IsFinal]
               OR ISNULL(target.[MailFolder], N'') <> ISNULL(source.[MailFolder], N''))
    THEN UPDATE SET
        target.[Code]       = source.[Code],
        target.[Name]       = source.[Name],
        target.[IsFinal]    = source.[IsFinal],
        target.[MailFolder] = source.[MailFolder],
        target.[ModifiedBy] = SUSER_SNAME(),
        target.[ModifiedOn] = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET
    THEN INSERT ([StatusId], [Code], [Name], [IsFinal], [MailFolder])
         VALUES (source.[StatusId], source.[Code], source.[Name], source.[IsFinal], source.[MailFolder]);

DECLARE @StatusCount INT = (SELECT COUNT(*) FROM [lkup].[Status]);
PRINT CONCAT('lkup.Status seeded: ', @StatusCount, ' row(s).');
