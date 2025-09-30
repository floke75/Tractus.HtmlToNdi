using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Launcher;

internal sealed class LauncherForm : Form
{
    private readonly TextBox _ndiNameTextBox;
    private readonly NumericUpDown _portNumeric;
    private readonly TextBox _urlTextBox;
    private readonly NumericUpDown _widthNumeric;
    private readonly NumericUpDown _heightNumeric;
    private readonly TextBox _frameRateTextBox;
    private readonly CheckBox _bufferingCheckBox;
    private readonly NumericUpDown _bufferDepthNumeric;
    private readonly NumericUpDown _telemetryNumeric;
    private readonly TextBox _windowlessFrameRateTextBox;
    private readonly CheckBox _disableVsyncCheckBox;
    private readonly CheckBox _disableFrameLimitCheckBox;

    public LauncherForm(LaunchConfiguration configuration)
    {
        Text = "Tractus HTML to NDI";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font(Font.FontFamily, 9.0f, FontStyle.Regular, GraphicsUnit.Point);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        Controls.Add(layout);

        _ndiNameTextBox = new TextBox { Width = 260 };
        _portNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Width = 120
        };
        _urlTextBox = new TextBox { Width = 260 };
        _widthNumeric = new NumericUpDown
        {
            Minimum = 320,
            Maximum = 7680,
            Width = 120
        };
        _heightNumeric = new NumericUpDown
        {
            Minimum = 240,
            Maximum = 4320,
            Width = 120
        };
        _frameRateTextBox = new TextBox { Width = 120 };
        _bufferingCheckBox = new CheckBox { Text = "Enable paced output buffer" };
        _bufferDepthNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 60,
            Width = 120
        };
        _telemetryNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 300,
            Width = 120
        };
        _windowlessFrameRateTextBox = new TextBox { Width = 120 };
        _disableVsyncCheckBox = new CheckBox { Text = "Disable GPU VSync" };
        _disableFrameLimitCheckBox = new CheckBox { Text = "Disable frame rate limit" };

        AddRow(layout, "NDI source name", _ndiNameTextBox);
        AddRow(layout, "Startup URL", _urlTextBox);
        AddRow(layout, "HTTP port", _portNumeric);
        AddRow(layout, "Browser width", _widthNumeric);
        AddRow(layout, "Browser height", _heightNumeric);
        AddRow(layout, "Target FPS", _frameRateTextBox);

        var bufferPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        bufferPanel.Controls.Add(_bufferingCheckBox);
        bufferPanel.Controls.Add(new Label { Text = "Depth", AutoSize = true, Padding = new Padding(10, 3, 0, 0) });
        bufferPanel.Controls.Add(_bufferDepthNumeric);
        AddRow(layout, "Output buffering", bufferPanel);

        AddRow(layout, "Telemetry (s)", _telemetryNumeric);
        AddRow(layout, "Windowless FPS", _windowlessFrameRateTextBox);
        AddRow(layout, string.Empty, _disableVsyncCheckBox);
        AddRow(layout, string.Empty, _disableFrameLimitCheckBox);

        var buttonsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 15, 0, 0)
        };

        var launchButton = new Button
        {
            Text = "Launch",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Padding = new Padding(15, 5, 15, 5)
        };
        launchButton.Click += OnLaunchClicked;

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Padding = new Padding(15, 5, 15, 5)
        };

        buttonsPanel.Controls.Add(launchButton);
        buttonsPanel.Controls.Add(cancelButton);
        layout.Controls.Add(buttonsPanel, 0, layout.RowCount);
        layout.SetColumnSpan(buttonsPanel, 2);

        AcceptButton = launchButton;
        CancelButton = cancelButton;

        _bufferingCheckBox.CheckedChanged += (_, _) => UpdateBufferControls();

        ApplyConfiguration(configuration);
        UpdateBufferControls();
    }

    public LaunchConfiguration? Result { get; private set; }

    private void ApplyConfiguration(LaunchConfiguration configuration)
    {
        _ndiNameTextBox.Text = configuration.NdiName;
        _portNumeric.Value = Math.Min(Math.Max(configuration.Port, (int)_portNumeric.Minimum), (int)_portNumeric.Maximum);
        _urlTextBox.Text = configuration.Url;
        _widthNumeric.Value = Math.Min(Math.Max(configuration.Width, (int)_widthNumeric.Minimum), (int)_widthNumeric.Maximum);
        _heightNumeric.Value = Math.Min(Math.Max(configuration.Height, (int)_heightNumeric.Minimum), (int)_heightNumeric.Maximum);
        _frameRateTextBox.Text = configuration.FrameRateText;
        _bufferingCheckBox.Checked = configuration.EnableBuffering;
        var depth = configuration.BufferDepth > 0 ? configuration.BufferDepth : 3;
        _bufferDepthNumeric.Value = Math.Min(Math.Max(depth, (int)_bufferDepthNumeric.Minimum), (int)_bufferDepthNumeric.Maximum);
        _telemetryNumeric.Value = Math.Min(Math.Max((decimal)configuration.TelemetryInterval.TotalSeconds, _telemetryNumeric.Minimum), _telemetryNumeric.Maximum);
        _windowlessFrameRateTextBox.Text = configuration.WindowlessFrameRateText ?? string.Empty;
        _disableVsyncCheckBox.Checked = configuration.DisableGpuVsync;
        _disableFrameLimitCheckBox.Checked = configuration.DisableFrameRateLimit;
    }

    private void UpdateBufferControls()
    {
        _bufferDepthNumeric.Enabled = _bufferingCheckBox.Checked;
    }

    private void OnLaunchClicked(object? sender, EventArgs e)
    {
        var ndiName = _ndiNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ndiName))
        {
            ShowValidationError("NDI source name is required.");
            DialogResult = DialogResult.None;
            return;
        }

        var urlText = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out _))
        {
            ShowValidationError("Enter a valid absolute URL.");
            DialogResult = DialogResult.None;
            return;
        }

        FrameRate frameRate;
        try
        {
            frameRate = FrameRate.Parse(_frameRateTextBox.Text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            ShowValidationError("Enter a valid frame rate (number or fraction).");
            DialogResult = DialogResult.None;
            return;
        }

        var enableBuffering = _bufferingCheckBox.Checked;
        var bufferDepth = enableBuffering ? (int)_bufferDepthNumeric.Value : 0;
        var telemetryInterval = TimeSpan.FromSeconds((double)_telemetryNumeric.Value);

        int? windowlessFrameRateOverride = null;
        var windowlessText = _windowlessFrameRateTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(windowlessText))
        {
            if (!double.TryParse(windowlessText, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowlessValue) || windowlessValue <= 0)
            {
                ShowValidationError("Enter a valid windowless frame rate override.");
                DialogResult = DialogResult.None;
                return;
            }

            windowlessFrameRateOverride = (int)Math.Clamp(Math.Round(windowlessValue), 1, 240);
        }

        Result = new LaunchConfiguration(
            ndiName,
            (int)_portNumeric.Value,
            urlText,
            (int)_widthNumeric.Value,
            (int)_heightNumeric.Value,
            frameRate,
            _frameRateTextBox.Text.Trim().Length > 0 ? _frameRateTextBox.Text.Trim() : configurationFallback(frameRate),
            enableBuffering,
            bufferDepth,
            telemetryInterval,
            windowlessFrameRateOverride,
            string.IsNullOrEmpty(windowlessText) ? null : windowlessText,
            _disableVsyncCheckBox.Checked,
            _disableFrameLimitCheckBox.Checked);
    }

    private static string configurationFallback(FrameRate frameRate)
    {
        return frameRate.ToString();
    }

    private static void AddRow(TableLayoutPanel layout, string labelText, Control control)
    {
        var rowIndex = layout.RowCount;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = labelText,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 6, 15, 6)
        };

        layout.Controls.Add(label, 0, rowIndex);
        control.Margin = new Padding(0, 6, 0, 6);
        layout.Controls.Add(control, 1, rowIndex);
        layout.RowCount++;
    }

    private static void ShowValidationError(string message)
    {
        MessageBox.Show(message, "Tractus HTML to NDI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
