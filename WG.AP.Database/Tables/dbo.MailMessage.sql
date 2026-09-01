-- One row per email, ever. This is the table that makes "the emails cannot be processed
-- again if it is deleted, duplicate, failed or already processed" true.
--
-- Two statements carry the whole mechanism (see MailMessageRepository):
--   1. discovery INSERT ... WHERE NOT EXISTS  -> re-delivery is a no-op, guaranteed by
--      UQ_MailMessage_MessageKeyHash below.
--   2. claim UPDATE ... JOIN lkup.Status WHERE IsFinal = 0  -> @@ROWCOUNT of 0 means the
--      message is finished and must not be parsed or moved again.
--
-- All of it depends on Graph immutable ids (Prefer: IdType="ImmutableId", applied by
-- GraphMailboxProcessor.ApplyImmutableId). Without that header the id changes when the
-- message is moved to Processed/Errors/NeedsReview, and every filed message would
-- re-enter as brand new work forever.
--
-- Transient failures deliberately get no status of their own. The extractor rethrows on
-- HttpRequestException/TaskCanceledException, so nothing final commits and the delta link
-- is never advanced: the row stays at MailNew and the next run re-delivers it. Retry works
-- through the absence of a final state, exactly as it did through the absence of a
-- committed delta link. AttemptCount and LastAttemptOn exist so "Ollama has been down for
-- three runs" is a query rather than a grep.
CREATE TABLE [dbo].[MailMessage]
(
    [MailMessageId]   BIGINT            IDENTITY(1,1) NOT NULL,
    [ProcessingRunId] BIGINT            NULL,
    [MailboxId]       UNIQUEIDENTIFIER  NOT NULL,
    [GraphMessageId]  NVARCHAR(512)     NOT NULL,   -- immutable id; ~120-200 chars observed

    -- A composite unique key on (MailboxId, GraphMessageId) would be over 1000 bytes and
    -- grow with the id, against SQL Server's 1700-byte nonclustered key limit - one
    -- longer-than-expected id would turn a dedup guarantee into an insert failure.
    -- A persisted SHA-256 is 32 bytes, forever.
    [MessageKeyHash]  AS CONVERT(BINARY(32), HASHBYTES('SHA2_256',
                          CONCAT(CONVERT(CHAR(36), [MailboxId]), N'|', [GraphMessageId]))) PERSISTED,

    [SenderAddress]   NVARCHAR(320)     NULL,
    [Subject]         NVARCHAR(500)     NULL,
    -- Nullable, and datetimeoffset, because MailMessageSummary.ReceivedDateTime is
    -- DateTimeOffset? and Graph supplies an offset. CreatedOn is the NOT NULL date axis.
    [ReceivedOn]      DATETIMEOFFSET(3) NULL,

    [StatusId]        INT               NOT NULL,
    [AttemptCount]    INT               NOT NULL CONSTRAINT [DF_MailMessage_AttemptCount] DEFAULT (0),
    [LastAttemptOn]   DATETIME2(3)      NULL,
    [ErrorMessage]    NVARCHAR(1000)    NULL,

    [CreatedBy]       NVARCHAR(128) NOT NULL CONSTRAINT [DF_MailMessage_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]       DATETIME2(3)  NOT NULL CONSTRAINT [DF_MailMessage_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]      NVARCHAR(128) NULL,
    [ModifiedOn]      DATETIME2(3)  NULL,

    CONSTRAINT [PK_MailMessage] PRIMARY KEY CLUSTERED ([MailMessageId]),
    CONSTRAINT [FK_MailMessage_Status] FOREIGN KEY ([StatusId])
        REFERENCES [lkup].[Status] ([StatusId]),
    CONSTRAINT [FK_MailMessage_ProcessingRun] FOREIGN KEY ([ProcessingRunId])
        REFERENCES [dbo].[ProcessingRun] ([ProcessingRunId]),
    -- Keeps an invoice status out of a mail column without a composite FK.
    -- Spelled as two comparisons rather than BETWEEN, and as ORed equalities rather
    -- than IN, throughout this schema. SQL Server rewrites both forms when it stores
    -- them, the dacpac model keeps what was written, and the two then never match - so
    -- every publish drops and recreates the constraint. Matching the stored form keeps a
    -- no-change publish silent, which is what makes a real change visible in the diff.
    CONSTRAINT [CK_MailMessage_StatusBand]   CHECK ([StatusId] >= 10 AND [StatusId] <= 19),
    CONSTRAINT [CK_MailMessage_AttemptCount] CHECK ([AttemptCount] >= 0)
);
GO

-- THE constraint. Re-delivery of an already-recorded message becomes a no-op.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_MailMessage_MessageKeyHash]
    ON [dbo].[MailMessage] ([MessageKeyHash]);
GO

-- The work queue. A finished row is not merely skipped by the code, it is invisible here.
CREATE NONCLUSTERED INDEX [IX_MailMessage_Status]
    ON [dbo].[MailMessage] ([StatusId], [CreatedOn]);
