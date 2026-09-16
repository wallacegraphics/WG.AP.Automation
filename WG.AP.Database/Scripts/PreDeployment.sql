/*
	Pre-deployment migration.

	These scripts run before the dacpac schema diff is applied. Keep them idempotent because every
	database publish may run them.
*/

PRINT '--- WG.AP.Database pre-deployment migration ---';
GO

IF OBJECT_ID(N'intgr.PaceSubmission', N'U') IS NOT NULL
   AND COL_LENGTH(N'intgr.PaceSubmission', N'StatusCode') IS NOT NULL
	  AND COL_LENGTH(N'intgr.PaceSubmission', N'StatusCodeId') IS NULL
BEGIN
	ALTER TABLE [intgr].[PaceSubmission]
		ADD [StatusCodeId] INT NULL;

	EXEC sys.sp_executesql N'
		UPDATE submission
		   SET [StatusCodeId] = CASE submission.[StatusCode]
				WHEN ''AlreadyEntered'' THEN 1
				WHEN ''BillCreated'' THEN 2
				WHEN ''DryRunPrepared'' THEN 3
				WHEN ''Error'' THEN 4
				WHEN ''InProgress'' THEN 5
				WHEN ''NoPo'' THEN 6
				WHEN ''Pending'' THEN 7
				WHEN ''PoNotReceived'' THEN 8
				WHEN ''RetryLater'' THEN 9
			END
		FROM [intgr].[PaceSubmission] AS submission
		WHERE submission.[StatusCodeId] IS NULL;

		IF EXISTS (SELECT 1 FROM [intgr].[PaceSubmission] WHERE [StatusCodeId] IS NULL)
		BEGIN
			THROW 51000, ''Cannot migrate intgr.PaceSubmission because one or more StatusCode values are not seeded Pace statuses.'', 1;
		END;';
END;
GO

IF OBJECT_ID(N'intgr.PaceSubmission', N'U') IS NOT NULL
   AND COL_LENGTH(N'intgr.PaceSubmission', N'PaceSubmissionStatusId') IS NOT NULL
   AND COL_LENGTH(N'intgr.PaceSubmission', N'StatusCodeId') IS NULL
BEGIN
	ALTER TABLE [intgr].[PaceSubmission]
		ADD [StatusCodeId] INT NULL;

	EXEC sys.sp_executesql N'
		UPDATE submission
		   SET [StatusCodeId] = CASE submission.[PaceSubmissionStatusId]
				WHEN 8 THEN 1 -- AlreadyEntered
				WHEN 5 THEN 2 -- BillCreated
				WHEN 4 THEN 3 -- DryRunPrepared
				WHEN 9 THEN 4 -- Error
				WHEN 2 THEN 5 -- InProgress
				WHEN 7 THEN 6 -- NoPo
				WHEN 1 THEN 7 -- Pending
				WHEN 6 THEN 8 -- PoNotReceived
				WHEN 3 THEN 9 -- RetryLater
			END
		FROM [intgr].[PaceSubmission] AS submission
		WHERE submission.[StatusCodeId] IS NULL;

		IF EXISTS (SELECT 1 FROM [intgr].[PaceSubmission] WHERE [StatusCodeId] IS NULL)
		BEGIN
			THROW 51002, ''Cannot migrate intgr.PaceSubmission because one or more PaceSubmissionStatusId values could not be copied to StatusCodeId.'', 1;
		END;';
END;
GO

IF OBJECT_ID(N'intgr.PaceSubmissionStatus', N'U') IS NOT NULL
	  AND COL_LENGTH(N'intgr.PaceSubmissionStatus', N'StatusCodeId') IS NULL
	  AND COL_LENGTH(N'intgr.PaceSubmissionStatus', N'StatusCode') IS NOT NULL
BEGIN
	ALTER TABLE [intgr].[PaceSubmissionStatus]
		ADD [StatusCodeId] INT NULL;

	EXEC sys.sp_executesql N'
		UPDATE status
		   SET [StatusCodeId] = CASE status.[StatusCode]
				WHEN ''AlreadyEntered'' THEN 1
				WHEN ''BillCreated'' THEN 2
				WHEN ''DryRunPrepared'' THEN 3
				WHEN ''Error'' THEN 4
				WHEN ''InProgress'' THEN 5
				WHEN ''NoPo'' THEN 6
				WHEN ''Pending'' THEN 7
				WHEN ''PoNotReceived'' THEN 8
				WHEN ''RetryLater'' THEN 9
			END
		FROM [intgr].[PaceSubmissionStatus] AS status
		WHERE status.[StatusCodeId] IS NULL;

		IF EXISTS (SELECT 1 FROM [intgr].[PaceSubmissionStatus] WHERE [StatusCodeId] IS NULL)
		BEGIN
			THROW 51001, ''Cannot migrate intgr.PaceSubmissionStatus because one or more StatusCode values do not have deterministic IDs.'', 1;
		END;';
END;
GO

IF OBJECT_ID(N'intgr.PaceSubmissionStatus', N'U') IS NOT NULL
   AND COL_LENGTH(N'intgr.PaceSubmissionStatus', N'PaceSubmissionStatusId') IS NOT NULL
   AND COL_LENGTH(N'intgr.PaceSubmissionStatus', N'StatusCodeId') IS NULL
BEGIN
	ALTER TABLE [intgr].[PaceSubmissionStatus]
		ADD [StatusCodeId] INT NULL;

	EXEC sys.sp_executesql N'
		UPDATE status
		   SET [StatusCodeId] = status.[PaceSubmissionStatusId]
		FROM [intgr].[PaceSubmissionStatus] AS status
		WHERE status.[StatusCodeId] IS NULL;

		IF EXISTS (SELECT 1 FROM [intgr].[PaceSubmissionStatus] WHERE [StatusCodeId] IS NULL)
		BEGIN
			THROW 51003, ''Cannot migrate intgr.PaceSubmissionStatus because one or more PaceSubmissionStatusId values could not be copied to StatusCodeId.'', 1;
		END;';
END;
GO

PRINT '--- pre-deployment migration complete ---';
GO
