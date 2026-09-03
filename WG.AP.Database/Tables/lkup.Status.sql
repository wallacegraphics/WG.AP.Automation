-- Every processing state in the system, for mail and for invoices alike.
--
-- StatusId values are hand-assigned constants, deliberately NOT IDENTITY, in bands:
--   10-19  mail
--   20-29  invoice
-- That buys two things at no cost: a C# enum can mirror the ids exactly, and each
-- referencing table gets a one-line CHECK on the band instead of a composite foreign
-- key back to an entity-type column.
--
-- IsFinal is the whole no-reprocess mechanism: the claim UPDATE joins here and gates on
-- IsFinal = 0, so "already processed / skipped / duplicate / failed / deleted" is one
-- predicate rather than four branches. A new no-reprocess reason is a seed row, not a
-- code change.
--
-- MailFolder is the destination folder for a message that reaches this status, and NULL
-- means "leave it in the Inbox" (which is how MailDeleted works, since the mailbox has
-- already removed the message). Folder display names are the MailDestinationFolder enum
-- member names by contract - GraphMailboxProcessor.EnsureFoldersExistAsync creates them
-- with destination.ToString().
CREATE TABLE [lkup].[Status]
(
    [StatusId]   INT           NOT NULL,
    [Code]       VARCHAR(30)   NOT NULL,
    [Name]       NVARCHAR(60)  NOT NULL,
    [IsFinal]    BIT           NOT NULL,
    [MailFolder] NVARCHAR(50)  NULL,

    [CreatedBy]  NVARCHAR(128) NOT NULL CONSTRAINT [DF_Status_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]  DATETIME2(3)  NOT NULL CONSTRAINT [DF_Status_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy] NVARCHAR(128) NULL,
    [ModifiedOn] DATETIME2(3)  NULL,

    CONSTRAINT [PK_Status]      PRIMARY KEY CLUSTERED ([StatusId]),
    CONSTRAINT [UQ_Status_Code] UNIQUE ([Code])
);
