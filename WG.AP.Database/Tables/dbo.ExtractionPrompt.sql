-- The Ollama prompt, stored per client and per PDF format, versioned.
--
-- Rows are INSERTed, never edited: dbo.Invoice.ExtractionPromptId pins the exact version
-- that produced an extraction, so an invoice read in October is still explainable next
-- July. Retiring a version is IsActive = 0. Never DELETE.
--
-- Edit prompts in Scripts/Seed/ExtractionPrompt.sql, not in SSMS. That file is the
-- readable, diffable, reviewable master copy, and publishing deploys it. An UPDATE in
-- SSMS would quietly break the insert-a-new-version rule above, and SSMS's grid flattens
-- a multi-line string onto one line anyway.
--
-- PromptTemplate is stored with ordinary CRLF so it reads correctly in SSMS, Notepad and
-- the seed file. The app normalises it to \n before substituting {{DocumentText}} and
-- posting to Ollama, so a replay sees byte-identical input no matter how the row was
-- authored. (C# raw string literals preserve the source file's newlines and .gitattributes
-- sets `* text=auto`, so the hardcoded prompt was never byte-stable across checkouts.)
CREATE TABLE [dbo].[ExtractionPrompt]
(
    [ExtractionPromptId] INT           IDENTITY(1,1) NOT NULL,
    [InvoiceFormatId]    INT           NOT NULL,
    [Version]            INT           NOT NULL,
    [PromptTemplate]     NVARCHAR(MAX) NOT NULL,
    [ResponseSchemaJson] NVARCHAR(MAX) NOT NULL,   -- the Ollama `format` payload, verbatim
    [ModelName]          NVARCHAR(100) NULL,       -- NULL => fall back to OllamaOptions.Model
    [IsActive]           BIT           NOT NULL CONSTRAINT [DF_ExtractionPrompt_IsActive] DEFAULT (0),
    [Notes]              NVARCHAR(500) NULL,

    [CreatedBy]          NVARCHAR(128) NOT NULL CONSTRAINT [DF_ExtractionPrompt_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]          DATETIME2(3)  NOT NULL CONSTRAINT [DF_ExtractionPrompt_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]         NVARCHAR(128) NULL,
    [ModifiedOn]         DATETIME2(3)  NULL,

    CONSTRAINT [PK_ExtractionPrompt]         PRIMARY KEY CLUSTERED ([ExtractionPromptId]),
    CONSTRAINT [UQ_ExtractionPrompt_Version] UNIQUE ([InvoiceFormatId], [Version]),
    CONSTRAINT [FK_ExtractionPrompt_InvoiceFormat] FOREIGN KEY ([InvoiceFormatId])
        REFERENCES [dbo].[InvoiceFormat] ([InvoiceFormatId]),
    CONSTRAINT [CK_ExtractionPrompt_Version] CHECK ([Version] >= 1),
    -- These two CHECKs stop the two ways this table actually breaks in production: a prompt
    -- deployed without the document interpolated into it (returns plausible garbage and
    -- looks like a model problem for a day), and a malformed schema that makes Ollama
    -- return prose instead of JSON so InvoiceFieldsJsonParser throws.
    CONSTRAINT [CK_ExtractionPrompt_Placeholder] CHECK (CHARINDEX(N'{{DocumentText}}', [PromptTemplate]) > 0),
    CONSTRAINT [CK_ExtractionPrompt_SchemaJson]  CHECK (ISJSON([ResponseSchemaJson]) = 1)
);
GO

-- Exactly one active prompt per format is a database guarantee, not a convention, so the
-- per-run catalog lookup cannot fan out.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_ExtractionPrompt_OneActive]
    ON [dbo].[ExtractionPrompt] ([InvoiceFormatId])
    WHERE [IsActive] = 1;
