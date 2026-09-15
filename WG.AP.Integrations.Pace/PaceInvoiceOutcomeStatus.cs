namespace WG.AP.Integrations.Pace;

public static class PaceInvoiceOutcomeStatus
{
    public const string DryRunPrepared = "DryRunPrepared";
    public const string BillCreated = "BillCreated";
    public const string PoNotReceived = "PoNotReceived";
    public const string NoPo = "NoPo";
    public const string AlreadyEntered = "AlreadyEntered";
    public const string Error = "Error";
    public const string RetryLater = "RetryLater";
}
