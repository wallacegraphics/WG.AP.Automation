using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Core.Abstractions;
using WG.AP.Email;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;
using WG.AP.Processor;

namespace WG.AP.Tests.Processing;

public class ProcessorTests
{
    private sealed class FakeMailSource : IMailSource
    {
        public MailboxDeltaResult DeltaResult { get; set; } = new([], "delta-1");
        public Dictionary<(string MessageId, string AttachmentId), byte[]> AttachmentContents { get; } = [];
        public List<(string MessageId, string AttachmentId)> AttachmentRequests { get; } = [];
        public List<(string MessageId, MailDestinationFolder Destination)> Moves { get; } = [];

        public Task ValidateAuthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureFoldersExistAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<MailMessageSummary> EnumerateInboxAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<MailboxDeltaResult> GetInboxDeltaAsync(string? deltaLink, CancellationToken cancellationToken) => Task.FromResult(DeltaResult);

        public Task<MailMessageSummary?> GetMessageAsync(string messageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
        {
            AttachmentRequests.Add((messageId, attachmentId));
            return Task.FromResult(AttachmentContents[(messageId, attachmentId)]);
        }

        public Task<string> MoveMessageAsync(string messageId, MailDestinationFolder destination, CancellationToken cancellationToken)
        {
            Moves.Add((messageId, destination));
            return Task.FromResult(messageId);
        }
    }

