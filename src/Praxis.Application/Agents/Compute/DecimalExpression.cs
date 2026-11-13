using System.Globalization;

namespace Praxis.Application.Agents.Compute;

/// <summary>
/// Exact decimal arithmetic for the agent: + - * / and parentheses, unary minus, percentages
/// (<c>30%</c> = 0.30), named variables and the functions round(x[, places]), floor, ceil, abs,
/// min, max and sum. A recursive-descent parser over <see cref="decimal"/> — no code execution,
/// no floating point. Inputs are bounded in length and nesting.
/// </summary>
public sealed class DecimalExpression
{
    private const int MaximumLength = 2_000;
    private const int MaximumDepth = 32;

    private readonly string text;
    private readonly IReadOnlyDictionary<string, decimal> variables;
    private int position;
    private int depth;

    private DecimalExpression(string text, IReadOnlyDictionary<string, decimal> variables)
    {
        this.text = text;
        this.variables = variables;
    }

    public static decimal Evaluate(string expression, IReadOnlyDictionary<string, decimal>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaximumLength)
        {
            throw new ArgumentException($"Provide an expression of 1–{MaximumLength} characters.");
        }

        DecimalExpression parser = new(expression, variables ?? new Dictionary<string, decimal>(StringComparer.Ordinal));
        decimal value = parser.Sum();
        parser.SkipSpace();
        return parser.position == parser.text.Length ? value : throw parser.Error("Unexpected text");
    }

    private decimal Sum()
    {
        decimal value = Product();
        while (true)
        {
            SkipSpace();
            if (Accept('+'))
            {
                value = checked(value + Product());
            }
            else if (Accept('-'))
            {
                value = checked(value - Product());
            }
            else
            {
                return value;
            }
        }
    }

    private decimal Product()
    {
        decimal value = Unary();
        while (true)
        {
            SkipSpace();
            if (Accept('*'))
            {
                value = checked(value * Unary());
            }
            else if (Accept('/'))
            {
                decimal divisor = Unary();
                value = divisor == 0 ? throw new ArgumentException("Division by zero.") : value / divisor;
            }
            else
            {
                return value;
            }
        }
    }

    private decimal Unary()
    {
        SkipSpace();
        if (Accept('-'))
        {
            return -Unary();
        }

        Accept('+');
        decimal value = Primary();
        SkipSpace();
        return Accept('%') ? value / 100m : value;
    }

    private decimal Primary()
    {
        SkipSpace();
        if (++depth > MaximumDepth)
        {
            throw new ArgumentException("The expression is nested too deeply.");
        }

        try
        {
            if (Accept('('))
            {
                decimal inner = Sum();
                SkipSpace();
                return Accept(')') ? inner : throw Error("Expected ')'");
            }

            if (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.'))
            {
                int start = position;
                while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] is '.' or '_' || IsGroupingComma(position)))
                {
                    position++;
                }

                string number = text[start..position].Replace("_", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
                return decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) ? value : throw Error("Invalid number");
            }

            if (position < text.Length && (char.IsAsciiLetter(text[position]) || text[position] == '_'))
            {
                int start = position;
                while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_'))
                {
                    position++;
                }

                string name = text[start..position];
                SkipSpace();
                return Accept('(') ? Function(name) : variables.TryGetValue(name, out decimal variable) ? variable : throw Error($"Unknown variable '{name}'");
            }

            throw Error("Expected a number, variable, function or '('");
        }
        finally
        {
            depth--;
        }
    }

    private decimal Function(string name)
    {
        List<decimal> arguments = [];
        SkipSpace();
        if (!Accept(')'))
        {
            do
            {
                arguments.Add(Sum());
                SkipSpace();
            }
            while (Accept(','));

            if (!Accept(')'))
            {
                throw Error("Expected ')' after function arguments");
            }
        }

        return (name.ToLowerInvariant(), arguments.Count) switch
        {
            ("round", 1) => Math.Round(arguments[0], 0, MidpointRounding.ToEven),
            ("round", 2) => Math.Round(arguments[0], (int)Math.Clamp(arguments[1], 0, 28), MidpointRounding.ToEven),
            ("floor", 1) => Math.Floor(arguments[0]),
            ("ceil", 1) => Math.Ceiling(arguments[0]),
            ("abs", 1) => Math.Abs(arguments[0]),
            ("min", > 0) => arguments.Min(),
            ("max", > 0) => arguments.Max(),
            ("sum", > 0) => arguments.Aggregate(0m, (total, value) => checked(total + value)),
            _ => throw new ArgumentException($"Unknown function '{name}' with {arguments.Count} argument(s). Use round, floor, ceil, abs, min, max, sum.")
        };
    }

    /// <summary>A comma followed by exactly three digits is digit grouping (1,234.50); otherwise it separates arguments.</summary>
    private bool IsGroupingComma(int index) =>
        text[index] == ',' && index + 3 < text.Length &&
        char.IsAsciiDigit(text[index + 1]) && char.IsAsciiDigit(text[index + 2]) && char.IsAsciiDigit(text[index + 3]) &&
        (index + 4 == text.Length || !char.IsAsciiDigit(text[index + 4]));

    private bool Accept(char expected)
    {
        if (position < text.Length && text[position] == expected)
        {
            position++;
            return true;
        }

        return false;
    }

    private void SkipSpace()
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private ArgumentException Error(string message) => new($"{message} at position {position}.");
}
