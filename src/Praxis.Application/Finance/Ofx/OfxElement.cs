namespace Praxis.Application.Finance.Ofx;

/// <summary>One OFX element: an aggregate with children or a leaf with a value.</summary>
public sealed class OfxElement(string name, string? value = null)
{
    private readonly List<OfxElement> children = [];

    public string Name { get; } = name;
    public string? Value { get; } = value;
    public IReadOnlyList<OfxElement> Children => children;

    internal void Add(OfxElement child) => children.Add(child);

    public OfxElement? Child(string childName) =>
        children.FirstOrDefault(child => child.Name == childName);

    /// <summary>The trimmed value of a direct leaf child, or null when absent or empty.</summary>
    public string? Text(string childName) => Child(childName)?.Value is { Length: > 0 } text ? text : null;

    public IEnumerable<OfxElement> Descendants(string descendantName)
    {
        foreach (OfxElement child in children)
        {
            if (child.Name == descendantName)
            {
                yield return child;
            }

            foreach (OfxElement nested in child.Descendants(descendantName))
            {
                yield return nested;
            }
        }
    }
}
