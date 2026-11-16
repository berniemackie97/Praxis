using System.Text;

namespace Praxis.Domain.Agents;

/// <summary>An owner-confirmed current name, with an older label retained for matching source material.</summary>
public sealed record AgentNameCorrection(string Previous, string Current, bool Business)
{
    public string Key => Previous.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    public string Describe() => $"Current {(Business ? "business " : "")}name: {Current} (older label: {Previous}).";
}
