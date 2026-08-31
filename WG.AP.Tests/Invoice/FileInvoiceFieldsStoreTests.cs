using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Invoice.Models;
using WG.AP.Invoice.Persistence;

namespace WG.AP.Tests.Invoice;

public class FileInvoiceFieldsStoreTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "WG.AP.Tests", Guid.NewGuid().ToString("N"));

    private FileInvoiceFieldsStore CreateStore() =>
        new(
            Options.Create(new InvoiceFieldsStoreOptions { DataDirectory = _dataDirectory }),
            NullLogger<FileInvoiceFieldsStore>.Instance);

    [Fact]
    public async Task SaveAsync_WritesAFileThatRoundTripsAllFields()
    {
        var store = CreateStore();
        var fields = new InvoiceFields(
            "INV-162109763", "SO-162950608", new DateOnly(2026, 7, 10), new DateOnly(2026, 9, 8),
            18.46m, "SanMar", "58188-GRA-7814", "76274-0000", "76274-0000", "Net60", "full document text");

        await store.SaveAsync(fields, CancellationToken.None);

        var path = Path.Combine(_dataDirectory, "INV-162109763.json");
        Assert.True(File.Exists(path));
        var readBack = JsonSerializer.Deserialize<InvoiceFields>(await File.ReadAllTextAsync(path));
        Assert.Equal(fields, readBack);
    }

    [Fact]
    public async Task SaveAsync_OverwritesThePreviousFile_ForTheSameInvoiceNumber()
    {
        var store = CreateStore();
        var first = new InvoiceFields("INV-1", null, null, null, 100m, null, null);
        var second = new InvoiceFields("INV-1", null, null, null, 200m, null, null);

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);

        var path = Path.Combine(_dataDirectory, "INV-1.json");
        var readBack = JsonSerializer.Deserialize<InvoiceFields>(await File.ReadAllTextAsync(path));
        Assert.Equal(200m, readBack!.Total);
        Assert.Single(Directory.GetFiles(_dataDirectory, "INV-1*.json"));
    }

    [Fact]
    public async Task SaveAsync_KeepsSeparateFiles_ForDifferentInvoiceNumbers()
    {
        var store = CreateStore();

        await store.SaveAsync(new InvoiceFields("INV-A", null, null, null, 1m, null, null), CancellationToken.None);
        await store.SaveAsync(new InvoiceFields("INV-B", null, null, null, 2m, null, null), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_dataDirectory, "INV-A.json")));
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "INV-B.json")));
    }

    [Fact]
    public async Task SaveAsync_CleansUpTheTempFile_WhenTheMoveFails()
    {
        var store = CreateStore();

        // Force File.Move(tempPath, path, overwrite: true) to fail deterministically by making the
        // destination an existing directory instead of a file.
        var destinationPath = Path.Combine(_dataDirectory, "INV-1.json");
        Directory.CreateDirectory(destinationPath);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SaveAsync(new InvoiceFields("INV-1", null, null, null, 1m, null, null), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_dataDirectory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
