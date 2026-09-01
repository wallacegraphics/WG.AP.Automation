/*
    dbo.Client seed.

    ClientId values are contract - dbo.Invoice's duplicate index excludes ClientId 0, so
    that sentinel must keep its number. Never renumber.

    Onboarding a new client is a row here plus a dbo.InvoiceFormat row plus a
    dbo.ExtractionPrompt row. No deploy: the Ollama tier handles an unfamiliar layout,
    which is what it is for.
*/
SET NOCOUNT ON;

DECLARE @Client TABLE
(
    ClientId     INT           NOT NULL PRIMARY KEY,
    Code         VARCHAR(30)   NOT NULL,
    Name         NVARCHAR(200) NOT NULL,
    EmailDomain  NVARCHAR(200) NULL,
    PaceVendorId NVARCHAR(50)  NULL,
    IsEnabled    BIT           NOT NULL
);

INSERT INTO @Client (ClientId, Code, Name, EmailDomain, PaceVendorId, IsEnabled)
VALUES
    -- The sentinel for "sender domain matched no client". Never enabled, no domain, and
    -- excluded from UQ_Invoice_ClientNumber so unknown-client invoice numbers cannot
    -- collide across clients.
    (0, 'UNKNOWN', N'(unresolved client)', NULL, NULL, 0),
    -- PaceVendorId stays NULL until the Pace integration ticket supplies it.
    (1, 'SANMAR',  N'SanMar',              N'sanmar.com', NULL, 1);

MERGE [dbo].[Client] AS target
USING @Client AS source
    ON target.[ClientId] = source.[ClientId]
WHEN MATCHED AND (target.[Code] <> source.[Code]
               OR target.[Name] <> source.[Name]
               OR ISNULL(target.[EmailDomain], N'')  <> ISNULL(source.[EmailDomain], N'')
               OR target.[IsEnabled] <> source.[IsEnabled])
    THEN UPDATE SET
        target.[Code]        = source.[Code],
        target.[Name]        = source.[Name],
        target.[EmailDomain] = source.[EmailDomain],
        target.[IsEnabled]   = source.[IsEnabled],
        target.[ModifiedBy]  = SUSER_SNAME(),
        target.[ModifiedOn]  = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET
    THEN INSERT ([ClientId], [Code], [Name], [EmailDomain], [PaceVendorId], [IsEnabled])
         VALUES (source.[ClientId], source.[Code], source.[Name],
                 source.[EmailDomain], source.[PaceVendorId], source.[IsEnabled]);

-- PaceVendorId is deliberately NOT overwritten on MATCHED: once the Pace ticket populates
-- it, re-publishing this seed must not blank it back out.

DECLARE @ClientCount INT = (SELECT COUNT(*) FROM [dbo].[Client]);
PRINT CONCAT('dbo.Client seeded: ', @ClientCount, ' row(s).');
