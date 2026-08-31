namespace WG.AP.Invoice.Persistence;

public sealed class InvoiceFieldsStoreOptions
{
    public const string SectionName = "InvoiceFieldsStore";

    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "invoice-fields");
}
