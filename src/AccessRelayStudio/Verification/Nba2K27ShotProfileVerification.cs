using System.IO;
using System.Text.Json.Nodes;
using EliteScriptStudio.Core;

internal static class Nba2K27ShotProfileVerification
{
    internal static void Run()
    {
        ValidationAndFingerprints();
        PersistenceAndSelection();
        BoundedReadsAndIdentity();
        MeterSpeedBlendAndLegacyDefaults();
        CameraViewsAndMigration();
    }

    private static Nba2K27ShotProfile Complete(string name = "Standing jumper") =>
        Nba2K27ShotProfile.Create() with
        {
            Name = name, HeightInches = 78, ThreePointRating = 85,
            Base = "Custom base", UpperRelease1 = "Custom upper one", UpperRelease2 = "Custom upper two"
        };

    private static void ValidationAndFingerprints()
    {
        var profile = Complete();
        profile.Validate();
        Check(profile.ConfigurationFingerprint.Length == 64, "Configuration identity is SHA-256.");
        Equal(profile.ConfigurationFingerprint, (profile with { Id = Guid.NewGuid(), Name = "Renamed" }).ConfigurationFingerprint);
        Equal(profile.ConfigurationFingerprint, (profile with { Base = " Custom base " }).ConfigurationFingerprint);
        foreach (var changed in new[]
        {
            profile with { HeightInches = 79 }, profile with { ThreePointRating = 86 },
            profile with { Base = "Different base" }, profile with { UpperRelease1 = "Different upper one" },
            profile with { UpperRelease2 = "Different upper two" },
            profile with { UsesShotMeter = true, MeterReleaseType = "Custom release type" },
            profile with { ShotSpeed = 3 }, profile with { Release1Percent = 40 },
            profile with { CameraView = "2K Low" }, profile with { CameraView = "2K Legacy" }
        }) Check(profile.ConfigurationFingerprint != changed.ConfigurationFingerprint, "Every game-setting edit invalidates configuration identity.");
        foreach (var invalid in new[]
        {
            Nba2K27ShotProfile.Create(), profile with { Id = Guid.Empty }, profile with { Name = " " },
            profile with { Name = "bad\nname" }, profile with { HeightInches = int.MaxValue },
            profile with { HeightInches = 0 }, profile with { ThreePointRating = -1 },
            profile with { ThreePointRating = 101 }, profile with { Base = "" },
            profile with { Base = "\ud800" },
            profile with { UsesShotMeter = true, MeterReleaseType = "" },
            profile with { UsesShotMeter = true, MeterReleaseType = "bad\nrelease" },
            profile with { ShotSpeed = 0 }, profile with { ShotSpeed = 5 },
            profile with { Release1Percent = -1 }, profile with { Release1Percent = 101 },
            profile with { CameraView = "" }, profile with { CameraView = "2k" },
            profile with { CameraView = "2K low" }, profile with { CameraView = " 2K" },
            profile with { CameraView = "Broadcast" }, profile with { CameraView = null! },
            profile with { UpperRelease1 = new string('x', 121) }, profile with { UpperRelease2 = " " }
        }) Expect<InvalidDataException>(invalid.Validate);
        Equal("6' 6\"", Nba2K27ShotProfile.FormatHeight(78));
        Equal(profile.ConfigurationFingerprint, (profile with { MeterReleaseType = "Dormant off-mode text" }).ConfigurationFingerprint);
        var meter = profile with { UsesShotMeter = true, MeterReleaseType = "Custom type" };
        Check(meter.ConfigurationFingerprint != (meter with { MeterReleaseType = "Another type" }).ConfigurationFingerprint,
            "Active meter release type changes invalidate configuration identity.");
    }

    private static void MeterSpeedBlendAndLegacyDefaults()
    {
        WithDirectory(directory =>
        {
            var store = new Nba2K27ShotProfileStore(directory);
            var profile = Complete() with { UsesShotMeter = true, MeterReleaseType = "User-entered meter release", ShotSpeed = 2, Release1Percent = 65 };
            store.Save(profile);
            Equal(profile, store.Load(profile.Id));
            Equal(35, store.Load(profile.Id).Release2Percent);
            foreach (int percent in new[] { 0, 1, 50, 99, 100 })
            {
                var blend = profile with { Release1Percent = percent };
                blend.Validate();
                Equal(100, blend.Release1Percent + blend.Release2Percent);
            }
            foreach (int speed in new[] { 1, 2, 3, 4 }) (profile with { ShotSpeed = speed }).Validate();
            var off = profile with { UsesShotMeter = false, MeterReleaseType = "Ignored old meter setting" };
            store.Save(off);
            Equal("", store.Load(off.Id).MeterReleaseType);
            Equal(off.ConfigurationFingerprint, store.Load(off.Id).ConfigurationFingerprint);

            string path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            var legacy = document["profile"]!.AsObject();
            foreach (string field in new[] { "usesShotMeter", "meterReleaseType", "shotSpeed", "release1Percent", "release2Percent" })
                legacy.Remove(field);
            File.WriteAllText(path, document.ToJsonString());
            var restored = store.Load(profile.Id);
            Equal(false, restored.UsesShotMeter);
            Equal("", restored.MeterReleaseType);
            Equal(4, restored.ShotSpeed);
            Equal(50, restored.Release1Percent);
            Equal(50, restored.Release2Percent);
            store.Select(restored.Id);
            Equal<Guid?>(restored.Id, store.LoadSelectedId());
        });
    }

