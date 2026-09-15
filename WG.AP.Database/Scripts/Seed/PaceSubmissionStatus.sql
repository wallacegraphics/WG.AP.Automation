/*
	intgr.PaceSubmissionStatus seed.

	These are integration-delivery states for the Pace outbox. They are deliberately separate from
	lkup.Status, which is scoped to mailbox and invoice extraction routing.
*/
SET NOCOUNT ON;

DECLARE @Status TABLE
(
	StatusCodeId           INT          NOT NULL PRIMARY KEY,
	StatusCode             VARCHAR(30)  NOT NULL UNIQUE,
	Name                   NVARCHAR(60) NOT NULL,
	IsFinal                BIT          NOT NULL
);

INSERT INTO @Status (StatusCodeId, StatusCode, Name, IsFinal)
VALUES
	(1, 'AlreadyEntered', N'Already entered',  1),
	(2, 'BillCreated',    N'Bill created',     1),
	(3, 'DryRunPrepared', N'Dry run prepared', 1),
	(4, 'Error',          N'Error',            1),
	(5, 'InProgress',     N'In progress',      0),
	(6, 'NoPo',           N'No PO',            1),
	(7, 'Pending',        N'Pending',          0),
	(8, 'PoNotReceived',  N'PO not received',  1),
	(9, 'RetryLater',     N'Retry later',      0);

IF EXISTS
(
	SELECT 1
	FROM [intgr].[PaceSubmissionStatus] AS target
	INNER JOIN @Status AS source
		ON source.[StatusCode] = target.[StatusCode]
	WHERE target.[StatusCodeId] <> source.[StatusCodeId]
)
BEGIN
	UPDATE target
	   SET target.[StatusCodeId] = target.[StatusCodeId] + 1000,
		   target.[ModifiedBy] = SUSER_SNAME(),
		   target.[ModifiedOn] = SYSUTCDATETIME()
	FROM [intgr].[PaceSubmissionStatus] AS target
	INNER JOIN @Status AS source
		ON source.[StatusCode] = target.[StatusCode];
END;

MERGE [intgr].[PaceSubmissionStatus] AS target
USING @Status AS source
	ON target.[StatusCode] = source.[StatusCode]
WHEN MATCHED AND (target.[Name] <> source.[Name]
			   OR target.[StatusCodeId] <> source.[StatusCodeId]
			   OR target.[IsFinal] <> source.[IsFinal])
	THEN UPDATE SET
		target.[StatusCodeId] = source.[StatusCodeId],
		target.[Name]       = source.[Name],
		target.[IsFinal]    = source.[IsFinal],
		target.[ModifiedBy] = SUSER_SNAME(),
		target.[ModifiedOn] = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET
	THEN INSERT ([StatusCodeId], [StatusCode], [Name], [IsFinal])
		 VALUES (source.[StatusCodeId], source.[StatusCode], source.[Name], source.[IsFinal]);

DECLARE @StatusCount INT = (SELECT COUNT(*) FROM [intgr].[PaceSubmissionStatus]);
PRINT CONCAT('intgr.PaceSubmissionStatus seeded: ', @StatusCount, ' row(s).');
