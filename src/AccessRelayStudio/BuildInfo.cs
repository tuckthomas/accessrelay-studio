using System.Reflection;

namespace EliteScriptStudio;

internal static class BuildInfo
{
    internal const string ProductName = "AccessRelay Studio Beta";
    internal static string Version { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "Local build";
    internal static string WindowTitle => $"{ProductName} {Version}";
    internal static string Subtitle => $"{Version} · Assistive input and voice access";
}
