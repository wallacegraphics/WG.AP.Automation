/*
    Negative tests for the guarantees this schema is supposed to make.

    Run by hand against a scratch database after publishing:

        sqlcmd -S "(localdb)\MSSQLLocalDB" -d WG_AP_SchemaCheck -E -i Tests\constraints.sql

    Every test asserts on the error number, because the point is not "an error happened"
    but "the specific constraint that protects this fired". A test that starts passing for
    the wrong reason is worse than one that fails.

    All work is rolled back at the end, so the script is re-runnable.

    Expected error numbers:
        2601 / 2627  unique index or unique constraint violation
        547          CHECK or FOREIGN KEY violation
*/
-- Required, not optional: dbo.MailMessage and dbo.Invoice carry indexes on persisted
-- computed columns, and SQL Server refuses any INSERT/UPDATE against such a table unless
-- QUOTED_IDENTIFIER and ANSI_NULLS are both ON (Msg 1934). SQLCMD defaults
-- QUOTED_IDENTIFIER to OFF, so a script run this way has to say so.
--
-- The application is unaffected: Microsoft.Data.SqlClient sets both ON when it opens a
-- connection. It is only command-line and legacy clients that need this.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT OFF;

-- A table variable, not a temp table: every result is recorded inside the transaction that
-- gets rolled back at the end, and a temp table's rows would roll back with everything
-- else - leaving an empty report that reads like a clean run.
DECLARE @Result TABLE
(
    Seq      INT IDENTITY(1,1),
    Area     VARCHAR(30)   NOT NULL,
    TestName VARCHAR(120)  NOT NULL,
    Outcome  VARCHAR(200)  NOT NULL
);

DECLARE @Expected INT, @Mailbox UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';

BEGIN TRANSACTION;

-------------------------------------------------------------------------------
-- Fixtures
-------------------------------------------------------------------------------
INSERT INTO dbo.ProcessingRun (MailboxId) VALUES (@Mailbox);
DECLARE @RunId BIGINT = SCOPE_IDENTITY();

INSERT INTO dbo.MailMessage (ProcessingRunId, MailboxId, GraphMessageId, StatusId)
VALUES (@RunId, @Mailbox, N'AAkALgAAAAAA-immutable-id-1', 10);
DECLARE @MsgId BIGINT = SCOPE_IDENTITY();

-- A second client, so "same invoice number, different client" is testable.
INSERT INTO dbo.Client (ClientId, Code, Name, EmailDomain, IsEnabled)
VALUES (900, 'TESTCO', N'Test Co', N'test-co.example', 1);

DECLARE @FormatId INT = (SELECT InvoiceFormatId FROM dbo.InvoiceFormat WHERE Code = 'SANMAR-PDF-SINGLE');

-- Eight attachments, so each invoice test gets its own (UQ_Invoice_Attachment is 1:1).
DECLARE @i INT = 1;
WHILE @i <= 8
BEGIN
    INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
    VALUES (@MsgId, CONCAT(N'att-', @i), CONCAT(N'INV-', @i, N'.pdf'), 1024);
    SET @i += 1;
END

DECLARE @Att TABLE (Ord INT, Id BIGINT);
INSERT INTO @Att (Ord, Id)
SELECT ROW_NUMBER() OVER (ORDER BY MailAttachmentId), MailAttachmentId
  FROM dbo.MailAttachment WHERE MailMessageId = @MsgId;

DECLARE @A1 BIGINT = (SELECT Id FROM @Att WHERE Ord = 1),
        @A2 BIGINT = (SELECT Id FROM @Att WHERE Ord = 2),
        @A3 BIGINT = (SELECT Id FROM @Att WHERE Ord = 3),
        @A4 BIGINT = (SELECT Id FROM @Att WHERE Ord = 4),
        @A5 BIGINT = (SELECT Id FROM @Att WHERE Ord = 5),
        @A6 BIGINT = (SELECT Id FROM @Att WHERE Ord = 6),
        @A7 BIGINT = (SELECT Id FROM @Att WHERE Ord = 7),
        @A8 BIGINT = (SELECT Id FROM @Att WHERE Ord = 8);

