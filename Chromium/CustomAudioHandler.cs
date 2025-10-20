
using CefSharp;
using CefSharp.Enums;
using CefSharp.Structs;
using NewTek;
using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Handles audio events from a CefSharp browser instance,
/// converting and sending the audio data to an NDI stream.
/// </summary>
public class CustomAudioHandler : IAudioHandler
{
    /// <summary>
    /// A mapping from CefSharp's ChannelLayout enum to the number of channels.
    /// </summary>
    public static readonly Dictionary<ChannelLayout, int> ChannelLayoutToChannelCount = new Dictionary<ChannelLayout, int>
    {
        { ChannelLayout.LayoutNone, 0 },
        { ChannelLayout.LayoutUnsupported, 0 },
        { ChannelLayout.LayoutMono, 1 },
        { ChannelLayout.LayoutStereo, 2 },
        { ChannelLayout.Layout2_1, 3 },
        { ChannelLayout.LayoutSurround, 4 },
        { ChannelLayout.Layout4_0, 4 },
        { ChannelLayout.Layout2_2, 4 },
        { ChannelLayout.LayoutQuad, 4 },
        { ChannelLayout.Layout5_0, 5 },
        { ChannelLayout.Layout5_1, 6 },
        { ChannelLayout.Layout5_0Back, 5 },
        { ChannelLayout.Layout5_1Back, 6 },
        { ChannelLayout.Layout7_0, 7 },
        { ChannelLayout.Layout7_1, 8 },
        { ChannelLayout.Layout7_1Wide, 8 },
        { ChannelLayout.LayoutStereoDownMix, 2 },
        { ChannelLayout.Layout2Point1, 3 },
        { ChannelLayout.Layout3_1, 4 },
        { ChannelLayout.Layout4_1, 5 },
        { ChannelLayout.Layout6_0, 6 },
        { ChannelLayout.Layout6_0Front, 6 },
        { ChannelLayout.LayoutHexagonal, 6 },
        { ChannelLayout.Layout6_1, 7 },
        { ChannelLayout.Layout6_1Back, 7 },
        { ChannelLayout.Layout6_1Front, 7 },
        { ChannelLayout.Layout7_0Front, 7 },
        { ChannelLayout.Layout7_1WideBack, 8 },
        { ChannelLayout.LayoutOctagonal, 8 },
        { ChannelLayout.LayoutStereoKeyboardAndMic, 2 },
        { ChannelLayout.Layout4_1QuadSize, 5 },
        { ChannelLayout.Layout5_1_4DownMix, 6 },
    };

    private AudioParameters AudioParameters { get; set; }
    private nint audioBufferPtr;
    private int audioBufferLengthInBytes;
    private int channelCount;

    /// <summary>
    /// Releases the unmanaged resources used by the audio handler.
    /// </summary>
    public void Dispose()
    {
        if (this.audioBufferPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(this.audioBufferPtr);
            this.audioBufferPtr = nint.Zero;
        }
    }

    /// <summary>
    /// Called to retrieve the audio parameters.
    /// </summary>
    /// <param name="chromiumWebBrowser">The Chromium web browser instance.</param>
    /// <param name="browser">The browser instance.</param>
    /// <param name="parameters">The audio parameters.</param>
    /// <returns>True if the audio parameters were retrieved successfully; otherwise, false.</returns>
    public bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref AudioParameters parameters)
    {
        //Console.WriteLine($"GetAudioParameters - Current thread: {Thread.CurrentThread.ManagedThreadId}");

        this.AudioParameters = parameters;

        if (this.audioBufferPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(this.audioBufferPtr);
            this.audioBufferPtr = nint.Zero;
            this.audioBufferLengthInBytes = 0;
        }

        this.channelCount = 0;

        if (!ChannelLayoutToChannelCount.ContainsKey(parameters.ChannelLayout))
        {
            return false;
        }

        this.channelCount = ChannelLayoutToChannelCount[parameters.ChannelLayout];

        // Declare a buffer large enough to hold 1s worth of data.
        this.audioBufferLengthInBytes =
            parameters.SampleRate * this.channelCount * sizeof(float);

        this.audioBufferPtr = Marshal.AllocHGlobal(this.audioBufferLengthInBytes);

        return true;
    }

    /// <summary>
    /// Called when an audio stream error occurs.
    /// </summary>
    /// <param name="chromiumWebBrowser">The Chromium web browser instance.</param>
    /// <param name="browser">The browser instance.</param>
    /// <param name="errorMessage">The error message.</param>
    public void OnAudioStreamError(IWebBrowser chromiumWebBrowser, IBrowser browser, string errorMessage)
    {
    }

    /// <summary>
    /// Called when an audio stream packet is received.
    /// </summary>
    /// <param name="chromiumWebBrowser">The Chromium web browser instance.</param>
    /// <param name="browser">The browser instance.</param>
    /// <param name="data">A pointer to the audio data.</param>
    /// <param name="noOfFrames">The number of frames in the packet.</param>
    /// <param name="pts">The presentation timestamp.</param>
    public unsafe void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, nint data, int noOfFrames, long pts)
    {
        //Console.WriteLine($"OnAudioStreamPacket - Current thread: {Thread.CurrentThread.ManagedThreadId}");
        if (this.audioBufferPtr == nint.Zero)
        {
            return;
        }

        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        var planarDataPtr = this.audioBufferPtr.ToPointer();

        float** dataPtr = (float**)data;

        for (var c = 0; c < this.channelCount; c++)
        {
            nint destPtr = (nint)dataPtr[c];
            Buffer.MemoryCopy(
                dataPtr[c],
                nint.Add(this.audioBufferPtr, sizeof(float) * c * noOfFrames).ToPointer(),
                sizeof(float) * noOfFrames,
                sizeof(float) * noOfFrames);
        }

        var audioFrame = new NDIlib.audio_frame_v2_t
        {
            channel_stride_in_bytes = sizeof(float) * noOfFrames,  // Stride for interleaved audio
            p_data = this.audioBufferPtr,//planarData,  // Pointer to the interleaved audio data
            no_channels = this.channelCount,  // Number of channels (e.g., 2 for stereo)
            no_samples = noOfFrames,  // Number of frames (each frame contains a sample per channel)
            sample_rate = this.AudioParameters.SampleRate,  // Sample rate of the audio
            timecode = pts // Set timecode (optional)
        };

        NDIlib.send_send_audio_v2(Program.NdiSenderPtr, ref audioFrame);
    }

    /// <summary>
    /// Called when the audio stream is started.
    /// </summary>
    /// <param name="chromiumWebBrowser">The Chromium web browser instance.</param>
    /// <param name="browser">The browser instance.</param>
    /// <param name="parameters">The audio parameters.</param>
    /// <param name="channels">The number of channels.</param>
    public void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, AudioParameters parameters, int channels)
    {
        this.AudioParameters = parameters;
    }

    /// <summary>
    /// Called when the audio stream is stopped.
    /// </summary>
    /// <param name="chromiumWebBrowser">The Chromium web browser instance.</param>
    /// <param name="browser">The browser instance.</param>
    public void OnAudioStreamStopped(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }
}
