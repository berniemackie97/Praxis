namespace Praxis.Application.Desktop.Contracts;

/// <summary>Acknowledgement that the worker is stopping.</summary>
public sealed record StopResponse(bool Stopping);
