-- One row per invoice PDF. The columns mirror WG.AP.Invoice.Models.InvoiceFields.
--
-- Invoice identity is Client + Invoice Number, enforced by UQ_Invoice_ClientNumber below.
-- The code catches SQL error 2601/2627 on that index and records InvoiceDuplicate: the
-- constraint decides, the code reports.
CREATE TABLE [dbo].[Invoice]
(
    [InvoiceId]          BIGINT        IDENTITY(1,1) NOT NULL,
    [MailMessageId]      BIGINT        NOT NULL,
    [MailAttachmentId]   BIGINT        NOT NULL,   -- every invoice comes from a PDF
    [ClientId]           INT           NOT NULL,   -- 0 = Unknown
    [InvoiceFormatId]    INT           NULL,

    [InvoiceNumber]      NVARCHAR(100) NULL,       -- raw, exactly as printed/extracted

    -- The normalised number, for reading and reporting. dbo.NormalizeInvoiceNumber holds
    -- the rule and explains it; NULL here means "no usable number".
    --
    -- The explicit CONVERT is not decoration. A computed column whose type SQL Server has
    -- to infer (a bare function call, a bare CASE) does not round-trip through the dacpac
    -- model, so sqlpackage decides the column changed and schedules a full TABLE REBUILD on
    -- every publish - dropping and recreating every constraint on the table and burying real
    -- changes in 30-odd lines of noise. Pinning the type makes a no-change publish a no-op.
    [InvoiceNumberKey]   AS (CONVERT(NVARCHAR(100),
                                 [dbo].[NormalizeInvoiceNumber]([InvoiceNumber]))) PERSISTED,

    -- The actual unique key for "one invoice number per client", as a hash.
    --
    -- Three things forced this shape, and it is worth knowing all three before changing it:
    --
    --  1. A filtered index cannot reference a computed column (Msg 10609), so the obvious
    --     UNIQUE (ClientId, InvoiceNumberKey) WHERE InvoiceNumberKey IS NOT NULL AND ClientId > 0
    --     cannot be created. Filtering on the raw [InvoiceNumber] instead is not equivalent:
    --     a number like '---' is non-NULL but normalises away to nothing, and a SQL Server
    --     unique index permits only ONE NULL, so the second such row would be rejected as a
    --     duplicate of the first - the same false-duplicate-on-a-real-payable failure that
    --     excluding ClientId 0 exists to prevent. So both exclusions live inside the key,
    --     and an excluded row gets a sentinel built from its own InvoiceId, which is unique
    --     by construction and cannot collide. Real keys start with a digit (the ClientId),
    --     sentinels start with '~', so those two ranges cannot overlap either.
    --
    --  2. A computed column cannot reference another computed column (Msg 1759), which is
    --     why this calls the function again rather than reusing InvoiceNumberKey.
    --
    --  3. It is a HASH rather than the key text because HASHBYTES compares bytes, so
    --     uniqueness here does not depend on the server's collation at all. Spelling this
    --     as an NVARCHAR key needed an explicit COLLATE to be trustworthy - and a computed
    --     column carrying a non-default collation does not round-trip through the dacpac
    --     model either, which put the Invoice table back into rebuild-on-every-publish.
    --     Hashing gets the same byte-exact guarantee with no collation involved, and it is
    --     the pattern dbo.MailMessage.MessageKeyHash already uses.
    --
    -- The opacity costs nothing in practice: ClientId and InvoiceNumberKey are both sitting
    -- in the row, and IX_Invoice_ClientNumberKey covers "what else has this number".
    [InvoiceDuplicateHash] AS (CONVERT(BINARY(32), HASHBYTES('SHA2_256',
                                 CASE WHEN [ClientId] > 0
                                            AND [dbo].[NormalizeInvoiceNumber]([InvoiceNumber]) IS NOT NULL
                                      THEN CONCAT(CONVERT(VARCHAR(11), [ClientId]), N'|',
                                                  [dbo].[NormalizeInvoiceNumber]([InvoiceNumber]))
                                      ELSE CONCAT(N'~', CONVERT(VARCHAR(20), [InvoiceId]))
                                 END))) PERSISTED,

    [InvoiceDate]        DATE          NULL,
    [DueDate]            DATE          NULL,
    [Total]              DECIMAL(19,4) NULL,
    [SalesOrder]         NVARCHAR(100) NULL,
    [CustomerPO]         NVARCHAR(200) NULL,
    [CustomerNumber]     NVARCHAR(100) NULL,
    [OrderAccount]       NVARCHAR(100) NULL,
    [Terms]              NVARCHAR(100) NULL,
    [ClientNameAsRead]   NVARCHAR(200) NULL,   -- what the PDF/model said, before mapping to ClientId
    [RawText]            NVARCHAR(MAX) NULL,   -- InvoiceFields.RawText: the audit copy and prompt input

    -- InvoiceFields serialized to JSON (System.Text.Json), verbatim as it will be sent to
    -- WG.AP.Integrations.Pace. Distinct from RawText: RawText is the plain extracted document
    -- text fed *into* extraction; FieldsJson is the structured result that comes *out* of it.
    -- NULL when extraction never produced an InvoiceFields (e.g. InvoiceError before extraction ran).
    [FieldsJson]         NVARCHAR(MAX) NULL,

    [ExtractionMethod]   VARCHAR(10)   NULL,   -- Regex | Ollama
    [ExtractionPromptId] INT           NULL,   -- which prompt version, when Ollama
    [StatusId]           INT           NOT NULL,
    [ErrorMessage]       NVARCHAR(1000) NULL,  -- also carries the review reason

    [CreatedBy]          NVARCHAR(128) NOT NULL CONSTRAINT [DF_Invoice_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]          DATETIME2(3)  NOT NULL CONSTRAINT [DF_Invoice_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]         NVARCHAR(128) NULL,
    [ModifiedOn]         DATETIME2(3)  NULL,

    CONSTRAINT [PK_Invoice] PRIMARY KEY CLUSTERED ([InvoiceId]),
    CONSTRAINT [FK_Invoice_MailMessage] FOREIGN KEY ([MailMessageId])
        REFERENCES [dbo].[MailMessage] ([MailMessageId]),
    CONSTRAINT [FK_Invoice_MailAttachment] FOREIGN KEY ([MailAttachmentId])
        REFERENCES [dbo].[MailAttachment] ([MailAttachmentId]),
    CONSTRAINT [FK_Invoice_Client] FOREIGN KEY ([ClientId])
        REFERENCES [dbo].[Client] ([ClientId]),
    CONSTRAINT [FK_Invoice_InvoiceFormat] FOREIGN KEY ([InvoiceFormatId])
        REFERENCES [dbo].[InvoiceFormat] ([InvoiceFormatId]),
    CONSTRAINT [FK_Invoice_ExtractionPrompt] FOREIGN KEY ([ExtractionPromptId])
        REFERENCES [dbo].[ExtractionPrompt] ([ExtractionPromptId]),
    CONSTRAINT [FK_Invoice_Status] FOREIGN KEY ([StatusId])
        REFERENCES [lkup].[Status] ([StatusId]),

    CONSTRAINT [CK_Invoice_StatusBand] CHECK ([StatusId] >= 20 AND [StatusId] <= 29),
    -- ORed equalities in the order SQL Server stores them (alphabetical), not IN - see the
    -- note on CK_MailMessage_StatusBand for why the spelling matters.
    CONSTRAINT [CK_Invoice_Method]     CHECK ([ExtractionMethod] IS NULL
                                              OR ([ExtractionMethod] = 'Ollama'
                                                  OR [ExtractionMethod] = 'Regex')),

    CONSTRAINT [CK_Invoice_FieldsJson] CHECK ([FieldsJson] IS NULL OR ISJSON([FieldsJson]) = 1),

    -- InvoiceExtracted (21) cannot mean anything looser than "all five required fields
    -- present". Client, InvoiceDate, InvoiceNumber and CustomerPO missing is a review; a
    -- Total that is null, zero or negative is an error, because that is not a
    -- low-confidence read, it is a wrong one.
    --
    -- Total needs BOTH the IS NOT NULL and the > 0. A CHECK constraint rejects only on
    -- FALSE, and NULL > 0 evaluates to UNKNOWN, so `[Total] > 0` on its own lets a null
    -- Total through - which is the one value most likely to appear when extraction quietly
    -- failed. Tests\constraints.sql covers this case for exactly that reason.
    CONSTRAINT [CK_Invoice_ExtractedIsComplete] CHECK (
        [StatusId] <> 21
        OR ([ClientId] > 0
            AND [InvoiceNumber] IS NOT NULL
            AND [InvoiceDate]   IS NOT NULL
            AND [CustomerPO]    IS NOT NULL
            AND [Total]         IS NOT NULL
            AND [Total] > 0))
);
GO

