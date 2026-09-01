-- The single definition of "are these two invoice numbers the same invoice".
--
-- This lives in a function rather than inline in dbo.Invoice because two computed columns
-- there both need it, and SQL Server forbids a computed column referencing another
-- computed column (Msg 1759). Writing the rule out twice would mean a future correction
-- could be applied to one copy and not the other - and the failure mode of that is either
-- paying an invoice twice or rejecting a real payable, so it is worth a function.
--
-- WITH SCHEMABINDING is required, not decorative: without it the function is treated as
-- non-deterministic and the computed columns that call it cannot be persisted or indexed.
--
-- The rule, and why each part of it is the way it is:
--   * leading zeros are KEPT. 0012345 and 12345 are different invoices at some clients,
--     and collapsing them would reject a real payable as a duplicate.
--   * space, hyphen, tab and NBSP are stripped, so INV-162393962 and INV 162393962 are
--     one invoice. PDF text extraction produces tabs and non-breaking spaces, and
--     SanmarPdfHeaderExtractor's \S+ captures happily include an NBSP.
--   * NULLIF maps a number that normalises away to nothing (say '---') onto NULL, so it
--     is treated as "no usable number" rather than as an empty number that every other
--     unusable number collides with.
--
-- Collation is deliberately NOT pinned here. A scalar function's return value takes the
-- database's collation whatever this body says, so a COLLATE clause in here would look
-- like a guarantee while providing none. The binary collation that actually makes the
-- uniqueness rule independent of the server's collation is applied on the indexed columns
-- in dbo.Invoice - which is where it has effect. Without it, an accent-insensitive server
-- collation would treat INVA001 and INVÁ001 as the same invoice, silently.
CREATE FUNCTION [dbo].[NormalizeInvoiceNumber]
(
    @InvoiceNumber NVARCHAR(100)
)
RETURNS NVARCHAR(100)
WITH SCHEMABINDING
AS
BEGIN
    RETURN NULLIF(
        UPPER(REPLACE(REPLACE(REPLACE(REPLACE(
            LTRIM(RTRIM(@InvoiceNumber)),
            N' ',       N''),
            N'-',       N''),
            NCHAR(9),   N''),
            NCHAR(160), N'')),
        N'');
END
