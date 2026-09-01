-- One PDF layout belonging to one client. This is the "per possible pdf" axis of the
-- prompt requirement: a client has 1..N formats, which are just rows sharing a ClientId
-- with nothing special-cased.
--
-- ExtractorKey selects which extractor runs, and it is not documentation - it is what
-- stops SanMar's regex tier being pointed at another client's invoice. PdfInvoiceField-
-- Extractor runs the regex tier only when the key is SANMAR_PDF_HEADER_V1 and otherwise
-- goes straight to Ollama. SanmarPdfHeaderExtractor.TryExtract is all-or-nothing, so a
-- cross-client match is unlikely rather than impossible, and it would be a silent
-- wrong-data path rather than a crash.
--
-- Retired with IsEnabled = 0, never DELETEd.
CREATE TABLE [dbo].[InvoiceFormat]
(
    [InvoiceFormatId] INT         IDENTITY(1,1) NOT NULL,
    [ClientId]        INT         NOT NULL,
    [Code]            VARCHAR(50) NOT NULL,   -- SANMAR-PDF-SINGLE, SANMAR-PDF-CREDIT
    [ExtractorKey]    VARCHAR(60) NOT NULL,
    [IsEnabled]       BIT         NOT NULL CONSTRAINT [DF_InvoiceFormat_IsEnabled] DEFAULT (1),

    [CreatedBy]       NVARCHAR(128) NOT NULL CONSTRAINT [DF_InvoiceFormat_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]       DATETIME2(3)  NOT NULL CONSTRAINT [DF_InvoiceFormat_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]      NVARCHAR(128) NULL,
    [ModifiedOn]      DATETIME2(3)  NULL,

    CONSTRAINT [PK_InvoiceFormat]      PRIMARY KEY CLUSTERED ([InvoiceFormatId]),
    CONSTRAINT [UQ_InvoiceFormat_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_InvoiceFormat_Client] FOREIGN KEY ([ClientId])
        REFERENCES [dbo].[Client] ([ClientId])
);
GO

CREATE NONCLUSTERED INDEX [IX_InvoiceFormat_Client]
    ON [dbo].[InvoiceFormat] ([ClientId])
    WHERE [IsEnabled] = 1;
