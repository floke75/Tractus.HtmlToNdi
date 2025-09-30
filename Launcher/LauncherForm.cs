using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Tractus.HtmlToNdi.Launcher;

public class LauncherForm : Form
{
    private readonly LauncherSettings _settings;

    private readonly TextBox _ndiNameText = new();
    private readonly NumericUpDown _portNumeric = new();
    private readonly TextBox _urlText = new();
    private readonly NumericUpDown _widthNumeric = new();
    private readonly NumericUpDown _heightNumeric = new();
    private readonly TextBox _fpsText = new();
    private readonly CheckBox _enableBufferCheck = new();
    private readonly NumericUpDown _bufferDepthNumeric = new();
    private readonly NumericUpDown _telemetryNumeric = new();
    private readonly TextBox _windowlessText = new();
    private readonly CheckBox _disableVsyncCheck = new();
    private readonly CheckBox _disableFrameLimitCheck = new();
    private readonly CheckBox _debugCheck = new();
    private readonly CheckBox _quietCheck = new();

    public LauncherSettings UpdatedSettings => _settings;

    public LauncherForm(LauncherSettings settings)
    {
        _settings = settings;
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Tractus HTML to NDI Launcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 480);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        void AddLabeledControl(string labelText, Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label
            {
                Text = labelText,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Margin = new Padding(0, 6, 6, 6)
            };
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(label, 0, layout.RowCount);
            layout.Controls.Add(control, 1, layout.RowCount);
            layout.RowCount++;
        }

        _ndiNameText.Text = _settings.NdiName;
        AddLabeledControl("NDI Source Name", _ndiNameText);

        _portNumeric.Minimum = 1;
        _portNumeric.Maximum = 65535;
        _portNumeric.Value = Math.Clamp(_settings.Port, 1, 65535);
        AddLabeledControl("HTTP Port", _portNumeric);

        _urlText.Text = _settings.Url;
        AddLabeledControl("Startup URL", _urlText);

        _widthNumeric.Minimum = 1;
        _widthNumeric.Maximum = 16384;
        _widthNumeric.Value = Math.Clamp(_settings.Width, 1, 16384);
        AddLabeledControl("Width", _widthNumeric);

        _heightNumeric.Minimum = 1;
        _heightNumeric.Maximum = 16384;
        _heightNumeric.Value = Math.Clamp(_settings.Height, 1, 16384);
        AddLabeledControl("Height", _heightNumeric);

        _fpsText.Text = _settings.Fps;
        AddLabeledControl("Frame Rate (fps or fraction)", _fpsText);

        _bufferDepthNumeric.Minimum = 0;
        _bufferDepthNumeric.Maximum = 120;
        _bufferDepthNumeric.Value = _settings.BufferDepth.HasValue
            ? Math.Clamp(_settings.BufferDepth.Value, 0, 120)
            : 0;
        AddLabeledControl("Buffer Depth (0 to use default)", _bufferDepthNumeric);

        _enableBufferCheck.Text = "Enable output buffer when depth is 0";
        _enableBufferCheck.Checked = _settings.EnableOutputBuffer;
        _enableBufferCheck.Margin = new Padding(0, 6, 0, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.Controls.Add(_enableBufferCheck, 1, layout.RowCount);
        layout.RowCount++;

        _telemetryNumeric.Minimum = 1;
        _telemetryNumeric.Maximum = 3600;
        _telemetryNumeric.DecimalPlaces = 1;
        _telemetryNumeric.Increment = 0.5M;
        _telemetryNumeric.Value = (decimal)Math.Clamp(_settings.TelemetryIntervalSeconds, 1, 3600);
        AddLabeledControl("Telemetry Interval (seconds)", _telemetryNumeric);

        _windowlessText.Text = _settings.WindowlessFrameRate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        AddLabeledControl("Windowless Frame Rate (optional)", _windowlessText);

        _disableVsyncCheck.Text = "Disable GPU VSync";
        _disableVsyncCheck.Checked = _settings.DisableGpuVsync;
        _disableVsyncCheck.Margin = new Padding(0, 6, 0, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.Controls.Add(_disableVsyncCheck, 1, layout.RowCount);
        layout.RowCount++;

        _disableFrameLimitCheck.Text = "Disable Chromium frame rate limit";
        _disableFrameLimitCheck.Checked = _settings.DisableFrameRateLimit;
        _disableFrameLimitCheck.Margin = new Padding(0, 6, 0, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.Controls.Add(_disableFrameLimitCheck, 1, layout.RowCount);
        layout.RowCount++;

        _debugCheck.Text = "Enable debug logging (-debug)";
        _debugCheck.Checked = _settings.DebugLogging;
        _debugCheck.Margin = new Padding(0, 6, 0, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.Controls.Add(_debugCheck, 1, layout.RowCount);
        layout.RowCount++;

        _quietCheck.Text = "Quiet console (-quiet)";
        _quietCheck.Checked = _settings.QuietLogging;
        _quietCheck.Margin = new Padding(0, 6, 0, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.Controls.Add(_quietCheck, 1, layout.RowCount);
        layout.RowCount++;

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0)
        };

        var launchButton = new Button
        {
            Text = "Launch",
            AutoSize = true,
            Margin = new Padding(6)
        };
        launchButton.Click += (_, _) => Launch();

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Margin = new Padding(6)
        };
        cancelButton.Click += (_, _) => Close();

        buttonPanel.Controls.Add(launchButton);
        buttonPanel.Controls.Add(cancelButton);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label(), 0, layout.RowCount);
        layout.Controls.Add(buttonPanel, 1, layout.RowCount);
        layout.RowCount++;

        Controls.Add(layout);

        AcceptButton = launchButton;
        CancelButton = cancelButton;
    }

    private void Launch()
    {
        if (string.IsNullOrWhiteSpace(_ndiNameText.Text))
        {
            MessageBox.Show(this, "Please provide an NDI source name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _ndiNameText.Focus();
            return;
        }

        if (!Uri.TryCreate(_urlText.Text, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Please provide a valid absolute URL.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _urlText.Focus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_windowlessText.Text) &&
            !double.TryParse(_windowlessText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowlessFps))
        {
            MessageBox.Show(this, "Windowless frame rate must be a number.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _windowlessText.Focus();
            return;
        }

        _settings.NdiName = _ndiNameText.Text.Trim();
        _settings.Port = (int)_portNumeric.Value;
        _settings.Url = _urlText.Text.Trim();
        _settings.Width = (int)_widthNumeric.Value;
        _settings.Height = (int)_heightNumeric.Value;
        _settings.Fps = _fpsText.Text.Trim();
        _settings.BufferDepth = (int)_bufferDepthNumeric.Value > 0 ? (int)_bufferDepthNumeric.Value : null;
        _settings.EnableOutputBuffer = _enableBufferCheck.Checked;
        _settings.TelemetryIntervalSeconds = (double)_telemetryNumeric.Value;
        _settings.WindowlessFrameRate = string.IsNullOrWhiteSpace(_windowlessText.Text) ? null : windowlessFps;
        _settings.DisableGpuVsync = _disableVsyncCheck.Checked;
        _settings.DisableFrameRateLimit = _disableFrameLimitCheck.Checked;
        _settings.DebugLogging = _debugCheck.Checked;
        _settings.QuietLogging = _quietCheck.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }
}
