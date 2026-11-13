using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace EliteScriptStudio.Desktop;

internal static class DesktopMediaResponse
{
    internal static async Task SendAsync(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args,
        Func<Uri, DesktopMedia?> read)
    {
        using var deferral = args.GetDeferral();
        try
        {
            var uri = new Uri(args.Request.Uri);
            var media = args.Request.Method == "GET" && uri.Scheme == "https" && uri.Host == "media.accessrelay.test"
                && uri.IsDefaultPort ? read(uri) : null;
            if (media is null)
            { args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store\r\n"); return; }
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            { writer.WriteBytes(media.Bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            args.Response = sender.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + media.ContentType
                + "\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://studio.local\r\nX-Content-Type-Options: nosniff\r\n");
        }
        catch { args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store\r\n"); }
        finally { deferral.Complete(); }
    }
}