-- THE invoice-identity constraint: one invoice number per client.
--
-- Unfiltered, because every exclusion already lives inside InvoiceDuplicateHash (see the
-- column comment for why it has to work that way).
--
-- The ClientId component matters MORE as clients are added, not less: ClientId 0 is
-- Unknown, so without excluding it two *different* clients' identical invoice numbers
-- would collide and one real payable would be rejected as already entered. Unknown-client
-- invoices are routed to InvoiceNeedsReview instead.
--
-- No fiscal-year component: SanMar does not reuse invoice numbers across years.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_Invoice_ClientNumber]
    ON [dbo].[Invoice] ([InvoiceDuplicateHash]);
GO

-- Reporting and duplicate investigation ("what else has this number?") read the readable
-- normalised form rather than the composite key.
CREATE NONCLUSTERED INDEX [IX_Invoice_ClientNumberKey]
    ON [dbo].[Invoice] ([ClientId], [InvoiceNumberKey]);
GO

-- One PDF yields one invoice, so re-running extraction after a crash is idempotent by
-- construction rather than by care.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_Invoice_Attachment]
    ON [dbo].[Invoice] ([MailAttachmentId]);
GO

CREATE NONCLUSTERED INDEX [IX_Invoice_Status]
    ON [dbo].[Invoice] ([StatusId], [CreatedOn]);
GO

CREATE NONCLUSTERED INDEX [IX_Invoice_MailMessage]
    ON [dbo].[Invoice] ([MailMessageId]);
