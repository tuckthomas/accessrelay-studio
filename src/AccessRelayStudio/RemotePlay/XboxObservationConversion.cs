using static EliteScriptStudio.RemotePlay.GStreamerNative;

namespace EliteScriptStudio.RemotePlay;

internal static class XboxObservationConversion
{
    internal const string Gpu = "d3d12convert ! video/x-raw(memory:D3D12Memory),format=BGRA ! d3d12download";

    internal static string Select(bool forceCpu, Action<string>? trace, out bool gpu)
    {
        gpu = false;
        if (!OperatingSystem.IsWindows()) return "videoconvert";
        bool d12 = Installed("d3d12download"), d11 = Installed("d3d11download");
        if (!Installed("switchbin"))
            return (d12 ? "d3d12download ! " : d11 ? "d3d11download ! " : "") + "videoconvert";

        gpu = !forceCpu && d12 && Installed("d3d12convert") && CanConvert();
        trace?.Invoke(gpu ? "D3D12 BGRA conversion verified for native NV12 surfaces." :
            forceCpu ? "CPU color conversion selected in Settings." : "Using CPU color conversion.");
        return Routes(gpu, d12, d11);
    }

    internal static string Routes(bool gpu, bool d12, bool d11)
    {
        // Select by the actual caps from decodebin on each negotiation. Do not upload
        // software or D3D11 frames to D3D12 just because a converter is installed.
        List<(string Caps, string Elements)> paths = [];
        if (gpu) paths.Add(("video/x-raw(memory:D3D12Memory),format=NV12", Gpu));
        if (d12) paths.Add(("video/x-raw(memory:D3D12Memory)", "d3d12download ! videoconvert"));
        if (d11) paths.Add(("video/x-raw(memory:D3D11Memory)", "d3d11download ! videoconvert"));
        paths.Add(("video/x-raw", "videoconvert"));
        return $"switchbin name=colorRoutes num-paths={paths.Count} " + string.Join(" ", paths.Select((path, i) =>
            $"path{i}::caps=\"{path.Caps}\" path{i}::element=\"{path.Elements}\""));
    }

    private static bool Installed(string name)
    {
        var factory = gst_element_factory_find(name);
        if (factory == 0) return false;
        gst_object_unref(factory);
        return true;
    }

    private static bool CanConvert()
    {
        // Exercise device creation, NV12 conversion and a BGRA download before opening Xbox.
        nint pipeline = gst_parse_launch("videotestsrc num-buffers=1 ! video/x-raw,format=NV12,width=2560,height=1440 ! " +
            "d3d12upload ! video/x-raw(memory:D3D12Memory),format=NV12 ! " + Routes(true, true, false) +
            " ! video/x-raw,format=BGRA ! appsink name=test sync=false", out var error);
        nint sink = 0, routes = 0;
        try
        {
            if (error != 0 || pipeline == 0) return false;
            sink = gst_bin_get_by_name(pipeline, "test");
            if (sink == 0 || gst_element_set_state(pipeline, 4) == 0) return false;
            var sample = gst_app_sink_try_pull_sample(sink, 2_000_000_000);
            if (sample == 0) return false;
            gst_mini_object_unref(sample);
            routes = gst_bin_get_by_name(pipeline, "colorRoutes");
            if (routes == 0) return false;
            Get(routes, "current-path", out int path, 0);
            return path == 0;
        }
        finally
        {
            if (error != 0) g_error_free(error);
            if (pipeline != 0) gst_element_set_state(pipeline, 1);
            if (routes != 0) gst_object_unref(routes);
            if (sink != 0) gst_object_unref(sink);
            if (pipeline != 0) gst_object_unref(pipeline);
        }
    }
}
