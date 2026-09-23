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
        Assert.False(result.RequiresReview);
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
        Assert.False(result.RequiresReview);
        Assert.Contains("locked/closed", result.ErrorMessage);
        Assert.Contains("2026-09-01", result.ErrorMessage);
        Assert.Contains("5201", result.ErrorMessage);
        Assert.False(createBillBatchCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenReadGLAccountingPeriodThrowsWithHttp200_ReturnsRetryLater_NotError()
    {
        var createBillBatchCalled = false;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                _ => throw new InvalidOperationException($"Unexpected FindAsync({type}) once the GL accounting period read has failed.")
            },
            OnReadGlAccountingPeriod = _ => throw new ApiException(
                "Could not deserialize the response body stream as WG.AP.Integrations.Pace.Generated.GLAccountingPeriod.",
                200,
                "not valid json",
                new Dictionary<string, IEnumerable<string>>(),
                null),
            OnCreateBillBatch = _ =>
            {
                createBillBatchCalled = true;
                throw new InvalidOperationException("CreateBillBatchAsync should not be called when the GL accounting period read failed.");
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.RetryLater, result.StatusCode);
        Assert.True(result.IsTransient);
        Assert.Contains("could not be parsed", result.ErrorMessage);
        Assert.Contains("not valid json", result.ResponseJson);
        Assert.False(createBillBatchCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenBillExistsUnderPoVendor_ReturnsAlreadyEnteredAndDoesNotCreateBill()
    {
        var createBillCalled = false;
        var client = new FakeWriteEnabledClient(descriptor => Task.FromResult(
            descriptor.ObjectName == "Bill" && descriptor.XpathFilter!.Contains("77000-0000", StringComparison.Ordinal)
                ? Group("Bill", Row(("id", 55555), ("vendor", "77000-0000"), ("invoiceNumber", "163939830"), ("billBatch", "16296"), ("postingStatus", "Open")))
                : BillableInvoiceValueObjects(descriptor.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => ["16296"],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnCreateBill = _ =>
            {
                createBillCalled = true;
                return new Bill { Id = 1 };
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.AlreadyEntered, result.StatusCode);
        Assert.Equal("55555", result.PaceBillId);
        Assert.False(createBillCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenPurchaseOrderHasNoVendor_ReturnsErrorAndDoesNotCreateBill()
    {
        var createBillCalled = false;
        var client = new FakeWriteEnabledClient(descriptor => Task.FromResult(descriptor.ObjectName == "PurchaseOrder"
            ? Group("PurchaseOrder", Row(("id", 1234)))
            : BillableInvoiceValueObjects(descriptor.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => ["16296"],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnCreateBill = _ =>
            {
                createBillCalled = true;
                return new Bill { Id = 1 };
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.Contains("has no vendor", result.ErrorMessage);
        Assert.False(createBillCalled);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenNoExistingBatch_CreatesOneAndCreatesBillWithLines()
    {
        BillBatch? createdBatchRequest = null;
        Bill? createdBillRequest = null;
        ValueObjectDescriptor? purchaseOrderLineDescriptor = null;
        string? glAccountingPeriodXpath = null;
        var createdBillLineRequests = new List<BillLine>();
        var client = new FakeWriteEnabledClient(_ =>
        {
            if (_.ObjectName == "PurchaseOrderLine")
            {
                purchaseOrderLineDescriptor = _;
            }

            return Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!));
        })
        {
            OnFind = (type, xpath) => type switch
            {
                "GLAccountingPeriod" => CaptureGlAccountingPeriodFind(xpath),
                "BillBatch" => [],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnCreateBillBatch = request =>
            {
                createdBatchRequest = request;
                return new BillBatch { Id = 16311 };
            },
            OnCreateBill = request =>
            {
                createdBillRequest = request;
                return new Bill { Id = 87835 };
            },
            OnCreateBillLine = request =>
            {
                createdBillLineRequests.Add(request!);
                return new BillLine { Id = 900 + createdBillLineRequests.Count };
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("2026-09-01", "2026-08-10", StringComparison.Ordinal)), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.BillCreated, result.StatusCode);
        Assert.Equal("16311", result.PaceBillBatchId);
        Assert.Equal("87835", result.PaceBillId);
        Assert.False(result.RequiresReview);

        Assert.NotNull(createdBatchRequest);
        Assert.Equal(5201, createdBatchRequest!.GlAccountingPeriod);
        var invoiceDate = new DateOnly(2026, 8, 10);
        var processingDate = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal("@accountingPeriod = 8 and @fiscalYear = 2026", glAccountingPeriodXpath);
        Assert.Equal(processingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), createdBatchRequest.Date);
        Assert.Equal($"AUTO {processingDate.Month}-{processingDate.Day}-{processingDate:yy}", createdBatchRequest.Description);
        Assert.Equal(PaceBillBatchResolver.EnteredBy, createdBatchRequest.EnteredBy);
        Assert.True(createdBatchRequest.Manual);
        Assert.NotNull(purchaseOrderLineDescriptor);
        Assert.DoesNotContain(purchaseOrderLineDescriptor!.Fields!, field => string.Equals(field.Xpath, "@purchaseOrder/@vendor", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(createdBillRequest);
        Assert.Equal(16311, createdBillRequest!.BillBatch);
        Assert.Equal(5201, createdBillRequest.PaymentPeriod);
        // The PO's own vendor ("77000-0000"), not the client's configured PaceVendorAccountNumber
        // ("76274-0000") - proves the bill is linked to the vendor on the actual PO, not just config.
        Assert.Equal("77000-0000", createdBillRequest.Vendor);
        Assert.Equal("163939830", createdBillRequest.InvoiceNumber);
        Assert.Equal("2887-2533", createdBillRequest.PoNumber);
        Assert.Equal("Open", createdBillRequest.PostingStatus);

        // Reference mirrors PoNumber; VoucherDate is the processing-date batch's own date;
        // DateDue comes straight from the extracted invoice fields.
        Assert.Equal("2887-2533", createdBillRequest.Reference);
        Assert.Equal(processingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), createdBillRequest.VoucherDate);
        Assert.Equal("2026-10-31", createdBillRequest.DateDue);

        // Pace rejects createBill outright (HTTP 500, "terms, Value required") if this is left null -
        // confirmed live against Pace staging on 2026-09-21. The fixture's Terms is "Net60" (see
        // InvoiceFieldsJson below), which ResolvePaceTermsId maps to Pace's "Net 60 Days" id.
        Assert.Equal(5010, createdBillRequest.Terms);

        Assert.Single(createdBillLineRequests);
        Assert.Equal(87835, createdBillLineRequests[0].Bill);
        Assert.Equal(144841, createdBillLineRequests[0].PurchaseOrderReceipt);
        Assert.Equal(5609, createdBillLineRequests[0].GlAccount);
        Assert.Equal(5024, createdBillLineRequests[0].GlDepartment);
        Assert.Equal("222260", createdBillLineRequests[0].Job);
        Assert.Equal("05", createdBillLineRequests[0].JobPart);
        Assert.Equal("13030", createdBillLineRequests[0].ActivityCode);

        ICollection<string> CaptureGlAccountingPeriodFind(string xpath)
        {
            glAccountingPeriodXpath = xpath;
            return ["5201"];
        }
    }

    [Theory]
    [InlineData("Net30", 1)]
    [InlineData("Net60", 5010)]
    [InlineData("Net90", 5018)]
    [InlineData("COD", 5010)]
    [InlineData("Net120", 5010)]
    public async Task PaceInvoiceService_WithWriteEnabled_MapsExtractedTermsToPaceTermsId_FallingBackWhenUnmapped(string extractedTerms, int expectedPaceTermsId)
    {
        // "COD" (not a "NetNN" token at all) and "Net120" (a day count Pace has no plain entry for)
        // both fall back to the same default as an invoice with no Terms extracted at all - the
        // fallback exists precisely because not every value on a real invoice will map cleanly.
        Bill? createdBillRequest = null;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => [],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnCreateBillBatch = _ => new BillBatch { Id = 16311 },
            OnCreateBill = request =>
            {
                createdBillRequest = request;
                return new Bill { Id = 87835 };
            },
            OnCreateBillLine = _ => new BillLine { Id = 901 }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        await service.SubmitAsync(NewSubmission(InvoiceFieldsJson.Replace("Net60", extractedTerms, StringComparison.Ordinal)), CancellationToken.None);

        Assert.NotNull(createdBillRequest);
        Assert.Equal(expectedPaceTermsId, createdBillRequest!.Terms);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenExistingBatchFound_ReusesIt_AndNeverCreatesANewOne()
    {
        var createBillBatchCalled = false;
        Bill? createdBillRequest = null;
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
            },
            OnCreateBill = request =>
            {
                createdBillRequest = request;
                return new Bill { Id = 87836 };
            },
            OnCreateBillLine = request => new BillLine { Id = 901 }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.BillCreated, result.StatusCode);
        Assert.Equal("16311", result.PaceBillBatchId);
        Assert.False(createBillBatchCalled);
        Assert.NotNull(createdBillRequest);
        Assert.Equal(16311, createdBillRequest!.BillBatch);
        Assert.Equal(5201, createdBillRequest.PaymentPeriod);
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenNoPoFound_CreatesNoPoBillWithPaymentPeriod()
    {
        string? billBatchXpath = null;
        Bill? createdBillRequest = null;
        BillLine? createdBillLineRequest = null;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(_.ObjectName switch
        {
            "Bill" => Group("Bill"),
            "PurchaseOrderLine" => Group("PurchaseOrderLine"),
            _ => throw new InvalidOperationException(_.ObjectName)
        }))
        {
            OnFind = (type, xpath) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => CaptureBillBatchFind(xpath),
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnCreateBill = request =>
            {
                createdBillRequest = request;
                return new Bill { Id = 87837 };
            },
            OnCreateBillLine = request =>
            {
                createdBillLineRequest = request;
                return new BillLine { Id = 902 };
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.BillCreated, result.StatusCode);
        Assert.Equal("16312", result.PaceBillBatchId);
        Assert.True(result.RequiresReview);
        Assert.Contains("vendor default GL account/department", result.ErrorMessage);
        Assert.Contains("AUTO ", billBatchXpath);
        Assert.DoesNotContain("AUTO-NOPO", billBatchXpath);
        Assert.NotNull(createdBillRequest);
        Assert.Equal(16312, createdBillRequest!.BillBatch);
        Assert.Equal(5201, createdBillRequest.PaymentPeriod);
        Assert.Equal("SANMAR", createdBillRequest.Vendor);
        Assert.NotNull(createdBillLineRequest);
        Assert.Equal(87837, createdBillLineRequest!.Bill);
        Assert.Null(createdBillLineRequest.PurchaseOrderReceipt);
        Assert.Equal(40701, createdBillLineRequest.GlAccount);
        Assert.Equal(655, createdBillLineRequest.GlDepartment);

        ICollection<string> CaptureBillBatchFind(string xpath)
        {
            billBatchXpath = xpath;
            return ["16312"];
        }
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenNoPoVendorGlDefaultsMissing_ReturnsErrorAfterBillBatchResolution()
    {
        var createBillCalled = false;
        string? billBatchXpath = null;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(_.ObjectName switch
        {
            "Bill" => Group("Bill"),
            "PurchaseOrderLine" => Group("PurchaseOrderLine"),
            _ => throw new InvalidOperationException(_.ObjectName)
        }))
        {
            OnFind = (type, xpath) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => CaptureBillBatchFind(xpath),
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" },
            OnReadVendor = _ => new Vendor { Id = "SANMAR", Name = "SANMAR" },
            OnCreateBill = _ =>
            {
                createBillCalled = true;
                throw new InvalidOperationException("CreateBillAsync should not be called when vendor GL defaults are missing.");
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(new PaceOptions
            {
                BaseUrl = "https://pacestaging.wallacegraphics.com",
                UserName = "pace-user",
                Password = "pace-password",
                WriteEnabled = true
            }),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.True(result.RequiresReview);
        Assert.Contains("default GL account/department", result.ErrorMessage);
        Assert.Contains("AUTO ", billBatchXpath);
        Assert.DoesNotContain("AUTO-NOPO", billBatchXpath);
        Assert.False(createBillCalled);

        ICollection<string> CaptureBillBatchFind(string xpath)
        {
            billBatchXpath = xpath;
            return ["16312"];
        }
    }

    [Fact]
    public async Task PaceInvoiceService_WithWriteEnabled_WhenCreateBillReturnsDuplicateInvoice500_ReturnsError()
    {
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(BillableInvoiceValueObjects(_.ObjectName!)))
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => ["16311"],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "T" },
            OnCreateBill = _ => throw new ApiException(
                "Cannot enter duplicate invoice numbers for the same vendor.",
                500,
                "Cannot enter duplicate invoice numbers for the same vendor.",
                new Dictionary<string, IEnumerable<string>>(),
                null)
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.Error, result.StatusCode);
        Assert.False(result.IsTransient);
        Assert.Contains("duplicate invoice", result.ErrorMessage);
    }

    [Fact]
    public async Task PaceInvoiceService_WhenNoPoLines_AndWriteDisabled_ReturnsDryRunPrepared()
    {
        var service = new PaceInvoiceService(
            new FakePaceClient(_ => Task.FromResult(Group(_.ObjectName!))),
            UnusedBillBatchResolver,
            Options.Create(NewPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
        Assert.True(result.RequiresReview);
        Assert.Contains("no matching Pace PO", result.ErrorMessage);
        Assert.Contains("2887-2533", result.ResponseJson);
    }

    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("O'Brien", "\"O'Brien\"")]
    [InlineData("a'b\"c", "concat('a', \"'\", 'b\"c')")]
    public void PaceXPath_StringLiteral_QuotesValues(string value, string expected)
    {
        Assert.Equal(expected, PaceXPath.StringLiteral(value));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"not-a-date\"")]
    public void PaceDateTimeOffsetConverter_WhenTokenIsNotAPaceDate_ThrowsJsonException(string json)
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        options.Converters.Add(new PaceDateTimeOffsetConverter());

        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>(json, options));
    }

    [Fact]
    public async Task PaceInvoiceService_WhenNoPoLines_AndWriteEnabled_CreatesBillWithVendorDefaultGlCoding()
    {
        BillBatch? createdBatchRequest = null;
        Bill? createdBillRequest = null;
        BillLine? createdBillLineRequest = null;
        var client = new FakeWriteEnabledClient(_ => Task.FromResult(Group(_.ObjectName!)))
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
                createdBatchRequest = request;
                return new BillBatch { Id = 16400 };
            },
            OnCreateBill = request =>
            {
                createdBillRequest = request;
                return new Bill { Id = 88001 };
            },
            OnCreateBillLine = request =>
            {
                createdBillLineRequest = request;
                return new BillLine { Id = 950 };
            }
        };

        var service = new PaceInvoiceService(
            client,
            new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance),
            Options.Create(WriteEnabledPaceOptions()),
            NullLogger<PaceInvoiceService>.Instance);

        var result = await service.SubmitAsync(NewSubmission(), CancellationToken.None);

        Assert.Equal(PaceInvoiceOutcomeStatus.BillCreated, result.StatusCode);
        Assert.Equal("16400", result.PaceBillBatchId);
        Assert.Equal("88001", result.PaceBillId);
        Assert.Equal("950", result.PaceBillLineId);
        Assert.True(result.RequiresReview);

        Assert.NotNull(createdBatchRequest);
        var processingDate = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(processingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), createdBatchRequest!.Date);
        Assert.Equal($"AUTO {processingDate.Month}-{processingDate.Day}-{processingDate:yy}", createdBatchRequest.Description);
        Assert.True(createdBatchRequest.Manual);

        Assert.NotNull(createdBillRequest);
        Assert.Equal(16400, createdBillRequest!.BillBatch);
        Assert.Equal("SANMAR", createdBillRequest.Vendor);
        Assert.Equal("163939830", createdBillRequest.InvoiceNumber);
        Assert.Equal("2887-2533", createdBillRequest.PoNumber);

        Assert.NotNull(createdBillLineRequest);
        Assert.Equal(88001, createdBillLineRequest!.Bill);
        Assert.Null(createdBillLineRequest.PurchaseOrderReceipt);
        Assert.Equal(40701, createdBillLineRequest.GlAccount);
        Assert.Equal(655, createdBillLineRequest.GlDepartment);
        Assert.Equal(77253.12, createdBillLineRequest.InvoiceAmount!.Value, precision: 2);
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

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
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

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
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

        Assert.Equal(PaceInvoiceOutcomeStatus.DryRunPrepared, result.StatusCode);
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

        Assert.Equal(PaceInvoiceOutcomeStatus.PoNotReceived, result.StatusCode);
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
                "PurchaseOrder" => DefaultPurchaseOrderValueObjects(),
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

        Assert.Equal(PaceInvoiceOutcomeStatus.PoNotReceived, result.StatusCode);
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

        Assert.Equal(PaceInvoiceOutcomeStatus.PoNotReceived, result.StatusCode);
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
    public async Task PaceInvoiceService_WhenPaceVendorAccountNumberIsMissing_ReturnsErrorBeforeDuplicateProbe()
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

        var result = await service.SubmitAsync(NewSubmission(paceVendorAccountNumber: null), CancellationToken.None);

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
                "PurchaseOrder" => Task.FromResult(DefaultPurchaseOrderValueObjects()),
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
        "PurchaseOrder" => Group("PurchaseOrder",
            Row(("id", 1234), ("vendor", "77000-0000"))),
        "PurchaseOrderReceipt" => Group("PurchaseOrderReceipt",
            Row(("id", 144841), ("purchaseOrderLine", 163108), ("quantity", 1), ("unitCost", 77253.12m), ("extendedPrice", 77253.12m), ("stockingUOM", "EA"))),
        "BillLine" => Group("BillLine"),
        "Bill" => Group("Bill"),
        "BillBatch" => Group("BillBatch", Row(("id", 16296))),
        _ => throw new InvalidOperationException(objectName)
    };

    private static ValueObjectsGroup DefaultPurchaseOrderValueObjects() =>
        Group("PurchaseOrder", Row(("id", 1234), ("vendor", "77000-0000")));

    private static PaceInvoiceSubmission NewSubmission(string fieldsJson = InvoiceFieldsJson, string? paceVendorAccountNumber = "76274-0000") =>
        new()
        {
            InvoiceId = 42,
            ClientCode = "SANMAR",
            ClientName = "SanMar",
            PaceVendorAccountNumber = paceVendorAccountNumber,
            Fields = PaceInvoiceFieldsParser.Parse(fieldsJson)
        };

    [Fact]
    public void PaceClient_JsonSerializerSettings_AllowsQuotedNumbersOnGeneratedTypes()
    {
        var settings = new SettingsExposingClient().ExposedJsonSerializerSettings;

        var period = System.Text.Json.JsonSerializer.Deserialize<GLAccountingPeriod>(
            """{"accountingPeriod":"9","fiscalYear":"2026","glPeriodStatus":"O"}""",
            settings);

        Assert.Equal(9, period!.AccountingPeriod);
        Assert.Equal(2026, period.FiscalYear);
        Assert.Equal("O", period.GlPeriodStatus);
    }

    [Fact]
    public void PaceClient_JsonSerializerSettings_ParsesPaceGmtLastModifiedFormat()
    {
        var settings = new SettingsExposingClient().ExposedJsonSerializerSettings;

        // The exact payload Pace staging returned for readGLAccountingPeriod?primaryKey=5203, which
        // failed to deserialize before PaceDateTimeOffsetConverter was added: lastModified is
        // "2025-12-29T18:57:36GMT-05:00" - a literal "GMT" between the time and the offset that .NET's
        // built-in ISO-8601 DateTimeOffset parser rejects.
        var period = System.Text.Json.JsonSerializer.Deserialize<GLAccountingPeriod>(
            """
            {"tags":null,"templateLine":0,"sourceOrganizationCompany":null,"ioID":null,"periodString":"09","id":5203,"lastModified":"2025-12-29T18:57:36GMT-05:00","month":null,"startDate":"2026-09-01","notes":null,"glPeriodStatus":"O","closingPeriod":false,"endDate":"2026-09-30","periodTitle":null,"mustRunTBPeriod":false,"accountingPeriod":9,"fiscalYear":2026}
            """,
            settings);

        Assert.Equal(5203, period!.Id);
        Assert.Equal("O", period.GlPeriodStatus);
        Assert.Equal(new DateTimeOffset(2025, 12, 29, 18, 57, 36, TimeSpan.FromHours(-5)), period.LastModified);
    }

    [Fact]
    public void PaceClient_JsonSerializerSettings_OmitsNullGeneratedPropertiesOnCreatePayloads()
    {
        var settings = new SettingsExposingClient().ExposedJsonSerializerSettings;

        var json = System.Text.Json.JsonSerializer.Serialize(new Bill
        {
            BillBatch = 16297,
            Vendor = "SANMAR",
            InvoiceNumber = "163773573"
        }, settings);

        Assert.DoesNotContain("\"id\"", json);
        Assert.DoesNotContain(":null", json);
        Assert.Contains("\"billBatch\":16297", json);
        Assert.Contains("\"vendor\":\"SANMAR\"", json);
        Assert.Contains("\"invoiceNumber\":\"163773573\"", json);
    }

    private sealed class SettingsExposingClient() : PaceClient(new HttpClient())
    {
        public System.Text.Json.JsonSerializerOptions ExposedJsonSerializerSettings => JsonSerializerSettings;
    }

    private sealed class FakePaceClient(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects) : PaceClient(new HttpClient())
    {
        public Func<string, Vendor>? OnReadVendor { get; set; }

        public override Task<ValueObjectsGroup> LoadValueObjectsAsync(ValueObjectDescriptor? valueObjectDescriptor = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            LoadValueObjectsOrDefaultPurchaseOrder(loadValueObjects, valueObjectDescriptor ?? new ValueObjectDescriptor());

        public override Task<Vendor> ReadVendorAsync(string primaryKey, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnReadVendor is null ? VendorDefault(primaryKey) : OnReadVendor(primaryKey));
    }

    private sealed class FakeWriteEnabledClient(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects) : PaceClient(new HttpClient())
    {
        public Func<string, string, ICollection<string>>? OnFind { get; set; }

        public Func<string, GLAccountingPeriod>? OnReadGlAccountingPeriod { get; set; }

        public Func<string, Vendor>? OnReadVendor { get; set; }

        public Func<BillBatch?, BillBatch>? OnCreateBillBatch { get; set; }

        public Func<Bill?, Bill>? OnCreateBill { get; set; }

        public Func<BillLine?, BillLine>? OnCreateBillLine { get; set; }

        public override Task<ValueObjectsGroup> LoadValueObjectsAsync(ValueObjectDescriptor? valueObjectDescriptor = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            LoadValueObjectsOrDefaultPurchaseOrder(loadValueObjects, valueObjectDescriptor ?? new ValueObjectDescriptor());

        public override Task<ICollection<string>> FindAsync(string type, string xpath, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnFind is null ? throw new InvalidOperationException($"Unexpected FindAsync({type}).") : OnFind(type, xpath));

        public override Task<GLAccountingPeriod> ReadGLAccountingPeriodAsync(string primaryKey, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnReadGlAccountingPeriod is null ? throw new InvalidOperationException("Unexpected ReadGLAccountingPeriodAsync.") : OnReadGlAccountingPeriod(primaryKey));

        public override Task<Vendor> ReadVendorAsync(string primaryKey, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnReadVendor is null ? VendorDefault(primaryKey) : OnReadVendor(primaryKey));

        public override Task<BillBatch> CreateBillBatchAsync(BillBatch? billBatch = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnCreateBillBatch is null ? throw new InvalidOperationException("Unexpected CreateBillBatchAsync.") : OnCreateBillBatch(billBatch));

        public override Task<Bill> CreateBillAsync(Bill? bill = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            OnCreateBill is null
                ? throw new InvalidOperationException("Unexpected CreateBillAsync.")
                : Task.FromResult(OnCreateBill(bill));

        public override Task<BillLine> CreateBillLineAsync(BillLine? billLine = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            OnCreateBillLine is null
                ? throw new InvalidOperationException("Unexpected CreateBillLineAsync.")
                : Task.FromResult(OnCreateBillLine(billLine));
    }

    private static Task<ValueObjectsGroup> LoadValueObjectsOrDefaultPurchaseOrder(Func<ValueObjectDescriptor, Task<ValueObjectsGroup>> loadValueObjects, ValueObjectDescriptor descriptor)
    {
        try
        {
            return loadValueObjects(descriptor);
        }
        catch (InvalidOperationException) when (string.Equals(descriptor.ObjectName, "PurchaseOrder", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(DefaultPurchaseOrderValueObjects());
        }
    }

    private static Vendor VendorDefault(string primaryKey) =>
        new() { Id = primaryKey, Name = primaryKey, GlAccount = 40701, GlDepartment = 655 };

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
