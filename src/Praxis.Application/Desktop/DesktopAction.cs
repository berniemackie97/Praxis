namespace Praxis.Application.Desktop;

/// <summary>One named desktop protocol action and the handler that produces its response data.</summary>
public sealed record DesktopAction(string Name, Func<DesktopRequestFields, CancellationToken, Task<object>> ExecuteAsync)
{
    /// <summary>An action whose handler completes synchronously.</summary>
    public static DesktopAction Of(string name, Func<DesktopRequestFields, object> execute) =>
        new(name, (fields, _) => Task.FromResult(execute(fields)));

    /// <summary>An action whose handler awaits I/O.</summary>
    public static DesktopAction OfAsync<TResponse>(string name, Func<DesktopRequestFields, CancellationToken, Task<TResponse>> execute)
        where TResponse : notnull =>
        new(name, async (fields, cancellationToken) => await execute(fields, cancellationToken));
}
