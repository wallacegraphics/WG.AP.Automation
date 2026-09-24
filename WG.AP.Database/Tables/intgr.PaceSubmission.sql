-- Durable outbox/audit row for sending one extracted invoice to Pace.
--
-- dbo.Invoice remains the extraction ledger. This table is the handoff to Pace so mailbox parsing,
-- routing and delta commits do not depend on Pace being reachable. One current row per invoice keeps
-- retries idempotent locally; response JSON preserves Pace validation/write evidence.
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

	[ResponseJson]       NVARCHAR(MAX)    NULL,
	[PaceBillBatchId]    NVARCHAR(100)    NULL,
	[PaceBillId]         NVARCHAR(100)    NULL,
	[PaceBillLineId]     NVARCHAR(100)    NULL,
	[ErrorMessage]       NVARCHAR(MAX)    NULL,

	[CreatedOn]          DATETIME2(3)     NOT NULL CONSTRAINT [DF_PaceSubmission_CreatedOn] DEFAULT (SYSUTCDATETIME()),
	[ModifiedOn]         DATETIME2(3)     NULL,

	-- Tracked separately from StatusCodeId so a submission can be Pace-final (and non-reclaimable) while its
	-- mail routing (SetStatusAsync + move) is still outstanding. NULL means "not yet confirmed routed"; the
	-- processor's recovery sweep replays routing for any final, review/error-worthy row still NULL here.
	-- Rows that predate these columns are NULL here too, yet must never be swept - that would re-route and
	-- re-alert on recent history. They are told apart by RoutingClaimedOn below, not by a backfill: a backfill
	-- from PreDeployment collides with the dacpac's own ADD, and PostDeployment runs on every publish.
	[RequiresReview]     BIT              NOT NULL CONSTRAINT [DF_PaceSubmission_RequiresReview] DEFAULT (0),
	[MailRoutedOn]       DATETIME2(3)     NULL,

	-- MailRoutedOn means routing is fully done: mail status set, message moved AND the alert/digest line
	-- delivered. NotifiedOn records the notification half on its own, so a retry that only needs to redo the
	-- move does not alert AP a second time. BillVendor is the vendor the bill was actually created under,
	-- persisted so a recovered digest names it rather than the client's configured account. RoutingClaimedOn
	-- is a lease: whichever run completed or swept the row owns its routing until the lease expires, so two
	-- overlapping runs cannot both route (and both notify) the same submission. A run that fails to route a
	-- row hands the lease back (an already-expired time, never NULL) at its end, so the next run retries it.
	-- RoutingClaimedOn doubles as the history marker: CompleteAsync is the only way a row becomes final and it
	-- always sets it, so NULL means "completed before this code existed" and the sweep never touches that row.
	-- Routing bookkeeping on these columns never bumps ModifiedOn: the recovery window is measured from the
	-- Pace outcome, and a lease renewal that reset it would keep a permanently failing row in the sweep forever.
	[NotifiedOn]         DATETIME2(3)     NULL,
	[BillVendor]         NVARCHAR(100)    NULL,
	[RoutingClaimedOn]   DATETIME2(3)     NULL,

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
GO

-- Supports the mail-routing recovery sweep (ClaimUnroutedFinalSubmissionsAsync): a seek on the recent
-- ModifiedOn window among rows not yet fully routed. RoutingClaimedOn IS NOT NULL keeps pre-existing history
-- (all NULL in both columns) out of the index as well as out of the sweep.
CREATE NONCLUSTERED INDEX [IX_PaceSubmission_RoutingRecovery]
	ON [intgr].[PaceSubmission] ([ModifiedOn])
	INCLUDE ([StatusCodeId], [RequiresReview], [RoutingClaimedOn], [NotifiedOn])
	WHERE [MailRoutedOn] IS NULL AND [RoutingClaimedOn] IS NOT NULL;
