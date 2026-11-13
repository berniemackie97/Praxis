using System.Text.RegularExpressions;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding.Profiles;

/// <summary>People and organizations with their role and contact points, each value printed in the evidence.</summary>
public sealed partial class ContactProfile : IRecordProfile
{
    public string Kind => RecordKinds.Contact;

    public string PromptFields => "contact: name, optional role, organization, email, phone";

    public IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string> fields, string evidence)
    {
        string? name = RecordFields.Text(fields, "name");
        if (name is null || !EvidenceLocator.Mentions(evidence, name))
        {
            return null;
        }

        Dictionary<string, string> normalized = new(StringComparer.Ordinal) { ["name"] = name };
        foreach (string field in new[] { "role", "organization" })
        {
            if (RecordFields.Text(fields, field) is { } value && EvidenceLocator.Mentions(evidence, value))
            {
                normalized[field] = value;
            }
        }

        if (RecordFields.Text(fields, "email") is { } email && Email().IsMatch(email) && evidence.Contains(email, StringComparison.OrdinalIgnoreCase))
        {
            normalized["email"] = email.ToLowerInvariant();
        }

        if (RecordFields.Text(fields, "phone") is { } phone && Digits(phone) is { Length: >= 7 } digits && Digits(evidence).Contains(digits, StringComparison.Ordinal))
        {
            normalized["phone"] = phone;
        }

        return normalized.Count > 1 ? normalized : null;
    }

    public string? SortKey(IReadOnlyDictionary<string, string> normalized) => null;

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,190}\.[^@\s]{2,}$", RegexOptions.None, 250)]
    private static partial Regex Email();
}
