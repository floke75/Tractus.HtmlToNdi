using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Tractus.HtmlToNdi.Launcher;

public class LauncherForm : Form
{
    private readonly TextBox _ndiNameTextBox;
    private readonly NumericUpDown _portNumeric;
    private readonly TextBox _urlTextBox;
    private readonly NumericUpDown _widthNumeric;
    private readonly NumericUpDown _heightNumeric;
    private readonly TextBox _frameRateTextBox;
    private readonly CheckBox _enableBufferingCheckbox;
    private readonly NumericUpDown _bufferDepthNumeric;
    private readonly NumericUpDown _telemetryNumeric;
    private readonly TextBox _windowlessFrameRateTextBox;
    private readonly CheckBox _disableGpuVsyncCheckbox;
    private readonly CheckBox _disableFrameRateLimitCheckbox;

    public LauncherSettings? SelectedSettings { get; private set; }

    public LauncherForm(LauncherSettings settings)
    {
        Text = "HTML to NDI Launcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        Controls.Add(layout);

        _ndiNameTextBox = new TextBox { Text = settings.NdiName, Dock = DockStyle.Fill };
        AddRow(layout, "NDI Source Name:", _ndiNameTextBox);

        _portNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(settings.Port, 1, 65535),
            Dock = DockStyle.Fill,
        };
        AddRow(layout, "HTTP Port:", _portNumeric);

        _urlTextBox = new TextBox { Text = settings.Url, Dock = DockStyle.Fill };
        AddRow(layout, "Startup URL:", _urlTextBox);

        _widthNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Value = Math.Clamp(settings.Width, 1, 10000),
            Dock = DockStyle.Fill,
        };
        AddRow(layout, "Width (px):", _widthNumeric);

        _heightNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Value = Math.Clamp(settings.Height, 1, 10000),
            Dock = DockStyle.Fill,
        };
        AddRow(layout, "Height (px):", _heightNumeric);

        _frameRateTextBox = new TextBox { Text = settings.FrameRate, Dock = DockStyle.Fill };
        AddRow(layout, "Frame Rate (fps):", _frameRateTextBox);

        _enableBufferingCheckbox = new CheckBox
        {
            Checked = settings.EnableOutputBuffer,
            Text = "Enable paced output buffer",
            AutoSize = true,
        };
        AddRow(layout, string.Empty, _enableBufferingCheckbox);

        _bufferDepthNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 30,
            Value = Math.Clamp(settings.BufferDepth, 1, 30),
            Dock = DockStyle.Fill,
            Enabled = settings.EnableOutputBuffer,
        };
        AddRow(layout, "Buffer Depth (frames):", _bufferDepthNumeric);

        _telemetryNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 600,
            Value = Convert.ToDecimal(Math.Clamp(settings.TelemetryIntervalSeconds, 1, 600)),
            Dock = DockStyle.Fill,
            DecimalPlaces = 1,
            Increment = 0.5m,
        };
        AddRow(layout, "Telemetry Interval (s):", _telemetryNumeric);

        _windowlessFrameRateTextBox = new TextBox
        {
            Text = settings.WindowlessFrameRate ?? string.Empty,
            Dock = DockStyle.Fill,
        };
        AddRow(layout, "Windowless Frame Rate Override:", _windowlessFrameRateTextBox);

        _disableGpuVsyncCheckbox = new CheckBox
        {
            Checked = settings.DisableGpuVsync,
            Text = "Disable GPU VSync",
            AutoSize = true,
        };
        AddRow(layout, string.Empty, _disableGpuVsyncCheckbox);

        _disableFrameRateLimitCheckbox = new CheckBox
        {
            Checked = settings.DisableFrameRateLimit,
            Text = "Disable frame rate limit",
            AutoSize = true,
        };
        AddRow(layout, string.Empty, _disableFrameRateLimitCheckbox);

        _enableBufferingCheckbox.CheckedChanged += (_, _) =>
        {
            _bufferDepthNumeric.Enabled = _enableBufferingCheckbox.Checked;
        };

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var launchButton = new Button
        {
            Text = "Launch",
            AutoSize = true,
        };
        launchButton.Click += (_, _) => Launch();

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };

        buttonPanel.Controls.Add(launchButton);
        buttonPanel.Controls.Add(cancelButton);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttonsRowIndex = layout.RowCount++;
        layout.Controls.Add(buttonPanel, 0, buttonsRowIndex);
        layout.SetColumnSpan(buttonPanel, 2);

        AcceptButton = launchButton;
        CancelButton = cancelButton;
    }

    private static void AddRow(TableLayoutPanel layout, string labelText, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var rowIndex = layout.RowCount++;

        if (!string.IsNullOrEmpty(labelText))
        {
            var label = new Label
            {
                Text = labelText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 12, 6),
            };
            layout.Controls.Add(label, 0, rowIndex);
        }
        else
        {
            layout.Controls.Add(new Label { AutoSize = true }, 0, rowIndex);
        }

        control.Margin = new Padding(0, 6, 0, 6);
        layout.Controls.Add(control, 1, rowIndex);
    }

    private void Launch()
    {
        if (string.IsNullOrWhiteSpace(_ndiNameTextBox.Text))
        {
            ShowValidationError("NDI source name is required.");
            return;
        }

        if (!Uri.TryCreate(_urlTextBox.Text.Trim(), UriKind.Absolute, out _))
        {
            ShowValidationError("Startup URL must be a valid absolute URI.");
            return;
        }

        var frameRate = _frameRateTextBox.Text.Trim();
        if (frameRate.Length == 0)
        {
            ShowValidationError("Frame rate is required (decimal or fraction).");
            return;
        }

        var windowlessOverride = _windowlessFrameRateTextBox.Text.Trim();
        if (windowlessOverride.Length > 0 && !double.TryParse(windowlessOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            ShowValidationError("Windowless frame rate override must be a number.");
            return;
        }

        SelectedSettings = new LauncherSettings
        {
            NdiName = _ndiNameTextBox.Text.Trim(),
            Port = Convert.ToInt32(_portNumeric.Value),
            Url = _urlTextBox.Text.Trim(),
            Width = Convert.ToInt32(_widthNumeric.Value),
            Height = Convert.ToInt32(_heightNumeric.Value),
            FrameRate = frameRate,
            EnableOutputBuffer = _enableBufferingCheckbox.Checked,
            BufferDepth = Convert.ToInt32(_bufferDepthNumeric.Value),
            TelemetryIntervalSeconds = decimal.ToDouble(_telemetryNumeric.Value),
            WindowlessFrameRate = windowlessOverride.Length > 0 ? windowlessOverride : null,
            DisableGpuVsync = _disableGpuVsyncCheckbox.Checked,
            DisableFrameRateLimit = _disableFrameRateLimitCheckbox.Checked,
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(this, message, "Validation error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
