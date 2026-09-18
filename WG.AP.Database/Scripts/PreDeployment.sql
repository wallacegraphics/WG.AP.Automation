/*
	Pre-deployment migration.

	These scripts run before the dacpac schema diff is applied. Keep them idempotent because every
	database publish may run them.
*/

PRINT '--- WG.AP.Database pre-deployment migration ---';
GO

IF OBJECT_ID(N'dbo.Client', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendorId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.Client') AND [name] = N'UQ_Client_PaceVendorId')
BEGIN
	EXEC sys.sp_executesql N'
		CREATE UNIQUE NONCLUSTERED INDEX [UQ_Client_PaceVendorId]
			ON [dbo].[Client] ([PaceVendorId])
			WHERE [PaceVendorId] IS NOT NULL;';
END;
GO

IF OBJECT_ID(N'dbo.Client', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendoreAccountNumber') IS NULL
BEGIN
	ALTER TABLE [dbo].[Client]
		ADD [PaceVendoreAccountNumber] NVARCHAR(50) NULL;
END;
GO

IF OBJECT_ID(N'dbo.Client', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendoreAccountNumber') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendorId') IS NOT NULL
BEGIN
	EXEC sys.sp_executesql N'
		UPDATE [dbo].[Client]
		   SET [PaceVendoreAccountNumber] = [PaceVendorId],
			   [ModifiedBy] = SUSER_SNAME(),
			   [ModifiedOn] = SYSUTCDATETIME()
		 WHERE NULLIF(LTRIM(RTRIM([PaceVendoreAccountNumber])), N'''') IS NULL
		   AND NULLIF(LTRIM(RTRIM([PaceVendorId])), N'''') IS NOT NULL;';
END;
GO

IF OBJECT_ID(N'dbo.Client', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendoreAccountNumber') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendoreId') IS NOT NULL
BEGIN
	EXEC sys.sp_executesql N'
		UPDATE [dbo].[Client]
		   SET [PaceVendoreAccountNumber] = [PaceVendoreId],
			   [ModifiedBy] = SUSER_SNAME(),
			   [ModifiedOn] = SYSUTCDATETIME()
		 WHERE NULLIF(LTRIM(RTRIM([PaceVendoreAccountNumber])), N'''') IS NULL
		   AND NULLIF(LTRIM(RTRIM([PaceVendoreId])), N'''') IS NOT NULL;';
END;
GO

IF OBJECT_ID(N'dbo.Client', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Client', N'PaceVendoreAccountNumber') IS NOT NULL
BEGIN
	UPDATE [dbo].[Client]
	   SET [PaceVendoreAccountNumber] = N'76274-0000',
		   [ModifiedBy] = SUSER_SNAME(),
		   [ModifiedOn] = SYSUTCDATETIME()
	 WHERE [ClientId] = 1
	   AND NULLIF(LTRIM(RTRIM([PaceVendoreAccountNumber])), N'') IS NULL;
END;
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

	IF COL_LENGTH(N'intgr.PaceSubmissionStatus', N'StatusCode') IS NULL
	BEGIN
		ALTER TABLE [intgr].[PaceSubmissionStatus]
			ADD [StatusCode] VARCHAR(30) NULL;
	END;

	IF COL_LENGTH(N'intgr.PaceSubmissionStatus', N'Name') IS NULL
	BEGIN
		ALTER TABLE [intgr].[PaceSubmissionStatus]
			ADD [Name] NVARCHAR(60) NULL;
	END;

	IF COL_LENGTH(N'intgr.PaceSubmissionStatus', N'IsFinal') IS NULL
	BEGIN
		ALTER TABLE [intgr].[PaceSubmissionStatus]
			ADD [IsFinal] BIT NULL;
	END;

	EXEC sys.sp_executesql N'
		UPDATE status
		   SET [StatusCodeId] = CASE status.[PaceSubmissionStatusId]
				WHEN 8 THEN 1 -- AlreadyEntered
				WHEN 5 THEN 2 -- BillCreated
				WHEN 4 THEN 3 -- DryRunPrepared
				WHEN 9 THEN 4 -- Error
				WHEN 2 THEN 5 -- InProgress
				WHEN 7 THEN 6 -- NoPo
				WHEN 1 THEN 7 -- Pending
				WHEN 6 THEN 8 -- PoNotReceived
				WHEN 3 THEN 9 -- RetryLater
			END,
			   [StatusCode] = COALESCE(status.[StatusCode], CASE status.[PaceSubmissionStatusId]
				WHEN 8 THEN ''AlreadyEntered''
				WHEN 5 THEN ''BillCreated''
				WHEN 4 THEN ''DryRunPrepared''
				WHEN 9 THEN ''Error''
				WHEN 2 THEN ''InProgress''
				WHEN 7 THEN ''NoPo''
				WHEN 1 THEN ''Pending''
				WHEN 6 THEN ''PoNotReceived''
				WHEN 3 THEN ''RetryLater''
			END),
			   [Name] = COALESCE(status.[Name], CASE status.[PaceSubmissionStatusId]
				WHEN 8 THEN N''Already entered''
				WHEN 5 THEN N''Bill created''
				WHEN 4 THEN N''Dry run prepared''
				WHEN 9 THEN N''Error''
				WHEN 2 THEN N''In progress''
				WHEN 7 THEN N''No PO''
				WHEN 1 THEN N''Pending''
				WHEN 6 THEN N''PO not received''
				WHEN 3 THEN N''Retry later''
			END),
			   [IsFinal] = COALESCE(status.[IsFinal], CASE status.[PaceSubmissionStatusId]
				WHEN 1 THEN CONVERT(BIT, 0) -- Pending
				WHEN 2 THEN CONVERT(BIT, 0) -- InProgress
				WHEN 3 THEN CONVERT(BIT, 0) -- RetryLater
				WHEN 4 THEN CONVERT(BIT, 1) -- DryRunPrepared
				WHEN 5 THEN CONVERT(BIT, 1) -- BillCreated
				WHEN 6 THEN CONVERT(BIT, 1) -- PoNotReceived
				WHEN 7 THEN CONVERT(BIT, 1) -- NoPo
				WHEN 8 THEN CONVERT(BIT, 1) -- AlreadyEntered
				WHEN 9 THEN CONVERT(BIT, 1) -- Error
			END)
		FROM [intgr].[PaceSubmissionStatus] AS status
		WHERE status.[StatusCodeId] IS NULL
		   OR status.[StatusCode] IS NULL
		   OR status.[Name] IS NULL
		   OR status.[IsFinal] IS NULL;

		IF EXISTS (SELECT 1 FROM [intgr].[PaceSubmissionStatus] WHERE [StatusCodeId] IS NULL OR [StatusCode] IS NULL OR [Name] IS NULL OR [IsFinal] IS NULL)
		BEGIN
			THROW 51003, ''Cannot migrate intgr.PaceSubmissionStatus because one or more PaceSubmissionStatusId values could not be mapped to canonical Pace status columns.'', 1;
		END;';
END;
GO

PRINT '--- pre-deployment migration complete ---';
GO
