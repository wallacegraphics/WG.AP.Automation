using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Persistence;

/// <summary>
/// Persists one JSON file per invoice as a stand-in for a real database. Writes go through a temp
/// file plus an atomic <see cref="File.Move(string, string, bool)"/> so a crash mid-write can't
/// leave a corrupt/partial file behind, mirroring <see cref="WG.AP.DataAccess.FileMailboxSyncStateStore"/>.
/// </summary>
public sealed class FileInvoiceFieldsStore(
    IOptions<InvoiceFieldsStoreOptions> options,
    ILogger<FileInvoiceFieldsStore> logger) : IInvoiceFieldsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task SaveAsync(InvoiceFields fields, CancellationToken cancellationToken)
    {
        var path = GetFilePath(fields.InvoiceNumber);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, fields, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to write invoice fields file {Path} for invoice {InvoiceNumber}.", path, fields.InvoiceNumber);
            throw;
        }
        finally
        {
            // File.Move already removed the temp file on the success path; this is a best-effort
            // cleanup for the failure path (serialization or the move itself throwing) so retries
            // after a transient failure don't leave orphaned *.tmp files behind indefinitely.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private string GetFilePath(string invoiceNumber)
    {
        var safeName = new string(invoiceNumber
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());

        return Path.Combine(options.Value.DataDirectory, $"{safeName}.json");
    }
}