-------------------------------------------------------------------------------
-- Mail: re-delivery cannot create a second row
-------------------------------------------------------------------------------
BEGIN TRY
    INSERT INTO dbo.MailMessage (ProcessingRunId, MailboxId, GraphMessageId, StatusId)
    VALUES (@RunId, @Mailbox, N'AAkALgAAAAAA-immutable-id-1', 10);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('MailMessage', 'same mailbox + Graph id rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'MailMessage', 'same mailbox + Graph id rejected',
           CASE WHEN ERROR_NUMBER() IN (2601, 2627) THEN 'PASS'
                ELSE CONCAT('FAIL - expected 2601/2627, got ', ERROR_NUMBER()) END;
END CATCH

-- Same Graph id in a DIFFERENT mailbox is a different item and must be allowed.
BEGIN TRY
    INSERT INTO dbo.MailMessage (ProcessingRunId, MailboxId, GraphMessageId, StatusId)
    VALUES (@RunId, '22222222-2222-2222-2222-222222222222', N'AAkALgAAAAAA-immutable-id-1', 10);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('MailMessage', 'same Graph id, other mailbox allowed', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'MailMessage', 'same Graph id, other mailbox allowed',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER(), ': ', ERROR_MESSAGE());
END CATCH

-- An invoice status in a mail column is structurally impossible.
BEGIN TRY
    INSERT INTO dbo.MailMessage (ProcessingRunId, MailboxId, GraphMessageId, StatusId)
    VALUES (@RunId, @Mailbox, N'wrong-band', 21 /* InvoiceExtracted */);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('MailMessage', 'invoice status rejected in mail column', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'MailMessage', 'invoice status rejected in mail column',
           CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
END CATCH

-------------------------------------------------------------------------------
-- Attachment: a stored path is meaningless without its hash
-------------------------------------------------------------------------------
BEGIN TRY
    INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes, StoredPath)
    VALUES (@MsgId, N'att-nohash', N'x.pdf', 10, N'2026\09\x.pdf');
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('MailAttachment', 'path without hash rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'MailAttachment', 'path without hash rejected',
           CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
END CATCH

-- Two attachments with the same filename on one message must be allowed: clients really
-- do send them, and the schema has to be able to hold them.
BEGIN TRY
    INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
    VALUES (@MsgId, N'att-dupname', N'INV-1.pdf', 2048);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('MailAttachment', 'duplicate filename allowed', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'MailAttachment', 'duplicate filename allowed',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER());
END CATCH

-------------------------------------------------------------------------------
-- Invoice identity: client + invoice number
-------------------------------------------------------------------------------
INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceFormatId, InvoiceNumber, StatusId)
VALUES (@MsgId, @A1, 1, @FormatId, N'INV-001', 20);

-- Punctuation and case must not create a second invoice.
BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @A2, 1, N'inv 001', 20);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'INV-001 vs "inv 001" rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'INV-001 vs "inv 001" rejected',
           CASE WHEN ERROR_NUMBER() IN (2601, 2627) THEN 'PASS'
                ELSE CONCAT('FAIL - expected 2601/2627, got ', ERROR_NUMBER()) END;
END CATCH

-- THE one that would silently block a real payable if it were wrong.
INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
VALUES (@MsgId, @A2, 1, N'INV-0012345', 20);

BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @A3, 1, N'INV-12345', 20);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'leading zeros kept: 0012345 <> 12345', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'leading zeros kept: 0012345 <> 12345',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER(), ' - leading zeros were collapsed');
END CATCH

-- The multi-client case: two clients may each have invoice INV-001.
BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @A4, 900, N'INV-001', 20);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'same number, different client allowed', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'same number, different client allowed',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER());
END CATCH

-- Unknown client (0) is excluded from the constraint, so two unresolved invoices sharing a
-- number must not be mistaken for each other.
INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
VALUES (@MsgId, @A5, 0, N'INV-999', 22);

BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @A6, 0, N'INV-999', 22);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'unknown client excluded from dup check', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'unknown client excluded from dup check',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER());
END CATCH

-- A number that normalises away to nothing must not collide with the next one like it.
INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
VALUES (@MsgId, @A7, 1, N'---', 22);

BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @A8, 1, N'  -  ', 22);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'unusable numbers do not collide', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'unusable numbers do not collide',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER());
END CATCH

