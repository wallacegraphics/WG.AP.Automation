using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Logging;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Integrations.Pace;
using WG.AP.Processor.Logging;

namespace WG.AP.Processor;

public sealed class PaceInvoiceProcessor(
    IMailSource mailSource,
    MailMessageRepository mailMessageRepository,
    PaceSubmissionRepository paceSubmissionRepository,
    IPaceInvoiceService paceInvoiceService,
    ErrorNotifier errorNotifier,
    ILogger<PaceInvoiceProcessor> logger)
{
    public Task ProcessPendingAsync(CancellationToken cancellationToken) =>
        ProcessPendingAsync(ProcessingRunContext.CurrentRunId, cancellationToken);

    public async Task ProcessPendingAsync(long? processingRunId, CancellationToken cancellationToken)
    {
        var enqueued = await paceSubmissionRepository.EnqueueExtractedInvoicesAsync(cancellationToken);
        logger.LogInformation("Pace submission enqueue complete: {EnqueuedCount} invoice(s) queued.", enqueued);

        var processed = 0;

        while (true)
        {
            var claim = await paceSubmissionRepository.ClaimNextAsync(processingRunId, cancellationToken);

            if (claim is null)
            {
                break;
            }

            processed++;
            await ProcessClaimAsync(claim, cancellationToken);
        }

        logger.LogInformation("Pace submission processing complete: {ProcessedCount} submission(s) processed.", processed);
    }

    private async Task ProcessClaimAsync(PaceSubmissionClaim claim, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(claim.PaceBillId) || !string.IsNullOrWhiteSpace(claim.PaceBillLineId))
            {
                await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
                {
                    PaceSubmissionId = claim.PaceSubmissionId,
                    ClaimToken = claim.ClaimToken,
                    StatusCode = PaceSubmissionStatus.AlreadyEntered,
                    PaceBillBatchId = claim.PaceBillBatchId,
                    PaceBillId = claim.PaceBillId,
                    PaceBillLineId = claim.PaceBillLineId,
                    ErrorMessage = "Pace IDs were already recorded locally before this retry; skipping duplicate Pace write."
                }, cancellationToken);

                return;
            }

            var fields = PaceInvoiceFieldsParser.Parse(claim.FieldsJson);
            var result = await paceInvoiceService.SubmitAsync(new PaceInvoiceSubmission
            {
                InvoiceId = claim.InvoiceId,
                PaceVendorId = claim.PaceVendorId,
                Fields = fields
            }, cancellationToken);

            if (result.IsTransient || result.StatusCode == PaceSubmissionStatus.RetryLater)
            {
                var savedRetry = await paceSubmissionRepository.RetryLaterAsync(new PaceSubmissionRetry
                {
                    PaceSubmissionId = claim.PaceSubmissionId,
                    ClaimToken = claim.ClaimToken,
                    NextAttemptOn = CalculateNextAttemptOn(claim.AttemptCount),
                    ResponseJson = result.ResponseJson,
                    ErrorMessage = result.ErrorMessage
                }, cancellationToken);

                if (!savedRetry)
                {
                    logger.LogWarning("Pace submission {PaceSubmissionId} retry update skipped because its claim token no longer matched.", claim.PaceSubmissionId);
                }

                return;
            }

            var savedCompletion = await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
            {
                PaceSubmissionId = claim.PaceSubmissionId,
                ClaimToken = claim.ClaimToken,
                StatusCode = result.StatusCode,
                ResponseJson = result.ResponseJson,
                PaceBillBatchId = result.PaceBillBatchId,
                PaceBillId = result.PaceBillId,
                PaceBillLineId = result.PaceBillLineId,
                ErrorMessage = result.ErrorMessage
            }, cancellationToken);

            if (!savedCompletion)
            {
                logger.LogWarning("Pace submission {PaceSubmissionId} completion skipped because its claim token no longer matched.", claim.PaceSubmissionId);
            }

            if (savedCompletion && result.StatusCode == PaceSubmissionStatus.Error)
            {
                await RoutePaceErrorAsync(claim, result.ErrorMessage, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            var savedCompletion = await paceSubmissionRepository.CompleteAsync(new PaceSubmissionCompletion
            {
                PaceSubmissionId = claim.PaceSubmissionId,
                ClaimToken = claim.ClaimToken,
                StatusCode = PaceSubmissionStatus.Error,
                ErrorMessage = exception.Message
            }, cancellationToken);

            if (!savedCompletion)
            {
                logger.LogWarning("Pace submission {PaceSubmissionId} parse-error update skipped because its claim token no longer matched.", claim.PaceSubmissionId);
            }

            if (savedCompletion)
            {
                await RoutePaceErrorAsync(
                    claim,
                    $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': stored invoice fields could not be processed. {exception.Message}",
                    cancellationToken);
            }
        }
    }

    private static DateTime CalculateNextAttemptOn(int attemptCount)
    {
        var delayMinutes = Math.Min(Math.Pow(2, Math.Max(1, attemptCount)), 60);
        return DateTime.UtcNow.AddMinutes(delayMinutes);
    }

    private async Task RoutePaceErrorAsync(PaceSubmissionClaim claim, string? errorMessage, CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? $"Pace invoice '{claim.InvoiceNumber}' for PO '{claim.CustomerPO}': Pace validation failed."
            : errorMessage;

        await mailMessageRepository.SetStatusAsync(claim.MailMessageId, ApStatus.MailError, message, cancellationToken);
        await mailSource.MoveMessageAsync(claim.GraphMessageId, MailDestinationFolder.Errors, cancellationToken);

        logger.LogInformation(
            "Pace validation error routed message {GraphMessageId} for invoice {InvoiceId} to Errors.",
            claim.GraphMessageId,
            claim.InvoiceId);

        await errorNotifier.NotifyAsync(
            $"AP Automation - Pace invoice error ({claim.InvoiceNumber ?? "unknown invoice"})",
            BuildPaceErrorNotificationBody(claim, message),
            cancellationToken);
    }

    private static string BuildPaceErrorNotificationBody(PaceSubmissionClaim claim, string errorMessage) =>
        $"<p>{Html(errorMessage)}</p>"
        + $"<p>Invoice: {Html(claim.InvoiceNumber ?? "unknown")}</p>"
        + $"<p>PO: {Html(claim.CustomerPO ?? "unknown")}</p>"
        + $"<p>InvoiceId: {claim.InvoiceId}</p>";

    private static string Html(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;
}
