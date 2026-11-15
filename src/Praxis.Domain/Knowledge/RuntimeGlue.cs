namespace Praxis.Domain.Knowledge;

/// <summary>
/// C and C++ runtime and compiler-support routines: string, memory, formatting, allocation and
/// exception-frame setup. Decompiled code calls them from almost every function, so as graph hubs
/// they outrank the concepts the owner actually studies. They stay real entities with real
/// relations; views rank their links last and never let them anchor a hub.
/// </summary>
public static class RuntimeGlue
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "strlen", "strnlen", "strcpy", "strncpy", "strcat", "strncat", "strcmp", "strncmp", "stricmp", "_stricmp", "_strnicmp",
        "strchr", "strrchr", "strstr", "strtok", "strdup", "_strdup", "wcslen", "wcscpy", "wcscmp",
        "memcpy", "memmove", "memset", "memcmp", "memchr",
        "malloc", "calloc", "realloc", "free", "operator new", "operator new[]", "operator delete", "operator delete[]",
        "sprintf", "snprintf", "_snprintf", "vsprintf", "vsnprintf", "_vsnprintf", "printf", "fprintf", "sscanf",
        "atoi", "atol", "atof", "itoa", "_itoa", "strtol", "strtoul", "strtod",
        "fopen", "fclose", "fread", "fwrite", "fseek", "ftell", "abort", "exit", "rand", "srand"
    };

    /// <summary>True when the name is a runtime or compiler-support routine rather than owner-relevant code.</summary>
    public static bool IsGlue(string name) => Names.Contains(name) || IsImplementationReserved(name);

    /// <summary>
    /// C and C++ reserve identifiers that begin with two underscores, or an underscore and a capital
    /// letter, for the compiler and runtime (<c>__EH_prolog</c>, <c>__chkstk</c>, <c>_CxxThrowException</c>).
    /// </summary>
    public static bool IsImplementationReserved(string name) =>
        name.Length > 2 && name[0] == '_' && (name[1] == '_' || char.IsAsciiLetterUpper(name[1]));
}
