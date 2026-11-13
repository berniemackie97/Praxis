namespace Praxis.Domain.Finance;

/// <summary>The closed category vocabulary models choose from when annotating transactions.</summary>
public static class SpendingCategories
{
    public const string Income = "income";
    public const string Interest = "interest";
    public const string Refund = "refund";
    public const string Transfer = "transfer";
    public const string Housing = "housing";
    public const string Utilities = "utilities";
    public const string Groceries = "groceries";
    public const string Dining = "dining";
    public const string Transportation = "transportation";
    public const string Shopping = "shopping";
    public const string Subscriptions = "subscriptions";
    public const string Health = "health";
    public const string Insurance = "insurance";
    public const string Entertainment = "entertainment";
    public const string Travel = "travel";
    public const string Fees = "fees";
    public const string Cash = "cash";
    public const string Taxes = "taxes";
    public const string Uncategorized = "uncategorized";

    public static readonly IReadOnlyList<string> All =
    [
        Income, Interest, Refund, Transfer, Housing, Utilities, Groceries, Dining, Transportation, Shopping,
        Subscriptions, Health, Insurance, Entertainment, Travel, Fees, Cash, Taxes, Uncategorized
    ];
}
