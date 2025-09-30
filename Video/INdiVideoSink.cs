using CefSharp.OffScreen;

namespace Tractus.HtmlToNdi.Video;

public interface INdiVideoSink
{
    void HandleFrame(OnPaintEventArgs args);
}
