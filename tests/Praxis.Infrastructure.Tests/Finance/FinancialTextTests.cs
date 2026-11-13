using System.Text;
using Praxis.Application.Finance;
using Praxis.Domain.Finance;
using Xunit;

namespace Praxis.Infrastructure.Tests.Finance;

public sealed class FinancialTextTests
{
    [Theory]
    [InlineData("$1,234.56", 1234.56, MoneyMarker.None)]
    [InlineData("-1,234.56", -1234.56, MoneyMarker.None)]
    [InlineData("(42.10)", -42.10, MoneyMarker.None)]
    [InlineData("42.10-", -42.10, MoneyMarker.None)]
    [InlineData("12.00 CR", 12.00, MoneyMarker.Credit)]
    [InlineData("12.00DR", 12.00, MoneyMarker.Debit)]
    [InlineData("1.234,56", 1234.56, MoneyMarker.None)]
    [InlineData("12,50", 12.50, MoneyMarker.None)]
    [InlineData("1,234", 1234, MoneyMarker.None)]
    [InlineData("USD 9.99", 9.99, MoneyMarker.None)]
    [InlineData("- $5.00", -5.00, MoneyMarker.None)]
    public void PrintedMoneyParsesExactly(string text, double expected, MoneyMarker marker)
    {
        Assert.True(MoneyText.TryParse(text, out decimal value, out MoneyMarker parsedMarker));
        Assert.Equal((decimal)expected, value);
        Assert.Equal(marker, parsedMarker);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("10/03/2026")]
    public void NonMoneyIsRejected(string text) => Assert.False(MoneyText.TryParse(text, out _, out _));

    [Fact]
    public void MonetaryAmountsKeepMinorUnitsAndRejectHiddenPrecision()
    {
        Assert.True(MonetaryAmount.TryFromDecimal(-12.3400m, "USD", out MonetaryAmount usd));
        Assert.Equal(-1234, usd.Minor);
        Assert.True(MonetaryAmount.TryFromDecimal(1500m, "JPY", out MonetaryAmount yen));
        Assert.Equal(1500, yen.Minor);
        Assert.True(MonetaryAmount.TryFromDecimal(1.234m, "KWD", out MonetaryAmount dinar));
        Assert.Equal(1234, dinar.Minor);
        Assert.False(MonetaryAmount.TryFromDecimal(1.005m, "USD", out _));
        Assert.False(MonetaryAmount.TryFromDecimal(1.5m, "JPY", out _));
        Assert.False(MonetaryAmount.TryFromDecimal(1m, "usd", out _));
        Assert.Equal(-12.34m, usd.ToDecimal());
    }

    [Theory]
    [InlineData("11/03/2026", DateOrder.MonthFirst, 2026, 11, 3)]
    [InlineData("03/11/2026", DateOrder.DayFirst, 2026, 11, 3)]
    [InlineData("2026-11-03", DateOrder.MonthFirst, 2026, 11, 3)]
    [InlineData("20261103", DateOrder.DayFirst, 2026, 11, 3)]
    [InlineData("11/ 3'26", DateOrder.MonthFirst, 2026, 11, 3)]
    [InlineData("Nov 3, 2026", DateOrder.MonthFirst, 2026, 11, 3)]
    [InlineData("3 November 2026", DateOrder.MonthFirst, 2026, 11, 3)]
    [InlineData("1/2/99", DateOrder.MonthFirst, 1999, 1, 2)]
    public void PrintedDatesParse(string text, DateOrder order, int year, int month, int day)
    {
        Assert.True(DateText.TryParse(text, order, null, out DateOnly date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Fact]
    public void YearlessDatesNeedAnExplicitYear()
    {
        Assert.False(DateText.TryParse("10/31", DateOrder.MonthFirst, null, out _));
        Assert.True(DateText.TryParse("10/31", DateOrder.MonthFirst, 2026, out DateOnly date));
        Assert.Equal(new DateOnly(2026, 10, 31), date);
        Assert.False(DateText.TryParse("02/30/2026", DateOrder.MonthFirst, null, out _));
    }

    [Fact]
    public void DateOrderComesFromEvidenceAndAmbiguityIsReported()
    {
        Assert.Equal(new DateOrderInference.Result(DateOrder.DayFirst, false),
            DateOrderInference.Infer(["01/02/2026", "25/02/2026"]));
        // Both orders read every value, but only day-first keeps the rows chronological.
        Assert.Equal(new DateOrderInference.Result(DateOrder.DayFirst, false),
            DateOrderInference.Infer(["01/02/2026", "05/02/2026", "03/03/2026"]));
        Assert.Equal(new DateOrderInference.Result(DateOrder.MonthFirst, true),
            DateOrderInference.Infer(["01/02/2026", "01/02/2026"]));
        Assert.Null(DateOrderInference.Infer(["not a date"]));
    }

    [Fact]
    public void ExportsDecodeAsUtf8OrWindows1252AndBinaryIsRefused()
    {
        Assert.Equal("Café", FinancialTextDecoder.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Café")]));
        Assert.Equal("Café", FinancialTextDecoder.Decode([0x43, 0x61, 0x66, 0xE9]));
        Assert.Equal("Caf€", FinancialTextDecoder.Decode([0x43, 0x61, 0x66, 0x80]));
        Assert.Null(FinancialTextDecoder.Decode([0x41, 0x00, 0x42]));
    }

    [Theory]
    [InlineData("Account ending in 4321", "4321")]
    [InlineData("Card number XXXX XXXX XXXX 9876", "9876")]
    [InlineData("Account Number: 000123456781234", "1234")]
    [InlineData("****5555 Visa", "5555")]
    public void MaskedAccountDigitsAreFound(string text, string expected) =>
        Assert.Equal(expected, AccountIdentity.FromText(text));

    [Theory]
    [InlineData("Chase1234_Activity_20261103.CSV", "1234")]
    [InlineData("statement-2026-10.pdf", null)]
    [InlineData("export_ending_7788.csv", "7788")]
    public void FileNamesYieldAccountDigitsButNotDates(string name, string? expected) =>
        Assert.Equal(expected, AccountIdentity.FromFileName(name));
}
