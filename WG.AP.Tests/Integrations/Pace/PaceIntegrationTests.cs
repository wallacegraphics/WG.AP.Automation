using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task HttpLoggingHandler_LogsFourLinesWithTheStatusDescription()
    {
        var logger = new CapturingLogger();
        var handler = new PaceHttpLoggingHandler(logger)
        {
            InnerHandler = new FixedStatusHandler(HttpStatusCode.NotFound)
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://pacestaging.wallacegraphics.com/rpc/rest/services/FindObjects/loadValueObjects");

        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(4, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal(LogLevel.Information, message.Level));

        // Only the two lines reporting the response carry a status - the numeric code alone is what
        // today's log shows, so the fix is specifically that these two also carry its plain-English
        // meaning rather than leaving the reader to already know what "404" means.
        Assert.Contains("404", logger.Messages[2].Text);
        Assert.Contains("NotFound", logger.Messages[2].Text);
        Assert.Contains("404", logger.Messages[3].Text);
        Assert.Contains("NotFound", logger.Messages[3].Text);
    }

    private sealed class FixedStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class CapturingLogger : ILogger<PaceHttpLoggingHandler>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
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
            UnusedBillBatchResolver,
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
        Assert.Contains("billableReceipts", result.ResponseJson);
        Assert.Contains("144841", result.ResponseJson);
        Assert.Contains("222260", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenPeriodClosed_ReturnsError_AndNeverCallsCreateBillBatch()
    {
        var createBillBatchCalled = false;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                _ => throw new InvalidOperationException($"Unexpected FindAsync({type}) once the period is known to be closed.")
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "F" },
            OnCreateBillBatch = _ =>
            {
                createBillBatchCalled = true;
                throw new InvalidOperationException("CreateBillBatchAsync should not be called for a closed period.");
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("not open for posting", result.ErrorMessage);
        Assert.Contains("5201", result.ErrorMessage);
        Assert.False(createBillBatchCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenNoExistingBatch_CreatesOne_AndReturnsErrorWithBatchIdUntilPR2()
    {
        BillBatch? createdRequest = null;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => [],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnCreateBillBatch = request =>
            {
                createdRequest = request;
                return new BillBatch { Id = 16311 };
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("not implemented until PR 2", result.ErrorMessage);
        Assert.Equal("16311", result.PaceBillBatchId);
        Assert.NotNull(createdRequest);
        Assert.Equal(5201, createdRequest!.GlAccountingPeriod);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), createdRequest.Date);
        Assert.Equal($"{DateOnly.FromDateTime(DateTime.Today):MM_dd_yyyy}_76274_APAutomation", createdRequest.Description);
        Assert.Equal(PaceBillBatchResolver.EnteredBy, createdRequest.EnteredBy);
        Assert.Null(createdRequest.Manual);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenExistingBatchFound_ReusesIt_AndNeverCreatesANewOne()
    {
        var createBillBatchCalled = false;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => ["16311"],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "T" },
            OnCreateBillBatch = _ =>
            {
                createBillBatchCalled = true;
                throw new InvalidOperationException("CreateBillBatchAsync should not be called when an open batch already exists.");
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("not implemented until PR 2", result.ErrorMessage);
        Assert.Equal("16311", result.PaceBillBatchId);
        Assert.False(createBillBatchCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenNoPoLines_ReturnsNoPo()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(Group(_.ObjectName!))),
            UnusedBillBatchResolver,
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("2887-2533", "ABC'123", StringComparison.Ordinal)), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.Equal("@purchaseOrder/@poNumber = \"ABC'123\"", purchaseOrderLineDescriptor?.XpathFilter);
    }

    [Fact]
    public async Task PaceInvoiceService_WithFiveDigitsBeforeDash_UsesFirstFiveDigitsForPacePoSearch()
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("2887-2533", "60119-COBB-3921", StringComparison.Ordinal)), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.Equal("@purchaseOrder/@poNumber = '60119'", purchaseOrderLineDescriptor?.XpathFilter);
    }

    [Fact]
    public async Task PaceInvoiceService_WithFourDigitPoFormat_UsesCsPrefixAndSecondNumberForPacePoSearch()
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("2887-2533", "2702-2225 Cobb Nutri Team", StringComparison.Ordinal)), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.NoPo, result.StatusCode);
        Assert.Equal("@purchaseOrder/@poNumber = 'CS2225'", purchaseOrderLineDescriptor?.XpathFilter);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPoLineNotReceived_ReturnsError()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName == "PurchaseOrderLine"
                ? Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 0)))
                : Group(_.ObjectName!))),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("no received PO lines", result.ErrorMessage);
        Assert.Contains("INV-163939830", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ErrorMessage);
        Assert.Contains("77253.12", result.ErrorMessage);
        Assert.Contains("163108", result.ErrorMessage);
        Assert.Contains("163108", result.ResponseJson);
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("no unpaid PO receipts", result.ErrorMessage);
        Assert.Contains("163108", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenEveryReceiptAlreadyBilledWithoutDuplicateBill_ReturnsError()
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Null(result.PaceBillId);
        Assert.Contains("no unpaid receipt quantity remains", result.ErrorMessage);
        Assert.Contains("144841", result.ErrorMessage);
        Assert.Contains("900", result.ErrorMessage);
        Assert.Contains("bill-123", result.ErrorMessage);
        Assert.Contains("bill-123", result.ResponseJson);
        Assert.Contains("144841", result.ResponseJson);
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
        Assert.Contains("144841", result.ResponseJson);
        Assert.Contains("\"invoiceAmount\": 77253.12", result.ResponseJson);
        Assert.Contains("\"poQuantity\": 2", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenMultipleUnpaidReceiptsMatchInvoiceTotal_ReturnsDryRunPrepared()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(_.ObjectName switch
            {
                "PurchaseOrderLine" => Group("PurchaseOrderLine",
                    Row(("id", 163108), ("qtyReceived", 1), ("glAccount", 5609), ("glDepartment", 5024), ("job", "222260")),
                    Row(("id", 163109), ("qtyReceived", 1), ("glAccount", 5609), ("glDepartment", 5024), ("job", "222260"))),
                "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt",
                    Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 70000m), ("extendedPrice", 70000m), ("stockingUOM", "EA")),
                    Row(("id", 144842), ("purchaseOrderLine", 163109), ("quantity", 1), ("unitCost", 7253.12m), ("extendedPrice", 7253.12m), ("stockingUOM", "EA"))),
                "BillLine" => Group("BillLine"),
                "Bill" => Group("Bill"),
                _ => throw new InvalidOperationException(_.ObjectName)
            })),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
        Assert.Contains("144841", result.ResponseJson);
        Assert.Contains("144842", result.ResponseJson);
        Assert.Contains("163108", result.ResponseJson);
        Assert.Contains("163109", result.ResponseJson);
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("does not match unpaid PO receipt total", result.ErrorMessage);
        Assert.Contains("77253.12", result.ErrorMessage);
        Assert.Contains("40", result.ErrorMessage);
        Assert.Contains("144841", result.ErrorMessage);
        Assert.Contains("144841", result.ResponseJson);
        Assert.Contains("163108", result.ResponseJson);
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
            UnusedBillBatchResolver,
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
            UnusedBillBatchResolver,
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
                    "Bill" => Group("Bill", Row(("id", 12345), ("vendor", "76274-0000"), ("invoiceNumber", "INV-163939830"), ("poNumber", "2887-2533"), ("billBatch", "987"), ("postingStatus", "Open"))),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("12345", result.PaceBillId);
        Assert.Equal("987", result.PaceBillBatchId);
        Assert.Contains("INV-163939830", result.ResponseJson);
        Assert.Equal("@vendor = '76274-0000' and @invoiceNumber = '163939830'", billDescriptor?.XpathFilter);
        Assert.False(purchaseOrderLineLookupCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_NormalizesInvoiceNumber_ForPaceBillLookup()
    {
        ValueObjectDescriptor? billDescriptor = null;
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                if (_.ObjectName == "Bill")
                {
                    billDescriptor = _;
                }

                return Task.FromResult(_.ObjectName switch
                {
                    "Bill" => Group("Bill"),
                    "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 0))),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("INV-163939830", "INV 163-939-830", StringComparison.Ordinal)), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Equal("@vendor = '76274-0000' and @invoiceNumber = '163939830'", billDescriptor?.XpathFilter);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenDuplicateBillProbeReturns404_ContinuesToPoReceiptProcessing()
    {
        var exception = new ApiException("missing bill", 404, "not found", new Dictionary<string, IEnumerable<string>>(), null);
        var purchaseOrderLineLookupCalled = false;
        var service = new PaceInvoiceService(
            new FakePaceClient(_ =>
            {
                if (_.ObjectName == "Bill")
                {
                    return Task.FromException<ValueObjectsGroup>(exception);
                }

                if (_.ObjectName == "PurchaseOrderLine")
                {
                    purchaseOrderLineLookupCalled = true;
                }

                return Task.FromResult(_.ObjectName switch
                {
                    "PurchaseOrderLine" => Group("PurchaseOrderLine", Row(("id", 163108), ("qtyReceived", 0))),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.True(purchaseOrderLineLookupCalled);
        Assert.DoesNotContain("was not found using normalized invoice number", result.ErrorMessage);
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
                    "Bill" => Group("Bill", Row(("id", 12345), ("vendor", "76274-0000"), ("invoiceNumber", "INV-163939830"), ("poNumber", "2887-2533"), ("billBatch", "987"), ("postingStatus", "Open"))),
                    _ => throw new InvalidOperationException(_.ObjectName)
                });
            }),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("12345", result.PaceBillId);
        Assert.Equal("987", result.PaceBillBatchId);
        Assert.False(purchaseOrderLineLookupCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenPaceVendoreAccountNumberIsMissing_ReturnsErrorBeforeDuplicateProbe()
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(paceVendoreAccountNumber: null), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("Pace vendor account number", result.ErrorMessage);
        Assert.False(billLookupCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsPurchaseOrderHttp404ToErrorWithPaceReason()
    {
        var exception = new ApiException("missing", 404, """
            <!DOCTYPE HTML PUBLIC "-//W3C//DTD HTML 4.01//EN" "http://www.w3.org/TR/html4/strict.dtd">
            <html><head>
            <title>404 Not Found</title>
            </head><body>
            <h1>Not Found</h1>
            <p>The requested URL was not found on this server.</p>
            </body></html>
            """, new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => _.ObjectName == "Bill"
                ? Task.FromResult(Group("Bill"))
                : Task.FromException<ValueObjectsGroup>(exception)),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Contains("Pace returned HTTP 404 while loading purchase order lines", result.ErrorMessage);
        Assert.Contains("INV-163939830", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ErrorMessage);
        Assert.Contains("The requested URL was not found on this server.", result.ErrorMessage);
        Assert.Contains("PurchaseOrderLine value-object endpoint", result.ErrorMessage);
        Assert.DoesNotContain("The HTTP status code", result.ErrorMessage);
        Assert.Contains("The requested URL was not found on this server.", result.ResponseJson);
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
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Contains("Pace returned HTTP 404 while loading purchase order receipts", result.ErrorMessage);
        Assert.Contains("receipt not found", result.ErrorMessage);
        Assert.Contains("INV-163939830", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ErrorMessage);
        Assert.DoesNotContain("The HTTP status code", result.ErrorMessage);
        Assert.Contains("receipt not found", result.ResponseJson);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsGeneric404ToReadableError()
    {
        var exception = new ApiException("The HTTP status code of the response was not expected (404).", 404, "not found", new Dictionary<string, IEnumerable<string>>(), null);
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (_, _) => throw exception
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Contains("Pace returned HTTP 404", result.ErrorMessage);
        Assert.Contains("Pace response: not found", result.ErrorMessage);
        Assert.Contains("INV-163939830", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ErrorMessage);
        Assert.DoesNotContain("The HTTP status code", result.ErrorMessage);
        Assert.Contains("not found", result.ResponseJson);
        Assert.Contains("statusCode", result.ResponseJson);
    }

    [Fact]
    public async Task PaceInvoiceService_MapsServerErrorToRetryLater()
    {
        var exception = new ApiException("unavailable", 503, "try later", new Dictionary<string, IEnumerable<string>>(), null);
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromException<ValueObjectsGroup>(exception)),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.RetryLater, result.StatusCode);
        Assert.True(result.IsTransient);
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
            UnusedBillBatchResolver,
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

    // Shared placeholder for tests where Pace:WriteEnabled is false: PaceInvoiceService never calls
    // ResolveOrCreateAsync on that path, so this resolver's underlying client is never invoked.
    private static readonly PaceBillBatchResolver UnusedBillBatchResolver =
        new(new FakePaceClient(_ => throw new InvalidOperationException("Not expected to be called when Pace:WriteEnabled is false.")), NullLogger<PaceBillBatchResolver>.Instance);

    private static PaceOptions WriteEnabledPaceOptions() =>
        new()
        {
            BaseUrl = "https://pacestaging.wallacegraphics.com",
            UserName = "pace-user",
            Password = "pace-password",
            WriteEnabled = true
        };

    // The same billable-line/receipt fixture as PaceInvoiceService_WithWriteDisabled_ReturnsDryRunPrepared,
    // reused so the write-enabled tests reach the bill-batch resolution branch instead of stopping earlier.
    private static ValueObjectsGroup BillableInvoiceValueObjects(string objectName) => objectName switch
    {
        "PurchaseOrderLine" => Group("PurchaseOrderLine",
            Row(("id", 163108), ("qtyReceived", 1), ("glAccount", 5609), ("glDepartment", 5024), ("job", "222260"), ("jobPart", "05"), ("activityCode", "13030"))),
        "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt",
            Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 77253.12m), ("extendedPrice", 77253.12m), ("stockingUOM", "EA"))),
        "BillLine" => Group("BillLine"),
        "Bill" => Group("Bill"),
        _ => throw new InvalidOperationException(objectName)
    };

    private static PaceInvoiceSubmission NewSubmission(string fieldsJson = InvoiceFieldsJson, string? paceVendoreAccountNumber = "76274-0000") =>
        new()
        {
            InvoiceId = 42,
            PaceVendoreAccountNumber = paceVendoreAccountNumber,
            Fields = PaceInvoiceFieldsParser.Parse(fieldsJson)
        };

    private sealed class FakePaceClient(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects) : PaceClient(new HttpClient())
    {
        public override Task<ValueObjectsGroup> LoadValueObjectsAsync(ValueObjectDescriptor? valueObjectDescriptor = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            loadValueObjects(valueObjectDescriptor ?? new ValueObjectDescriptor());
    }

    private sealed class FakeWriteEnabledClient(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects) : PaceClient(new HttpClient())
    {
        public Func<string, string, ICollection<string>>? OnFind { get; set; }

        public Func<string, GLAccountingPeriod>? OnReadGlAccountingPeriod { get; set; }

        public Func<BillBatch?, BillBatch>? OnCreateBillBatch { get; set; }

        public override Task<ValueObjectsGroup> LoadValueObjectsAsync(ValueObjectDescriptor? valueObjectDescriptor = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            loadValueObjects(valueObjectDescriptor ?? new ValueObjectDescriptor());

        public override Task<ICollection<string>> FindAsync(string type, string xpath, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnFind is null ? throw new InvalidOperationException($"Unexpected FindAsync({type}).") : OnFind(type, xpath));

        public override Task<GLAccountingPeriod> ReadGLAccountingPeriodAsync(string primaryKey, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnReadGlAccountingPeriod is null ? throw new InvalidOperationException("Unexpected ReadGLAccountingPeriodAsync.") : OnReadGlAccountingPeriod(primaryKey));

        public override Task<BillBatch> CreateBillBatchAsync(BillBatch? billBatch = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnCreateBillBatch is null ? throw new InvalidOperationException("Unexpected CreateBillBatchAsync.") : OnCreateBillBatch(billBatch));
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
