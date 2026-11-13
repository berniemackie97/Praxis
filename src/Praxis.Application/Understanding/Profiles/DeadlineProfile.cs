using System.Globalization;
using Praxis.Domain.Finance;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding.Profiles;

/// <summary>Due dates and obligations — bills, renewals, filings, deliverables — with the amount when one is stated.</summary>
public sealed class DeadlineProfile : IRecordProfile
{
    public string Kind => RecordKinds.Deadline;

    public string PromptFields => "deadline: title, due (YYYY-MM-DD), optional amount (number) and currency (ISO 4217)";

    public IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string> fields, string evidence)
    {
        string? title = RecordFields.Text(fields, "title");
        string? due = RecordFields.VerifiedMoment(RecordFields.Text(fields, "due"), evidence);
        if (title is null || due is null)
        {
            return null;
        }

        Dictionary<string, string> normalized = new(StringComparer.Ordinal) { ["title"] = title, ["due"] = due[..10] };
        if (RecordFields.Text(fields, "amount") is { } amountText &&
            decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount))
        {
            string currency = (RecordFields.Text(fields, "currency") ?? "USD").ToUpperInvariant();
            if (!PrintedValues.HasAmount(evidence, amount) || !MonetaryAmount.TryFromDecimal(amount, currency, out MonetaryAmount exact))
            {
                return null;
            }

            normalized["amount_minor"] = exact.Minor.ToString(CultureInfo.InvariantCulture);
            normalized["currency"] = currency;
        }

        return normalized;
    }

    public string? SortKey(IReadOnlyDictionary<string, string> normalized) => normalized.GetValueOrDefault("due");
}
