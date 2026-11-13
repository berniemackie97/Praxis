using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Praxis.Infrastructure.Tests.Finance;

/// <summary>Synthetic, non-private statements in three issuer styles, rendered as real PDFs.</summary>
internal static class StatementFixtures
{
    internal static readonly string[] SectionedChecking =
    [
        "Example Credit Union",
        "Account Statement",
        "Account Number: XXXXXX4321",
        "Statement Period: 10/01/2026 - 10/31/2026",
        "Beginning Balance $1,000.00",
        "Ending Balance $2,175.55",
        "DEPOSITS AND ADDITIONS",
        "10/01 ACME CORP PAYROLL 2,500.00",
        "10/15 MOBILE DEPOSIT 100.00",
        "WITHDRAWALS AND SUBTRACTIONS",
        "10/03 SAFEWAY #1234 SEATTLE WA 54.21",
        "10/05 RENT PAYMENT OCTOBER 1,250.00",
        "Unit 4B lease",
        "10/12 NETFLIX.COM 15.99",
        "10/20 SHELL OIL 57444 104.25",
        "Total withdrawals 1,424.45"
    ];

    internal static readonly string[] RunningBalanceSavings =
    [
        "Statement of Account",
        "Account ending in 7788",
        "For the period November 1, 2026 to November 30, 2026",
        "Opening balance 500.00",
        "Date Description Amount Balance",
        "Nov 2 COFFEE BEAN 4.50 495.50",
        "Nov 5 PAYROLL ACME 1,000.00 1,495.50",
        "Nov 9 GROCERY OUTLET 45.50 1,450.00",
        "Closing balance 1,450.00"
    ];

    internal static readonly string[] CreditCard =
    [
        "Example Card Services",
        "Credit Card Statement",
        "Account number ending in 9876",
        "Opening/Closing Date 10/02/26 - 11/01/26",
        "Previous Balance $1,200.00",
        "New Balance $345.10",
        "Minimum Payment Due $35.00",
        "Payment Due Date 11/26/26",
        "PAYMENTS AND OTHER CREDITS",
        "10/15 PAYMENT THANK YOU -1,000.00",
        "10/18 AMAZON RETURN -25.00",
        "PURCHASES",
        "10/03 CHIPOTLE 0921 32.50",
        "10/07 UBER TRIP 18.40",
        "10/21 WHOLE FOODS MARKET 96.12",
        "10/28 SPOTIFY USA 11.99",
        "FEES CHARGED",
        "10/30 LATE FEE 11.09"
    ];

    /// <summary>A statement whose rows carry no direction a parser can establish.</summary>
    internal static readonly string[] Ambiguous =
    [
        "Statement",
        "Account ending 3333",
        "Statement Period: 09/01/2026 - 09/30/2026",
        "Beginning Balance 100.00",
        "Ending Balance 80.00",
        "09/05 BOOKSTORE 30.00",
        "09/10 SALE PROCEEDS 10.00"
    ];

    internal static byte[] Pdf(IReadOnlyList<string> lines, int linesPerPage = 40)
    {
        PdfDocumentBuilder builder = new();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (int start = 0; start < Math.Max(1, lines.Count); start += linesPerPage)
        {
            PdfPageBuilder page = builder.AddPage(PageSize.Letter);
            double y = 740;
            foreach (string line in lines.Skip(start).Take(linesPerPage))
            {
                page.AddText(line, 10, new PdfPoint(50, y), font);
                y -= 16;
            }
        }

        return builder.Build();
    }
}
