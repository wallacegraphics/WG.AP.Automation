-- One row per execution of WG.AP.Processing.
--
-- IsSuccessful is nullable on purpose: NULL means "still running". A row left at NULL
-- with a StartedOn in the past is therefore the crash detector, which is more than
-- Environment.ExitCode = 1 gives an unattended Task Scheduler job today.
CREATE TABLE [dbo].[ProcessingRun]
(
    [ProcessingRunId] BIGINT           IDENTITY(1,1) NOT NULL,
    [MailboxId]       UNIQUEIDENTIFIER NOT NULL,
    [StartedOn]       DATETIME2(3)     NOT NULL CONSTRAINT [DF_ProcessingRun_StartedOn] DEFAULT (SYSUTCDATETIME()),
    [FinishedOn]      DATETIME2(3)     NULL,
    [MessageCount]    INT              NOT NULL CONSTRAINT [DF_ProcessingRun_MessageCount] DEFAULT (0),
    [InvoiceCount]    INT              NOT NULL CONSTRAINT [DF_ProcessingRun_InvoiceCount] DEFAULT (0),
    [IsSuccessful]    BIT              NULL,
    [ErrorMessage]    NVARCHAR(1000)   NULL,

    [CreatedBy]       NVARCHAR(128) NOT NULL CONSTRAINT [DF_ProcessingRun_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]       DATETIME2(3)  NOT NULL CONSTRAINT [DF_ProcessingRun_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]      NVARCHAR(128) NULL,
    [ModifiedOn]      DATETIME2(3)  NULL,

    CONSTRAINT [PK_ProcessingRun] PRIMARY KEY CLUSTERED ([ProcessingRunId]),
    CONSTRAINT [CK_ProcessingRun_Counts] CHECK ([MessageCount] >= 0 AND [InvoiceCount] >= 0)
);
GO

-- Finds runs that never reported an outcome.
CREATE NONCLUSTERED INDEX [IX_ProcessingRun_Unfinished]
    ON [dbo].[ProcessingRun] ([StartedOn])
    WHERE [IsSuccessful] IS NULL;
