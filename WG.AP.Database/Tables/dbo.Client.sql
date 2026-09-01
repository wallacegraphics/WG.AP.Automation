-- A client that sends us invoices. ClientId 0 is the Unknown sentinel, used whenever a
-- sender domain matches no row here; that value is excluded from the duplicate-invoice
-- index (see dbo.Invoice) so two different clients' identical invoice numbers can never
-- be mistaken for one another.
--
-- PaceVendorId keeps the word "Vendor" on purpose: it holds the id of Pace's own entity,
-- which Pace's API literally calls Vendor (readVendor, Vendor.Id). Renaming it would break
-- the mapping to the API it mirrors. It is a string, not an int - Pace returns Vendor.Id
-- as a string, which is a documented landmine.
--
-- Clients are retired with IsEnabled = 0, never DELETEd: historical invoices point at the
-- client and format that produced them, and reports must stay accurate.
CREATE TABLE [dbo].[Client]
(
    [ClientId]     INT           NOT NULL,   -- assigned constants: 0 = Unknown, 1 = SanMar
    [Code]         VARCHAR(30)   NOT NULL,
    [Name]         NVARCHAR(200) NOT NULL,
    [EmailDomain]  NVARCHAR(200) NULL,       -- 'sanmar.com'; how a sender resolves to a client
    [PaceVendorId] NVARCHAR(50)  NULL,
    [IsEnabled]    BIT           NOT NULL CONSTRAINT [DF_Client_IsEnabled] DEFAULT (1),

    [CreatedBy]    NVARCHAR(128) NOT NULL CONSTRAINT [DF_Client_CreatedBy] DEFAULT (SUSER_SNAME()),
    [CreatedOn]    DATETIME2(3)  NOT NULL CONSTRAINT [DF_Client_CreatedOn] DEFAULT (SYSUTCDATETIME()),
    [ModifiedBy]   NVARCHAR(128) NULL,
    [ModifiedOn]   DATETIME2(3)  NULL,

    CONSTRAINT [PK_Client]      PRIMARY KEY CLUSTERED ([ClientId]),
    CONSTRAINT [UQ_Client_Code] UNIQUE ([Code])
);
GO

-- Load-bearing now that the sender domain is how a client is resolved: two clients
-- claiming one domain would make resolution non-deterministic.
CREATE UNIQUE NONCLUSTERED INDEX [UQ_Client_EmailDomain]
    ON [dbo].[Client] ([EmailDomain])
    WHERE [EmailDomain] IS NOT NULL;
GO

CREATE UNIQUE NONCLUSTERED INDEX [UQ_Client_PaceVendorId]
    ON [dbo].[Client] ([PaceVendorId])
    WHERE [PaceVendorId] IS NOT NULL;