-- Accents must distinguish two invoices even though the database collation is
-- accent-insensitive (SQL_Latin1_General_CP1_CI_AS). This is what UQ_Invoice_ClientNumber
-- being a HASH buys: uniqueness that does not depend on the server's collation. Spelled as
-- a collated nvarchar key instead, this pair would silently be treated as one invoice on an
-- AI collation - and as a bonus that spelling also rebuilt the table on every publish.
INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
VALUES (@MsgId, N'att-accent-1', N'accent-1.pdf', 512);
DECLARE @AccA BIGINT = SCOPE_IDENTITY();
INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
VALUES (@MsgId, N'att-accent-2', N'accent-2.pdf', 512);
DECLARE @AccB BIGINT = SCOPE_IDENTITY();

INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
VALUES (@MsgId, @AccA, 1, N'INVA001', 20);

BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @AccB, 1, N'INVÁ001', 20);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'accents distinguish invoices', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'accents distinguish invoices',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER(), ' - collation folded the accent away');
END CATCH

-- One PDF yields one invoice, so re-extraction after a crash is idempotent.
BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId)
    VALUES (@MsgId, @A1, 1, N'INV-777', 20);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'second invoice per attachment rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'second invoice per attachment rejected',
           CASE WHEN ERROR_NUMBER() IN (2601, 2627) THEN 'PASS'
                ELSE CONCAT('FAIL - expected 2601/2627, got ', ERROR_NUMBER()) END;
END CATCH

-------------------------------------------------------------------------------
-- "Extracted" cannot mean anything looser than all five required fields
-------------------------------------------------------------------------------
DECLARE @Cases TABLE (Ord INT IDENTITY(1,1), Label VARCHAR(80), ClientId INT,
                      Num NVARCHAR(100), Dt DATE, PO NVARCHAR(200), Total DECIMAL(19,4));
INSERT INTO @Cases (Label, ClientId, Num, Dt, PO, Total) VALUES
    ('missing CustomerPO',   1, N'E-1', '2026-09-01', NULL,    100.00),
    ('missing InvoiceDate',  1, N'E-2', NULL,         N'PO-1', 100.00),
    ('missing InvoiceNumber',1, NULL,   '2026-09-01', N'PO-1', 100.00),
    ('Total = 0',            1, N'E-4', '2026-09-01', N'PO-1', 0.00),
    ('Total negative',       1, N'E-5', '2026-09-01', N'PO-1', -5.00),
    ('Total NULL',           1, N'E-6', '2026-09-01', N'PO-1', NULL),
    ('client unresolved',    0, N'E-7', '2026-09-01', N'PO-1', 100.00);

DECLARE @Ord INT = 1, @Cases_n INT = (SELECT COUNT(*) FROM @Cases);
WHILE @Ord <= @Cases_n
BEGIN
    DECLARE @Label VARCHAR(80), @CId INT, @Num NVARCHAR(100), @Dt DATE,
            @PO NVARCHAR(200), @Tot DECIMAL(19,4), @AttId BIGINT;

    SELECT @Label = Label, @CId = ClientId, @Num = Num, @Dt = Dt, @PO = PO, @Tot = Total
      FROM @Cases WHERE Ord = @Ord;

    INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
    VALUES (@MsgId, CONCAT(N'att-chk-', @Ord), CONCAT(N'chk-', @Ord, N'.pdf'), 512);
    SET @AttId = SCOPE_IDENTITY();

    BEGIN TRY
        INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber,
                                 InvoiceDate, CustomerPO, Total, StatusId)
        VALUES (@MsgId, @AttId, @CId, @Num, @Dt, @PO, @Tot, 21 /* InvoiceExtracted */);
        INSERT INTO @Result (Area, TestName, Outcome)
        VALUES ('Invoice', CONCAT('Extracted rejects: ', @Label), 'FAIL - the insert succeeded');
    END TRY
    BEGIN CATCH
        INSERT INTO @Result (Area, TestName, Outcome)
        SELECT 'Invoice', CONCAT('Extracted rejects: ', @Label),
               CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                    ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
    END CATCH

    SET @Ord += 1;
END

-- A complete row must of course be accepted.
INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
VALUES (@MsgId, N'att-ok', N'ok.pdf', 512);
DECLARE @OkAtt BIGINT = SCOPE_IDENTITY();

BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber,
                             InvoiceDate, CustomerPO, Total, StatusId, ExtractionMethod)
    VALUES (@MsgId, @OkAtt, 1, N'E-OK', '2026-09-01', N'PO-1', 100.00, 21, 'Regex');
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'Extracted accepts a complete row', 'PASS');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'Extracted accepts a complete row',
           CONCAT('FAIL - rejected with ', ERROR_NUMBER(), ': ', ERROR_MESSAGE());
END CATCH

-- ExtractionMethod is a closed set.
BEGIN TRY
    UPDATE dbo.Invoice SET ExtractionMethod = 'Magic' WHERE MailAttachmentId = @OkAtt;
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'unknown ExtractionMethod rejected', 'FAIL - the update succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'unknown ExtractionMethod rejected',
           CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
END CATCH

-- FieldsJson must be well-formed JSON when present.
INSERT INTO dbo.MailAttachment (MailMessageId, GraphAttachmentId, FileName, SizeInBytes)
VALUES (@MsgId, N'att-badjson', N'badjson.pdf', 512);
DECLARE @BadJsonAtt BIGINT = SCOPE_IDENTITY();

BEGIN TRY
    INSERT INTO dbo.Invoice (MailMessageId, MailAttachmentId, ClientId, InvoiceNumber, StatusId, FieldsJson)
    VALUES (@MsgId, @BadJsonAtt, 1, N'E-BADJSON', 20, N'not json');
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Invoice', 'malformed FieldsJson rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Invoice', 'malformed FieldsJson rejected',
           CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
END CATCH

