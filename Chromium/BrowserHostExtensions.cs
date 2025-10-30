using System;
using System.Reflection;
using CefSharp;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Provides compatibility helpers for interacting with <see cref="IBrowserHost"/> APIs that changed across CefSharp versions.
/// </summary>
internal static class BrowserHostExtensions
{
    private const string ManualBeginFrameMethod = "SetAutoBeginFrameEnabled";
    private const string FrameRateControllerProperty = "FrameRateController";

    /// <summary>
    /// Attempts to toggle Chromium's automatic begin-frame scheduling.
    /// </summary>
    /// <param name="host">The browser host to control.</param>
    /// <param name="enabled">Whether automatic begin-frame scheduling should be enabled.</param>
    /// <exception cref="NotSupportedException">Thrown when the current CefSharp build does not expose any compatible API.</exception>
    public static void SetAutoBeginFrameEnabled(this IBrowserHost host, bool enabled)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        // CefSharp 123 and earlier surface SetAutoBeginFrameEnabled directly on IBrowserHost.
        var directMethod = host.GetType().GetMethod(
            ManualBeginFrameMethod,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (directMethod is not null)
        {
            directMethod.Invoke(host, new object[] { enabled });
            return;
        }

        // CefSharp 129 moved the API behind IFrameRateController.
        var controller = host
            .GetType()
            .GetProperty(
                FrameRateControllerProperty,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(host);
        if (controller is not null)
        {
            var controllerMethod = controller.GetType().GetMethod(
                ManualBeginFrameMethod,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (controllerMethod is not null)
            {
                controllerMethod.Invoke(controller, new object[] { enabled });
                return;
            }
        }

        throw new NotSupportedException("Manual begin-frame control is not supported by this CefSharp build.");
    }
}
