namespace WG.AP.Invoice.Models;

/// <summary>A manifest row successfully matched to an attached PDF by voucher/filename.</summary>
public sealed record ManifestPair(string Voucher, string AttachmentName, ManifestRow Row);
