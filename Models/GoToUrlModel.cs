namespace Tractus.HtmlToNdi.Models;

/// <summary>
/// Represents the model for the "set URL" API endpoint.
/// </summary>
public class GoToUrlModel
{
    /// <summary>
    /// Gets or sets the URL to navigate to.
    /// </summary>
    public string Url { get; set; }
}

/// <summary>
/// Represents the model for the "send keystroke" API endpoint.
/// </summary>
public class SendKeystrokeModel
{
    /// <summary>
    /// Gets or sets the string of characters to send as keystrokes.
    /// </summary>
    public string ToSend { get; set; }
}