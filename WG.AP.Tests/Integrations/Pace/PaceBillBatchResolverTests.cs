using Microsoft.Extensions.Logging.Abstractions;
using WG.AP.Integrations.Pace;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Tests.Integrations.Pace;

public sealed class PaceBillBatchResolverTests
{
    private static readonly DateOnly BatchDate = new(2026, 7, 31);
    private const string BatchDescription = "07_31_2026_76274_APAutomation";

    [Fact]
    public async Task ResolveOrCreateAsync_WhenPeriodIsClosed_ReturnsPeriodClosed_AndNeverLooksUpBillBatch()
    {
        var billBatchLookupAttempted = false;
        var client = new FakeBillBatchClient
        {
            OnFind = (type, _) =>
            {
                if (type == "BillBatch")
                {
                    billBatchLookupAttempted = true;
                }

                return type switch
                {
                    "GLAccountingPeriod" => ["5201"],
                    _ => throw new InvalidOperationException(type)
                };
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "F" }
        };

        var resolver = new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance);

        var result = await resolver.ResolveOrCreateAsync(BatchDate, BatchDescription, CancellationToken.None);

        var closed = Assert.IsType<BillBatchResolution.PeriodClosed>(result);
        Assert.Equal(5201, closed.GlAccountingPeriodId);
        Assert.Equal("F", closed.GlPeriodStatus);
        Assert.False(billBatchLookupAttempted);
    }

    [Theory]
    [InlineData("T")]
    [InlineData("O")]
    public async Task ResolveOrCreateAsync_WhenExistingOpenBatchFound_ReturnsResolved_AndNeverCreatesABatch(string periodStatus)
    {
        var createCalled = false;
        var client = new FakeBillBatchClient
        {
            OnFind = (type, _) => type switch
            {
                "GLAccountingPeriod" => ["5201"],
                "BillBatch" => ["16311"],
                _ => throw new InvalidOperationException(type)
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = periodStatus },
            OnCreateBillBatch = _ =>
            {
                createCalled = true;
                throw new InvalidOperationException("CreateBillBatchAsync should not be called when an open batch already exists.");
            }
        };

        var resolver = new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance);

        var result = await resolver.ResolveOrCreateAsync(BatchDate, BatchDescription, CancellationToken.None);

        var resolved = Assert.IsType<BillBatchResolution.Resolved>(result);
        Assert.Equal(16311, resolved.BillBatchId);
        Assert.Equal(5201, resolved.GlAccountingPeriodId);
        Assert.False(createCalled);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_WhenNoExistingBatch_CreatesOne_WithExpectedFields()
    {
        BillBatch? createdRequest = null;
        var client = new FakeBillBatchClient
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

        var resolver = new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance);

        var result = await resolver.ResolveOrCreateAsync(BatchDate, BatchDescription, CancellationToken.None);

        var resolved = Assert.IsType<BillBatchResolution.Resolved>(result);
        Assert.Equal(16311, resolved.BillBatchId);
        Assert.Equal(5201, resolved.GlAccountingPeriodId);

        Assert.NotNull(createdRequest);
        Assert.Equal("2026-07-31", createdRequest!.Date);
        Assert.Equal(5201, createdRequest.GlAccountingPeriod);
        Assert.Equal(BatchDescription, createdRequest.Description);
        Assert.Equal(PaceBillBatchResolver.EnteredBy, createdRequest.EnteredBy);
        Assert.Equal("0", createdRequest.Status);
        Assert.Null(createdRequest.Manual);
        Assert.False(createdRequest.Approved);
        Assert.False(createdRequest.Posted);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_UsesUnquotedDateLiteral_NotAStringComparison()
    {
        string? billBatchXpath = null;
        var client = new FakeBillBatchClient
        {
            OnFind = (type, xpath) =>
            {
                if (type == "BillBatch")
                {
                    billBatchXpath = xpath;
                }

                return type switch
                {
                    "GLAccountingPeriod" => ["5201"],
                    "BillBatch" => ["16311"],
                    _ => throw new InvalidOperationException(type)
                };
            },
            OnReadGlAccountingPeriod = _ => new GLAccountingPeriod { Id = 5201, GlPeriodStatus = "O" }
        };

        var resolver = new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance);

        await resolver.ResolveOrCreateAsync(BatchDate, BatchDescription, CancellationToken.None);

        Assert.Equal("@date = date(2026,7,31) and @description = '07_31_2026_76274_APAutomation' and @posted = 'false'", billBatchXpath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ResolveOrCreateAsync_WhenGlAccountingPeriodMatchCountIsNotOne_ReturnsUnresolvable_AndNeverLooksUpBillBatch(int matchCount)
    {
        var billBatchLookupAttempted = false;
        var client = new FakeBillBatchClient
        {
            OnFind = (type, _) =>
            {
                if (type == "BillBatch")
                {
                    billBatchLookupAttempted = true;
                }

                return type switch
                {
                    "GLAccountingPeriod" => Enumerable.Range(1, matchCount).Select(i => i.ToString()).ToArray(),
                    _ => throw new InvalidOperationException(type)
                };
            }
        };

        var resolver = new PaceBillBatchResolver(client, NullLogger<PaceBillBatchResolver>.Instance);

        var result = await resolver.ResolveOrCreateAsync(BatchDate, BatchDescription, CancellationToken.None);

        Assert.IsType<BillBatchResolution.Unresolvable>(result);
        Assert.False(billBatchLookupAttempted);
    }

    private sealed class FakeBillBatchClient : PaceClient
    {
        public FakeBillBatchClient() : base(new HttpClient())
        {
        }

        public Func<string, string, ICollection<string>>? OnFind { get; set; }

        public Func<string, GLAccountingPeriod>? OnReadGlAccountingPeriod { get; set; }

        public Func<BillBatch?, BillBatch>? OnCreateBillBatch { get; set; }

        public override Task<ICollection<string>> FindAsync(string type, string xpath, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnFind is null ? throw new InvalidOperationException($"Unexpected FindAsync({type}).") : OnFind(type, xpath));

        public override Task<GLAccountingPeriod> ReadGLAccountingPeriodAsync(string primaryKey, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnReadGlAccountingPeriod is null ? throw new InvalidOperationException("Unexpected ReadGLAccountingPeriodAsync.") : OnReadGlAccountingPeriod(primaryKey));

        public override Task<BillBatch> CreateBillBatchAsync(BillBatch? billBatch = null, string? txnId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OnCreateBillBatch is null ? throw new InvalidOperationException("Unexpected CreateBillBatchAsync.") : OnCreateBillBatch(billBatch));
    }
}
