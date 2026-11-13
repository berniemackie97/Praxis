using System.Text;
using Praxis.Application.Documents.Pdf;
using Praxis.Application.Finance;
using Praxis.Application.Finance.Statements;
using Praxis.Domain.Finance;
using Praxis.Infrastructure.Documents;
using Xunit;

namespace Praxis.Infrastructure.Tests.Finance;

public sealed class PdfStatementTests
{
    private static readonly FinancialInput Input = new("statement.pdf", "src-fallback");
    private readonly PdfPigTextReader reader = new();

    [Fact]
    public void PdfTextIsReconstructedIntoOrderedLinesWithPageLocators()
    {
        PdfTextResult result = reader.Read(StatementFixtures.Pdf(StatementFixtures.SectionedChecking, linesPerPage: 9), 10);
        IReadOnlyList<PdfTextLine> lines = Assert.IsAssignableFrom<IReadOnlyList<PdfTextLine>>(result.Lines);
        Assert.Equal("Example Credit Union", lines[0].Text);
        Assert.Equal("10/01 ACME CORP PAYROLL 2,500.00", lines[7].Text);
        Assert.Equal(2, lines[^1].Page);
        Assert.Equal(StatementFixtures.SectionedChecking.Length, lines.Count);
    }

    [Fact]
    public void UnreadableEmptyAndOversizedPdfsFailWithReasons()
    {
        Assert.Contains(reader.Read(Encoding.ASCII.GetBytes("%PDF-1.7\nnot really a pdf"), 10).ReasonCode, new[] { "pdf_malformed", "pdf_no_text" });
        Assert.Equal("pdf_no_text", reader.Read(StatementFixtures.Pdf([]), 10).ReasonCode);
        Assert.Equal("pdf_too_many_pages", reader.Read(StatementFixtures.Pdf(StatementFixtures.SectionedChecking, linesPerPage: 2), 3).ReasonCode);
    }

    [Fact]
    public void TranscriptionIsAcceptedOnlyWhenPrintedAndReconciled()
    {
        string text = string.Join('\n', StatementFixtures.Ambiguous);
        const string good = """
            {"isStatement":true,"accountKind":"deposit","accountLastFour":"3333","openingBalance":"100.00","closingBalance":"80.00","transactions":[
              {"date":"2026-09-05","description":"BOOKSTORE","amount":"-30.00"},
              {"date":"2026-09-10","description":"SALE PROCEEDS","amount":"10.00"}]}
            """;
        FinancialDocument accepted = Assert.IsType<FinancialDocument>(ModelStatementReader.Read(good, text, Input, StatementExtractionPrompt.Version)?.Document);
        Assert.Equal(StatementVerifications.Balanced, accepted.Statements[0].Verification);
        Assert.Equal(new DateOnly(2026, 9, 1), accepted.Statements[0].PeriodStart);
        Assert.All(accepted.Transactions, row => Assert.Equal(TransactionOrigins.Model, row.Origin));
        Assert.Equal("acct-3333", accepted.Accounts[0].AccountKey);

        string unbalanced = good.Replace("\"10.00\"", "\"-10.00\"", StringComparison.Ordinal);
        string invented = good.Replace("\"-30.00\"", "\"-31.00\"", StringComparison.Ordinal).Replace("\"80.00\"", "\"79.00\"", StringComparison.Ordinal);
        string outsidePeriod = good.Replace("2026-09-05", "2025-01-05", StringComparison.Ordinal);
        foreach (string response in new[] { unbalanced, invented, outsidePeriod, "not json", "{}" })
        {
            Assert.Null(ModelStatementReader.Read(response, text, Input, StatementExtractionPrompt.Version));
        }
    }

    [Fact]
    public void CardTranscriptionTreatsBalancesAsOwed()
    {
        string text = string.Join('\n', StatementFixtures.CreditCard);
        const string card = """
            {"isStatement":true,"accountKind":"card","accountLastFour":"9876","openingBalance":"1,200.00","closingBalance":"345.10","transactions":[
              {"date":"2026-10-15","description":"PAYMENT THANK YOU","amount":"1,000.00"},
              {"date":"2026-10-18","description":"AMAZON RETURN","amount":"25.00"},
              {"date":"2026-10-03","description":"CHIPOTLE 0921","amount":"-32.50"},
              {"date":"2026-10-07","description":"UBER TRIP","amount":"-18.40"},
              {"date":"2026-10-21","description":"WHOLE FOODS MARKET","amount":"-96.12"},
              {"date":"2026-10-28","description":"SPOTIFY USA","amount":"-11.99"},
              {"date":"2026-10-30","description":"LATE FEE","amount":"-11.09"}]}
            """;
        FinancialDocument document = Assert.IsType<FinancialDocument>(ModelStatementReader.Read(card, text, Input, StatementExtractionPrompt.Version)?.Document);
        Assert.Equal(AccountKinds.CreditCard, document.Accounts[0].Kind);
        Assert.Equal(-120000L, document.Statements[0].OpeningBalance?.Minor);
        Assert.Equal(-34510L, document.Statements[0].ClosingBalance?.Minor);
    }

    [Fact]
    public void ANotAStatementVerdictIsReported() =>
        Assert.True(ModelStatementReader.Read("""{"isStatement":false,"accountKind":"deposit","openingBalance":"","closingBalance":"","transactions":[]}""",
            "Meeting notes", Input, StatementExtractionPrompt.Version)?.NotFinancial);

    [Fact]
    public void BalanceProofUsesOwnerEquitySignForEveryAccountKind()
    {
        MonetaryAmount Usd(long minor) => new(minor, "USD");
        Assert.Equal(StatementVerifications.Balanced,
            StatementBalanceProof.Verify([Usd(-500), Usd(1000)], Usd(-1000), Usd(-500), []).Verification);
        Assert.Equal("balance_mismatch", StatementBalanceProof.Verify([Usd(-500)], Usd(1000), Usd(600), []).Note);
        Assert.Equal(StatementVerifications.RunningBalance,
            StatementBalanceProof.Verify([Usd(-450), Usd(100000)], Usd(50000), null, [Usd(49550), Usd(149550)]).Verification);
        Assert.Equal("mixed_currency", StatementBalanceProof.Verify([new MonetaryAmount(1, "EUR")], Usd(0), Usd(1), []).Note);
    }
}
