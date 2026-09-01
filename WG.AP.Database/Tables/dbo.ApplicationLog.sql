-- One row per emitted log event. Mirrors what FileLogger writes today.
--
-- ProcessingRunId and MailMessageId deliberately have NO foreign keys. An FK would make
-- the log insert fail precisely when the referenced row was rolled back, which is exactly
-- when the log matters. These are correlation ids, not relationships, and this is the one
-- place the schema gives up referential integrity on purpose.
--
-- Three matching rules for the sink (see SqlLoggerProvider), all following FileLogger's
-- principle that logging can never break processing:
--   * write on its own connection, outside any ambient transaction - a log line inside a
--     transaction that later rolls back disappears, which is the worst property of DB logging
--   * swallow its own exceptions, and never report them through ILogger (infinite recursion);
--     the file log is where a failing DB sink surfaces
--   * keep the file log. It is the only sink that still works when the database is the
--     broken thing.
--
-- No columns for structured template properties, EventId or scopes, because FileLogger
-- discards all three today (BeginScope returns null). Adding them is a code change first.
--
-- Retention is 1 year, as a DELETE by LoggedOn. Note the file log is configured at 60 days
-- (FileLogging:LogFilesRetentionDays), so the two now differ deliberately.
CREATE TABLE [dbo].[ApplicationLog]
(
    [ApplicationLogId] BIGINT        IDENTITY(1,1) NOT NULL,
    [LoggedOn]         DATETIME2(3)  NOT NULL CONSTRAINT [DF_ApplicationLog_LoggedOn] DEFAULT (SYSUTCDATETIME()),
    [LogLevel]         TINYINT       NOT NULL,   -- Microsoft.Extensions.Logging.LogLevel int value
    [Category]         NVARCHAR(256) NULL,       -- fully-qualified type name, as FileLogger writes
    [Message]          NVARCHAR(MAX) NOT NULL,
    [Exception]        NVARCHAR(MAX) NULL,       -- exception.ToString(), as FileLogger writes
    [ProcessingRunId]  BIGINT        NULL,
    [MailMessageId]    BIGINT        NULL,

    [CreatedBy]        NVARCHAR(128) NOT NULL CONSTRAINT [DF_ApplicationLog_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]        DATETIME2(3)  NOT NULL CONSTRAINT [DF_ApplicationLog_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]       NVARCHAR(128) NULL,
    [ModifiedOn]       DATETIME2(3)  NULL,

    CONSTRAINT [PK_ApplicationLog] PRIMARY KEY CLUSTERED ([ApplicationLogId]),
    -- Trace(0) .. Critical(5). None(6) is filtered out before it reaches a sink.
    CONSTRAINT [CK_ApplicationLog_LogLevel] CHECK ([LogLevel] >= 0 AND [LogLevel] <= 5)
);
GO

CREATE NONCLUSTERED INDEX [IX_ApplicationLog_LoggedOn]
    ON [dbo].[ApplicationLog] ([LoggedOn])
    INCLUDE ([LogLevel]);
GO

CREATE NONCLUSTERED INDEX [IX_ApplicationLog_ProcessingRun]
    ON [dbo].[ApplicationLog] ([ProcessingRunId])
    WHERE [ProcessingRunId] IS NOT NULL;
