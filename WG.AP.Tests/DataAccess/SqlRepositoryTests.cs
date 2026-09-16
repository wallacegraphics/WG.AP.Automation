using Dapper;
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

    private static async Task SkipUnlessPaceSchemaPublishedAsync(SqlConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        var exists = await connection.ExecuteScalarAsync<int>(
            """
            SELECT CASE
                WHEN OBJECT_ID(N'intgr.PaceSubmission', N'U') IS NOT NULL
                 AND COL_LENGTH(N'intgr.PaceSubmission', N'StatusCodeId') IS NOT NULL
                 AND COL_LENGTH(N'intgr.PaceSubmissionStatus', N'StatusCodeId') IS NOT NULL
                    THEN 1
                ELSE 0
            END;
            """);

        Skip.If(exists == 0, "Publish the WG.AP.Database project with the normalized Pace submission schema before running Pace SQL repository tests.");
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
            PaceBillLineId = "line-1"
        }, CancellationToken.None);

        Assert.True(completed);
        await AssertPaceSubmissionStatusAsync(factory, secondClaim.PaceSubmissionId, PaceSubmissionStatus.BillCreatedId, PaceSubmissionStatus.BillCreated);

        var staleTokenUpdate = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = secondClaim.PaceSubmissionId,
            ClaimToken = secondClaim.ClaimToken,
            StatusCode = PaceSubmissionStatus.Error,
            ErrorMessage = "stale token"
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
            ErrorMessage = "stale token"
        }, CancellationToken.None);

        Assert.False(staleTokenUpdate);

        var completed = await paceSubmissions.CompleteAsync(new PaceSubmissionCompletion
        {
            PaceSubmissionId = reclaimed.PaceSubmissionId,
            ClaimToken = reclaimed.ClaimToken,
            StatusCode = PaceSubmissionStatus.NoPo,
            ResponseJson = "{}"
        }, CancellationToken.None);

        Assert.True(completed);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_RoutePaceError_SetsMailErrorMovesMessageAndSendsAlert()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var (mailMessageId, _) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();
        var processor = new PaceInvoiceProcessor(
            mailSource,
            new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance),
            new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance),
            new StubPaceInvoiceService(new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.Error }),
            new ErrorNotifier(
                mailSender,
                Options.Create(new AlertOptions { Recipients = ["errors@wallacegraphics.com"] }),
                NullLogger<ErrorNotifier>.Instance),
            NullLogger<PaceInvoiceProcessor>.Instance);
        var claim = new PaceSubmissionClaim
        {
            PaceSubmissionId = 1,
            InvoiceId = 42,
            MailMessageId = mailMessageId,
            GraphMessageId = graphMessageId,
            AttemptCount = 1,
            ClaimToken = Guid.NewGuid(),
            FieldsJson = "{}",
            InvoiceNumber = "INV-PACE-ERR",
            CustomerPO = "PO-PACE-ERR",
            Total = 123.45m,
            PaceVendorId = "SANMAR-PACE"
        };
        var routeMethod = typeof(PaceInvoiceProcessor).GetMethod("RoutePaceErrorAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(routeMethod);
        await (Task)routeMethod.Invoke(processor, [claim, "Pace invoice 'INV-PACE-ERR' for PO 'PO-PACE-ERR': no unpaid PO receipts were found.", CancellationToken.None])!;

        var mailStatus = await LoadMailStatusAsync(factory, mailMessageId);
        Assert.Equal((int)ApStatus.MailError, mailStatus.StatusId);
        Assert.Contains("no unpaid PO receipts", mailStatus.ErrorMessage);
        Assert.Equal((graphMessageId, MailDestinationFolder.Errors), mailSource.LastMove);
        Assert.NotNull(mailSender.LastRequest);
        Assert.Contains("INV-PACE-ERR", mailSender.LastRequest.Subject);
        Assert.Contains("PO-PACE-ERR", mailSender.LastRequest.Body);
        Assert.Equal(["errors@wallacegraphics.com"], mailSender.LastRequest.ToAddresses);
    }

    [SkippableFact]
    public async Task PaceInvoiceProcessor_WhenStoredFieldsJsonIsMalformed_RoutesMailErrorMovesMessageAndSendsAlert()
    {
        SkipUnlessConfigured();

        var factory = CreateFactory();
        await SkipUnlessPaceSchemaPublishedAsync(factory);

        var invoices = new InvoiceRepository(factory, NullLogger<InvoiceRepository>.Instance);
        var paceSubmissions = new PaceSubmissionRepository(factory, NullLogger<PaceSubmissionRepository>.Instance);
        var (mailMessageId, mailAttachmentId) = await CreateRecordedPdfAsync(factory);
        var graphMessageId = await LoadGraphMessageIdAsync(factory, mailMessageId);
        var invoice = await invoices.RecordAsync(NewInvoice(mailMessageId, mailAttachmentId, $"INV-{Guid.NewGuid():N}"[..20]) with
        {
            FieldsJson = "{}"
        }, CancellationToken.None);
        var mailSource = new RecordingMailSource();
        var mailSender = new RecordingMailSender();
        var processor = new PaceInvoiceProcessor(
            mailSource,
            new MailMessageRepository(factory, NullLogger<MailMessageRepository>.Instance),
            paceSubmissions,
            new StubPaceInvoiceService(new PaceInvoiceSubmissionResult { StatusCode = PaceInvoiceOutcomeStatus.DryRunPrepared }),
            new ErrorNotifier(
                mailSender,
                Options.Create(new AlertOptions { Recipients = ["errors@wallacegraphics.com"] }),
                NullLogger<ErrorNotifier>.Instance),
            NullLogger<PaceInvoiceProcessor>.Instance);

        await processor.ProcessPendingAsync(processingRunId: null, CancellationToken.None);

        var claim = await LoadPaceSubmissionByInvoiceAsync(factory, invoice.InvoiceId!.Value);
        var mailStatus = await LoadMailStatusAsync(factory, mailMessageId);
        Assert.Equal(PaceSubmissionStatus.Error, claim.StatusCode);
        Assert.Contains("JSON deserialization", claim.ErrorMessage);
        Assert.Equal((int)ApStatus.MailError, mailStatus.StatusId);
        Assert.Contains("stored invoice fields could not be processed", mailStatus.ErrorMessage);
        Assert.Equal((graphMessageId, MailDestinationFolder.Errors), mailSource.LastMove);
        Assert.NotNull(mailSender.LastRequest);
        Assert.Contains("stored invoice fields could not be processed", mailSender.LastRequest.Body);
        Assert.Equal(["errors@wallacegraphics.com"], mailSender.LastRequest.ToAddresses);
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
                ErrorMessage = "Completed by Pace SQL repository test while skipping unrelated scratch-database work item."
            }, CancellationToken.None);
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
        public (string MessageId, MailDestinationFolder Destination)? LastMove { get; private set; }

        public Task ValidateAuthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureFoldersExistAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<MailMessageSummary> EnumerateInboxAsync(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<MailMessageSummary>();

        public Task<MailboxDeltaResult> GetInboxDeltaAsync(string? deltaLink, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<MailMessageSummary?> GetMessageAsync(string messageId, CancellationToken cancellationToken) => Task.FromResult<MailMessageSummary?>(null);

        public Task<byte[]> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());

        public Task<string> MoveMessageAsync(string messageId, MailDestinationFolder destination, CancellationToken cancellationToken)
        {
            LastMove = (messageId, destination);
            return Task.FromResult(messageId);
        }
    }

    private sealed class RecordingMailSender : IMailSender
    {
        public MailSendRequest? LastRequest { get; private set; }

        public Task SendMailAsync(MailSendRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }

    private sealed class StubPaceInvoiceService(PaceInvoiceSubmissionResult result) : IPaceInvoiceService
    {
        public Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
