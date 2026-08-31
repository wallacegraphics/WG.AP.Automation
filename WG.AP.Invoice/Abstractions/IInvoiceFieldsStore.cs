using WG.AP.Invoice.Models;

namespace WG.AP.Invoice.Abstractions;

/// <summary>
/// Persists extracted invoice fields as a stand-in for a real database, which doesn't exist yet.
/// </summary>
public interface IInvoiceFieldsStore
{
    Task SaveAsync(InvoiceFields fields, CancellationToken cancellationToken);
}
