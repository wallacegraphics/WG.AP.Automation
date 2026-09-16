using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WG.AP.Integrations.Pace;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Tests.Integrations.Pace;

public sealed class PaceIntegrationTests
{
    [Fact]
    public async Task BasicAuthHandler_AddsConfiguredAuthorizationHeader()
    {
        var captureHandler = new CaptureHandler();
        var handler = new PaceBasicAuthHandler(Options.Create(new PaceOptions
        {
            BaseUrl = "https://pacestaging.wallacegraphics.com",
            UserName = "pace-user",
            Password = "pace-password"
        }))
        {
            InnerHandler = captureHandler
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://pacestaging.wallacegraphics.com/rpc/rest/services/ReadObject/readVendor");

        await invoker.SendAsync(request, CancellationToken.None);

        var expectedToken = Convert.ToBase64String(Encoding.ASCII.GetBytes("pace-user:pace-password"));
        Assert.Equal(new AuthenticationHeaderValue("Basic", expectedToken), captureHandler.Request?.Headers.Authorization);
    }

    [Theory]
    [InlineData("https://pacestaging.wallacegraphics.com", "https://pacestaging.wallacegraphics.com/rpc/rest/services")]
    [InlineData("https://pacestaging.wallacegraphics.com/", "https://pacestaging.wallacegraphics.com/rpc/rest/services")]
    [InlineData("https://pacestaging.wallacegraphics.com/rpc/rest/services", "https://pacestaging.wallacegraphics.com/rpc/rest/services")]
    public void BuildPaceServiceBaseUrl_AppendsRestServicePathOnce(string baseUrl, string expected)
    {
        Assert.Equal(expected, PaceServiceCollectionExtensions.BuildPaceServiceBaseUrl(baseUrl));
    }

    [Fact]
    public void AddPaceIntegration_RegistersTheFullGeneratedPaceClient()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PaceOptions.SectionName}:BaseUrl"] = "https://pacestaging.wallacegraphics.com",
                [$"{PaceOptions.SectionName}:UserName"] = "pace-user",
                [$"{PaceOptions.SectionName}:Password"] = "pace-password"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPaceIntegration(configuration);

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IPaceClient>();

