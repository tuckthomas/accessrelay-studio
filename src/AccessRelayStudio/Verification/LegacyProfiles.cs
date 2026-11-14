using System.Collections.Immutable;
using EliteScriptStudio.Core;

// Historical fixtures exercise old profile playback/import compatibility.
// Production CreateDefault creates an empty Lua-only controller profile.
internal static class LegacyProfiles
{
    internal static StudioProfile CreateDefault(string name = "PC Profile") => new()
    {
        Id = Guid.NewGuid(), Name = name,
        Bindings = new Dictionary<ControlId, ScriptBinding>
        {
            [ControlId.LargeRight] = new(ScriptKind.Rhythm), [ControlId.LargeLeft] = new(ScriptKind.IntentionalMiss),
            [ControlId.SmallRight] = new(ScriptKind.Moving), [ControlId.SmallLeft] = new(ScriptKind.Stepback)
        }.ToImmutableDictionary()
    };
}