-------------------------------------------------------------------------------
-- Client and prompt configuration
-------------------------------------------------------------------------------
BEGIN TRY
    INSERT INTO dbo.Client (ClientId, Code, Name, EmailDomain)
    VALUES (901, 'DUPDOM', N'Duplicate domain', N'sanmar.com');
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('Client', 'duplicate EmailDomain rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'Client', 'duplicate EmailDomain rejected',
           CASE WHEN ERROR_NUMBER() IN (2601, 2627) THEN 'PASS'
                ELSE CONCAT('FAIL - expected 2601/2627, got ', ERROR_NUMBER()) END;
END CATCH

DECLARE @Schema NVARCHAR(MAX) = N'{"type":"object"}';

BEGIN TRY
    INSERT INTO dbo.ExtractionPrompt (InvoiceFormatId, Version, PromptTemplate, ResponseSchemaJson, IsActive)
    VALUES (@FormatId, 99, N'Extract fields. {{DocumentText}}', @Schema, 1);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('ExtractionPrompt', 'second active version rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'ExtractionPrompt', 'second active version rejected',
           CASE WHEN ERROR_NUMBER() IN (2601, 2627) THEN 'PASS'
                ELSE CONCAT('FAIL - expected 2601/2627, got ', ERROR_NUMBER()) END;
END CATCH

BEGIN TRY
    INSERT INTO dbo.ExtractionPrompt (InvoiceFormatId, Version, PromptTemplate, ResponseSchemaJson, IsActive)
    VALUES (@FormatId, 98, N'Extract fields from this invoice. No document here.', @Schema, 0);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('ExtractionPrompt', 'prompt without {{DocumentText}} rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'ExtractionPrompt', 'prompt without {{DocumentText}} rejected',
           CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
END CATCH

BEGIN TRY
    INSERT INTO dbo.ExtractionPrompt (InvoiceFormatId, Version, PromptTemplate, ResponseSchemaJson, IsActive)
    VALUES (@FormatId, 97, N'Extract fields. {{DocumentText}}', N'not json', 0);
    INSERT INTO @Result (Area, TestName, Outcome)
    VALUES ('ExtractionPrompt', 'malformed response schema rejected', 'FAIL - the insert succeeded');
END TRY
BEGIN CATCH
    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'ExtractionPrompt', 'malformed response schema rejected',
           CASE WHEN ERROR_NUMBER() = 547 THEN 'PASS'
                ELSE CONCAT('FAIL - expected 547, got ', ERROR_NUMBER()) END;
END CATCH

-------------------------------------------------------------------------------
-- The claim gate: this is the "cannot be processed again" assertion
-------------------------------------------------------------------------------
DECLARE @Statuses TABLE (StatusId INT, Code VARCHAR(30), IsFinal BIT);
INSERT INTO @Statuses SELECT StatusId, Code, IsFinal FROM lkup.Status WHERE StatusId BETWEEN 10 AND 19;

DECLARE @S INT, @Code VARCHAR(30), @IsFinal BIT, @Claimed INT;
DECLARE StatusCursor CURSOR LOCAL FAST_FORWARD FOR SELECT StatusId, Code, IsFinal FROM @Statuses ORDER BY StatusId;
OPEN StatusCursor;
FETCH NEXT FROM StatusCursor INTO @S, @Code, @IsFinal;

WHILE @@FETCH_STATUS = 0
BEGIN
    UPDATE dbo.MailMessage SET StatusId = @S WHERE MailMessageId = @MsgId;

    UPDATE m
       SET AttemptCount = m.AttemptCount + 1, LastAttemptOn = SYSUTCDATETIME()
      FROM dbo.MailMessage m
      JOIN lkup.Status s ON s.StatusId = m.StatusId
     WHERE m.MailMessageId = @MsgId AND s.IsFinal = 0;

    SET @Claimed = @@ROWCOUNT;

    INSERT INTO @Result (Area, TestName, Outcome)
    SELECT 'ClaimGate', CONCAT(@Code, ' (IsFinal=', @IsFinal, ') claimable?'),
           CASE WHEN @IsFinal = 1 AND @Claimed = 0 THEN 'PASS - not claimed'
                WHEN @IsFinal = 0 AND @Claimed = 1 THEN 'PASS - claimed'
                ELSE CONCAT('FAIL - @@ROWCOUNT was ', @Claimed) END;

    FETCH NEXT FROM StatusCursor INTO @S, @Code, @IsFinal;
END

CLOSE StatusCursor;
DEALLOCATE StatusCursor;

-------------------------------------------------------------------------------
-- The normalisation rule itself
-------------------------------------------------------------------------------
INSERT INTO @Result (Area, TestName, Outcome)
SELECT 'Normalize', TestName,
       CASE WHEN Actual = Expected OR (Actual IS NULL AND Expected IS NULL) THEN 'PASS'
            ELSE CONCAT('FAIL - got ', ISNULL(QUOTENAME(Actual, ''''), 'NULL')) END
  FROM (
    SELECT 'INV-162393962 = INV 162393962' AS TestName,
           dbo.NormalizeInvoiceNumber(N'INV-162393962') AS Actual,
           dbo.NormalizeInvoiceNumber(N'INV 162393962') AS Expected
    UNION ALL SELECT 'lower case folds up',
           dbo.NormalizeInvoiceNumber(N'inv-001'), dbo.NormalizeInvoiceNumber(N'INV-001')
    UNION ALL SELECT 'tab stripped',
           dbo.NormalizeInvoiceNumber(CONCAT(N'INV', NCHAR(9), N'001')), N'INV001'
    UNION ALL SELECT 'NBSP stripped',
           dbo.NormalizeInvoiceNumber(CONCAT(N'INV', NCHAR(160), N'001')), N'INV001'
    UNION ALL SELECT 'leading zeros preserved',
           dbo.NormalizeInvoiceNumber(N'0012345'), N'0012345'
    UNION ALL SELECT 'all-punctuation becomes NULL',
           dbo.NormalizeInvoiceNumber(N' - - '), NULL
    UNION ALL SELECT 'NULL in, NULL out',
           dbo.NormalizeInvoiceNumber(NULL), NULL
  ) AS t;

-- Distinct numbers must stay distinct.
INSERT INTO @Result (Area, TestName, Outcome)
SELECT 'Normalize', '0012345 <> 12345',
       CASE WHEN dbo.NormalizeInvoiceNumber(N'0012345') <> dbo.NormalizeInvoiceNumber(N'12345')
            THEN 'PASS' ELSE 'FAIL - leading zeros collapsed' END;

-------------------------------------------------------------------------------
-- Report
-------------------------------------------------------------------------------
ROLLBACK TRANSACTION;

SELECT Seq, Area, TestName, Outcome FROM @Result ORDER BY Seq;

DECLARE @Failed INT = (SELECT COUNT(*) FROM @Result WHERE Outcome LIKE 'FAIL%');
DECLARE @Total  INT = (SELECT COUNT(*) FROM @Result);
PRINT CONCAT('=== ', @Total - @Failed, ' passed, ', @Failed, ' failed, ', @Total, ' total ===');

IF @Failed > 0
    THROW 51001, 'Constraint tests failed. See the result set above.', 1;
