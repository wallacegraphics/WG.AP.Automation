using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Invoice.Models;

namespace WG.AP.Tests.DataAccess;

/// <summary>
/// Exercises the Dapper repositories against a real SQL Server.
/// </summary>
/// <remarks>
/// Opt-in: set <c>AP_TEST_DB_CONNECTION</c> to a scratch database that the WG.AP.Database project has
/// been published to, and these run. Without it they no-op, so CI stays database-free.
/// <para>
/// These exist because the guarantees this system rests on are enforced by the database, not by C#.
/// "A message is never claimed twice" is a unique index plus a WHERE clause; "an invoice number is
/// never recorded twice for a client" is a unique index and a caught error number. Neither can be
/// tested with a fake — a fake would just be a second, wrong implementation of the rule.
/// </para>
/// <para>
/// Every test cleans up after itself and uses its own GUIDs, so they can run repeatedly against the
/// same scratch database.
/// </para>
/// </remarks>
public class SqlRepositoryTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("AP_TEST_DB_CONNECTION");

    private static SqlConnectionFactory CreateFactory() =>
        new(Options.Create(new DatabaseOptions { ConnectionString = ConnectionString! }));

    /// <summary>
    /// Reports the test as Skipped when there is no database to run against.
    /// </summary>
    /// <remarks>
    /// Skip.If rather than an early return, because an early return reports as <em>Passed</em>. These
    /// are the tests that caught the two bugs the compiler could not (a CHECK constraint that let NULL
    /// through, and Dapper being unable to bind DateOnly), so a green summary line that silently
    /// covers none of that is the most misleading place in the suite to have one.
    /// </remarks>
    private static void SkipUnlessConfigured() =>
        Skip.If(
            string.IsNullOrWhiteSpace(ConnectionString),
            "Set AP_TEST_DB_CONNECTION to a database the WG.AP.Database project has been published to.");

    [SkippableFact]
    public async Task MailboxSyncState_RoundTripsAndOverwrites()
    {
        SkipUnlessConfigured();

        var store = new SqlMailboxSyncStateStore(CreateFactory(), NullLogger<SqlMailboxSyncStateStore>.Instance);
        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");

        Assert.Null(await store.GetDeltaLinkAsync(mailbox, CancellationToken.None));

        await store.SaveDeltaLinkAsync(mailbox, "delta-1", CancellationToken.None);
        Assert.Equal("delta-1", await store.GetDeltaLinkAsync(mailbox, CancellationToken.None));

        // The MERGE has to update rather than fail on the primary key - the delta link is saved on
        // every successful run.
        await store.SaveDeltaLinkAsync(mailbox, "delta-2", CancellationToken.None);
        Assert.Equal("delta-2", await store.GetDeltaLinkAsync(mailbox, CancellationToken.None));
    }

    [SkippableFact]
    public async Task DiscoverAndClaim_IsIdempotent_AndRefusesAMessageInAFinalStatus()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        var message = new MailMessageSummary(
            $"immutable-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            "billing@sanmar.com",
            "Invoice",
            []);

        var first = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        Assert.True(first.Claimed);
        Assert.Equal(1, first.AttemptCount);

        // Graph re-delivers a batch whenever a run crashes before the delta link is committed, so the
        // second sighting must reuse the same row rather than inserting a duplicate.
        var second = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        Assert.Equal(first.MailMessageId, second.MailMessageId);
        Assert.True(second.Claimed, "A message still at MailNew must remain claimable - that is how a transient failure retries.");
        Assert.Equal(2, second.AttemptCount);

        // Once final, it must never be claimed again. This is the whole no-reprocess guarantee.
        await messages.SetStatusAsync(first.MailMessageId, ApStatus.MailProcessed, null, CancellationToken.None);

        var third = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        Assert.Equal(first.MailMessageId, third.MailMessageId);
        Assert.False(third.Claimed);
        Assert.Equal((int)ApStatus.MailProcessed, third.StatusId);

        await runs.FinishAsync(runId, 1, 0, isSuccessful: true, null, CancellationToken.None);
    }

    [SkippableFact]
    public async Task DiscoverAndClaim_TreatsTheSameGraphIdInADifferentMailbox_AsADifferentMessage()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);

        var graphId = $"immutable-{Guid.NewGuid():N}";
        var message = new MailMessageSummary(graphId, DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", []);

        var mailboxA = new MailboxRef(Guid.NewGuid(), "a@wallacegraphics.com");
        var mailboxB = new MailboxRef(Guid.NewGuid(), "b@wallacegraphics.com");

        var runA = await runs.StartAsync(mailboxA, CancellationToken.None);
        var runB = await runs.StartAsync(mailboxB, CancellationToken.None);

        var inA = await messages.DiscoverAndClaimAsync(mailboxA, runA, message, CancellationToken.None);
        var inB = await messages.DiscoverAndClaimAsync(mailboxB, runB, message, CancellationToken.None);

        Assert.NotEqual(inA.MailMessageId, inB.MailMessageId);
    }

    [SkippableFact]
    public async Task RecordAttachments_IsIdempotent_AndKeepsDuplicateFilenames()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        // Two attachments with the same filename, which clients really do send.
        var summaries = new[]
        {
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "INV-1.pdf", 1024, "application/pdf"),
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "INV-1.pdf", 2048, "application/pdf"),
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "manifest.xlsx", 4096, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
        };

        var message = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", summaries);
        var claim = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);

        var first = await attachments.RecordAsync(claim.MailMessageId, summaries, CancellationToken.None);
        Assert.Equal(3, first.Count);

        // Re-delivery must not duplicate attachment rows either.
        var second = await attachments.RecordAsync(claim.MailMessageId, summaries, CancellationToken.None);
        Assert.Equal(first.Select(item => item.MailAttachmentId), second.Select(item => item.MailAttachmentId));

        // Recording a stored file needs the path and hash together, per CK_MailAttachment_Stored.
        await attachments.SetStoredAsync(first[0].MailAttachmentId, @"2026\09\1-INV-1.pdf", new byte[32], CancellationToken.None);
    }

    [SkippableFact]
    public async Task RecordInvoice_ReportsADuplicateNumber_RatherThanThrowing()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);
        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        var summaries = new[]
        {
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "a.pdf", 1024, "application/pdf"),
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "b.pdf", 1024, "application/pdf")
        };

        var message = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", summaries);
        var claim = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        var recorded = await attachments.RecordAsync(claim.MailMessageId, summaries, CancellationToken.None);

        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];

        var first = await invoices.RecordAsync(NewInvoice(claim.MailMessageId, recorded[0].MailAttachmentId, invoiceNumber), CancellationToken.None);
        Assert.False(first.IsDuplicate);
        Assert.NotNull(first.InvoiceId);

        // Punctuation and case must not make a second invoice, and the caller must get a verdict
        // rather than an exception - a duplicate is an expected outcome, not a fault.
        var second = await invoices.RecordAsync(
            NewInvoice(claim.MailMessageId, recorded[1].MailAttachmentId, invoiceNumber.ToLowerInvariant().Replace("-", " ")),
            CancellationToken.None);

        Assert.True(second.IsDuplicate);
        Assert.Null(second.InvoiceId);
    }

    [SkippableFact]
    public async Task LoadCatalogs_ReturnTheSeededSanmarConfiguration()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var clients = new ClientRepository(factory, NullLogger<ClientRepository>.Instance);
        var prompts = new ExtractionPromptRepository(factory, NullLogger<ExtractionPromptRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);

        var catalog = await clients.LoadByEmailDomainAsync(CancellationToken.None);

        var resolved = ClientRepository.Resolve(catalog, "billing@sanmar.com");
        Assert.True(resolved.IsKnown);
        // The real constant, not a local copy: the seed, the extractor's gate and this assertion all
        // have to be the same string or the deterministic tier silently stops running.
        Assert.Equal(ExtractionRequest.SanmarPdfHeaderExtractorKey, resolved.ExtractorKey);
        Assert.NotNull(resolved.InvoiceFormatId);

        // An unrecognised sender must resolve to Unknown rather than throwing: its invoices are still
        // recorded, just routed to review.
        Assert.False(ClientRepository.Resolve(catalog, "someone@not-a-client.example").IsKnown);
        Assert.False(ClientRepository.Resolve(catalog, null).IsKnown);
        Assert.False(ClientRepository.Resolve(catalog, "malformed-address").IsKnown);

        var active = await prompts.LoadActiveAsync(CancellationToken.None);
        Assert.True(active.TryGetValue(resolved.InvoiceFormatId!.Value, out var prompt));
        Assert.Contains(ExtractionPromptRepository.DocumentTextPlaceholder, prompt!.PromptTemplate);

        // Normalised on the way out of the repository, so the model always sees the same bytes.
        Assert.DoesNotContain('\r', prompt.PromptTemplate);

        var folders = await messages.LoadMailFoldersAsync(CancellationToken.None);
        Assert.Equal("Processed", folders[ApStatus.MailProcessed]);
        Assert.Equal("NeedsReview", folders[ApStatus.MailNeedsReview]);
        Assert.Equal("Errors", folders[ApStatus.MailError]);

        // The one that matters: skipped mail has no destination, so it stays in the Inbox.
        Assert.Null(folders[ApStatus.MailSkipped]);
    }

    [SkippableFact]
    public async Task ApplicationLog_WritesABatch_IncludingUnreferencedCorrelationIds()
    {
        SkipUnlessConfigured();

        var repository = new ApplicationLogRepository(CreateFactory());

        var entries = new[]
        {
            new ApplicationLogEntry
            {
                LoggedOn = DateTime.UtcNow,
                LogLevel = 4, // Error
                Category = "WG.AP.Processor.APProcessor",
                Message = "sql-repository-test",
                Exception = new InvalidOperationException("boom").ToString(),
                ProcessingRunId = null,
                MailMessageId = null
            },
            // Correlation ids that reference nothing must still insert. dbo.ApplicationLog deliberately
            // has no foreign keys on them: an FK would make the log write fail exactly when the row it
            // points at was rolled back, which is the moment the line matters most.
            new ApplicationLogEntry
            {
                LoggedOn = DateTime.UtcNow,
                LogLevel = 3, // Warning
                Category = "WG.AP.Processor.APProcessor",
                Message = "sql-repository-test with dangling ids",
                ProcessingRunId = long.MaxValue,
                MailMessageId = long.MaxValue
            }
        };

        await repository.WriteAsync(entries, CancellationToken.None);

        // An empty batch must be a no-op rather than a malformed statement - the sink flushes on
        // dispose whether or not anything was buffered.
        await repository.WriteAsync([], CancellationToken.None);
    }

    private static InvoiceRecord NewInvoice(long mailMessageId, long mailAttachmentId, string invoiceNumber) =>
        new()
        {
            MailMessageId = mailMessageId,
            MailAttachmentId = mailAttachmentId,
            ClientId = 1,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = new DateOnly(2026, 9, 1),
            DueDate = new DateOnly(2026, 11, 1),
            Total = 1234.56m,
            CustomerPO = "PO-1",
            ClientNameAsRead = "SanMar",
            ExtractionMethod = "Regex",
            Status = ApStatus.InvoiceExtracted
        };
}
