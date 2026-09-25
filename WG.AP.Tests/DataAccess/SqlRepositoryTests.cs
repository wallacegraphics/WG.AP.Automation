using Dapper;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;
using WG.AP.Invoice.Models;
using WG.AP.Processor;

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
        new(Options.Create(new DatabaseOptions
        {
            ConnectionString = ConnectionString!,
            PaceRoutingLeaseMinutes = 10,
            PaceRecoveryWindowDays = 7
        }));

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

    private static async Task SkipUnlessPaceSchemaPublishedAsync(SqlConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        var exists = await connection.ExecuteScalarAsync<int>(
            """
            SELECT CASE
                WHEN OBJECT_ID(N'intgr.PaceSubmission', N'U') IS NOT NULL
                 AND COL_LENGTH(N'intgr.PaceSubmission', N'StatusCodeId') IS NOT NULL
                 AND COL_LENGTH(N'intgr.PaceSubmissionStatus', N'StatusCodeId') IS NOT NULL
                 AND COL_LENGTH(N'dbo.Client', N'PaceVendorAccountNumber') IS NOT NULL
                    THEN 1
                ELSE 0
            END;
            """);

        Skip.If(exists == 0, "Publish the WG.AP.Database project with the normalized Pace submission schema and Client.PaceVendorAccountNumber before running Pace SQL repository tests.");
    }

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
    public async Task RecordAlreadyFinalReplayNeedsReview_OnASecondOccurrenceInTheSameRun_StillReturnsTheRow()
    {
        SkipUnlessConfigured();

        // The exact regression this covers: Graph can redeliver the same already-final message more
        // than once within a single run's delta batch (e.g. right after this pipeline itself moved it
        // into a folder). The first occurrence must record one synthetic NeedsReview row; every later
        // occurrence in the same run must still report that same row rather than silently vanishing.
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

        var original = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        Assert.True(original.Claimed);
        await messages.SetStatusAsync(original.MailMessageId, ApStatus.MailNeedsReview, null, CancellationToken.None);

        var first = await messages.RecordAlreadyFinalReplayNeedsReviewAsync(mailbox, runId, message, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal((int)ApStatus.MailNeedsReview, first!.StatusId);

        var second = await messages.RecordAlreadyFinalReplayNeedsReviewAsync(mailbox, runId, message, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(first.MailMessageId, second!.MailMessageId);

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
    public async Task DiscoverAndClaim_WithAChangedGraphId_WhenOriginalIsFinal_InsertsANewRow()
    {
        SkipUnlessConfigured();

        // Once the first row is final, a different Graph message id is treated as a distinct arrival
        // and gets its own dbo.MailMessage row.
        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        var firstArrival = new MailMessageSummary(
            $"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", []);
        var first = await messages.DiscoverAndClaimAsync(mailbox, runId, firstArrival, CancellationToken.None);
        Assert.True(first.Claimed);

        // Same email, marked final - as it would be right after being moved to a destination folder.
        await messages.SetStatusAsync(first.MailMessageId, ApStatus.MailProcessed, null, CancellationToken.None);

        // Reissued Graph id: once the original row is final, this is treated as a distinct
        // delivery and gets its own row.
        var reissuedArrival = new MailMessageSummary(
            $"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", []);
        var second = await messages.DiscoverAndClaimAsync(mailbox, runId, reissuedArrival, CancellationToken.None);

        Assert.NotEqual(first.MailMessageId, second.MailMessageId);
        Assert.True(second.Claimed);
        Assert.Equal((int)ApStatus.MailNew, second.StatusId);
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
    public async Task RecordAttachments_OnARetryAfterSetStored_CarriesForwardTheStoredPathAndHash()
    {
        SkipUnlessConfigured();

        // The exact property ProcessPdfAsync's retry branch depends on: once a prior attempt has
        // fetched, written and recorded an attachment, a later re-delivery of the same message must
        // see that stored path and hash through this same RecordAsync call, so it can read the file
        // back locally instead of re-fetching from Graph and re-writing it.
        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        MailAttachmentSummary[] summaries =
        [
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "INV-1.pdf", 1024, "application/pdf")
        ];

        var message = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", summaries);
        var claim = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);

        var firstSight = (await attachments.RecordAsync(claim.MailMessageId, summaries, CancellationToken.None))[0];
        Assert.Null(firstSight.StoredPath);
        Assert.Null(firstSight.ContentSha256);

        var storedPath = @"2026\09\1-INV-1.pdf";
        var storedHash = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());
        await attachments.SetStoredAsync(firstSight.MailAttachmentId, storedPath, storedHash, CancellationToken.None);

        var retry = (await attachments.RecordAsync(claim.MailMessageId, summaries, CancellationToken.None))[0];
        Assert.Equal(firstSight.MailAttachmentId, retry.MailAttachmentId);
        Assert.Equal(storedPath, retry.StoredPath);
        Assert.Equal(storedHash, retry.ContentSha256);
    }

    [SkippableFact]
    public async Task FindDuplicateByHash_FindsAnEarlierAttachment_WithTheSameContent()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        var firstSummary = new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "INV-1.pdf", 1024, "application/pdf");
        var firstMessage = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", [firstSummary]);
        var firstClaim = await messages.DiscoverAndClaimAsync(mailbox, runId, firstMessage, CancellationToken.None);
        var firstRecorded = (await attachments.RecordAsync(firstClaim.MailMessageId, [firstSummary], CancellationToken.None))[0];

        var sharedHash = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());

        // Nothing has this hash yet - the very first sighting of a PDF is never a duplicate of itself.
        Assert.Null(await attachments.FindDuplicateByHashAsync(firstRecorded.MailAttachmentId, sharedHash, CancellationToken.None));

        await attachments.SetStoredAsync(firstRecorded.MailAttachmentId, @"2026\09\1-INV-1.pdf", sharedHash, CancellationToken.None);

        var secondSummary = new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "INV-1 (resent).pdf", 1024, "application/pdf");
        var secondMessage = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", [secondSummary]);
        var secondClaim = await messages.DiscoverAndClaimAsync(mailbox, runId, secondMessage, CancellationToken.None);
        var secondRecorded = (await attachments.RecordAsync(secondClaim.MailMessageId, [secondSummary], CancellationToken.None))[0];

        // A different email, byte-identical PDF: must be found even though the filename differs.
        var duplicate = await attachments.FindDuplicateByHashAsync(secondRecorded.MailAttachmentId, sharedHash, CancellationToken.None);
        Assert.NotNull(duplicate);
        Assert.Equal(firstRecorded.MailAttachmentId, duplicate.MailAttachmentId);
        Assert.Equal(firstClaim.MailMessageId, duplicate.MailMessageId);
        Assert.Equal(firstMessage.Subject, duplicate.Subject);

        // The file the first attachment already wrote to - a retry that finds this match must point
        // its own row at this same path rather than writing a second copy of identical bytes.
        Assert.Equal(@"2026\09\1-INV-1.pdf", duplicate.StoredPath);
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
    public async Task RecordInvoice_TreatsTheSameAttachmentTwiceAsIdempotent_NotAsADuplicateNumber()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);
        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);

        MailAttachmentSummary[] summaries =
        [
            new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "a.pdf", 1024, "application/pdf")
        ];

        var message = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", summaries);
        var claim = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        var recorded = await attachments.RecordAsync(claim.MailMessageId, summaries, CancellationToken.None);

        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = NewInvoice(claim.MailMessageId, recorded[0].MailAttachmentId, invoiceNumber);

        var first = await invoices.RecordAsync(invoice, CancellationToken.None);
        Assert.False(first.IsDuplicate);

        // This is the crash-recovery path, not a duplicate invoice: the message was claimed, the
        // invoice recorded, and the process died before the status went final - so the next run
        // re-delivers the message and extracts the same PDF again. UQ_Invoice_Attachment rejects the
        // second insert, and reading that as a duplicate number would file a good invoice into
        // NeedsReview. The caller has to get the row that is already there.
        var replay = await invoices.RecordAsync(invoice, CancellationToken.None);

        Assert.False(replay.IsDuplicate);
        Assert.Equal(first.InvoiceId, replay.InvoiceId);
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

        // Skipped mail (no PDF attachment) is routed to NeedsReview so a human still sees it,
        // rather than being left invisible in the Inbox.
        Assert.Equal("NeedsReview", folders[ApStatus.MailSkipped]);

        // MailDeleted has no destination — the mailbox has already removed the message — so it's
        // the one that actually stays wherever it is.
        Assert.Null(folders[ApStatus.MailDeleted]);
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

    [SkippableFact]
    public async Task PaceSubmission_Enqueue_IsIdempotent_AndClaimReturnsFieldsJson()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);
        await AssertPaceSubmissionUsesStatusLookupIdAsync(factory);

        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var processingRunId = await runs.StartAsync(new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com"), CancellationToken.None);

        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);

        Assert.True(await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None) >= 1);
        Assert.Equal(0, await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None));

        var claim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value, processingRunId);

        Assert.NotNull(claim);
        Assert.Equal(invoice.InvoiceId, claim.InvoiceId);
        Assert.Equal(1, claim.AttemptCount);
        Assert.Contains("InvoiceNumber", claim.FieldsJson);
        await AssertPaceSubmissionStatusAsync(factory, claim.PaceSubmissionId, PaceSubmissionStatus.InProgressId, PaceSubmissionStatus.InProgress);
        Assert.Equal(processingRunId, await LoadPaceSubmissionProcessingRunIdAsync(factory, claim.PaceSubmissionId));
    }

    [SkippableFact]
    public async Task PaceSubmission_RetryThenComplete_UsesClaimTokenAndIncrementsAttempts()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);
        await AssertPaceSubmissionUsesStatusLookupIdAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);

        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        var firstClaim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        Assert.NotNull(firstClaim);

        var retrySaved = await paceSubmissions.RetryLaterAsync(new PaceSubmissionRetry
        {
            PaceSubmissionId = firstClaim.PaceSubmissionId,
            ClaimToken = firstClaim.ClaimToken,
            NextAttemptOn = DateTime.UtcNow.AddSeconds(-1),
            ResponseJson = "{}",
            ErrorMessage = "temporary"
        }, CancellationToken.None);

        Assert.True(retrySaved);
        await AssertPaceSubmissionStatusAsync(factory, firstClaim.PaceSubmissionId, PaceSubmissionStatus.RetryLaterId, PaceSubmissionStatus.RetryLater);

        var secondClaim = await paceSubmissions.ClaimNextAsync(processingRunId: null, CancellationToken.None);
        Assert.NotNull(secondClaim);
        Assert.Equal(firstClaim.PaceSubmissionId, secondClaim.PaceSubmissionId);
        Assert.Equal(2, secondClaim.AttemptCount);
        await AssertPaceSubmissionStatusAsync(factory, secondClaim.PaceSubmissionId, PaceSubmissionStatus.InProgressId, PaceSubmissionStatus.InProgress);

        var completed = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = secondClaim.PaceSubmissionId,
            ClaimToken = secondClaim.ClaimToken,
            StatusCode = PaceSubmissionStatus.BillCreated,
            ResponseJson = "{}",
            PaceBillBatchId = "batch-1",
            PaceBillId = "bill-1",
            PaceBillLineId = "line-1",
            RequiresReview = false
        }, CancellationToken.None);

        Assert.True(completed);
        await AssertPaceSubmissionStatusAsync(factory, secondClaim.PaceSubmissionId, PaceSubmissionStatus.BillCreatedId, PaceSubmissionStatus.BillCreated);

        var staleTokenUpdate = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = secondClaim.PaceSubmissionId,
            ClaimToken = secondClaim.ClaimToken,
            StatusCode = PaceSubmissionStatus.Error,
            ErrorMessage = "stale token",
            RequiresReview = false
        }, CancellationToken.None);

        Assert.False(staleTokenUpdate);
    }

    [SkippableFact]
    public async Task PaceSubmission_StaleInProgressClaim_IsReclaimedWithNewToken()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);
        await AssertPaceSubmissionUsesStatusLookupIdAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);

        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        var firstClaim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        Assert.Equal(1, firstClaim.AttemptCount);
        await ForcePaceSubmissionClaimStaleAsync(factory, firstClaim.PaceSubmissionId);

        var reclaimed = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId.Value);

        Assert.Equal(firstClaim.PaceSubmissionId, reclaimed.PaceSubmissionId);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.NotEqual(firstClaim.ClaimToken, reclaimed.ClaimToken);
        await AssertPaceSubmissionStatusAsync(factory, reclaimed.PaceSubmissionId, PaceSubmissionStatus.InProgressId, PaceSubmissionStatus.InProgress);

        var staleTokenUpdate = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = firstClaim.PaceSubmissionId,
            ClaimToken = firstClaim.ClaimToken,
            StatusCode = PaceSubmissionStatus.Error,
            ErrorMessage = "stale token",
            RequiresReview = false
        }, CancellationToken.None);

        Assert.False(staleTokenUpdate);

        var completed = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = reclaimed.PaceSubmissionId,
            ClaimToken = reclaimed.ClaimToken,
            StatusCode = PaceSubmissionStatus.NoPo,
            ResponseJson = "{}",
            RequiresReview = false
        }, CancellationToken.None);

        Assert.True(completed);
    }

    [SkippableFact]
    public async Task PaceSubmission_UnroutedSubmissionOlderThanRecoveryWindow_IsNotSwept()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        var claim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = claim.PaceSubmissionId,
            ClaimToken = claim.ClaimToken,
            StatusCode = PaceSubmissionStatus.NoPo,
            RequiresReview = true
        }, CancellationToken.None);

        // CompleteAsync gives the completing run the routing lease; expire it so only the window is under test.
        await ExpirePaceSubmissionRoutingLeaseAsync(factory, invoice.InvoiceId.Value);
        var freshSweep = await paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None);
        Assert.Contains(freshSweep, submission => submission.PaceSubmissionId == claim.PaceSubmissionId);

        await BackdatePaceSubmissionAsync(factory, claim.PaceSubmissionId, days: 30);
        await ExpirePaceSubmissionRoutingLeaseAsync(factory, invoice.InvoiceId.Value);

        // Every row that predates the MailRoutedOn column looks exactly like this one. Without the window the
        // first run after deploy would replay routing for the whole history - re-moving old mail, overwriting
        // mail statuses a human may since have corrected, and sending one alert per historical row.
        var agedSweep = await paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None);
        Assert.DoesNotContain(agedSweep, submission => submission.PaceSubmissionId == claim.PaceSubmissionId);
    }

    [SkippableFact]
    public async Task PaceSubmission_Requeue_ClearsPreviousRoutingSoRecoveryStillCoversTheNewOutcome()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        // First pass: a dry run that went to NeedsReview and was fully routed, digest included.
        var dryRunClaim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = dryRunClaim.PaceSubmissionId,
            ClaimToken = dryRunClaim.ClaimToken,
            StatusCode = PaceSubmissionStatus.DryRunPrepared,
            RequiresReview = true
        }, CancellationToken.None);
        await paceSubmissions.MarkNotifiedAsync([dryRunClaim.PaceSubmissionId], CancellationToken.None);
        await paceSubmissions.MarkMailRoutedAsync(dryRunClaim.PaceSubmissionId, CancellationToken.None);

        // Requeued the way RequeuePaceDryRunPrepared.sql does it, once Pace writes are switched on.
        await RequeuePaceSubmissionLikeTheOperationsScriptAsync(factory, dryRunClaim.PaceSubmissionId);

        var liveClaim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId.Value);
        await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = liveClaim.PaceSubmissionId,
            ClaimToken = liveClaim.ClaimToken,
            StatusCode = PaceSubmissionStatus.BillCreated,
            RequiresReview = true
        }, CancellationToken.None);

        // The new outcome has not been routed or notified yet. Stale values from the dry run would hide it from
        // the recovery sweep for good if this second route failed.
        Assert.False(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId.Value));
        Assert.False(await IsPaceSubmissionNotifiedAsync(factory, invoice.InvoiceId.Value));
        Assert.NotNull(await LoadPaceSubmissionRoutingClaimedOnAsync(factory, invoice.InvoiceId.Value));

        await ExpirePaceSubmissionRoutingLeaseAsync(factory, invoice.InvoiceId.Value);
        var sweep = await paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None);
        var recovered = Assert.Single(sweep, submission => submission.PaceSubmissionId == liveClaim.PaceSubmissionId);
        Assert.False(recovered.AlreadyNotified);

        await MarkPaceSubmissionMailRoutedForCleanupAsync(factory, invoice.InvoiceId.Value);
    }

    [SkippableFact]
    public async Task PaceSubmission_OverlongBillVendor_IsTruncatedRatherThanFailingTheCompletion()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        var claim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        var overlongVendorName = new string('V', PaceSubmissionRepository.BillVendorMaxLength + 25);

        // BillVendor is filled from the Pace vendor's Id or Name. A name longer than the column must not fail
        // the save: by now the bill already exists in Pace, and a failed completion would get it resubmitted.
        var completed = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = claim.PaceSubmissionId,
            ClaimToken = claim.ClaimToken,
            StatusCode = PaceSubmissionStatus.BillCreated,
            RequiresReview = false,
            BillVendor = overlongVendorName
        }, CancellationToken.None);

        Assert.True(completed);
        Assert.Equal(overlongVendorName[..PaceSubmissionRepository.BillVendorMaxLength], await LoadPaceSubmissionBillVendorAsync(factory, claim.PaceSubmissionId));
    }

    [SkippableFact]
    public async Task PaceSubmission_RowCompletedBeforeRoutingTrackingExisted_IsNeverSwept()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        var claim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = claim.PaceSubmissionId,
            ClaimToken = claim.ClaimToken,
            StatusCode = PaceSubmissionStatus.Error,
            ErrorMessage = "Completed by the old code, before routing tracking existed.",
            RequiresReview = false
        }, CancellationToken.None);

        // Exactly what every row that existed at deploy looks like: final, recent (well inside the window),
        // MailRoutedOn NULL - and RoutingClaimedOn NULL, because the old code never set it. Sweeping these would
        // re-route and re-alert on the whole last week of history on the first run after deploy.
        await ClearPaceSubmissionRoutingClaimAsync(factory, invoice.InvoiceId.Value);

        var sweep = await paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None);
        Assert.DoesNotContain(sweep, submission => submission.PaceSubmissionId == claim.PaceSubmissionId);

        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();
        await CreatePaceProcessor(factory, mailSource, mailSender, new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.Error })
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        Assert.DoesNotContain(mailSource.Moves, move => move.MessageId == graphMessageId);
        Assert.DoesNotContain(mailSender.Requests, request => request.Body.Contains(invoiceNumber));

        await MarkPaceSubmissionMailRoutedForCleanupAsync(factory, invoice.InvoiceId.Value);
    }

    [SkippableFact]
    public async Task PaceSubmission_RoutingLease_KeepsTheSweepOffARowAnotherRunIsRouting_AndConcurrentSweepsGetDisjointRows()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]), CancellationToken.None);
        await paceSubmissions.EnqueueExtractedInvoicesAsync(CancellationToken.None);

        var claim = await ClaimUntilInvoiceAsync(paceSubmissions, invoice.InvoiceId!.Value);
        await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = claim.PaceSubmissionId,
            ClaimToken = claim.ClaimToken,
            StatusCode = PaceSubmissionStatus.Error,
            ErrorMessage = "Routing lease test.",
            RequiresReview = false
        }, CancellationToken.None);

        // Just completed: the completing run is routing it right now, so an overlapping run's sweep must not.
        var whileOwned = await paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None);
        Assert.DoesNotContain(whileOwned, submission => submission.PaceSubmissionId == claim.PaceSubmissionId);

        await ExpirePaceSubmissionRoutingLeaseAsync(factory, invoice.InvoiceId.Value);

        // Two overlapping runs sweeping at once: the claim renews the lease atomically, so exactly one gets it.
        var sweeps = await Task.WhenAll(
            paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None),
            paceSubmissions.ClaimUnroutedFinalSubmissionsAsync(CancellationToken.None));
        Assert.Single(sweeps.SelectMany(sweep => sweep), submission => submission.PaceSubmissionId == claim.PaceSubmissionId);

        await MarkPaceSubmissionMailRoutedForCleanupAsync(factory, invoice.InvoiceId.Value);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_RoutePaceError_SetsMailErrorMovesMessageAndReportsItInTheSummary()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();

        await CreatePaceProcessor(factory, mailSource, mailSender, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.Error,
                ErrorMessage = $"Pace invoice '{invoiceNumber}' for PO 'PO-1': no unpaid PO receipts were found."
            })
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var mailStatus = await LoadMailStatusAsync(factory, mailMessageId);
        Assert.Equal((int)ApStatus.MailError, mailStatus.StatusId);
        Assert.Contains("no unpaid PO receipts", mailStatus.ErrorMessage);
        Assert.Single(mailSource.Moves, move => move == (graphMessageId, MailDestinationFolder.Errors));

        // One summary for this vendor email: the error under Errors, with the redundant
        // "Pace invoice '...' for PO '...':" lead-in dropped, and the folder it went to as the last line.
        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.StartsWith(RunSummaryEmailBuilder.SubjectPrefix, summary.Subject);
        Assert.EndsWith("Routed to Errors", summary.Subject);
        Assert.Equal(["errors@wallacegraphics.com"], summary.ToAddresses);
        Assert.Contains("=== 4. Errors (1) ===", summary.Body);
        Assert.Contains($"<b>Invoice {invoiceNumber}</b> | PO PO-1 | Vendor ", summary.Body);
        Assert.Contains("Invoice date 09/01/2026 | Invoice total $1,234.56", summary.Body);
        Assert.Contains("No unpaid PO receipts were found.", summary.Body);
        Assert.EndsWith("<p><b>Routed to: Errors</b></p>", summary.Body);
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId!.Value));
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenStoredFieldsJsonIsMalformed_RoutesMailErrorMovesMessageAndReportsItInTheSummary()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber) with
        {
            FieldsJson = "{}"
        }, CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();

        await CreatePaceRun(factory, mailSource, mailSender, new StubPaceInvoiceService(new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.DryRunPrepared }))
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var claim = await LoadPaceSubmissionByInvoiceAsync(factory, invoice.InvoiceId!.Value);
        var mailStatus = await LoadMailStatusAsync(factory, mailMessageId);
        Assert.Equal(PaceSubmissionStatus.Error, claim.StatusCode);
        Assert.Contains("JSON deserialization", claim.ErrorMessage);
        Assert.Equal((int)ApStatus.MailError, mailStatus.StatusId);
        Assert.Contains("stored invoice fields could not be processed", mailStatus.ErrorMessage);
        Assert.Contains((graphMessageId, MailDestinationFolder.Errors), mailSource.Moves);

        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.StartsWith(RunSummaryEmailBuilder.SubjectPrefix, summary.Subject);
        Assert.Contains($"<b>Invoice {invoiceNumber}</b>", summary.Body);
        Assert.Contains("Stored invoice fields could not be processed", summary.Body);
        Assert.Equal(["errors@wallacegraphics.com"], summary.ToAddresses);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenPaceServiceThrows_RecordsAPaceFailureNotAFieldsError()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();

        // The stored fields are fine; Pace itself failed. That must not be reported as a bad invoice.
        await CreatePaceRun(factory, mailSource, mailSender, new ThrowingPaceInvoiceService(new InvalidOperationException("Pace createBill did not return an id.")))
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var submission = await LoadPaceSubmissionByInvoiceAsync(factory, invoice.InvoiceId!.Value);
        Assert.Equal(PaceSubmissionStatus.Error, submission.StatusCode);
        Assert.Contains("Pace processing failed: Pace createBill did not return an id.", submission.ErrorMessage);
        Assert.DoesNotContain("stored invoice fields", submission.ErrorMessage);
        Assert.Contains((graphMessageId, MailDestinationFolder.Errors), mailSource.Moves);

        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.Contains("Pace processing failed: Pace createBill did not return an id.", summary.Body);
        Assert.EndsWith("<p><b>Routed to: Errors</b></p>", summary.Body);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenPaceReturnsPoNotReceived_RoutesMailErrorMovesMessageAndReportsItInTheSummary()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();
        var poNotReceivedMessage = "Pace invoice 'INV-1' for PO 'PO-1': no received PO lines were found.";

        // PoNotReceived is no longer returned (a PO with no approved receipt is billed with default coding),
        // but rows completed with it before this change must still route to Errors.
        await CreatePaceProcessor(factory, mailSource, mailSender, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.PoNotReceived,
                ErrorMessage = poNotReceivedMessage
            })
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var claim = await LoadPaceSubmissionByInvoiceAsync(factory, invoice.InvoiceId!.Value);
        var mailStatus = await LoadMailStatusAsync(factory, mailMessageId);
        Assert.Equal(PaceSubmissionStatus.PoNotReceived, claim.StatusCode);
        Assert.Equal(poNotReceivedMessage, claim.ErrorMessage);
        Assert.Equal((int)ApStatus.MailError, mailStatus.StatusId);
        Assert.Contains(poNotReceivedMessage, mailStatus.ErrorMessage);
        Assert.Contains((graphMessageId, MailDestinationFolder.Errors), mailSource.Moves);

        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.Contains("=== 4. Errors (1) ===", summary.Body);
        Assert.Contains("No received PO lines were found.", summary.Body);
        Assert.EndsWith("<p><b>Routed to: Errors</b></p>", summary.Body);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenPaceSucceeds_MovesTheEmailToProcessedAndReportsItAsASuccess()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();

        await CreatePaceProcessor(factory, mailSource, mailSender, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
                PaceBillBatchId = "812",
                PaceBillId = "4521",
                PaceBillLineId = "1,2",
                BillVendor = "SANMAR",
                Summary = new PaceBillSummary
                {
                    Case = PaceInvoiceCase.NotBilled,
                    VendorId = "SANMAR",
                    VendorName = "SanMar Corporation",
                    InvoiceTotal = 1234.56m,
                    BillTotal = 1234.56m,
                    BillBatchDescription = "AUTO 9-25-26",
                    ReceiptIdsAdded = [90011, 90012]
                }
            })
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        // The mailbox step no longer moves a parsed email; the Pace step's success moves it to Processed, once.
        Assert.Single(mailSource.Moves, move => move.MessageId == graphMessageId);
        Assert.Contains((graphMessageId, MailDestinationFolder.Processed), mailSource.Moves);
        Assert.Equal((int)ApStatus.MailProcessed, (await LoadMailStatusAsync(factory, mailMessageId)).StatusId);
        Assert.True(await IsPaceSubmissionNotifiedAsync(factory, invoice.InvoiceId!.Value));
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId.Value));

        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.EndsWith("Pace: 1 succeeded, 0 need review, 0 errors | Routed to Processed", summary.Subject);
        Assert.Contains("=== 2. Pace – Success (1) ===", summary.Body);
        Assert.Contains("Vendor SANMAR (SanMar Corporation)", summary.Body);
        Assert.Contains("Bill 4521 created in batch 812 (AUTO 9-25-26) with 2 PO receipt line(s) (receipts 90011, 90012).", summary.Body);
        Assert.Contains("Bill total $1,234.56 matches the invoice total.", summary.Body);
        Assert.EndsWith("<p><b>Routed to: Processed</b></p>", summary.Body);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_EmailWithSeveralInvoices_IsMovedOnceAfterTheLastOne_ToTheWorstFolder()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, firstAttachmentId) = await CreateRecordedPdfAsync(factory);
        var secondAttachmentId = await AddRecordedPdfAsync(factory, mailMessageId);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var successNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var reviewNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var successInvoice = await invoices.RecordAsync(NewInvoice(mailMessageId, firstAttachmentId, successNumber), CancellationToken.None);
        var reviewInvoice = await invoices.RecordAsync(NewInvoice(mailMessageId, secondAttachmentId, reviewNumber), CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();

        await CreatePaceRun(factory, mailSource, mailSender, new PerInvoiceStubPaceInvoiceService(new Dictionary<long, PaceInvoiceSubmissionResult>
            {
                [successInvoice.InvoiceId!.Value] = new() { StatusCode = PaceInvoiceOutcomeStatus.BillCreated, PaceBillBatchId = "812", PaceBillId = "4521" },
                [reviewInvoice.InvoiceId!.Value] = new() { StatusCode = PaceInvoiceOutcomeStatus.BillCreated, PaceBillBatchId = "812", PaceBillId = "4523", RequiresReview = true }
            }, fallback: new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.Error }))
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        // One move, after both invoices were decided, to the worse of the two outcomes.
        Assert.Equal([(graphMessageId, MailDestinationFolder.NeedsReview)], mailSource.Moves.Where(move => move.MessageId == graphMessageId));
        Assert.Equal((int)ApStatus.MailNeedsReview, (await LoadMailStatusAsync(factory, mailMessageId)).StatusId);

        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(successNumber));
        Assert.Contains(reviewNumber, summary.Body);
        Assert.Contains("Pace: 1 succeeded, 1 needs review, 0 errors.", summary.Body);
        Assert.True(summary.Body.IndexOf("=== 2. Pace – Success", StringComparison.Ordinal) < summary.Body.IndexOf("=== 3. Pace –", StringComparison.Ordinal));
        Assert.EndsWith("<p><b>Routed to: NeedsReview</b></p>", summary.Body);
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, successInvoice.InvoiceId.Value));
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, reviewInvoice.InvoiceId.Value));
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenPaceIsUnreachable_LeavesTheEmailInTheInbox_ThenGivesUpAtMaxAttemptsAndRoutesToErrors()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var unreachable = new StubPaceInvoiceService(new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.RetryLater,
            IsTransient = true,
            ErrorMessage = "Pace request timed out."
        });

        // First attempt of two: retried later, and the email stays where it is.
        var firstMailSource = new RecordingMailSource();
        await CreatePaceRun(factory, firstMailSource, new RecordingMailSender(), unreachable, maxAttempts: 2)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        Assert.Equal(PaceSubmissionStatus.RetryLater, (await LoadPaceSubmissionByInvoiceAsync(factory, invoice.InvoiceId!.Value)).StatusCode);
        Assert.DoesNotContain(firstMailSource.Moves, move => move.MessageId == graphMessageId);

        // Second and last attempt: given up on as an Error, so the email goes to Errors and is reported.
        await MakePaceSubmissionRetryDueAsync(factory, invoice.InvoiceId.Value);
        var secondMailSource = new RecordingMailSource();
        var secondSender = new RecordingMailSender();
        await CreatePaceRun(factory, secondMailSource, secondSender, unreachable, maxAttempts: 2)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var submission = await LoadPaceSubmissionByInvoiceAsync(factory, invoice.InvoiceId.Value);
        Assert.Equal(PaceSubmissionStatus.Error, submission.StatusCode);
        Assert.Contains("Pace could not be reached after 2 attempts: Pace request timed out.", submission.ErrorMessage);
        Assert.Contains((graphMessageId, MailDestinationFolder.Errors), secondMailSource.Moves);

        var summary = Assert.Single(secondSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.Contains("Pace could not be reached after 2 attempts", summary.Body);
        Assert.EndsWith("<p><b>Routed to: Errors</b></p>", summary.Body);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenMultipleInvoicesRequireReview_MovesEachToNeedsReviewWithItsOwnSummary()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);

        var (firstMailMessageId, firstMailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var firstGraphMessageId = await LoadGraphMessageIdAsync(factory, firstMailMessageId);
        var firstInvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var firstInvoice = await invoices.RecordAsync(NewInvoice(firstMailMessageId, firstMailAttachmentId, firstInvoiceNumber), CancellationToken.None);

        var (secondMailMessageId, secondMailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var secondGraphMessageId = await LoadGraphMessageIdAsync(factory, secondMailMessageId);
        var secondInvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var secondInvoice = await invoices.RecordAsync(NewInvoice(secondMailMessageId, secondMailAttachmentId, secondInvoiceNumber), CancellationToken.None);

        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();

        await CreatePaceRun(factory, mailSource, mailSender, new PerInvoiceStubPaceInvoiceService(new Dictionary<long, PaceInvoiceSubmissionResult>
            {
                [firstInvoice.InvoiceId!.Value] = new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
                    PaceBillBatchId = "111",
                    PaceBillId = "222",
                    RequiresReview = true
                },
                [secondInvoice.InvoiceId!.Value] = new PaceInvoiceSubmissionResult
                {
                    StatusCode = PaceInvoiceOutcomeStatus.DryRunPrepared,
                    RequiresReview = true
                }
            }))
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        Assert.Equal((int)ApStatus.MailNeedsReview, (await LoadMailStatusAsync(factory, firstMailMessageId)).StatusId);
        Assert.Equal((int)ApStatus.MailNeedsReview, (await LoadMailStatusAsync(factory, secondMailMessageId)).StatusId);
        Assert.Contains((firstGraphMessageId, MailDestinationFolder.NeedsReview), mailSource.Moves);
        Assert.Contains((secondGraphMessageId, MailDestinationFolder.NeedsReview), mailSource.Moves);

        // One summary per vendor email.
        foreach (var invoiceNumber in new[] { firstInvoiceNumber, secondInvoiceNumber })
        {
            var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
            Assert.Contains("=== 3. Pace – Bill created with default GL account/department – Needs review (1) ===", summary.Body);
            Assert.EndsWith("<p><b>Routed to: NeedsReview</b></p>", summary.Body);
        }
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenNeedsReviewMoveFails_StillSendsSummaryAndLeavesSubmissionUnroutedForRecovery()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);

        var mailSource = new RecordingMailSource { MoveFailure = new HttpRequestException("Graph move failed") };
        var mailSender = new RecordingMailSender();

        // A routing failure must not escape the Pace step - the Pace submission is already final by the time
        // the move fails, so the rest of the batch (and the summary) must not be lost with it.
        await CreatePaceProcessor(factory, mailSource, mailSender, new PaceInvoiceSubmissionResult
            {
                StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
                PaceBillBatchId = "111",
                PaceBillId = "222",
                BillVendor = "SANMAR",
                RequiresReview = true
            })
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var mailStatus = await LoadMailStatusAsync(factory, mailMessageId);
        Assert.Equal((int)ApStatus.MailNeedsReview, mailStatus.StatusId);

        var summary = Assert.Single(mailSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.Contains("Vendor SANMAR", summary.Body);

        // The email was not moved, so the summary does not claim it was.
        Assert.DoesNotContain("Routed to:", summary.Body);

        // The summary went out, so the notification half is done and only the move is left for the sweep.
        Assert.True(await IsPaceSubmissionNotifiedAsync(factory, invoice.InvoiceId!.Value));
        Assert.False(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId!.Value));

        // The run handed its lease back on the way out, so the very next run can retry the move - but as an
        // already-expired time, never NULL, which would mark the row as pre-deploy history and hide it forever.
        var routingClaimedOn = await LoadPaceSubmissionRoutingClaimedOnAsync(factory, invoice.InvoiceId!.Value);
        Assert.NotNull(routingClaimedOn);
        Assert.True(routingClaimedOn <= DateTime.UtcNow.AddMinutes(-9), $"Lease was not released: RoutingClaimedOn = {routingClaimedOn:O}.");

        // Left unrouted on purpose to prove the point above, but this database is shared across the test
        // run - an unrouted row would otherwise get swept up and re-routed by RecoverUnroutedSubmissionsAsync
        // inside an unrelated later test's run.
        await MarkPaceSubmissionMailRoutedForCleanupAsync(factory, invoice.InvoiceId!.Value);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_RecoversUnroutedSubmissionOnNextRun()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var needsReviewResult = new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
            PaceBillBatchId = "111",
            PaceBillId = "222",
            BillVendor = "SANMAR",
            RequiresReview = true
        };

        var firstRunSender = new RecordingMailSender();
        await CreatePaceProcessor(factory, new RecordingMailSource { MoveFailure = new HttpRequestException("Graph move failed") }, firstRunSender, needsReviewResult)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);
        Assert.False(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId!.Value));
        Assert.Single(firstRunSender.Requests, request => request.Body.Contains(invoiceNumber));

        // The submission is already final (BillCreated), so a second run must recover the failed mail route
        // through the startup sweep rather than by reprocessing Pace - ClaimNextAsync would never pick this
        // row up again. It starts straight after the first, well inside the routing lease: the first run
        // handed the lease back when it ended, so the retry must not wait for the lease to lapse.
        var recoveredMailSource = new RecordingMailSource();
        var recoveredSender = new RecordingMailSender();
        await CreatePaceProcessor(factory, recoveredMailSource, recoveredSender, new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.Error })
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        Assert.Contains((graphMessageId, MailDestinationFolder.NeedsReview), recoveredMailSource.Moves);
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId!.Value));

        // The first run's summary already reported this invoice; retrying the move must not report it again.
        Assert.DoesNotContain(recoveredSender.Requests, request => request.Body.Contains(invoiceNumber));
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenErrorMoveFails_ReportedOnceInSummary_AndRecoveryRedoesOnlyTheMove()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var errorResult = new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.Error,
            ErrorMessage = "Pace rejected the bill."
        };

        var firstRunSender = new RecordingMailSender();
        await CreatePaceProcessor(factory, new RecordingMailSource { MoveFailure = new HttpRequestException("Graph move failed") }, firstRunSender, errorResult)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        // The summary line is recorded before the move, so a failed move can't leave a failed invoice
        // unreported - and the summary does not claim a move that did not happen.
        var firstSummary = Assert.Single(firstRunSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.StartsWith(RunSummaryEmailBuilder.SubjectPrefix, firstSummary.Subject);
        Assert.Contains("Pace rejected the bill.", firstSummary.Body);
        Assert.DoesNotContain("Routed to:", firstSummary.Body);
        Assert.True(await IsPaceSubmissionNotifiedAsync(factory, invoice.InvoiceId!.Value));
        Assert.False(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId.Value));

        // No lease expiry: the next run retries straight away, even inside the lease.
        var recoveredMailSource = new RecordingMailSource();
        var recoveredSender = new RecordingMailSender();
        await CreatePaceProcessor(factory, recoveredMailSource, recoveredSender, errorResult)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        Assert.Contains((graphMessageId, MailDestinationFolder.Errors), recoveredMailSource.Moves);
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId.Value));
        Assert.DoesNotContain(recoveredSender.Requests, request => request.Body.Contains(invoiceNumber));
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_ErrorAndNeedsReviewOnDifferentEmails_EachGetsItsOwnSummary()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (errorMailMessageId, errorMailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var errorInvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var errorInvoice = await invoices.RecordAsync(NewInvoice(errorMailMessageId, errorMailAttachmentId, errorInvoiceNumber), CancellationToken.None);
        var (reviewMailMessageId, reviewMailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var reviewInvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var reviewInvoice = await invoices.RecordAsync(NewInvoice(reviewMailMessageId, reviewMailAttachmentId, reviewInvoiceNumber), CancellationToken.None);

        var mailSender = new RecordingMailSender();
        await CreatePaceRun(factory, new RecordingMailSource(), mailSender, new PerInvoiceStubPaceInvoiceService(new Dictionary<long, PaceInvoiceSubmissionResult>
            {
                [errorInvoice.InvoiceId!.Value] = new()
                {
                    StatusCode = PaceInvoiceOutcomeStatus.Error,
                    ErrorMessage = $"Pace invoice '{errorInvoiceNumber}' for PO 'PO-1': invoice number does not start with INV - review manually."
                },
                [reviewInvoice.InvoiceId!.Value] = new()
                {
                    StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
                    PaceBillBatchId = "111",
                    PaceBillId = "222",
                    BillVendor = "SANMAR",
                    RequiresReview = true
                }
            }, fallback: new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.Error }))
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var errorSummary = Assert.Single(mailSender.Requests, request => request.Body.Contains(errorInvoiceNumber));
        Assert.Contains("=== 4. Errors (1) ===", errorSummary.Body);
        Assert.Contains("Invoice number does not start with INV - review manually.", errorSummary.Body);
        Assert.EndsWith("<p><b>Routed to: Errors</b></p>", errorSummary.Body);
        Assert.DoesNotContain(reviewInvoiceNumber, errorSummary.Body);

        var reviewSummary = Assert.Single(mailSender.Requests, request => request.Body.Contains(reviewInvoiceNumber));
        Assert.Contains("=== 3. Pace – Bill created with default GL account/department – Needs review (1) ===", reviewSummary.Body);
        Assert.EndsWith("<p><b>Routed to: NeedsReview</b></p>", reviewSummary.Body);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenSummarySendFails_NextRunResendsItNamingThePersistedBillVendor()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var invoiceNumber = $"INV-{Guid.NewGuid():N}"[..20];
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, invoiceNumber), CancellationToken.None);
        var needsReviewResult = new PaceInvoiceSubmissionResult
        {
            StatusCode = PaceInvoiceOutcomeStatus.BillCreated,
            PaceBillBatchId = "111",
            PaceBillId = "222",
            // Deliberately not the client's configured account number: a recovered summary used to fall back to it.
            BillVendor = "NOPO-DEFAULT-VENDOR",
            RequiresReview = true
        };

        await CreatePaceProcessor(factory, new RecordingMailSource(), new RecordingMailSender { SendFailure = new HttpRequestException("Graph send failed") }, needsReviewResult)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        // Moved, but the summary never arrived - so the row is not done.
        Assert.False(await IsPaceSubmissionNotifiedAsync(factory, invoice.InvoiceId!.Value));
        Assert.False(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId.Value));

        // No lease expiry: the next run retries straight away, even inside the lease.
        var recoveredSender = new RecordingMailSender();
        await CreatePaceProcessor(factory, new RecordingMailSource(), recoveredSender, needsReviewResult)
            .ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var summary = Assert.Single(recoveredSender.Requests, request => request.Body.Contains(invoiceNumber));
        Assert.Contains("Vendor NOPO-DEFAULT-VENDOR", summary.Body);
        Assert.EndsWith("<p><b>Routed to: NeedsReview</b></p>", summary.Body);
        Assert.True(await IsPaceSubmissionNotifiedAsync(factory, invoice.InvoiceId.Value));
        Assert.True(await IsPaceSubmissionMailRoutedAsync(factory, invoice.InvoiceId.Value));
    }

    private static async Task AssertPaceSubmissionUsesStatusLookupIdAsync(SqlConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        var hasDirectStatusCode = await connection.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN COL_LENGTH(N'intgr.PaceSubmission', N'StatusCode') IS NULL THEN 0 ELSE 1 END;");
        Assert.Equal(0, hasDirectStatusCode);

        var hasIdForeignKey = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.tables AS parentTable
                ON parentTable.object_id = fkc.parent_object_id
            INNER JOIN sys.schemas AS parentSchema
                ON parentSchema.schema_id = parentTable.schema_id
            INNER JOIN sys.columns AS parentColumn
                ON parentColumn.object_id = fkc.parent_object_id
               AND parentColumn.column_id = fkc.parent_column_id
            INNER JOIN sys.tables AS referencedTable
                ON referencedTable.object_id = fkc.referenced_object_id
            INNER JOIN sys.schemas AS referencedSchema
                ON referencedSchema.schema_id = referencedTable.schema_id
            INNER JOIN sys.columns AS referencedColumn
                ON referencedColumn.object_id = fkc.referenced_object_id
               AND referencedColumn.column_id = fkc.referenced_column_id
            WHERE fk.name = N'FK_PaceSubmission_Status'
              AND parentSchema.name = N'intgr'
              AND parentTable.name = N'PaceSubmission'
              AND parentColumn.name = N'StatusCodeId'
              AND referencedSchema.name = N'intgr'
              AND referencedTable.name = N'PaceSubmissionStatus'
              AND referencedColumn.name = N'StatusCodeId';
            """);

        Assert.Equal(1, hasIdForeignKey);

        var statusIdIsPrimaryKey = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.key_constraints AS keyConstraint
            INNER JOIN sys.index_columns AS indexColumn
                ON indexColumn.object_id = keyConstraint.parent_object_id
               AND indexColumn.index_id = keyConstraint.unique_index_id
            INNER JOIN sys.columns AS columnInfo
                ON columnInfo.object_id = indexColumn.object_id
               AND columnInfo.column_id = indexColumn.column_id
            INNER JOIN sys.tables AS tableInfo
                ON tableInfo.object_id = keyConstraint.parent_object_id
            INNER JOIN sys.schemas AS schemaInfo
                ON schemaInfo.schema_id = tableInfo.schema_id
            WHERE keyConstraint.[type] = 'PK'
              AND keyConstraint.[name] = N'PK_PaceSubmissionStatus'
              AND schemaInfo.[name] = N'intgr'
              AND tableInfo.[name] = N'PaceSubmissionStatus'
              AND columnInfo.[name] = N'StatusCodeId';
            """);

        Assert.Equal(1, statusIdIsPrimaryKey);

        var statusCodeIsUnique = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.key_constraints AS keyConstraint
            INNER JOIN sys.index_columns AS indexColumn
                ON indexColumn.object_id = keyConstraint.parent_object_id
               AND indexColumn.index_id = keyConstraint.unique_index_id
            INNER JOIN sys.columns AS columnInfo
                ON columnInfo.object_id = indexColumn.object_id
               AND columnInfo.column_id = indexColumn.column_id
            INNER JOIN sys.tables AS tableInfo
                ON tableInfo.object_id = keyConstraint.parent_object_id
            INNER JOIN sys.schemas AS schemaInfo
                ON schemaInfo.schema_id = tableInfo.schema_id
            WHERE keyConstraint.[type] = 'UQ'
              AND keyConstraint.[name] = N'UQ_PaceSubmissionStatus_StatusCode'
              AND schemaInfo.[name] = N'intgr'
              AND tableInfo.[name] = N'PaceSubmissionStatus'
              AND columnInfo.[name] = N'StatusCode';
            """);

        Assert.Equal(1, statusCodeIsUnique);
    }

    private static async Task AssertPaceSubmissionStatusAsync(SqlConnectionFactory factory, long paceSubmissionId, int expectedStatusId, string expectedStatusCode)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        var status = await connection.QuerySingleAsync<(int StatusCodeId, string StatusCode)>(
            """
            SELECT submission.[StatusCodeId], status.[StatusCode]
            FROM [intgr].[PaceSubmission] AS submission
            INNER JOIN [intgr].[PaceSubmissionStatus] AS status
                ON status.[StatusCodeId] = submission.[StatusCodeId]
            WHERE submission.[PaceSubmissionId] = @PaceSubmissionId;
            """,
            new { PaceSubmissionId = paceSubmissionId });

        Assert.Equal(expectedStatusId, status.StatusCodeId);
        Assert.Equal(expectedStatusCode, status.StatusCode);
    }

    private static async Task ForcePaceSubmissionClaimStaleAsync(SqlConnectionFactory factory, long paceSubmissionId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [ClaimedOn] = DATEADD(MINUTE, -90, SYSUTCDATETIME()),
                   [ModifiedOn] = SYSUTCDATETIME()
             WHERE [PaceSubmissionId] = @PaceSubmissionId;
            """,
            new { PaceSubmissionId = paceSubmissionId });
    }

    private static async Task<string> LoadGraphMessageIdAsync(SqlConnectionFactory factory, long mailMessageId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        return await connection.QuerySingleAsync<string>(
            "SELECT [GraphMessageId] FROM [dbo].[MailMessage] WHERE [MailMessageId] = @MailMessageId;",
            new { MailMessageId = mailMessageId });
    }

    private static async Task<(int StatusId, string? ErrorMessage)> LoadMailStatusAsync(SqlConnectionFactory factory, long mailMessageId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        return await connection.QuerySingleAsync<(int StatusId, string? ErrorMessage)>(
            "SELECT [StatusId], [ErrorMessage] FROM [dbo].[MailMessage] WHERE [MailMessageId] = @MailMessageId;",
            new { MailMessageId = mailMessageId });
    }

    private static async Task<(string StatusCode, string? ErrorMessage)> LoadPaceSubmissionByInvoiceAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        return await connection.QuerySingleAsync<(string StatusCode, string? ErrorMessage)>(
            """
            SELECT status.[StatusCode], submission.[ErrorMessage]
            FROM [intgr].[PaceSubmission] AS submission
            INNER JOIN [intgr].[PaceSubmissionStatus] AS status
                ON status.[StatusCodeId] = submission.[StatusCodeId]
            WHERE submission.[InvoiceId] = @InvoiceId;
            """,
            new { InvoiceId = invoiceId });
    }

    private static async Task<bool> IsPaceSubmissionMailRoutedAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        var mailRoutedOn = await connection.QuerySingleAsync<DateTime?>(
            "SELECT [MailRoutedOn] FROM [intgr].[PaceSubmission] WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });

        return mailRoutedOn is not null;
    }

    private static async Task<bool> IsPaceSubmissionNotifiedAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        var notifiedOn = await connection.QuerySingleAsync<DateTime?>(
            "SELECT [NotifiedOn] FROM [intgr].[PaceSubmission] WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });

        return notifiedOn is not null;
    }

    /// <summary>
    /// Stands in for the scheduled gap between runs: the run that completed or swept a row holds its routing
    /// lease, and a later run only picks the row up once that lease has lapsed.
    /// </summary>
    private static async Task ExpirePaceSubmissionRoutingLeaseAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            "UPDATE [intgr].[PaceSubmission] SET [RoutingClaimedOn] = DATEADD(DAY, -1, SYSUTCDATETIME()) WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });
    }

    private static async Task<DateTime?> LoadPaceSubmissionRoutingClaimedOnAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        return await connection.QuerySingleAsync<DateTime?>(
            "SELECT [RoutingClaimedOn] FROM [intgr].[PaceSubmission] WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });
    }

    private static async Task<string?> LoadPaceSubmissionBillVendorAsync(SqlConnectionFactory factory, long paceSubmissionId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        return await connection.QuerySingleAsync<string?>(
            "SELECT [BillVendor] FROM [intgr].[PaceSubmission] WHERE [PaceSubmissionId] = @PaceSubmissionId;",
            new { PaceSubmissionId = paceSubmissionId });
    }

    /// <summary>
    /// Mirrors the UPDATE in WG.AP.Database/Scripts/Operations/RequeuePaceDryRunPrepared.sql, which touches only
    /// these columns - in particular not MailRoutedOn or NotifiedOn.
    /// </summary>
    private static async Task RequeuePaceSubmissionLikeTheOperationsScriptAsync(SqlConnectionFactory factory, long paceSubmissionId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [StatusCodeId] = @PendingStatusId,
                   [NextAttemptOn] = NULL,
                   [ClaimToken] = NULL,
                   [ClaimedOn] = NULL,
                   [ErrorMessage] = NULL,
                   [ModifiedOn] = SYSUTCDATETIME()
             WHERE [PaceSubmissionId] = @PaceSubmissionId;
            """,
            new { PaceSubmissionId = paceSubmissionId, PendingStatusId = PaceSubmissionStatus.PendingId });
    }

    private static async Task ClearPaceSubmissionRoutingClaimAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            "UPDATE [intgr].[PaceSubmission] SET [RoutingClaimedOn] = NULL WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });
    }

    private static PaceRun CreatePaceProcessor(
        SqlConnectionFactory factory,
        RecordingMailSource mailSource,
        RecordingMailSender mailSender,
        PaceInvoiceSubmissionResult result) =>
        CreatePaceRun(factory, mailSource, mailSender, new StubPaceInvoiceService(result));

    private static PaceRun CreatePaceRun(
        SqlConnectionFactory factory,
        RecordingMailSource mailSource,
        RecordingMailSender mailSender,
        IPaceInvoiceService paceInvoiceService,
        int maxAttempts = 5) =>
        new(factory, mailSource, mailSender, paceInvoiceService, maxAttempts);

    /// <summary>
    /// One run's Pace step the way Program.cs drives it: process the queue, send the run's summary emails,
    /// then record which summary lines were delivered and which emails were routed.
    /// </summary>
    private sealed class PaceRun(
        SqlConnectionFactory factory,
        RecordingMailSource mailSource,
        RecordingMailSender mailSender,
        IPaceInvoiceService paceInvoiceService,
        int maxAttempts)
    {
        public RunSummary Summary { get; } = new();

        public async Task ProcessPendingAsync(long? processingRunId, CancellationToken cancellationToken)
        {
            var alertOptions = Options.Create(new AlertOptions { Recipients = ["errors@wallacegraphics.com"] });
            var processor = new PaceInvoiceProcessor(
                mailSource,
                new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance),
                new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance),
                paceInvoiceService,
                Summary,
                Options.Create(new PaceOptions { BaseUrl = "https://pace.test", UserName = "user", Password = "password", MaxAttempts = maxAttempts }),
                NullLogger<PaceInvoiceProcessor>.Instance);
            var notifier = new RunSummaryNotifier(
                Summary,
                new ErrorNotifier(mailSender, alertOptions, NullLogger<ErrorNotifier>.Instance),
                alertOptions,
                NullLogger<RunSummaryNotifier>.Instance);

            try
            {
                await processor.ProcessPendingAsync(processingRunId, cancellationToken);
            }
            finally
            {
                await processor.FinalizeSummaryAsync(await notifier.SendAsync(CancellationToken.None));
            }
        }
    }

    private static async Task<long> AddRecordedPdfAsync(SqlConnectionFactory factory, long mailMessageId)
    {
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);
        var summary = new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "invoice-2.pdf", 1024, "application/pdf");
        var recorded = await attachments.RecordAsync(mailMessageId, [summary], CancellationToken.None);

        return recorded.Single(item => item.Attachment.Id == summary.Id).MailAttachmentId;
    }

    /// <summary>Stands in for the backoff delay passing: the RetryLater row becomes claimable again now.</summary>
    private static async Task MakePaceSubmissionRetryDueAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            "UPDATE [intgr].[PaceSubmission] SET [NextAttemptOn] = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });
    }

    private static async Task BackdatePaceSubmissionAsync(SqlConnectionFactory factory, long paceSubmissionId, int days)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            """
            UPDATE [intgr].[PaceSubmission]
               SET [CreatedOn]  = DATEADD(DAY, -@Days, SYSUTCDATETIME()),
                   [ModifiedOn] = DATEADD(DAY, -@Days, SYSUTCDATETIME())
             WHERE [PaceSubmissionId] = @PaceSubmissionId;
            """,
            new { PaceSubmissionId = paceSubmissionId, Days = days });
    }

    private static async Task MarkPaceSubmissionMailRoutedForCleanupAsync(SqlConnectionFactory factory, long invoiceId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        await connection.ExecuteAsync(
            "UPDATE [intgr].[PaceSubmission] SET [MailRoutedOn] = SYSUTCDATETIME() WHERE [InvoiceId] = @InvoiceId;",
            new { InvoiceId = invoiceId });
    }

    private static async Task<PaceSubmissionClaim> ClaimUntilInvoiceAsync(PaceSubmissionRepository repository, long invoiceId, long? processingRunId = null)
    {
        for (var i = 0; i < 100; i++)
        {
            var claim = await repository.ClaimNextAsync(processingRunId, CancellationToken.None);

            Assert.NotNull(claim);

            if (claim.InvoiceId == invoiceId)
            {
                return claim;
            }

            await repository.CompleteAsync(new PaceSubmissionCompletion
            {
                PaceSubmissionId = claim.PaceSubmissionId,
                ClaimToken = claim.ClaimToken,
                StatusCode = PaceSubmissionStatus.Error,
                ErrorMessage = "Completed by Pace SQL repository test while skipping unrelated scratch-database work item.",
                RequiresReview = false
            }, CancellationToken.None);

            // This bypasses PaceInvoiceProcessor entirely, so no mail was ever routed for it - mark it routed
            // anyway so it isn't picked up by RecoverUnroutedSubmissionsAsync inside an unrelated later test.
            await repository.MarkMailRoutedAsync(claim.PaceSubmissionId, CancellationToken.None);
        }

        throw new InvalidOperationException($"Pace submission for invoice {invoiceId} was not claimed within the test limit.");
    }

    private static async Task<long?> LoadPaceSubmissionProcessingRunIdAsync(SqlConnectionFactory factory, long paceSubmissionId)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        return await connection.QuerySingleAsync<long?>(
            "SELECT [ProcessingRunId] FROM [intgr].[PaceSubmission] WHERE [PaceSubmissionId] = @PaceSubmissionId;",
            new { PaceSubmissionId = paceSubmissionId });
    }

    private static async Task<(long MailMessageId, long MailAttachmentId)> CreateRecordedPdfAsync(SqlConnectionFactory factory)
    {
        var runs = new ProcessingRunRepository(factory, NullLogger<ProcessingRunRepository>.Instance);
        var messages = new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance);
        var attachments = new MailAttachmentRepository(factory, NullLogger<MailAttachmentRepository>.Instance);

        var mailbox = new MailboxRef(Guid.NewGuid(), "sql-test@wallacegraphics.com");
        var runId = await runs.StartAsync(mailbox, CancellationToken.None);
        var summary = new MailAttachmentSummary($"att-{Guid.NewGuid():N}", "invoice.pdf", 1024, "application/pdf");
        var message = new MailMessageSummary($"immutable-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, "billing@sanmar.com", "Invoice", [summary]);
        var claim = await messages.DiscoverAndClaimAsync(mailbox, runId, message, CancellationToken.None);
        var recorded = await attachments.RecordAsync(claim.MailMessageId, [summary], CancellationToken.None);

        return (claim.MailMessageId, recorded[0].MailAttachmentId);
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
            FieldsJson = """
            {
              "InvoiceNumber": "INV-TEST",
              "SalesOrder": "SO-TEST",
              "InvoiceDate": "2026-09-01",
              "DueDate": "2026-11-01",
              "Total": 1234.56,
              "ClientName": "SanMar",
              "CustomerPO": "PO-1",
              "CustomerNumber": "76274-0000",
              "OrderAccount": "76274-0000",
              "Terms": "Net60"
            }
            """,
            ExtractionMethod = "Regex",
            Status = ApStatus.InvoiceExtracted
        };

    private sealed class RecordingMailSource : IMailSource
    {
        public List<(string MessageId, MailDestinationFolder Destination)> Moves { get; } = [];

        public (string MessageId, MailDestinationFolder Destination)? LastMove => Moves.Count == 0 ? null : Moves[^1];

        public Task ValidateAuthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureFoldersExistAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<MailMessageSummary> EnumerateInboxAsync(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<MailMessageSummary>();

        public Task<MailboxDeltaResult> GetInboxDeltaAsync(string? deltaLink, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<MailMessageSummary?> GetMessageAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult<MailMessageSummary?>(null);

        public Task<byte[]> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());

        public Exception? MoveFailure { get; init; }

        public Task<string> MoveMessageAsync(string messageId, MailDestinationFolder destination, CancellationToken cancellationToken)
        {
            if (MoveFailure is not null)
            {
                return Task.FromException<string>(MoveFailure);
            }

            Moves.Add((messageId, destination));
            return Task.FromResult(messageId);
        }
    }

    private sealed class RecordingMailSender : IMailSender
    {
        public List<MailSendRequest> Requests { get; } = [];

        public MailSendRequest? LastRequest => Requests.Count == 0 ? null : Requests[^1];

        public Exception? SendFailure { get; init; }

        public Task SendMailAsync(MailSendRequest request, CancellationToken cancellationToken)
        {
            if (SendFailure is not null)
            {
                return Task.FromException(SendFailure);
            }

            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class StubPaceInvoiceService(PaceInvoiceSubmissionResult result) : IPaceInvoiceService
    {
        public Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingPaceInvoiceService(Exception exception) : IPaceInvoiceService
    {
        public Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken) => Task.FromException<PaceInvoiceSubmissionResult>(exception);
    }

    /// <summary>
    /// <paramref name="fallback"/> answers for invoices not in the dictionary - stray Pending rows left in the
    /// shared test database by other tests - so they don't turn into KeyNotFoundException parse errors.
    /// </summary>
    private sealed class PerInvoiceStubPaceInvoiceService(
        Dictionary<long, PaceInvoiceSubmissionResult> resultsByInvoiceId,
        PaceInvoiceSubmissionResult? fallback = null) : IPaceInvoiceService
    {
        public Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken) =>
            Task.FromResult(fallback is null
                ? resultsByInvoiceId[submission.InvoiceId]
                : resultsByInvoiceId.GetValueOrDefault(submission.InvoiceId, fallback));
    }
}
