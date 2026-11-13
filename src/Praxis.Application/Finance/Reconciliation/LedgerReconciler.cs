using System.Globalization;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Reconciliation;

/// <summary>
/// Merges rows from overlapping documents into one transaction each. Rows meet when they share
/// a provider transaction ID, or the same account, date, exact amount and occurrence number
/// (the nth identical charge that day in each document — so two genuine same-day charges stay
/// two). A remaining row may match a row from another document of the same account and amount
/// within three days when their merchant wording overlaps or one is pending (posting dates
/// often differ from transaction dates by a day or two). Rows of one document never merge with
/// each other except as exact provider-ID duplicates.
/// </summary>
public static class LedgerReconciler
{
    private const int MatchWindowDays = 3;
    private const int PendingWindowDays = 7;

    public static IReadOnlyList<IReadOnlyList<StoredTransaction>> Reconcile(IReadOnlyList<StoredTransaction> rows)
    {
        DisjointSet groups = new(rows.Select(row => (row.Evidence.ItemId, row.Evidence.Revision)).ToArray());
        string[] fingerprints = Fingerprints(rows);
        Dictionary<string, int> byFingerprint = new(StringComparer.Ordinal);
        Dictionary<string, int> byProviderId = new(StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            if (byFingerprint.TryGetValue(fingerprints[index], out int first))
            {
                groups.Union(first, index);
            }
            else
            {
                byFingerprint[fingerprints[index]] = index;
            }

            if (rows[index].Row.ProviderId is { Length: > 0 } providerId)
            {
                string key = rows[index].Row.AccountKey + "|" + providerId;
                if (byProviderId.TryGetValue(key, out int prior))
                {
                    groups.Union(prior, index);
                }
                else
                {
                    byProviderId[key] = index;
                }
            }
        }

        MatchNearby(rows, groups);
        return groups.Groups()
            .Select(members => (IReadOnlyList<StoredTransaction>)members.Select(index => rows[index]).OrderBy(Preference).ToArray())
            .OrderBy(group => group[0].Row.PostedOn).ThenBy(group => group[0].Row.AccountKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A stable identifier for a reconciled group, independent of row order.</summary>
    public static string TransactionId(IReadOnlyList<StoredTransaction> group)
    {
        string anchor = group.Select(row => $"{row.Row.AccountKey}|{row.Row.ProviderId ?? ""}|{row.Row.PostedOn:yyyy-MM-dd}|{row.Row.Amount.Minor}|{row.Evidence.ItemId:N}|{row.Row.Ordinal}")
            .Min(StringComparer.Ordinal)!;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(anchor));
        return "txn-" + Convert.ToHexStringLower(hash)[..20];
    }

    private static string[] Fingerprints(IReadOnlyList<StoredTransaction> rows)
    {
        string[] fingerprints = new string[rows.Count];
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        foreach ((StoredTransaction row, int index) in rows.Select((row, index) => (row, index))
                     .OrderBy(pair => pair.row.Evidence.ItemId).ThenBy(pair => pair.row.Evidence.Revision).ThenBy(pair => pair.row.Row.Ordinal))
        {
            string basis = string.Create(CultureInfo.InvariantCulture,
                $"{row.Row.AccountKey}|{row.Row.PostedOn:yyyy-MM-dd}|{row.Row.Amount.Minor}|{row.Row.Amount.Currency}");
            string documentKey = $"{row.Evidence.ItemId:N}:{row.Evidence.Revision}|{basis}";
            int occurrence = occurrences.GetValueOrDefault(documentKey) + 1;
            occurrences[documentKey] = occurrence;
            fingerprints[index] = basis + "|" + occurrence.ToString(CultureInfo.InvariantCulture);
        }

        return fingerprints;
    }

    private static void MatchNearby(IReadOnlyList<StoredTransaction> rows, DisjointSet groups)
    {
        foreach (IGrouping<string, int> bucket in Enumerable.Range(0, rows.Count)
                     .GroupBy(index => $"{rows[index].Row.AccountKey}|{rows[index].Row.Amount.Minor}|{rows[index].Row.Amount.Currency}"))
        {
            int[] members = bucket.OrderBy(index => rows[index].Row.PostedOn).ToArray();
            if (members.Length < 2)
            {
                continue;
            }

            for (int left = 0; left < members.Length; left++)
            {
                for (int right = left + 1; right < members.Length; right++)
                {
                    StoredTransaction a = rows[members[left]];
                    StoredTransaction b = rows[members[right]];
                    int days = b.Row.PostedOn.DayNumber - a.Row.PostedOn.DayNumber;
                    bool pending = a.Row.Status == TransactionStatuses.Pending || b.Row.Status == TransactionStatuses.Pending;
                    if (days > (pending ? PendingWindowDays : MatchWindowDays))
                    {
                        break;
                    }

                    if (groups.Find(members[left]) == groups.Find(members[right]) ||
                        SharesDocument(groups, rows, members[left], members[right]))
                    {
                        continue;
                    }

                    if (pending || WordingOverlaps(a.Row.Description, b.Row.Description))
                    {
                        groups.Union(members[left], members[right]);
                    }
                }
            }
        }
    }

    private static bool SharesDocument(DisjointSet groups, IReadOnlyList<StoredTransaction> rows, int left, int right) =>
        groups.DocumentsOf(left).Overlaps(groups.DocumentsOf(right));

    private static bool WordingOverlaps(string left, string right)
    {
        HashSet<string> a = Tokens(left);
        HashSet<string> b = Tokens(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        int shared = a.Count(b.Contains);
        return shared * 3 >= Math.Min(a.Count, b.Count);
    }

    private static HashSet<string> Tokens(string text) =>
        text.ToLowerInvariant().Split([' ', '*', '#', '-', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && !token.All(char.IsAsciiDigit)).ToHashSet(StringComparer.Ordinal);

    private static (int, int, int, string, int) Preference(StoredTransaction row) =>
        (row.Row.Status == TransactionStatuses.Posted ? 0 : 1,
         row.Row.ProviderId is null ? 1 : 0,
         FormatRank(row.Evidence.Format),
         row.Evidence.ItemId.ToString("N"),
         row.Row.Ordinal);

    private static int FormatRank(string format) => format switch
    {
        FinancialFormats.Ofx => 0,
        FinancialFormats.PdfStatement => 1,
        FinancialFormats.Delimited => 2,
        _ => 3
    };

    /// <summary>Union-find over rows that also tracks which documents each group draws from.</summary>
    private sealed class DisjointSet
    {
        private readonly int[] parent;
        private readonly Dictionary<int, HashSet<(Guid, int)>> documents = [];

        public DisjointSet(IReadOnlyList<(Guid ItemId, int Revision)> rowDocuments)
        {
            parent = Enumerable.Range(0, rowDocuments.Count).ToArray();
            for (int index = 0; index < rowDocuments.Count; index++)
            {
                documents[index] = [rowDocuments[index]];
            }
        }

        public int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        public IReadOnlySet<(Guid, int)> DocumentsOf(int index) => documents[Find(index)];

        public void Union(int left, int right)
        {
            int a = Find(left);
            int b = Find(right);
            if (a == b)
            {
                return;
            }

            (int keep, int merged) = (Math.Min(a, b), Math.Max(a, b));
            parent[merged] = keep;
            documents[keep].UnionWith(documents[merged]);
            documents.Remove(merged);
        }

        public IEnumerable<int[]> Groups() =>
            Enumerable.Range(0, parent.Length).GroupBy(Find).Select(group => group.ToArray());
    }
}