        Assert.IsType<PaceClient>(client);
    }

    [Fact]
    public void PaceInvoiceFieldsParser_ParsesStoredInvoiceFieldsJson()
    {
        var fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson);

        Assert.Equal("INV-163939830", fields.InvoiceNumber);
        Assert.Equal("2887-2533", fields.CustomerPO);
        Assert.Equal(new DateOnly(2026, 9, 1), fields.InvoiceDate);
        Assert.Equal(77253.12m, fields.Total);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteDisabled_ReturnsDryRunPrepared()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                return Task.FromResult(_.ObjectName switch
                {
                    "PurchaseOrderLine" => Group("PurchaseOrderLine",
                        Row(("id", 163108), ("qtyReceived", 1), ("glAccount", 5609), ("glDepartment", 5024), ("job", "222260"), ("jobPart", "05"), ("activityCode", "13030"))),
                    "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt",
                        Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 77253.12m), ("extendedPrice", 77253.12m), ("stockingUOM", "EA"))),
                    "BillLine" => Group("BillLine"),
                    "Bill" => Group("Bill"),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            Options.Create(new PaceOptions
            {
                BaseUrl = "https://pacestaging.wallacegraphics.com",
                UserName = "pace-user",
                Password = "pace-password",
                WriteEnabled = false
            }),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Null(result.RequestJson);
        Assert.Contains("billableReceipts", result.ResponseJson);
        Assert.Contains("144841", result.ResponseJson);
        Assert.Contains("222260", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenNoPoLines_ReturnsNoPo()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(Group(_.ObjectName!))),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.Contains("2887-2533", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WithApostropheInPoNumber_UsesXPathStringLiteral()
    {
        ValueObjectDescriptor? purchaseOrderLineDescriptor = null;
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                if (_.ObjectName == "PurchaseOrderLine")
                {
                    purchaseOrderLineDescriptor = _;
                }

                return Task.FromResult(Group(_.ObjectName!));
            }),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("2887-2533", "ABC'123", StringComparison.Ordinal)), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.Equal("@purchaseOrder/@poNumber = \"ABC'123\"", purchaseOrderLineDescriptor?.XpathFilter);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPoLineNotReceived_ReturnsPoNotReceived()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName == "PurchaseOrderLine"
                ? Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 0)))
                : Group(_.ObjectName!))),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.PoNotReceived, result.StatusCode);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPoReceiptsAreMissing_ReturnsError()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt"),
                _ => Group(_.ObjectName!)
            })),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("no unpaid PO receipts", result.ErrorMessage);
        Assert.Contains("163108", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenEveryReceiptAlreadyBilled_ReturnsAlreadyEntered()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt", Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("billedQuantity", 1), ("billedAmount", 40), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine", Row(("id", 900), ("purchaseOrderReceipt", 144841), ("bill", "bill-123"))),
                "Bill" => Group("Bill"),
                _ => throw new InvalidOperationException(_.ObjectName)
            })),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("bill-123", result.PaceBillId);
        Assert.Contains("bill-123", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenReceiptPartiallyBilled_ReturnsRemainingQuantityAsBillable()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 3), ("glAccount", 5609), ("glDepartment", 5024), ("job", "222260"))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt", Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 3), ("unitCost", 38626.56m), ("extendedPrice", 115879.68m), ("billedQuantity", 1), ("billedAmount", 38626.56m), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine", Row(("id", 900), ("purchaseOrderReceipt", 144841), ("bill", "bill-123"))),
                "Bill" => Group("Bill"),
                _ => throw new InvalidOperationException(_.ObjectName)
            })),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
        Assert.Contains("144841", result.ResponseJson);
        Assert.Contains("\"invoiceAmount\": 77253.12", result.ResponseJson);
        Assert.Contains("\"poQuantity\": 2", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenReceiptTotalDiffersFromInvoiceTotal_ReturnsError()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1), ("glAccount", 5609), ("glDepartment", 5024), ("job", "222260"))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt", Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine"),
                "Bill" => Group("Bill"),
                _ => throw new InvalidOperationException(_.ObjectName)
            })),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("does not match unpaid PO receipt total", result.ErrorMessage);
        Assert.Contains("77253.12", result.ErrorMessage);
        Assert.Contains("40", result.ErrorMessage);
        Assert.Contains("144841", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenReceivedLineIsInvoiceComplete_ReturnsAlreadyEntered()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1), ("invoiceComplete", true))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt", Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine"),
                "Bill" => Group("Bill"),
                _ => throw new InvalidOperationException(_.ObjectName)
            })),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Contains("invoiceComplete", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenReceiptsExceedOnePage_UsesPagedResults()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", 1, Row(("id", 163108), ("qtyReceived", 2), ("glAccount", 5609))),
                "PurchaseOrderReceipt" when _.Offset == 0 => Group("PurchaseOrderReceipt", 501, Enumerable.Range(1, 500)
                    .Select(id => Row(("id", id), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("billedQuantity", 1), ("billedAmount", 40), ("stockingUOM", "EA")))
                    .ToArray()),
                "PurchaseOrderReceipt" when _.Offset == 500 => Group("PurchaseOrderReceipt", 501, Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 77253.12m), ("extendedPrice", 77253.12m), ("billedQuantity", 0), ("billedAmount", 0), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine"),
                "Bill" => Group("Bill"),
                _ => throw new InvalidOperationException($"{_.ObjectName} offset {_.Offset}")
            })),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
        Assert.Contains("144841", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPaceBillExistsForVendorAndInvoice_ReturnsAlreadyEntered()
    {
        ValueObjectDescriptor? billDescriptor = null;
        var purchaseOrderLineLookupCalled = false;
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                if (_.ObjectName == "Bill")
                {
                    billDescriptor = _;
                }
                else if (_.ObjectName == "PurchaseOrderLine")
                {
                    purchaseOrderLineLookupCalled = true;
                }

                return Task.FromResult(_.ObjectName switch
                {
                    "Bill" => Group("Bill", Row(("id", 12345), ("vendor", "SANMAR-PACE"), ("invoiceNumber", "INV-163939830"), ("poNumber", "2887-2533"), ("billBatch", "987"), ("postingStatus", "Open"))),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("12345", result.PaceBillId);
        Assert.Equal("987", result.PaceBillBatchId);
        Assert.Contains("INV-163939830", result.ResponseJson);
        Assert.Equal("@vendor = 'SANMAR-PACE' and @invoiceNumber = 'INV-163939830'", billDescriptor?.XpathFilter);
        Assert.False(purchaseOrderLineLookupCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPaceBillExists_ReturnsAlreadyEnteredBeforePoReceiptGates()
    {
        var purchaseOrderLineLookupCalled = false;
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                if (_.ObjectName == "PurchaseOrderLine")
                {
                    purchaseOrderLineLookupCalled = true;
                }

                return Task.FromResult(_.ObjectName switch
                {
                    "Bill" => Group("Bill", Row(("id", 12345), ("vendor", "SANMAR-PACE"), ("invoiceNumber", "INV-163939830"), ("poNumber", "2887-2533"), ("billBatch", "987"), ("postingStatus", "Open"))),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("12345", result.PaceBillId);
        Assert.Equal("987", result.PaceBillBatchId);
        Assert.False(purchaseOrderLineLookupCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPaceVendorIdIsMissing_ReturnsErrorBeforeDuplicateProbe()
    {
        var billLookupCalled = false;
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                if (_.ObjectName == "Bill")
                {
                    billLookupCalled = true;
                }

                return Task.FromResult(_.ObjectName switch
                {
                    "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1))),
                    "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt", Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("stockingUOM", "EA"))),
                    "BillLine" => Group("BillLine"),
                    "Bill" => Group("Bill"),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(paceVendorId: null), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("Pace vendor id", result.ErrorMessage);
        Assert.False(billLookupCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsPurchaseOrder404ToNoPo()
    {
        var exception = new ApiException("missing", 404, "not found", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => _.ObjectName == "Bill"
                ? Task.FromResult(Group("Bill"))
                : Task.FromException<ValueObjectsGroup>(exception)),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Null(result.RequestJson);
        Assert.Contains("not found", result.ResponseJson);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsReceiptLookup404ToError()
    {
        var exception = new ApiException("receipt missing", 404, "receipt not found", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => _.ObjectName switch
            {
                "PurchaseOrderLine" => Task.FromResult(Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1)))),
                "PurchaseOrderReceipt" => Task.FromException<ValueObjectsGroup>(exception),
                _ => Task.FromResult(Group(_.ObjectName!))
            }),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Contains("receipt not found", result.ResponseJson);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsServerErrorToRetryLater()
    {
        var exception = new ApiException("unavailable", 503, "try later", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromException<ValueObjectsGroup>(exception)),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.RetryLater, result.StatusCode);
        Assert.True(result.IsTransient);
        Assert.Null(result.RequestJson);
        Assert.Contains("try later", result.ResponseJson);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task PaceInvoiceService_MapsAuthenticationFailureToRetryLater(int statusCode)
    {
        var exception = new ApiException("auth failed", statusCode, "not authorized", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromException<ValueObjectsGroup>(exception)),
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.RetryLater, result.StatusCode);
        Assert.True(result.IsTransient);
        Assert.Contains(statusCode.ToString(), result.ErrorMessage);
        Assert.Contains("credentials or permissions", result.ErrorMessage);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static PaceOptions NewPaceOptions() =>
        new()
        {
            BaseUrl = "https://pacestaging.wallacegraphics.com",
            UserName = "pace-user",
            Password = "pace-password",
            WriteEnabled = false
        };

    private static PaceInvoiceSubmission NewSubmission(string fieldsJson = InvoiceFieldsJson, string? paceVendorId = "SANMAR-PACE") =>
        new()
        {
            InvoiceId = 42,
            PaceVendorId = paceVendorId,
            Fields = PaceInvoiceFieldsParser.Parse(fieldsJson)
        };

    private sealed class FakePaceClient(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects) : PaceClient(new HttpClient())
    {
        public override Task<ValueObjectsGroup> LoadValueObjectsAsync(ValueObjectDescriptor? valueObjectDescriptor = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            loadValueObjects(valueObjectDescriptor ?? new ValueObjectDescriptor());
    }

    private static ValueObjectsGroup Group(string objectName, params ValueObject[] rows) =>
        Group(objectName, rows.Length, rows);

    private static ValueObjectsGroup Group(string objectName, int totalRecords, params ValueObject[] rows) =>
        new()
        {
            ObjectName = objectName,
            TotalRecords = totalRecords,
            ValueObjects = rows
        };

    private static ValueObject Row(params (string Name, object? Value)[] fields) =>
        new()
        {
            Fields = fields.Select(field => new ValueField { Name = field.Name, Value = field.Value }).ToList()
        };

    private const string InvoiceFieldsJson = """
    {
      "InvoiceNumber": "INV-163939830",
      "SalesOrder": "SO-164759343",
      "InvoiceDate": "2026-09-01",
      "DueDate": "2026-10-31",
      "Total": 77253.12,
      "ClientName": "SanMar",
      "CustomerPO": "2887-2533",
      "CustomerNumber": "76274-0000",
      "OrderAccount": "76274-0000",
      "Terms": "Net60"
    }
    """;

}
