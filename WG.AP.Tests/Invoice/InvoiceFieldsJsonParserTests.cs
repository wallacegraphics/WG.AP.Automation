using WG.AP.Invoice.AI;

namespace WG.AP.Tests.Invoice;

public class InvoiceFieldsJsonParserTests
{
    [Fact]
    public void Parse_AllFieldsPresent_MapsEveryField()
    {
        const string json = """
            {
              "InvoiceNumber": "INV-162393962",
              "SalesOrder": "SO-163263179",
              "InvoiceDate": "7/21/2026",
              "DueDate": "9/19/2026",
              "Total": 438.90,
              "VendorName": "SanMar",
              "CustomerPO": "2702-2225 Cobb Nutri Team",
              "CustomerNumber": "76274-0000",
              "OrderAccount": "76274-0000",
              "Terms": "Net60"
            }
            """;

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Equal("INV-162393962", fields.InvoiceNumber);
        Assert.Equal("SO-163263179", fields.SalesOrder);
        Assert.Equal(new DateOnly(2026, 7, 21), fields.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 9, 19), fields.DueDate);
        Assert.Equal(438.90m, fields.Total);
        Assert.Equal("SanMar", fields.ClientName);
        Assert.Equal("2702-2225 Cobb Nutri Team", fields.CustomerPO);
        Assert.Equal("76274-0000", fields.CustomerNumber);
        Assert.Equal("76274-0000", fields.OrderAccount);
        Assert.Equal("Net60", fields.Terms);
        Assert.Equal(string.Empty, fields.RawText);
    }

    [Fact]
    public void Parse_MissingInvoiceNumber_Throws()
    {
        const string json = """{"InvoiceNumber": "", "Total": 0}""";

        Assert.Throws<InvalidOperationException>(() => InvoiceFieldsJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_AmountAsString_IsParsedNumerically()
    {
        const string json = """{"InvoiceNumber": "INV-1", "Total": "438.90"}""";

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Equal(438.90m, fields.Total);
    }

    [Theory]
    [InlineData("-39.11")]
    [InlineData("0")]
    public void Parse_NegativeOrZeroAmountAsString_ParsesThroughUnchanged(string totalText)
    {
        // A negative total (e.g. a credit memo) is valid data and must never be converted to a
        // positive/absolute value by this parser - it is a plain numeric parse, not a business rule.
        var json = $$"""{"InvoiceNumber": "INV-1", "Total": "{{totalText}}"}""";

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Equal(decimal.Parse(totalText), fields.Total);
    }

    [Fact]
    public void Parse_NegativeAmountAsNumber_ParsesThroughUnchanged()
    {
        const string json = """{"InvoiceNumber": "INV-1", "Total": -39.11}""";

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Equal(-39.11m, fields.Total);
    }

    [Fact]
    public void Parse_MissingOptionalFields_AreNull()
    {
        const string json = """{"InvoiceNumber": "INV-1", "Total": 100}""";

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Null(fields.SalesOrder);
        Assert.Null(fields.InvoiceDate);
        Assert.Null(fields.DueDate);
        Assert.Null(fields.ClientName);
        Assert.Null(fields.CustomerPO);
        Assert.Null(fields.CustomerNumber);
        Assert.Null(fields.OrderAccount);
        Assert.Null(fields.Terms);
    }

    [Fact]
    public void Parse_UnparseableDate_IsNullRatherThanThrowing()
    {
        const string json = """{"InvoiceNumber": "INV-1", "Total": 0, "DueDate": "not a date"}""";

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Null(fields.DueDate);
    }

    [Fact]
    public void Parse_CamelCaseKeys_StillMapsFields()
    {
        const string json = """{"invoiceNumber": "INV-1", "total": 100, "dueDate": "9/8/2026"}""";

        var fields = InvoiceFieldsJsonParser.Parse(json);

        Assert.Equal("INV-1", fields.InvoiceNumber);
        Assert.Equal(100m, fields.Total);
        Assert.Equal(new DateOnly(2026, 9, 8), fields.DueDate);
    }
}
