namespace WG.AP.Integrations.Pace;

public interface IPaceInvoiceService
{
    Task<PaceInvoiceSubmissionResult> SubmitAsync(PaceInvoiceSubmission submission, CancellationToken cancellationToken);
}
