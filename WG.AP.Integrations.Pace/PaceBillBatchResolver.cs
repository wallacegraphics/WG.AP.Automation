using System.Globalization;
using Microsoft.Extensions.Logging;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Integrations.Pace;

public sealed class PaceBillBatchResolver(IPaceClient paceClient, ILogger<PaceBillBatchResolver> logger)
{
    private const string CurrentPeriodStatus = "T";
    private const string OpenPeriodStatus = "O";
    public const string EnteredBy = "APAutomation";

    public async Task<BillBatchResolution> ResolveOrCreateAsync(DateOnly accountingPeriodDate, DateOnly batchDate, string description, CancellationToken cancellationToken)
    {
        var periodIds = await paceClient.FindAsync(
            "GLAccountingPeriod",
            $"@accountingPeriod = {accountingPeriodDate.Month} and @fiscalYear = {accountingPeriodDate.Year}",
            cancellationToken: cancellationToken);

        if (periodIds.Count != 1)
        {
            var reason = $"Pace GL accounting period lookup for {accountingPeriodDate.Month}/{accountingPeriodDate.Year} returned {periodIds.Count} match(es); expected exactly 1.";
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

        logger.LogInformation(
            "Pace GL accounting period {GlAccountingPeriodId} resolved and open for posting (status '{GlPeriodStatus}') for invoice period {AccountingPeriodMonth}/{AccountingPeriodYear}.",
            periodId,
            periodStatus,
            accountingPeriodDate.Month,
            accountingPeriodDate.Year);

        var batchIds = await paceClient.FindAsync(
            "BillBatch",
            $"@date = date({batchDate.Year},{batchDate.Month},{batchDate.Day}) and @description = {XPathStringLiteral(description)} and @posted = 'false'",
            cancellationToken: cancellationToken);

        // Pace has been observed to silently create a BillBatch whose id is the literal value 0
        // instead of failing or assigning a real sequence number (see GetNextBillBatchIdAsync below).
        // Pace's own server treats a submitted billBatch = 0 as "no value" for the FK on Bill, so id 0
        // is never usable - it must be excluded here too, or a stray one would get "reused" forever.
        var validBatchIds = batchIds
            .Select(id => int.Parse(id, CultureInfo.InvariantCulture))
            .Where(id => id > 0)
            .ToList();

        if (validBatchIds.Count < batchIds.Count)
        {
            logger.LogWarning(
                "Pace returned {InvalidCount} bill batch id(s) that are not usable (<= 0) for {BatchDate}; ignoring them.",
                batchIds.Count - validBatchIds.Count,
                batchDate);
        }

        if (validBatchIds.Count > 0)
        {
            if (validBatchIds.Count > 1)
            {
                logger.LogWarning(
                    "Pace returned {BatchCount} open bill batch(es) for {BatchDate}; using the lowest id.",
                    validBatchIds.Count,
                    batchDate);
            }

            var existingBatchId = validBatchIds.Min();

            logger.LogInformation(
                "Pace bill batch {BillBatchId} for period {GlAccountingPeriodId} reused for '{Description}' on {BatchDate}.",
                existingBatchId,
                periodId,
                description,
                batchDate);

            return new BillBatchResolution.Resolved(existingBatchId, periodId);
        }

        // Pace's own auto-assignment for a new BillBatch has been observed to silently return id 0
        // instead of a real sequence number, which downstream fails as an invalid Bill.billBatch
        // reference. Rather than rely on that, compute the next id ourselves from the current max.
        var nextBatchId = await GetNextBillBatchIdAsync(cancellationToken);

        var createdBatch = await paceClient.CreateBillBatchAsync(new BillBatch
        {
            Id = nextBatchId,
            Date = batchDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            GlAccountingPeriod = periodId,
            Description = description,
            EnteredBy = EnteredBy,
            Status = "0",
            Manual = true,
            Approved = false,
            Posted = false
        }, cancellationToken: cancellationToken);

        if (createdBatch.Id is null or <= 0)
        {
            throw new InvalidOperationException($"Pace createBillBatch did not return a usable id (requested {nextBatchId}, got {(createdBatch.Id is null ? "null" : createdBatch.Id.Value.ToString(CultureInfo.InvariantCulture))}).");
        }

        logger.LogInformation(
            "Pace created bill batch {BillBatchId} for period {GlAccountingPeriodId}, description '{Description}', date {BatchDate}.",
            createdBatch.Id.Value,
            periodId,
            description,
            batchDate);

        return new BillBatchResolution.Resolved(createdBatch.Id.Value, periodId);
    }

    /// <summary>
    /// Reads the current highest <c>BillBatch.Id</c> in Pace and returns the next one. Pace's own
    /// auto-assignment for a new BillBatch has been observed to silently return id 0 (which Pace's
    /// server then rejects as an invalid <c>Bill.billBatch</c> reference) instead of a real sequence
    /// number, so the id is generated here instead of trusting Pace to assign it.
    /// </summary>
    private async Task<int> GetNextBillBatchIdAsync(CancellationToken cancellationToken)
    {
        var page = await paceClient.LoadValueObjectsAsync(new ValueObjectDescriptor
        {
            ObjectName = "BillBatch",
            Fields = [new FieldDescriptor { Name = "id", Xpath = "@id" }],
            XpathSorts = [new XPathDataSort { Xpath = "@id", Descending = true }],
            Limit = 1,
            Offset = 0
        }, cancellationToken: cancellationToken);

        var highestId = page.ValueObjects?
            .FirstOrDefault()?.Fields?
            .FirstOrDefault(field => string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        // ValueField.Value is object? with no custom converter, so System.Text.Json deserializes it as
        // a boxed JsonElement rather than a primitive - Convert.ToInt32(object) requires IConvertible,
        // which JsonElement does not implement, and throws. Convert.ToString falls back to the object's
        // own ToString() instead, which for a JSON number returns its raw text ("16334") - the same
        // two-step pattern PaceInvoiceService.GetNullableInt already uses for this exact value shape.
        var highestIdText = Convert.ToString(highestId, CultureInfo.InvariantCulture);
        var maxId = string.IsNullOrWhiteSpace(highestIdText) ? 0 : int.Parse(highestIdText, CultureInfo.InvariantCulture);

        return maxId + 1;
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
