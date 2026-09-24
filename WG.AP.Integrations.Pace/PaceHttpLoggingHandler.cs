using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WG.AP.Integrations.Pace;

/// <summary>
/// Replaces the default <c>HttpClientFactory</c> request/response logging for the Pace client.
/// </summary>
/// <remarks>
/// The framework's default logging handlers ("LogicalHandler"/"ClientHandler") log the same four
/// checkpoints this class does, but their message text is hardcoded inside those (internal, sealed)
/// handler classes with no way to inject a different format - the status line is always the bare
/// numeric code, with no indication of what it means. Reproducing the same four checkpoints here, in
/// the same order and wording, is what lets the two status-bearing lines say "200 (OK)" or
/// "404 (NotFound)" instead of just the number, without changing the shape of the log a reader is
/// used to. <see cref="PaceServiceCollectionExtensions.AddPaceIntegration"/> removes the default
/// handlers for this one named client so the two don't double up.
/// </remarks>
internal sealed class PaceHttpLoggingHandler(ILogger<PaceHttpLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Start processing HTTP request {Method} {RequestUri}", request.Method, request.RequestUri);
        logger.LogInformation("Sending HTTP request {Method} {RequestUri}", request.Method, request.RequestUri);

        var stopwatch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        // HttpStatusCode.ToString() gives the named reason ("OK", "NotFound", ...) regardless of
        // protocol version - response.ReasonPhrase is frequently empty on HTTP/2, which Pace staging
        // uses, so it can't be relied on here.
        logger.LogInformation(
            "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode} ({StatusDescription})",
            stopwatch.ElapsedMilliseconds, (int)response.StatusCode, response.StatusCode);
        logger.LogInformation(
            "End processing HTTP request after {ElapsedMilliseconds}ms - {StatusCode} ({StatusDescription})",
            stopwatch.ElapsedMilliseconds, (int)response.StatusCode, response.StatusCode);

        return response;
    }
}
