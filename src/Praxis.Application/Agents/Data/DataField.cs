namespace Praxis.Application.Agents.Data;

/// <summary>
/// One field of a dataset. <c>money</c> values are integer minor units in the row's currency and
/// are only ever summed within a currency.
/// </summary>
public sealed record DataField(string Name, string Type, string Description, bool Groupable)
{
    public const string Text = "text";
    public const string Date = "date";
    public const string Money = "money";
    public const string Integer = "integer";
}
