-- Durable outbox/audit row for sending one extracted invoice to Pace.
--
-- dbo.Invoice remains the extraction ledger. This table is the handoff to Pace so mailbox parsing,
-- routing and delta commits do not depend on Pace being reachable. One current row per invoice keeps
-- retries idempotent locally; request/response JSON preserve exactly what was/would be sent.
CREATE TABLE [intgr].[PaceSubmission]
(
	[PaceSubmissionId]   BIGINT           IDENTITY(1,1) NOT NULL,
	[InvoiceId]          BIGINT           NOT NULL,
	[StatusCodeId]      INT              NOT NULL,
	[AttemptCount]       INT              NOT NULL CONSTRAINT [DF_PaceSubmission_AttemptCount] DEFAULT (0),
	[NextAttemptOn]      DATETIME2(3)     NULL,

	[ClaimToken]         UNIQUEIDENTIFIER NULL,
	[ClaimedOn]          DATETIME2(3)     NULL,
	[ProcessingRunId]    BIGINT           NULL,

	[RequestJson]        NVARCHAR(MAX)    NULL,
	[ResponseJson]       NVARCHAR(MAX)    NULL,
	[PaceBillBatchId]    NVARCHAR(100)    NULL,
	[PaceBillId]         NVARCHAR(100)    NULL,
	[PaceBillLineId]     NVARCHAR(100)    NULL,
	[ErrorMessage]       NVARCHAR(MAX)    NULL,

	[CreatedOn]          DATETIME2(3)     NOT NULL CONSTRAINT [DF_PaceSubmission_CreatedOn] DEFAULT (SYSUTCDATETIME()),
	[ModifiedOn]         DATETIME2(3)     NULL,

	CONSTRAINT [PK_PaceSubmission] PRIMARY KEY CLUSTERED ([PaceSubmissionId]),
	CONSTRAINT [FK_PaceSubmission_Invoice] FOREIGN KEY ([InvoiceId])
		REFERENCES [dbo].[Invoice] ([InvoiceId]),
	CONSTRAINT [FK_PaceSubmission_Status] FOREIGN KEY ([StatusCodeId])
		REFERENCES [intgr].[PaceSubmissionStatus] ([StatusCodeId])
		ON UPDATE CASCADE,
	CONSTRAINT [FK_PaceSubmission_ProcessingRun] FOREIGN KEY ([ProcessingRunId])
		REFERENCES [dbo].[ProcessingRun] ([ProcessingRunId]),
	CONSTRAINT [UQ_PaceSubmission_Invoice] UNIQUE ([InvoiceId]),
	CONSTRAINT [CK_PaceSubmission_AttemptCount] CHECK ([AttemptCount] >= 0),
	CONSTRAINT [CK_PaceSubmission_ClaimTogether] CHECK
	(
		([ClaimToken] IS NULL AND [ClaimedOn] IS NULL)
		OR ([ClaimToken] IS NOT NULL AND [ClaimedOn] IS NOT NULL)
	),
	CONSTRAINT [CK_PaceSubmission_RequestJson_IsJson] CHECK ([RequestJson] IS NULL OR ISJSON([RequestJson]) = 1),
	CONSTRAINT [CK_PaceSubmission_ResponseJson_IsJson] CHECK ([ResponseJson] IS NULL OR ISJSON([ResponseJson]) = 1)
);
GO

CREATE NONCLUSTERED INDEX [IX_PaceSubmission_WorkQueue_Pending]
	ON [intgr].[PaceSubmission] ([StatusCodeId], [NextAttemptOn], [CreatedOn])
	INCLUDE ([InvoiceId], [AttemptCount])
	WHERE [StatusCodeId] = 7;
GO

CREATE NONCLUSTERED INDEX [IX_PaceSubmission_WorkQueue_RetryLater]
	ON [intgr].[PaceSubmission] ([StatusCodeId], [NextAttemptOn], [CreatedOn])
	INCLUDE ([InvoiceId], [AttemptCount])
	WHERE [StatusCodeId] = 9;
GO

CREATE UNIQUE NONCLUSTERED INDEX [UX_PaceSubmission_ClaimToken]
	ON [intgr].[PaceSubmission] ([ClaimToken])
	WHERE [ClaimToken] IS NOT NULL;
