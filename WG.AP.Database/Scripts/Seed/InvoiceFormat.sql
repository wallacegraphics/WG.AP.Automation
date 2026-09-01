/*
    dbo.InvoiceFormat seed.

    ExtractorKey is functional, not descriptive: PdfInvoiceFieldExtractor runs the
    SanMar regex tier only when the resolved key is SANMAR_PDF_HEADER_V1, and otherwise
    goes straight to Ollama. That gate is what keeps SanMar's patterns off another
    client's invoice once client #2 exists.

    Only SANMAR-PDF-SINGLE is seeded. SANMAR-PDF-CREDIT is real (credit memos print
    CR-005662167 style numbers) but has no prompt or samples yet, so seeding it would
    create a format that resolves to nothing.
*/
SET NOCOUNT ON;

DECLARE @InvoiceFormat TABLE
(
    ClientCode   VARCHAR(30) NOT NULL,
    Code         VARCHAR(50) NOT NULL PRIMARY KEY,
    ExtractorKey VARCHAR(60) NOT NULL,
    IsEnabled    BIT         NOT NULL
);

INSERT INTO @InvoiceFormat (ClientCode, Code, ExtractorKey, IsEnabled)
VALUES
    ('SANMAR', 'SANMAR-PDF-SINGLE', 'SANMAR_PDF_HEADER_V1', 1);

MERGE [dbo].[InvoiceFormat] AS target
USING (
    SELECT c.[ClientId], f.[Code], f.[ExtractorKey], f.[IsEnabled]
      FROM @InvoiceFormat AS f
      JOIN [dbo].[Client] AS c ON c.[Code] = f.[ClientCode]
) AS source
    ON target.[Code] = source.[Code]
WHEN MATCHED AND (target.[ClientId]     <> source.[ClientId]
               OR target.[ExtractorKey] <> source.[ExtractorKey]
               OR target.[IsEnabled]    <> source.[IsEnabled])
    THEN UPDATE SET
        target.[ClientId]     = source.[ClientId],
        target.[ExtractorKey] = source.[ExtractorKey],
        target.[IsEnabled]    = source.[IsEnabled],
        target.[ModifiedBy]   = SUSER_SNAME(),
        target.[ModifiedOn]   = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET
    THEN INSERT ([ClientId], [Code], [ExtractorKey], [IsEnabled])
         VALUES (source.[ClientId], source.[Code], source.[ExtractorKey], source.[IsEnabled]);

DECLARE @InvoiceFormatCount INT = (SELECT COUNT(*) FROM [dbo].[InvoiceFormat]);
PRINT CONCAT('dbo.InvoiceFormat seeded: ', @InvoiceFormatCount, ' row(s).');
