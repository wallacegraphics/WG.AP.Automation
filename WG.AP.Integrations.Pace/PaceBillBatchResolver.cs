using System.Globalization;
using Microsoft.Extensions.Logging;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Integrations.Pace;

public sealed class PaceBillBatchResolver(IPaceClient paceClient, ILogger<PaceBillBatchResolver> logger)
{
    private const string CurrentPeriodStatus = "T";
    private const string OpenPeriodStatus = "O";
    public const string EnteredBy = "APAutomation";

    public async Task<BillBatchResolution> ResolveOrCreateAsync(DateOnly batchDate, string description, CancellationToken cancellationToken)
    {
        var periodIds = await paceClient.FindAsync(
            "GLAccountingPeriod",
            $"@accountingPeriod = {batchDate.Month} and @fiscalYear = {batchDate.Year}",
            cancellationToken: cancellationToken);

        if (periodIds.Count != 1)
        {
            var reason = $"Pace GL accounting period lookup for {batchDate.Month}/{batchDate.Year} returned {periodIds.Count} match(es); expected exactly 1.";
            logger.LogError("{Reason}", reason);
            return new BillBatchResolution.Unresolvable(reason);
        }

        var periodPrimaryKey = periodIds.Single();
        var period = await paceClient.ReadGLAccountingPeriodAsync(periodPrimaryKey, cancellationToken: cancellationToken);
        var periodId = period.Id ?? int.Parse(periodPrimaryKey, CultureInfo.InvariantCulture);
        var periodStatus = period.GlPeriodStatus;

        if (!string.Equals(periodStatus, CurrentPeriodStatus, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(periodStatus, OpenPeriodStatus, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Pace GL accounting period {GlAccountingPeriodId} is not open for posting; status is '{GlPeriodStatus}'.",
                periodId,
                periodStatus);
            return new BillBatchResolution.PeriodClosed(periodId, periodStatus ?? "unknown");
        }

        var batchIds = await paceClient.FindAsync(
            "BillBatch",
            $"@date = date({batchDate.Year},{batchDate.Month},{batchDate.Day}) and @description = {XPathStringLiteral(description)} and @posted = 'false'",
            cancellationToken: cancellationToken);

        if (batchIds.Count > 0)
        {
            if (batchIds.Count > 1)
            {
                logger.LogWarning(
                    "Pace returned {BatchCount} open bill batch(es) for {BatchDate}; using the lowest id.",
                    batchIds.Count,
                    batchDate);
            }

            var existingBatchId = batchIds.Select(id => int.Parse(id, CultureInfo.InvariantCulture)).Min();
            return new BillBatchResolution.Resolved(existingBatchId, periodId);
        }

        var createdBatch = await paceClient.CreateBillBatchAsync(new BillBatch
        {
            Date = batchDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            GlAccountingPeriod = periodId,
            Description = description,
            EnteredBy = EnteredBy,
            Status = "0",
            Approved = false,
            Posted = false
        }, cancellationToken: cancellationToken);

        if (createdBatch.Id is null)
        {
            throw new InvalidOperationException("Pace createBillBatch did not return an id.");
        }

        return new BillBatchResolution.Resolved(createdBatch.Id.Value, periodId);
    }

    private static string XPathStringLiteral(string value)
    {
        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{value}'";
        }

        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value}\"";
        }

        return $"concat({string.Join(", \"'\", ", value.Split('\'').Select(part => $"'{part}'"))})";
    }
}

public abstract record BillBatchResolution
{
    public sealed record Resolved(int BillBatchId, int GlAccountingPeriodId) : BillBatchResolution;

    public sealed record PeriodClosed(int GlAccountingPeriodId, string GlPeriodStatus) : BillBatchResolution;

    public sealed record Unresolvable(string Reason) : BillBatchResolution;
}
