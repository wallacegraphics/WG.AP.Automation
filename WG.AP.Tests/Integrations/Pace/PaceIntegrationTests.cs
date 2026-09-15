using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
                        Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("stockingUOM", "EA"))),
                    "BillLine" => Group("BillLine"),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            Options.Create(new PaceOptions
            {
                BaseUrl = "https://pacestaging.wallacegraphics.com",
                UserName = "pace-user",
                Password = "pace-password",
                WriteEnabled = false
            }));

        var result = await service.SubmitAsync(new PaceInvoiceSubmission
        {
            InvoiceId = 42,
            Fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson)
        }, CancellationToken.None);

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
            Options.Create(NewPaceOptions()));

        var result = await service.SubmitAsync(new PaceInvoiceSubmission
        {
            InvoiceId = 42,
            Fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson)
        }, CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.Contains("2887-2533", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPoLineNotReceived_ReturnsPoNotReceived()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName == "PurchaseOrderLine"
                ? Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 0)))
                : Group(_.ObjectName!))),
            Options.Create(NewPaceOptions()));

        var result = await service.SubmitAsync(new PaceInvoiceSubmission
        {
            InvoiceId = 42,
            Fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson)
        }, CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.PoNotReceived, result.StatusCode);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenEveryReceiptAlreadyBilled_ReturnsAlreadyEntered()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 1))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt", Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 40), ("extendedPrice", 40), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine", Row(("id", 900), ("purchaseOrderReceipt", 144841), ("bill", "bill-123"))),
                _ => throw new InvalidOperationException(_.ObjectName)
            })),
            Options.Create(NewPaceOptions()));

        var result = await service.SubmitAsync(new PaceInvoiceSubmission
        {
            InvoiceId = 42,
            Fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson)
        }, CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("bill-123", result.PaceBillId);
        Assert.Contains("bill-123", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsPurchaseOrder404ToNoPo()
    {
        var exception = new ApiException("missing", 404, "not found", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromException<ValueObjectsGroup>(exception)),
            Options.Create(NewPaceOptions()));

        var result = await service.SubmitAsync(new PaceInvoiceSubmission
        {
            InvoiceId = 42,
            Fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson)
        }, CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Null(result.RequestJson);
        Assert.Contains("not found", result.ResponseJson);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsServerErrorToRetryLater()
    {
        var exception = new ApiException("unavailable", 503, "try later", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromException<ValueObjectsGroup>(exception)),
            Options.Create(NewPaceOptions()));

        var result = await service.SubmitAsync(new PaceInvoiceSubmission
        {
            InvoiceId = 42,
            Fields = PaceInvoiceFieldsParser.Parse(InvoiceFieldsJson)
        }, CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.RetryLater, result.StatusCode);
        Assert.True(result.IsTransient);
        Assert.Null(result.RequestJson);
        Assert.Contains("try later", result.ResponseJson);
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

    private sealed class FakePaceClient(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects) : PaceClient(new HttpClient())
    {
        public override Task<ValueObjectsGroup> LoadValueObjectsAsync(ValueObjectDescriptor? valueObjectDescriptor = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            loadValueObjects(valueObjectDescriptor ?? new ValueObjectDescriptor());
    }

    private static ValueObjectsGroup Group(string objectName, params ValueObject[] rows) =>
        new()
        {
            ObjectName = objectName,
            TotalRecords = rows.Length,
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
