namespace Praxis.Application.Desktop.Contracts;

/// <summary>Something the owner should know about work in progress, such as a reached limit or failing calls. Tone is info, warning or critical.</summary>
public sealed record ActivityNotice(string Tone, string Text);
