namespace EliteScriptStudio.Desktop;

internal static class DesktopEnvironment
{
    private static readonly string? IsolatedRoot = FindVerificationRoot();
    internal static bool IsVerification => IsolatedRoot is not null;
    internal static string? DataDirectory(string component) => IsolatedRoot is null ? null : Path.Combine(IsolatedRoot, component);
    private static string? FindVerificationRoot()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--ui-verification-data");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }
}
