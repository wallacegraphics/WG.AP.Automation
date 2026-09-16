-- Status values for the Pace submission outbox.
--
-- Kept separate from lkup.Status because these are integration-delivery states, not mailbox or
-- invoice extraction states. The codes are the durable contract used by the Pace processor and by
-- operations/reporting queries.
CREATE TABLE [intgr].[PaceSubmissionStatus]
(
	[StatusCodeId]           INT           NOT NULL,
	[StatusCode]             VARCHAR(30)   NOT NULL,
	[Name]                   NVARCHAR(60)  NOT NULL,
	[IsFinal]                BIT           NOT NULL,

	[CreatedBy]              NVARCHAR(128) NOT NULL CONSTRAINT [DF_PaceSubmissionStatus_CreatedBy] DEFAULT (SUSER_SNAME()),
	[CreatedOn]              DATETIME2(3)  NOT NULL CONSTRAINT [DF_PaceSubmissionStatus_CreatedOn] DEFAULT (SYSUTCDATETIME()),
	[ModifiedBy]             NVARCHAR(128) NULL,
	[ModifiedOn]             DATETIME2(3)  NULL,

	CONSTRAINT [PK_PaceSubmissionStatus] PRIMARY KEY CLUSTERED ([StatusCodeId]),
	CONSTRAINT [UQ_PaceSubmissionStatus_StatusCode] UNIQUE ([StatusCode])
);
