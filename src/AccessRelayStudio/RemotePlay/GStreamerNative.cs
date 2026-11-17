using System.Reflection;
using System.Runtime.InteropServices;

namespace EliteScriptStudio.RemotePlay;

// GStreamer's public C ABI. The action-signal overloads below are the fixed x64
// signatures of the corresponding GObject varargs calls (pointer/integer arguments).
internal static class GStreamerNative
{
    private const string Gst = "relay:gst", Sdp = "relay:sdp", Web = "relay:webrtc", App = "relay:app", Video = "relay:video", GLib = "relay:glib", Obj = "relay:object";
    private static string? _windowsBin;
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        if (OperatingSystem.IsWindows())
        {
            string? configured = Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64");
            string[] roots = [configured ?? "", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gstreamer", "1.0", "msvc_x86_64"),
                Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "gstreamer", "1.0", "msvc_x86_64")];
            _windowsBin = roots.Where(Path.IsPathFullyQualified).Select(root => Path.Combine(root, "bin"))
                .FirstOrDefault(bin => File.Exists(Path.Combine(bin, "gstreamer-1.0-0.dll"))) ?? throw new DllNotFoundException();
            // The out-of-process plugin scanner and dynamically loaded codecs also resolve transitive DLLs here.
            Environment.SetEnvironmentVariable("PATH", _windowsBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }
        NativeLibrary.SetDllImportResolver(typeof(GStreamerNative).Assembly, Resolve);
        if (gst_init_check(0, 0, out var error) == 0) throw Error(error, "GStreamer could not initialize.");
        gst_version(out uint major, out uint minor, out _, out _);
        if (major != 1 || minor < 24) throw new NotSupportedException("Direct Xbox capture requires 64-bit GStreamer 1.24 or newer.");
        return true;
    });
    internal static void Initialize()
    {
        try { _ = Initialized.Value; }
        catch (DllNotFoundException) { throw new InvalidOperationException("Install the complete GStreamer MSVC x86_64 runtime and restart Studio to use direct Xbox capture."); }
    }

    private static nint Resolve(string library, Assembly assembly, DllImportSearchPath? search)
    {
        string? stem = library switch { Gst => "gstreamer-1.0", Web => "gstwebrtc-1.0", Sdp => "gstsdp-1.0",
            App => "gstapp-1.0", Video => "gstvideo-1.0", GLib => "glib-2.0", Obj => "gobject-2.0", _ => null };
        if (stem is null) return 0;
        if (!OperatingSystem.IsWindows()) return NativeLibrary.Load("lib" + stem + ".so.0");
        return NativeLibrary.Load(Path.Combine(_windowsBin!, stem + "-0.dll"), assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
    }

    internal static Exception Error(nint error, string context)
    {
        if (error != 0) g_error_free(error);
        return new InvalidOperationException(context);
    }
    internal static string Text(nint p) => Marshal.PtrToStringUTF8(p) ?? "";
    [StructLayout(LayoutKind.Sequential)]
    internal struct Map
    {
        public nint Memory;
        public int Flags;
        public nint Data;
        public nuint Size, MaxSize;
        private nint user0, user1, user2, user3, reserved0, reserved1, reserved2, reserved3;
    }

    [DllImport(Gst)] private static extern int gst_init_check(nint argc, nint argv, out nint error);
    [DllImport(Gst)] private static extern void gst_version(out uint major, out uint minor, out uint micro, out uint nano);
    [DllImport(Gst)] internal static extern nint gst_parse_launch(string pipeline, out nint error);
    [DllImport(Gst)] internal static extern nint gst_element_get_factory(nint element);
    [DllImport(Gst)] internal static extern nint gst_element_factory_get_metadata(nint factory, string key);
    [DllImport(Gst)] internal static extern nint gst_object_get_name(nint obj);
    [DllImport(Gst)] internal static extern nint gst_bin_get_by_name(nint bin, string name);
    [DllImport(Gst)] internal static extern int gst_element_set_state(nint element, int state);
    [DllImport(Gst)] internal static extern int gst_pad_get_direction(nint pad);
    [DllImport(Gst)] internal static extern int gst_pad_link(nint source, nint sink);
    [DllImport(Gst)] internal static extern nint gst_element_get_static_pad(nint element, string name);
    [DllImport(Gst)] internal static extern nint gst_element_get_bus(nint pipeline);
    [DllImport(Gst)] internal static extern nint gst_bus_timed_pop_filtered(nint bus, ulong timeout, uint types);
    [DllImport(Gst)] internal static extern void gst_object_unref(nint obj);
    [DllImport(Gst)] internal static extern void gst_mini_object_unref(nint obj);
    [DllImport(Gst)] internal static extern nint gst_structure_from_string(string value, nint end);
    [DllImport(Gst)] internal static extern void gst_structure_free(nint structure);
    [DllImport(Gst)] internal static extern nint gst_caps_from_string(string caps);
    [DllImport(Gst)] internal static extern nint gst_caps_get_structure(nint caps, uint index);
    [DllImport(Gst)] internal static extern int gst_structure_get_int(nint s, string name, out int value);
    [DllImport(Gst)] internal static extern int gst_structure_get_uint(nint s, string name, out uint value);
    [DllImport(Gst)] internal static extern int gst_structure_get_int64(nint s, string name, out long value);
    [DllImport(Gst)] internal static extern int gst_structure_get_uint64(nint s, string name, out ulong value);
    [DllImport(Gst)] internal static extern int gst_structure_get_double(nint s, string name, out double value);
    [DllImport(Gst)] internal static extern nint gst_structure_get_string(nint s, string name);
    [DllImport(Gst)] internal static extern nint gst_structure_get_name(nint s);
    [DllImport(Gst)] internal static extern int gst_structure_n_fields(nint s);
    [DllImport(Gst)] internal static extern nint gst_structure_nth_field_name(nint s, uint index);
    [DllImport(Gst)] internal static extern nint gst_structure_get_value(nint s, string name);
    [DllImport(Gst)] internal static extern nint gst_structure_get_type();
    [DllImport(Gst)] internal static extern nint gst_promise_new();
    [DllImport(Gst)] internal static extern int gst_promise_wait(nint promise);
    [DllImport(Gst)] internal static extern void gst_promise_interrupt(nint promise);
    [DllImport(Gst)] internal static extern nint gst_promise_get_reply(nint promise);
    [DllImport(Gst)] internal static extern nint gst_sample_get_buffer(nint sample);
    [DllImport(Gst)] internal static extern nint gst_sample_get_caps(nint sample);
    [DllImport(Gst)] internal static extern int gst_buffer_map(nint buffer, out Map map, int flags);
    [DllImport(Gst)] internal static extern void gst_buffer_unmap(nint buffer, ref Map map);
    [DllImport(Video)] internal static extern nint gst_buffer_get_video_meta(nint buffer);
    [DllImport(Video)] internal static extern int gst_video_meta_map(nint meta, uint plane, out Map map, out nint data, out int stride, int flags);
    [DllImport(Video)] internal static extern int gst_video_meta_unmap(nint meta, uint plane, ref Map map);
    [DllImport(App)] internal static extern nint gst_app_sink_try_pull_sample(nint sink, ulong timeoutNs);
    [DllImport(Sdp)] internal static extern nint gst_sdp_message_as_text(nint message);
    [DllImport(Sdp)] internal static extern int gst_sdp_message_new_from_text([MarshalAs(UnmanagedType.LPUTF8Str)] string text, out nint sdp);
    [DllImport(Web)] internal static extern nint gst_webrtc_session_description_new(int type, nint sdp);
    [DllImport(Web)] internal static extern void gst_webrtc_session_description_free(nint description);
    [DllImport(Web)] internal static extern int gst_webrtc_data_channel_send_string_full(nint channel, [MarshalAs(UnmanagedType.LPUTF8Str)] string message, nint error);
    [DllImport(Web)] internal static extern int gst_webrtc_data_channel_send_data_full(nint channel, nint bytes, nint error);
    [DllImport(GLib)] internal static extern nint g_bytes_new(in byte data, nuint size);
    [DllImport(GLib)] internal static extern nint g_bytes_get_data(nint bytes, out nuint size);
    [DllImport(GLib)] internal static extern void g_bytes_unref(nint data);
    [DllImport(GLib)] internal static extern void g_free(nint p);
    [DllImport(GLib)] internal static extern void g_error_free(nint p);
    [DllImport(Obj)] internal static extern nint g_value_get_boxed(nint value);
    [DllImport(Obj)] internal static extern int g_type_check_value_holds(nint value, nint type);
    [DllImport(Obj, EntryPoint = "g_object_get")] internal static extern void Get(nint obj, string name, out int result, nint end);
    [DllImport(Obj, EntryPoint = "g_object_get")] internal static extern void GetPointer(nint obj, string name, out nint result, nint end);
    [DllImport(Obj, EntryPoint = "g_object_get")] internal static extern void GetSize(nint obj, string name, out ulong result, nint end);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name")] internal static extern void Promise(nint obj, string name, nint arg, nint promise);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name")] internal static extern void Transceiver(nint obj, string name, int direction, nint caps, out nint result);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name")] internal static extern void Channel(nint obj, string name, string label, nint config, out nint result);
    [DllImport(Obj, EntryPoint = "g_signal_emit_by_name")] internal static extern void Candidate(nint obj, string name, uint index, string candidate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MessageHandler(nint channel, nint message, nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ElementHandler(nint bin, nint subBin, nint element, nint data);
    [DllImport(Obj, EntryPoint = "g_signal_connect_data")] internal static extern nuint ConnectElement(nint obj, string signal, ElementHandler callback, nint data, nint destroy, int flags);
    [DllImport(Obj)] internal static extern nuint g_signal_connect_data(nint obj, string signal, MessageHandler callback, nint data, nint destroy, int flags);
    [DllImport(Obj)] internal static extern void g_signal_handler_disconnect(nint obj, nuint id);
}
