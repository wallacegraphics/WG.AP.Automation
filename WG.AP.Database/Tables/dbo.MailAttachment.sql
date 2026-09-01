-- One row per attachment on an email.
--
-- Kind keeps 'Excel' even though nothing reads Excel any more, and that is deliberate:
-- a recorded row is how "a manifest arrived and we ignored it" stays visible, which
-- matters the day someone asks whether we could have caught something. Excel attachments
-- are recorded but never downloaded, so their StoredPath and ContentSha256 stay NULL.
--
-- File bytes live on a UNC share (7-year retention). StoredPath is relative to a root
-- held in configuration, so moving the share is a config change rather than an UPDATE
-- across history.
CREATE TABLE [dbo].[MailAttachment]
(
    [MailAttachmentId]  BIGINT        IDENTITY(1,1) NOT NULL,
    [MailMessageId]     BIGINT        NOT NULL,
    [GraphAttachmentId] NVARCHAR(512) NOT NULL,
    [FileName]          NVARCHAR(400) NOT NULL,
    [ContentType]       NVARCHAR(200) NULL,
    [SizeInBytes]       BIGINT        NOT NULL,

    -- The filename-suffix-only classification (APProcessor.IsPdf/IsExcel) in exactly one
    -- place, and indexable. LIKE is case-insensitive under the model collation, which
    -- matches the StringComparison.OrdinalIgnoreCase the C# used.
    --
    -- The explicit CONVERT is required, not cosmetic: a bare CASE leaves SQL Server to
    -- infer the type, that inference does not round-trip through the dacpac model, and
    -- sqlpackage then schedules a full TABLE REBUILD on every publish - dropping and
    -- recreating every constraint on the table each time.
    [Kind]              AS (CONVERT(VARCHAR(5),
                                 CASE WHEN [FileName] LIKE '%.pdf'  THEN 'Pdf'
                                      WHEN [FileName] LIKE '%.xlsx' THEN 'Excel'
                                      ELSE 'Other' END)) PERSISTED,

    [StoredPath]        NVARCHAR(600) NULL,
    [ContentSha256]     BINARY(32)    NULL,

    [CreatedBy]         NVARCHAR(128) NOT NULL CONSTRAINT [DF_MailAttachment_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]         DATETIME2(3)  NOT NULL CONSTRAINT [DF_MailAttachment_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]        NVARCHAR(128) NULL,
    [ModifiedOn]        DATETIME2(3)  NULL,

    CONSTRAINT [PK_MailAttachment] PRIMARY KEY CLUSTERED ([MailAttachmentId]),
    CONSTRAINT [FK_MailAttachment_MailMessage] FOREIGN KEY ([MailMessageId])
        REFERENCES [dbo].[MailMessage] ([MailMessageId]),
    CONSTRAINT [CK_MailAttachment_Size] CHECK ([SizeInBytes] >= 0),
    -- A stored path is meaningless without its hash. Write the file first, then the row:
    -- an orphan file is harmless, a row pointing at nothing is not.
    CONSTRAINT [CK_MailAttachment_Stored] CHECK (
        ([StoredPath] IS NULL     AND [ContentSha256] IS NULL)
     OR ([StoredPath] IS NOT NULL AND [ContentSha256] IS NOT NULL))
);
GO

-- Attachment ids are unique only within a message, so the message id is part of the key.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_MailAttachment_Graph]
    ON [dbo].[MailAttachment] ([MailMessageId], [GraphAttachmentId]);
GO

-- Content-level duplicate DETECTION across messages. Deliberately not unique: a
-- legitimate client resend is the same bytes, so it must be findable, not rejected.
--
-- Note there is also deliberately no unique index on (MailMessageId, FileName) - clients
-- really do send two attachments with the same name, and the schema has to hold them.
CREATE NONCLUSTERED INDEX [IX_MailAttachment_Sha256]
    ON [dbo].[MailAttachment] ([ContentSha256])
    WHERE [ContentSha256] IS NOT NULL;
