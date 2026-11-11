namespace Praxis.Application.Desktop;

/// <summary>The desktop protocol actions of one responsibility (library, search, verification, ...).</summary>
public interface IDesktopActionGroup
{
    IEnumerable<DesktopAction> Actions { get; }
}
