using Praxis.Application.Finance;
using Praxis.Application.Finance.Delimited;
using Praxis.Application.Finance.Ofx;
using Praxis.Application.Finance.Qif;
using Praxis.Domain.Finance;
using Xunit;

namespace Praxis.Infrastructure.Tests.Finance;

public sealed class StructuredExportParserTests
{
    private static readonly FinancialInput Input = new("download.qfx", "src-fallback");

    public const string SgmlOfx = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        CHARSET:1252

        <OFX>
        <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20261105120000<LANGUAGE>ENG
        <FI><ORG>Example Bank<FID>1001</FI></SONRS></SIGNONMSGSRSV1>
        <BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <STMTRS><CURDEF>USD
        <BANKACCTFROM><BANKID>121000248<ACCTID>000123456781234<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20261001<DTEND>20261031
        <STMTTRN><TRNTYPE>DIRECTDEP<DTPOSTED>20261001120000.000[-5:EST]<TRNAMT>2500.00<FITID>F1<NAME>ACME CORP PAYROLL</STMTTRN>
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261003<TRNAMT>-54.21<FITID>F2<NAME>SAFEWAY #1234<MEMO>GROCERIES</STMTTRN>
        <STMTTRN><TRNTYPE>CHECK<DTPOSTED>20261010<TRNAMT>-1200.00<FITID>F3<CHECKNUM>1042<NAME>Check 1042</STMTTRN>
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261012<TRNAMT>-15.99<FITID>F4<NAME>NETFLIX.COM &amp; CO</STMTTRN>
        </BANKTRANLIST>
        <LEDGERBAL><BALAMT>1229.80<DTASOF>20261031</LEDGERBAL>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1>
        </OFX>
        """;

    public const string XmlCardOfx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?OFX OFXHEADER="200" VERSION="220" SECURITY="NONE"?>
        <OFX>
          <CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS>
            <CURDEF>USD</CURDEF>
            <CCACCTFROM><ACCTID>4111111111119876</ACCTID></CCACCTFROM>
            <BANKTRANLIST>
              <DTSTART>20261001</DTSTART><DTEND>20261031</DTEND>
              <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261004</DTPOSTED><TRNAMT>-32.50</TRNAMT><FITID>C1</FITID><NAME>CHIPOTLE 0921</NAME></STMTTRN>
              <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20261020</DTPOSTED><TRNAMT>500.00</TRNAMT><FITID>C2</FITID><NAME>PAYMENT THANK YOU</NAME></STMTTRN>
            </BANKTRANLIST>
            <LEDGERBAL><BALAMT>-210.40</BALAMT><DTASOF>20261031</DTASOF></LEDGERBAL>
          </CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public void SgmlOfxYieldsSignedRowsProviderIdsAndMaskedAccount()
    {
        OfxParser parser = new();
        Assert.True(parser.CanParse(SgmlOfx));
        FinancialDocument document = Assert.IsType<FinancialDocument>(parser.Parse(SgmlOfx, Input).Document);
        FinancialAccount account = Assert.Single(document.Accounts);
        Assert.Equal("acct-1234", account.AccountKey);
        Assert.Equal("…1234", account.Mask);
        Assert.Equal(AccountKinds.Checking, account.Kind);
        Assert.Equal("Example Bank", account.Institution);
        Assert.DoesNotContain(document.Accounts, value => value.DisplayName.Contains("0001234567", StringComparison.Ordinal));
        Assert.Equal(4, document.Transactions.Count);
        Assert.Equal([250000L, -5421L, -120000L, -1599L], document.Transactions.Select(row => row.Amount.Minor));
        Assert.Equal("SAFEWAY #1234 GROCERIES", document.Transactions[1].Description);
        Assert.Equal("NETFLIX.COM & CO", document.Transactions[3].Description);
        Assert.Equal("1042", document.Transactions[2].CheckNumber);
        Assert.Equal("F2", document.Transactions[1].ProviderId);
        Assert.Equal(new DateOnly(2026, 10, 1), document.Transactions[0].PostedOn);
        Assert.Equal("stmttrn:2", document.Transactions[1].Locator);
        ParsedStatement statement = Assert.Single(document.Statements);
        Assert.Equal(StatementVerifications.ProviderExport, statement.Verification);
        Assert.Equal(122980L, statement.ClosingBalance?.Minor);
        Assert.Equal(new DateOnly(2026, 10, 31), statement.PeriodEnd);
    }

    [Fact]
    public void XmlCardOfxIsACardAccountWithOwnerSignedAmounts()
    {
        FinancialDocument document = Assert.IsType<FinancialDocument>(new OfxParser().Parse(XmlCardOfx, Input).Document);
        Assert.Equal(AccountKinds.CreditCard, Assert.Single(document.Accounts).Kind);
        Assert.Equal("acct-9876", document.Accounts[0].AccountKey);
        Assert.Equal([-3250L, 50000L], document.Transactions.Select(row => row.Amount.Minor));
    }

    [Fact]
    public void MalformedOrInvestmentOfxIsRejectedWithAReason()
    {
        Assert.Equal("ofx_malformed", new OfxParser().Parse("OFXHEADER:100\n<OFX><STMTRS", Input).ReasonCode);
        Assert.Equal("investment_statement_unsupported",
            new OfxParser().Parse("<OFX><INVSTMTMSGSRSV1><INVSTMTTRNRS><INVSTMTRS><CURDEF>USD</INVSTMTRS></INVSTMTTRNRS></INVSTMTMSGSRSV1></OFX>", Input).ReasonCode);
    }

    [Fact]
    public void QifCardListInfersDateOrderAndKeepsCategories()
    {
        const string qif = """
            !Account
            NVisa ending 5555
            TCCard
            ^
            !Type:CCard
            D25/10'26
            T-42.10
            PTRADER JOE'S #552
            LGroceries
            ^
            D28/10'26
            T200.00
            PPAYMENT - THANK YOU
            L[Checking]
            ^
            """;
        QifParser parser = new();
        Assert.True(parser.CanParse(qif));
        FinancialDocument document = Assert.IsType<FinancialDocument>(parser.Parse(qif, Input).Document);
        Assert.Equal("acct-5555", Assert.Single(document.Accounts).AccountKey);
        Assert.Equal(AccountKinds.CreditCard, document.Accounts[0].Kind);
        Assert.Equal(new DateOnly(2026, 10, 25), document.Transactions[0].PostedOn);
        Assert.Equal("Groceries", document.Transactions[0].ProviderCategory);
        Assert.Equal("transfer", document.Transactions[1].ProviderType);
        Assert.Equal([-4210L, 20000L], document.Transactions.Select(row => row.Amount.Minor));
        Assert.Equal("line:6", document.Transactions[0].Locator);
    }

    [Fact]
    public void ModelMappingIsAppliedToEveryRowAndProvenByTheRunningBalance()
    {
        const string csv = "Account Name : Everyday Checking\r\nAccount Number : XXXXXX4321\r\n\r\n" +
                           "Date;Details;Debit;Credit;Balance\r\n" +
                           "02/10/2026;\"Rent; October\";1.250,00;;750,00\r\n" +
                           "05/10/2026;Salary ACME;;2.000,00;2.750,00\r\n" +
                           "28/10/2026;\"Coffee \"\"Bean\"\"\";4,50;;2.745,50\r\n";
        IReadOnlyList<DelimitedRow> rows = Rows(csv);
        DelimitedMapping mapping = Assert.IsType<DelimitedMapping>(DelimitedMappingReader.Read("""
            {"isFinancial":true,"headerRow":2,"dateColumn":0,"dateOrder":"day_first","descriptionColumns":[1],"amountColumn":null,
             "debitColumn":2,"creditColumn":3,"amountSign":"as_is","balanceColumn":4,"statusColumn":null,"accountColumn":null,
             "currency":"EUR","accountLastFour":"4321","accountKind":"checking"}
            """, 5, rows.Count));
        FinancialDocument document = Assert.IsType<FinancialDocument>(DelimitedMappingApplier.Apply(mapping, rows, Input, requireProof: true));
        Assert.Equal("acct-4321", document.Accounts[0].AccountKey);
        Assert.Equal("EUR", document.Accounts[0].Currency);
        Assert.Equal([-125000L, 200000L, -450L], document.Transactions.Select(row => row.Amount.Minor));
        Assert.Equal("Coffee \"Bean\"", document.Transactions[2].Description);
        Assert.Equal(StatementVerifications.RunningBalance, document.Statements[0].Verification);
        Assert.Equal("row:5", document.Transactions[0].Locator);
    }

    [Fact]
    public void AWrongSignInTheMappingFailsTheRunningBalanceProof()
    {
        const string csv = """
            Date,Description,Amount,Balance
            11/01/2026,COFFEE,5.00,95.00
            11/02/2026,REFUND FROM SHOP,-10.00,105.00
            11/03/2026,GROCERY,20.00,85.00
            """;
        IReadOnlyList<DelimitedRow> rows = Rows(csv);
        string Mapping(string sign) => $$"""
            {"isFinancial":true,"headerRow":0,"dateColumn":0,"dateOrder":"month_first","descriptionColumns":[1],"amountColumn":2,
             "amountSign":"{{sign}}","balanceColumn":3,"currency":"USD","accountKind":"checking"}
            """;
        Assert.Null(DelimitedMappingApplier.Apply(DelimitedMappingReader.Read(Mapping("as_is"), 4, rows.Count)!, rows, Input, requireProof: false));
        FinancialDocument inverted = Assert.IsType<FinancialDocument>(
            DelimitedMappingApplier.Apply(DelimitedMappingReader.Read(Mapping("inverted"), 4, rows.Count)!, rows, Input, requireProof: false));
        Assert.Equal([-500L, 1000L, -2000L], inverted.Transactions.Select(row => row.Amount.Minor));
    }

    [Fact]
    public void UnprovenMappingsAreAcceptedFromStrongTiersButNotFromTheLocalTier()
    {
        const string csv = """
            Transaction Date,Description,Amount,Status
            10/03/2026,STARBUCKS STORE 01234,-6.45,Posted
            10/09/2026,Payment Thank You-Mobile,250.00,Pending
            """;
        IReadOnlyList<DelimitedRow> rows = Rows(csv);
        DelimitedMapping mapping = DelimitedMappingReader.Read("""
            {"isFinancial":true,"headerRow":0,"dateColumn":0,"dateOrder":"month_first","descriptionColumns":[1],"amountColumn":2,
             "amountSign":"as_is","statusColumn":3,"currency":"USD","accountKind":"credit_card"}
            """, 4, rows.Count)!;
        Assert.Null(DelimitedMappingApplier.Apply(mapping, rows, new FinancialInput("Chase9988_Activity.CSV", "src-x"), requireProof: true));
        FinancialDocument document = Assert.IsType<FinancialDocument>(
            DelimitedMappingApplier.Apply(mapping, rows, new FinancialInput("Chase9988_Activity.CSV", "src-x"), requireProof: false));
        Assert.Equal(StatementVerifications.ProviderExport, document.Statements[0].Verification);
        Assert.Equal("acct-9988", document.Accounts[0].AccountKey);
        Assert.Equal(TransactionStatuses.Pending, document.Transactions[1].Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"isFinancial\":true,\"headerRow\":0,\"dateColumn\":9,\"dateOrder\":\"month_first\",\"descriptionColumns\":[],\"amountColumn\":1,\"amountSign\":\"as_is\",\"currency\":\"USD\",\"accountKind\":\"checking\"}")]
    [InlineData("{\"isFinancial\":true,\"headerRow\":0,\"dateColumn\":0,\"dateOrder\":\"month_first\",\"descriptionColumns\":[],\"amountColumn\":1,\"debitColumn\":2,\"amountSign\":\"as_is\",\"currency\":\"USD\",\"accountKind\":\"checking\"}")]
    [InlineData("{\"isFinancial\":true,\"headerRow\":0,\"dateColumn\":0,\"dateOrder\":\"sideways\",\"descriptionColumns\":[],\"amountColumn\":1,\"amountSign\":\"as_is\",\"currency\":\"USD\",\"accountKind\":\"checking\"}")]
    [InlineData("{\"isFinancial\":true,\"headerRow\":0,\"dateColumn\":0,\"dateOrder\":\"month_first\",\"descriptionColumns\":[],\"amountColumn\":1,\"amountSign\":\"as_is\",\"currency\":\"dollars\",\"accountKind\":\"checking\"}")]
    public void InvalidMappingsAreRejected(string content) => Assert.Null(DelimitedMappingReader.Read(content, 3, 10));

    [Fact]
    public void AMappingThatParsesTooFewRowsIsRejected()
    {
        IReadOnlyList<DelimitedRow> rows = Rows("Date,Amount\n10/01/2026,5.00\nnot,a row\nalso,bad\n");
        DelimitedMapping mapping = DelimitedMappingReader.Read("""
            {"isFinancial":true,"headerRow":0,"dateColumn":0,"dateOrder":"month_first","descriptionColumns":[],"amountColumn":1,
             "amountSign":"as_is","currency":"USD","accountKind":"checking"}
            """, 2, rows.Count)!;
        Assert.Null(DelimitedMappingApplier.Apply(mapping, rows, Input, requireProof: false));
    }

    [Fact]
    public void ANonFinancialVerdictIsCarriedThrough() =>
        Assert.False(DelimitedMappingReader.Read("{\"isFinancial\":false}", 2, 3)!.IsFinancial);

    private static IReadOnlyList<DelimitedRow> Rows(string text) =>
        DelimitedReader.Read(text, DelimitedReader.DetectDelimiter(text)!.Value, 1_000)!;
}
