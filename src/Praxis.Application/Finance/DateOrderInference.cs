namespace Praxis.Application.Finance;

/// <summary>
/// Chooses the field order of a column of numeric dates from all of its values. When both
/// month-first and day-first read every value, the order under which the rows are
/// chronological wins; only when the evidence cannot separate them is month-first assumed,
/// and the assumption is reported.
/// </summary>
public static class DateOrderInference
{
    public sealed record Result(DateOrder Order, bool Assumed);

    public static Result? Infer(IReadOnlyList<string> values)
    {
        string[] present = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (present.Length == 0)
        {
            return null;
        }

        DateOrder[] viable = Enum.GetValues<DateOrder>()
            .Where(order => present.All(value => DateText.TryParse(value, order, 2000, out _)))
            .ToArray();
        switch (viable.Length)
        {
            case 0:
                return null;
            case 1:
                return new Result(viable[0], false);
        }

        DateOrder[] chronological = viable.Where(order => IsMonotonic(present, order)).ToArray();
        if (chronological.Length == 1)
        {
            return new Result(chronological[0], false);
        }

        if (viable.Contains(DateOrder.YearFirst) && viable.Length == Enum.GetValues<DateOrder>().Length)
        {
            // Every value is ISO-shaped; the order is irrelevant.
            return new Result(DateOrder.YearFirst, false);
        }

        return new Result(viable.Contains(DateOrder.MonthFirst) ? DateOrder.MonthFirst : viable[0], true);
    }

    private static bool IsMonotonic(IReadOnlyList<string> values, DateOrder order)
    {
        DateOnly[] dates = values.Select(value =>
        {
            DateText.TryParse(value, order, 2000, out DateOnly date);
            return date;
        }).ToArray();
        bool ascending = true;
        bool descending = true;
        for (int index = 1; index < dates.Length; index++)
        {
            ascending &= dates[index] >= dates[index - 1];
            descending &= dates[index] <= dates[index - 1];
        }

        return ascending || descending;
    }
}
