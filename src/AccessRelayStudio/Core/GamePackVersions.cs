using System.Globalization;
using System.Numerics;

namespace EliteScriptStudio.Core;

internal static class GamePackVersions
{
    internal static readonly IComparer<string> Order = Comparer<string>.Create(Compare);

    private static int Compare(string? left, string? right)
    {
        if (left is null || right is null || left == right) return string.CompareOrdinal(left, right);
        var a = left.Split('-', 2);
        var b = right.Split('-', 2);
        bool numericA = Version.TryParse(a[0], out var versionA);
        bool numericB = Version.TryParse(b[0], out var versionB);
        if (numericA != numericB) return numericA.CompareTo(numericB);
        if (!numericA) return string.CompareOrdinal(left, right);
        int core = versionA!.CompareTo(versionB);
        if (core != 0) return core;
        // A release supersedes prereleases of the same numeric version.
        if (a.Length != b.Length) return b.Length.CompareTo(a.Length);
        if (a.Length == 1) return 0;

        var labelsA = a[1].Split('.');
        var labelsB = b[1].Split('.');
        for (int i = 0; i < Math.Min(labelsA.Length, labelsB.Length); i++)
        {
            bool numberA = BigInteger.TryParse(labelsA[i], NumberStyles.None, CultureInfo.InvariantCulture, out var valueA);
            bool numberB = BigInteger.TryParse(labelsB[i], NumberStyles.None, CultureInfo.InvariantCulture, out var valueB);
            if (numberA != numberB) return numberB.CompareTo(numberA);
            int label = numberA ? valueA.CompareTo(valueB) : string.CompareOrdinal(labelsA[i], labelsB[i]);
            if (label != 0) return label;
        }
        return labelsA.Length.CompareTo(labelsB.Length);
    }
}
