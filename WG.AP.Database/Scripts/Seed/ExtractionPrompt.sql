/*
    dbo.ExtractionPrompt seed. THIS FILE IS THE MASTER COPY OF THE PROMPT.

    Edit prompts here, not in SSMS. This file is version-controlled, diffable and
    reviewable; publishing the project deploys it. An UPDATE in SSMS would quietly break
    the insert-a-new-version rule that dbo.Invoice.ExtractionPromptId depends on, and
    SSMS's grid flattens a multi-line string onto one line anyway.

    To change a prompt:
        1. add a new block below with Version = N + 1 and IsActive = 1
        2. set the previous version's IsActive = 0 in the same block
        3. publish
    UQ_ExtractionPrompt_OneActive makes step 2 mandatory rather than merely tidy.

    Version 1 is a verbatim transcription of PdfInvoiceFieldExtractor.BuildPrompt, with
    {{documentText}} renamed to {{DocumentText}} as the only change - so swapping the
    hardcoded prompt for this row is a behavioural no-op.

    One thing deliberately NOT renamed: the prompt and the response schema still say
    "VendorName". Those are the contract with InvoiceFieldsJsonParser (which looks that key
    up) and they are model-facing English where "vendor" reads naturally for whoever issued
    the invoice. Rewording the prompt would change extraction behaviour, which is exactly
    what this seed exists to avoid. The Client rename applies to the schema and the C#
    types; the value lands in dbo.Invoice.ClientNameAsRead.

    Newlines: the template is normalised to CRLF on insert regardless of this file's own
    line endings, so it reads correctly in SSMS and Notepad and does not depend on how git
    checked the file out. The app normalises to \n before substituting {{DocumentText}} and
    posting to Ollama, so the model always sees byte-identical input.
*/
SET NOCOUNT ON;

DECLARE @FormatCode VARCHAR(50) = 'SANMAR-PDF-SINGLE';
DECLARE @Version    INT         = 1;

DECLARE @PromptTemplate NVARCHAR(MAX) = N'Extract fields from this invoice text.

Return only valid JSON with exactly these keys:
{
  "InvoiceNumber": "",
  "SalesOrder": "",
  "InvoiceDate": "",
  "DueDate": "",
  "Total": 0,
  "VendorName": "",
  "CustomerPO": "",
  "CustomerNumber": "",
  "OrderAccount": "",
  "Terms": ""
}

Rules:
- InvoiceNumber is the invoice/voucher number the vendor assigned (example: INV-162393962 or CR-005662167).
- VendorName is the supplier/company shown in the logo/header.
- Total is the invoice''s printed total amount due (may be labeled "Total", "Total Due", or "Subtotal amount").
- CustomerPO is the customer''s purchase order reference (may be labeled "Customer PO", "PO Number", or "Customer Purchase Order").
- CustomerNumber is the vendor-assigned customer account identifier (may be labeled "Customer Number", "Customer Account", or "Account #").
- OrderAccount is the account the specific order was placed under (may be labeled "Order Account" or "Account Number"); it is often the same value as CustomerNumber.
- Terms is the payment terms printed on the invoice (may be labeled "Terms" or "Terms of Payment"), e.g. "Net60".
- Match fields by meaning, not by exact label text - vendors phrase these labels differently and that phrasing isn''t controlled.
- Dates should be returned exactly as printed on the document.
- If a field is missing, keep empty string, and 0 for Total.
- Total must be numeric only.

Document:
{{DocumentText}}';

-- Normalise any mix of LF / CRLF to CRLF, so storage does not depend on this file's
-- checked-out line endings.
SET @PromptTemplate = REPLACE(REPLACE(@PromptTemplate, CHAR(13) + CHAR(10), CHAR(10)),
                              CHAR(10), CHAR(13) + CHAR(10));

-- PdfInvoiceFieldExtractor.ResponseFormat serialised by System.Text.Json with default
-- options: the outer keys stay lowercase and the field names stay PascalCase, exactly as
-- declared in the anonymous object, so a future "what schema did we send" diff is a plain
-- string compare.
DECLARE @ResponseSchemaJson NVARCHAR(MAX) = N'{"type":"object","properties":{"InvoiceNumber":{"type":"string"},"SalesOrder":{"type":"string"},"InvoiceDate":{"type":"string"},"DueDate":{"type":"string"},"Total":{"type":"number"},"VendorName":{"type":"string"},"CustomerPO":{"type":"string"},"CustomerNumber":{"type":"string"},"OrderAccount":{"type":"string"},"Terms":{"type":"string"}},"required":["InvoiceNumber","Total"]}';

-- NULL means "use OllamaOptions.Model". Pin a model here only when a prompt was tuned
-- against a specific one, so an ops-driven qwen3:14b -> qwen3:32b change cannot silently
-- regress this format.
DECLARE @ModelName NVARCHAR(100) = NULL;

DECLARE @InvoiceFormatId INT =
    (SELECT [InvoiceFormatId] FROM [dbo].[InvoiceFormat] WHERE [Code] = @FormatCode);

IF @InvoiceFormatId IS NULL
BEGIN
    -- Fail the publish rather than silently skipping the prompt: a format with no active
    -- prompt means every Ollama fallback throws at 6am.
    THROW 51000, 'ExtractionPrompt seed: invoice format SANMAR-PDF-SINGLE was not found. Seed dbo.InvoiceFormat first.', 1;
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[ExtractionPrompt]
                WHERE [InvoiceFormatId] = @InvoiceFormatId AND [Version] = @Version)
BEGIN
    INSERT INTO [dbo].[ExtractionPrompt]
        ([InvoiceFormatId], [Version], [PromptTemplate], [ResponseSchemaJson],
         [ModelName], [IsActive], [Notes])
    VALUES
        (@InvoiceFormatId, @Version, @PromptTemplate, @ResponseSchemaJson,
         @ModelName, 1,
         N'Verbatim transcription of PdfInvoiceFieldExtractor.BuildPrompt; {{documentText}} renamed to {{DocumentText}}.');

    PRINT CONCAT('dbo.ExtractionPrompt: inserted ', @FormatCode, ' v', @Version, ' (active).');
END
ELSE
BEGIN
    -- Existing rows are immutable by policy. Re-publishing must not rewrite a prompt that
    -- historical invoices point at, so this is a no-op rather than an UPDATE.
    PRINT CONCAT('dbo.ExtractionPrompt: ', @FormatCode, ' v', @Version, ' already present; left unchanged.');
END