    private static void CameraViewsAndMigration()
    {
        Equal("2K", Nba2K27ShotProfile.Create().CameraView);
        Check(Nba2K27ShotProfile.CameraViews.SequenceEqual(new[] { "2K", "2K Low", "2K Legacy" }),
            "Exactly the requested camera views are exposed.");
        var profile = Complete();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        WithDirectory(directory =>
        {
            var store = new Nba2K27ShotProfileStore(directory);
            foreach (string camera in Nba2K27ShotProfile.CameraViews)
            {
                var edited = profile with { CameraView = camera };
                edited.Validate();
                fingerprints.Add(edited.ConfigurationFingerprint);
                store.Save(edited); store.Select(edited.Id);
                Equal(edited, new Nba2K27ShotProfileStore(directory).Load(edited.Id));
                Equal<Guid?>(edited.Id, store.LoadSelectedId());
            }
            Equal(3, fingerprints.Count);
            var duplicate = store.Duplicate(profile.Id, "Copied legacy-camera shot");
            Equal("2K Legacy", duplicate.CameraView);
            Equal(store.Load(profile.Id).ConfigurationFingerprint, duplicate.ConfigurationFingerprint);

            string path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["profile"]!.AsObject().Remove("cameraView");
            File.WriteAllText(path, document.ToJsonString());
            var legacy = store.Load(profile.Id);
            Equal("2K", legacy.CameraView);
            Equal(profile.ConfigurationFingerprint, legacy.ConfigurationFingerprint);
            document["profile"]!["cameraView"] = "Unsupported view";
            File.WriteAllText(path, document.ToJsonString());
            Expect<InvalidDataException>(() => store.Load(profile.Id));
            Equal<Guid?>(null, store.LoadSelectedId());
        });
    }

    private static void PersistenceAndSelection()
    {
        WithDirectory(directory =>
        {
            var store = new Nba2K27ShotProfileStore(directory);
            Equal(0, store.LoadAll().Count);
            Equal<Guid?>(null, store.LoadSelectedId());
            Expect<InvalidDataException>(() => store.Save(Nba2K27ShotProfile.Create()));
            var first = Complete();
            var second = Complete("Moving jumper") with { Base = "Another custom base", HeightInches = 80 };
            store.Save(first); store.Save(second); store.Select(first.Id);
            Equal(2, store.LoadAll().Count);
            Equal<Guid?>(first.Id, new Nba2K27ShotProfileStore(directory).LoadSelectedId());
            var edit = first with { ThreePointRating = 92, UpperRelease2 = "Updated upper" };
            store.Save(edit);
            Equal(edit, store.Load(first.Id));
            Equal<Guid?>(first.Id, store.LoadSelectedId());
            var copy = store.Duplicate(first.Id, " Copy ");
            Equal("Copy", copy.Name);
            Check(copy.Id != first.Id, "Duplicate gets a new ID.");
            Equal(edit.ConfigurationFingerprint, copy.ConfigurationFingerprint);
            Equal(3, store.LoadAll().Count);
            store.Select(copy.Id);
            store.Delete(first.Id);
            Equal<Guid?>(copy.Id, store.LoadSelectedId());
            store.Delete(copy.Id);
            Equal<Guid?>(null, store.LoadSelectedId());
            Equal(second, store.LoadAll().Single());
            Expect<FileNotFoundException>(() => store.Select(first.Id));
            Expect<ArgumentException>(() => store.Delete(Guid.Empty));
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic writes leave no temporary files.");
        });
    }

    private static void BoundedReadsAndIdentity()
    {
        WithDirectory(directory =>
        {
            var store = new Nba2K27ShotProfileStore(directory);
            var first = Complete();
            store.Save(first); store.Select(first.Id);
            string path = Path.Combine(directory, first.Id.ToString("N") + ".json");
            string original = File.ReadAllText(path);
            var document = JsonNode.Parse(original)!;
            document["version"] = 999;
            File.WriteAllText(path, document.ToJsonString());
            Expect<InvalidDataException>(() => store.Load(first.Id));
            Equal<Guid?>(null, store.LoadSelectedId());
            document["version"] = 1;
            document["profile"]!["id"] = Guid.NewGuid().ToString();
            File.WriteAllText(path, document.ToJsonString());
            Expect<InvalidDataException>(() => store.Load(first.Id));
            File.WriteAllText(path, new string(' ', 65537));
            Expect<InvalidDataException>(() => store.Load(first.Id));
            Equal(0, store.LoadAll().Count);
            File.WriteAllText(path, original);
            Equal(first, store.Load(first.Id));
            File.WriteAllText(Path.Combine(directory, "selected-shot-profile.txt"), "../outside");
            Equal<Guid?>(null, store.LoadSelectedId());
            var macroStore = new ProfileStore(Path.Combine(directory, "macros"));
            Equal(0, macroStore.LoadAll().Count); // Separate storage does not enumerate shot documents.
            // Existing entries may still be edited at capacity; new IDs are bounded.
            for (int i = 1; i < Nba2K27ShotProfile.MaximumProfiles; i++) store.Save(Complete("Profile " + i));
            Expect<InvalidOperationException>(() => store.Save(Complete("One too many")));
            store.Save(first with { Name = "Existing profile edited at capacity" });
            Equal(Nba2K27ShotProfile.MaximumProfiles, store.LoadAll().Count);
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.ShotProfile.Verify." + Guid.NewGuid().ToString("N"));
        try { action(directory); }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
                // Tests never delete recursively; remove only their empty named test folder.
                Directory.Delete(directory);
            }
        }
    }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
