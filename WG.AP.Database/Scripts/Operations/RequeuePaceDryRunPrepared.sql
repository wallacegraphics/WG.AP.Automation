/*
	Requeue reviewed Pace dry-run submissions.

	Dry run is controlled by application configuration:
		Pace:WriteEnabled = false  -- validate and prepare Pace bill payloads only
		Pace:WriteEnabled = true   -- allow real Pace writes when write mapping is enabled

	This script does not enable or disable dry run. It only promotes reviewed rows that already reached
	DryRunPrepared back to Pending so they can be claimed again after Pace writes are intentionally enabled.

	Recommended use:
		1. Run with @Approve = 0 and a ProcessingRun/date filter to review the candidate rows.
		2. Confirm Pace:WriteEnabled is true for the processor environment.
		3. Change @Approve to 1 and run the same filtered script.

	Safety:
		- @Approve must be 1 before any update occurs.
		- At least one filter is required unless @AllowAllDryRunRows is explicitly set to 1.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Approve BIT = 0;
DECLARE @AllowAllDryRunRows BIT = 0;

DECLARE @ProcessingRunId BIGINT = NULL;
DECLARE @CreatedOnOrAfter DATETIME2(3) = NULL;
DECLARE @CreatedOnBefore DATETIME2(3) = NULL;

DECLARE @PendingStatusCodeId INT;
DECLARE @DryRunPreparedStatusCodeId INT;

SELECT @PendingStatusCodeId = [StatusCodeId]
FROM [intgr].[PaceSubmissionStatus]
WHERE [StatusCode] = 'Pending';

SELECT @DryRunPreparedStatusCodeId = [StatusCodeId]
FROM [intgr].[PaceSubmissionStatus]
WHERE [StatusCode] = 'DryRunPrepared';

IF @PendingStatusCodeId IS NULL OR @DryRunPreparedStatusCodeId IS NULL
BEGIN
	RAISERROR('Required Pace submission statuses Pending and DryRunPrepared were not found.', 16, 1);
	RETURN;
END;

IF @AllowAllDryRunRows <> 1
   AND @ProcessingRunId IS NULL
   AND @CreatedOnOrAfter IS NULL
   AND @CreatedOnBefore IS NULL
BEGIN
	RAISERROR('Set at least one filter, or explicitly set @AllowAllDryRunRows = 1.', 16, 1);
	RETURN;
END;

SELECT
	submission.[PaceSubmissionId],
	submission.[InvoiceId],
	invoice.[InvoiceNumber],
	invoice.[CustomerPO],
	invoice.[Total],
	submission.[ProcessingRunId],
	submission.[AttemptCount],
	submission.[CreatedOn],
	submission.[ModifiedOn],
	submission.[ResponseJson]
FROM [intgr].[PaceSubmission] AS submission
INNER JOIN [dbo].[Invoice] AS invoice
	ON invoice.[InvoiceId] = submission.[InvoiceId]
WHERE submission.[StatusCodeId] = @DryRunPreparedStatusCodeId
  AND (@ProcessingRunId IS NULL OR submission.[ProcessingRunId] = @ProcessingRunId)
  AND (@CreatedOnOrAfter IS NULL OR submission.[CreatedOn] >= @CreatedOnOrAfter)
  AND (@CreatedOnBefore IS NULL OR submission.[CreatedOn] < @CreatedOnBefore)
ORDER BY submission.[PaceSubmissionId];

DECLARE @CandidateCount INT =
(
	SELECT COUNT(*)
	FROM [intgr].[PaceSubmission] AS submission
	WHERE submission.[StatusCodeId] = @DryRunPreparedStatusCodeId
	  AND (@ProcessingRunId IS NULL OR submission.[ProcessingRunId] = @ProcessingRunId)
	  AND (@CreatedOnOrAfter IS NULL OR submission.[CreatedOn] >= @CreatedOnOrAfter)
	  AND (@CreatedOnBefore IS NULL OR submission.[CreatedOn] < @CreatedOnBefore)
);

PRINT CONCAT('DryRunPrepared candidate row(s): ', @CandidateCount);

IF @Approve <> 1
BEGIN
	RAISERROR('Review the selected DryRunPrepared rows, then set @Approve = 1 to requeue them.', 16, 1);
	RETURN;
END;

BEGIN TRANSACTION;

UPDATE submission
   SET [StatusCodeId] = @PendingStatusCodeId,
	   [NextAttemptOn] = NULL,
	   [ClaimToken] = NULL,
	   [ClaimedOn] = NULL,
	   [ErrorMessage] = NULL,
	   [ModifiedOn] = SYSUTCDATETIME()
OUTPUT
	inserted.[PaceSubmissionId],
	inserted.[InvoiceId],
	inserted.[ProcessingRunId],
	deleted.[StatusCodeId] AS [PreviousStatusCodeId],
	inserted.[StatusCodeId] AS [NewStatusCodeId],
	inserted.[ModifiedOn]
FROM [intgr].[PaceSubmission] AS submission
WHERE submission.[StatusCodeId] = @DryRunPreparedStatusCodeId
  AND (@ProcessingRunId IS NULL OR submission.[ProcessingRunId] = @ProcessingRunId)
  AND (@CreatedOnOrAfter IS NULL OR submission.[CreatedOn] >= @CreatedOnOrAfter)
  AND (@CreatedOnBefore IS NULL OR submission.[CreatedOn] < @CreatedOnBefore);

DECLARE @RequeuedCount INT = @@ROWCOUNT;

COMMIT TRANSACTION;

PRINT CONCAT('Pace dry-run submission row(s) requeued to Pending: ', @RequeuedCount);
