using System.Reflection;
using System.Runtime.Serialization;
using Serilog;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Models;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class CefWrapperInputValidationTests
{
    [Fact]
    public void SetUrl_DoesNotThrow_WhenUrlIsNull()
    {
        var wrapper = CreateWrapper();
        SetUrlProperty(wrapper, "existing");

        var exception = Record.Exception(() => wrapper.SetUrl(null));

        Assert.Null(exception);
        Assert.Equal("existing", wrapper.Url);
    }

    [Fact]
    public void SetUrl_DoesNotThrow_WhenUrlIsWhitespace()
    {
        var wrapper = CreateWrapper();
        SetUrlProperty(wrapper, "existing");

        var exception = Record.Exception(() => wrapper.SetUrl("   "));

        Assert.Null(exception);
        Assert.Equal("existing", wrapper.Url);
    }

    [Fact]
    public void SendKeystrokes_DoesNotThrow_WhenModelIsNull()
    {
        var wrapper = CreateWrapper();

        var exception = Record.Exception(() => wrapper.SendKeystrokes(null));

        Assert.Null(exception);
    }

    [Fact]
    public void SendKeystrokes_DoesNotThrow_WhenPayloadIsEmpty()
    {
        var wrapper = CreateWrapper();
        var model = new SendKeystrokeModel
        {
            ToSend = string.Empty,
        };

        var exception = Record.Exception(() => wrapper.SendKeystrokes(model));

        Assert.Null(exception);
    }

    private static CefWrapper CreateWrapper()
    {
        var wrapper = (CefWrapper)FormatterServices.GetUninitializedObject(typeof(CefWrapper));
        SetField(wrapper, "logger", CreateLogger());
        SetField(wrapper, "browser", null);
        return wrapper;
    }

    private static ILogger CreateLogger()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .CreateLogger();
    }

    private static void SetField<T>(CefWrapper wrapper, string name, T value)
    {
        typeof(CefWrapper).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(wrapper, value);
    }

    private static void SetUrlProperty(CefWrapper wrapper, string value)
    {
        typeof(CefWrapper).GetProperty("Url", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(wrapper, value);
    }
}
