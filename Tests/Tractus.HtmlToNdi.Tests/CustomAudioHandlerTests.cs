using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Tractus.HtmlToNdi.Chromium;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class CustomAudioHandlerTests
{
    [Fact]
    public void OnAudioStreamPacket_ReallocatesBuffer_WhenPacketExceedsCapacity()
    {
        var handler = new CustomAudioHandler();
        try
        {
            const int channelCount = 2;
            const int initialFrames = 8;
            var initialBytes = sizeof(float) * channelCount * initialFrames;

            SetField(handler, "channelCount", channelCount);
            SetField(handler, "audioBufferLengthInBytes", initialBytes);
            SetField(handler, "audioBufferPtr", Marshal.AllocHGlobal(initialBytes));

            var frameCount = initialFrames * 4;
            var perChannelBytes = sizeof(float) * frameCount;

            var channelPointers = new IntPtr[channelCount];
            try
            {
                for (var i = 0; i < channelCount; i++)
                {
                    channelPointers[i] = Marshal.AllocHGlobal(perChannelBytes);
                    var samples = Enumerable.Repeat((float)(i + 1), frameCount).ToArray();
                    Marshal.Copy(samples, 0, channelPointers[i], frameCount);
                }

                var pointerArray = Marshal.AllocHGlobal(IntPtr.Size * channelCount);
                try
                {
                    for (var i = 0; i < channelCount; i++)
                    {
                        Marshal.WriteIntPtr(pointerArray, i * IntPtr.Size, channelPointers[i]);
                    }

                    handler.OnAudioStreamPacket(null!, null!, pointerArray, frameCount, pts: 123);

                    var updatedLength = (int)GetField(handler, "audioBufferLengthInBytes");
                    Assert.Equal(sizeof(float) * channelCount * frameCount, updatedLength);

                    var bufferPtr = (IntPtr)GetField(handler, "audioBufferPtr");
                    Assert.NotEqual(IntPtr.Zero, bufferPtr);

                    var firstChannel = new float[frameCount];
                    Marshal.Copy(bufferPtr, firstChannel, 0, frameCount);
                    Assert.All(firstChannel, value => Assert.Equal(1f, value));

                    var secondChannel = new float[frameCount];
                    Marshal.Copy(IntPtr.Add(bufferPtr, perChannelBytes), secondChannel, 0, frameCount);
                    Assert.All(secondChannel, value => Assert.Equal(2f, value));
                }
                finally
                {
                    Marshal.FreeHGlobal(pointerArray);
                }
            }
            finally
            {
                foreach (var ptr in channelPointers)
                {
                    if (ptr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    private static void SetField<T>(CustomAudioHandler handler, string name, T value)
    {
        typeof(CustomAudioHandler).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(handler, value);
    }

    private static object GetField(CustomAudioHandler handler, string name)
    {
        return typeof(CustomAudioHandler).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(handler)!;
    }
}