    private sealed class FakeSyncStateStore : IMailboxSyncStateStore
    {
        public Task<string?> GetDeltaLinkAsync(string mailboxUser, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task SaveDeltaLinkAsync(string mailboxUser, string deltaLink, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeInvoiceFieldExtractor : IInvoiceFieldExtractor
    {
        public Func<byte[], InvoiceFields>? OnExtract { get; set; }

        public Task<InvoiceFields> ExtractAsync(byte[] pdfBytes, CancellationToken cancellationToken) =>
            OnExtract is not null
                ? Task.FromResult(OnExtract(pdfBytes))
                : throw new InvalidOperationException("Simulated suspicious PDF — cannot be parsed.");
    }

    private sealed class FakeManifestVerifier : IAttachmentManifestVerifier
    {
        public Func<byte[], IReadOnlyList<ManifestRow>>? OnReadManifest { get; set; }
        public Func<IReadOnlyList<ManifestRow>, IReadOnlyList<MailAttachmentSummary>, ManifestReconciliation>? OnReconcile { get; set; }
        public Func<ManifestRow, InvoiceFields, InvoiceFieldComparisonResult>? OnCompareFields { get; set; }

        public IReadOnlyList<ManifestRow> ReadManifest(byte[] excelBytes) => OnReadManifest!(excelBytes);

        public ManifestReconciliation Reconcile(IReadOnlyList<ManifestRow> manifestRows, IReadOnlyList<MailAttachmentSummary> pdfAttachments) =>
            OnReconcile is not null ? OnReconcile(manifestRows, pdfAttachments) : new ManifestReconciliation([], [], [], [], []);

        public InvoiceFieldComparisonResult CompareFields(ManifestRow row, InvoiceFields extractedFields) =>
            OnCompareFields is not null ? OnCompareFields(row, extractedFields) : new InvoiceFieldComparisonResult(row.Voucher, []);
    }

    private static (APProcessor Processor, FakeMailSource MailSource) CreateProcessor(
        FakeInvoiceFieldExtractor? extractor = null,
        FakeManifestVerifier? verifier = null)
    {
        var mailSource = new FakeMailSource();
        var mailboxOptions = Options.Create(new MailboxOptions
        {
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
            MailboxUser = "test-mailbox@wallacegraphics.com",
            IsTestMailbox = true
        });

        var syncProcessor = new MailboxSyncProcessor(mailSource, new FakeSyncStateStore(), mailboxOptions, NullLogger<MailboxSyncProcessor>.Instance);

        var processor = new APProcessor(
            mailSource,
            syncProcessor,
            extractor ?? new FakeInvoiceFieldExtractor(),
            verifier ?? new FakeManifestVerifier(),
            mailboxOptions,
            NullLogger<APProcessor>.Instance);

        return (processor, mailSource);
    }

    private static MailMessageSummary MessageWith(string id, params MailAttachmentSummary[] attachments) =>
        new(id, DateTimeOffset.UtcNow, "vendor@example.com", "Invoice email", attachments);

    [Fact]
    public async Task ProcessInvoicesAsync_MessageWithNoAttachments_IsLeftUntouched()
    {
        var (processor, mailSource) = CreateProcessor();
        mailSource.DeltaResult = new MailboxDeltaResult([MessageWith("m1")], "delta-1");

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Empty(mailSource.Moves);
    }

    [Fact]
    public async Task ProcessInvoicesAsync_PdfWithNoExcelManifest_RoutesToNeedsReview()
    {
        var (processor, mailSource) = CreateProcessor();
        var message = MessageWith("m1", new MailAttachmentSummary("a1", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.NeedsReview), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_UnreadableManifest_RoutesToErrors()
    {
        var verifier = new FakeManifestVerifier { OnReadManifest = _ => throw new InvalidOperationException("corrupt workbook") };
        var (processor, mailSource) = CreateProcessor(verifier: verifier);

        var message = MessageWith(
            "m1",
            new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"),
            new MailAttachmentSummary("a2", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1, 2, 3];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.Errors), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_UnparseablePdf_RoutesToErrors()
    {
        var row = new ManifestRow("INV-1", null, null, null, 100m, null, null, null, null);
        var verifier = new FakeManifestVerifier
        {
            OnReadManifest = _ => [row],
            OnReconcile = (_, _) => new ManifestReconciliation([], [], [], [], [new ManifestPair("INV-1", "INV-1.pdf", row)])
        };
        var extractor = new FakeInvoiceFieldExtractor(); // OnExtract left null -> throws, simulating a suspicious PDF
        var (processor, mailSource) = CreateProcessor(extractor, verifier);

        var message = MessageWith(
            "m1",
            new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"),
            new MailAttachmentSummary("a2", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1];
        mailSource.AttachmentContents[("m1", "a2")] = [2];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.Errors), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_ManifestMismatch_RoutesToNeedsReview()
    {
        var row = new ManifestRow("INV-1", null, null, null, 100m, null, null, null, null);
        var verifier = new FakeManifestVerifier
        {
            OnReadManifest = _ => [row],
            OnReconcile = (_, _) => new ManifestReconciliation(["INV-1"], [], [], [], [])
        };
        var (processor, mailSource) = CreateProcessor(verifier: verifier);

        var message = MessageWith("m1", new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.NeedsReview), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_ManifestDiscrepancy_SkipsPdfExtraction()
    {
        var row = new ManifestRow("INV-1", null, null, null, 100m, null, null, null, null);
        var verifier = new FakeManifestVerifier
        {
            OnReadManifest = _ => [row],
            OnReconcile = (_, _) => new ManifestReconciliation(["INV-2"], [], [], [], [new ManifestPair("INV-1", "INV-1.pdf", row)])
        };
        var (processor, mailSource) = CreateProcessor(verifier: verifier);

        var message = MessageWith(
            "m1",
            new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"),
            new MailAttachmentSummary("a2", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1];
        mailSource.AttachmentContents[("m1", "a2")] = [2];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.NeedsReview), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_FieldMismatch_RoutesToNeedsReview()
    {
        var row = new ManifestRow("INV-1", null, null, null, 100m, null, null, null, null);
        var extractedFields = new InvoiceFields("INV-1", null, null, null, 999m, null, null);
        var verifier = new FakeManifestVerifier
        {
            OnReadManifest = _ => [row],
            OnReconcile = (_, _) => new ManifestReconciliation([], [], [], [], [new ManifestPair("INV-1", "INV-1.pdf", row)]),
            OnCompareFields = (r, f) => new InvoiceFieldComparisonResult(r.Voucher, [new FieldMismatch("InvoiceAmount", "100.00", "999.00")])
        };
        var extractor = new FakeInvoiceFieldExtractor { OnExtract = _ => extractedFields };
        var (processor, mailSource) = CreateProcessor(extractor, verifier);

        var message = MessageWith(
            "m1",
            new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"),
            new MailAttachmentSummary("a2", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1];
        mailSource.AttachmentContents[("m1", "a2")] = [2];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.NeedsReview), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_EverythingReconciles_RoutesToProcessed()
    {
        var row = new ManifestRow("INV-1", null, null, null, 100m, null, null, null, null);
        var extractedFields = new InvoiceFields("INV-1", null, null, null, 100m, null, null);
        var verifier = new FakeManifestVerifier
        {
            OnReadManifest = _ => [row],
            OnReconcile = (_, _) => new ManifestReconciliation([], [], [], [], [new ManifestPair("INV-1", "INV-1.pdf", row)]),
            OnCompareFields = (r, f) => new InvoiceFieldComparisonResult(r.Voucher, [])
        };
        var extractor = new FakeInvoiceFieldExtractor { OnExtract = _ => extractedFields };
        var (processor, mailSource) = CreateProcessor(extractor, verifier);

        var message = MessageWith(
            "m1",
            new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"),
            new MailAttachmentSummary("a2", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1];
        mailSource.AttachmentContents[("m1", "a2")] = [2];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

        Assert.Equal(("m1", MailDestinationFolder.Processed), Assert.Single(mailSource.Moves));
    }

    [Fact]
    public async Task ProcessInvoicesAsync_DuplicatePdfFilenames_DoesNotThrow_AndRoutesToProcessed()
    {
        var row = new ManifestRow("INV-1", null, null, null, 100m, null, null, null, null);
        var extractedFields = new InvoiceFields("INV-1", null, null, null, 100m, null, null);
        var verifier = new FakeManifestVerifier
        {
            OnReadManifest = _ => [row],
            OnReconcile = (_, _) => new ManifestReconciliation([], [], [], [], [new ManifestPair("INV-1", "INV-1.pdf", row)]),
            OnCompareFields = (r, f) => new InvoiceFieldComparisonResult(r.Voucher, [])
        };
        var extractor = new FakeInvoiceFieldExtractor { OnExtract = _ => extractedFields };
        var (processor, mailSource) = CreateProcessor(extractor, verifier);

        var message = MessageWith(
            "m1",
            new MailAttachmentSummary("a1", "manifest.xlsx", 100, "application/vnd.openxmlformats"),
            new MailAttachmentSummary("a2", "INV-1.pdf", 100, "application/pdf"),
            new MailAttachmentSummary("a3", "INV-1.pdf", 100, "application/pdf"));
        mailSource.DeltaResult = new MailboxDeltaResult([message], "delta-1");
        mailSource.AttachmentContents[("m1", "a1")] = [1];
        mailSource.AttachmentContents[("m1", "a2")] = [2];
        mailSource.AttachmentContents[("m1", "a3")] = [3];

        await processor.ProcessInvoicesAsync(CancellationToken.None);

Assert.Equal(("m1", MailDestinationFolder.Processed), Assert.Single(mailSource.Moves));
Assert.Equal(2, mailSource.AttachmentRequests.Count);
Assert.Contains(("m1", "a2"), mailSource.AttachmentRequests);
Assert.DoesNotContain(("m1", "a3"), mailSource.AttachmentRequests);
    }
}
