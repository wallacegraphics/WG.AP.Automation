namespace WG.AP.Invoice.Models;

/// <summary>One disagreeing field between a manifest row and its matched PDF's extracted fields.</summary>
public sealed record FieldMismatch(string FieldName, string ExcelValue, string PdfValue);
