-- The Graph delta link, one row per mailbox. Replaces FileMailboxSyncStateStore's
-- one-JSON-file-per-mailbox scheme.
--
-- Keyed on MailboxId (the Graph user object id) rather than the address: the GUID
-- survives a mailbox rename, and it fixes a real bug in the file store, whose
-- GetFilePath sanitiser maps every non-alphanumeric character to '_' so that
-- a.b@x.com and a_b@x.com collide on one file.
--
-- DeltaLink is NVARCHAR(MAX) because $deltatoken length is not contractual, this table
-- holds one row per mailbox so the LOB costs nothing, and a silently truncated delta
-- link causes a full resync that nobody would think to diagnose.
CREATE TABLE [dbo].[MailboxSyncState]
(
    [MailboxId]   UNIQUEIDENTIFIER NOT NULL,
    [MailboxUser] NVARCHAR(256)    NOT NULL,   -- the UPN, for humans reading the table
    [DeltaLink]   NVARCHAR(MAX)    NOT NULL,

    [CreatedBy]   NVARCHAR(128) NOT NULL CONSTRAINT [DF_MailboxSyncState_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]   DATETIME2(3)  NOT NULL CONSTRAINT [DF_MailboxSyncState_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]  NVARCHAR(128) NULL,
    [ModifiedOn]  DATETIME2(3)  NULL,

    CONSTRAINT [PK_MailboxSyncState] PRIMARY KEY CLUSTERED ([MailboxId])
);
