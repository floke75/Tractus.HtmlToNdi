using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tractus.HtmlToNdi.Launcher;

/// <summary>
/// Represents the main form for the launcher application.
/// </summary>
public sealed class LauncherForm : Form
{
    private readonly TextBox _ndiNameTextBox;
    private readonly NumericUpDown _portNumericUpDown;
    private readonly TextBox _urlTextBox;
    private readonly NumericUpDown _widthNumericUpDown;
    private readonly NumericUpDown _heightNumericUpDown;
    private readonly TextBox _frameRateTextBox;
    private readonly CheckBox _enableBufferingCheckBox;
    private readonly NumericUpDown _bufferDepthNumericUpDown;
    private readonly CheckBox _allowLatencyExpansionCheckBox;
    private readonly NumericUpDown _telemetryNumericUpDown;
    private readonly TextBox _windowlessFrameRateTextBox;
    private readonly CheckBox _disableGpuVsyncCheckBox;
    private readonly CheckBox _disableFrameRateLimitCheckBox;
    private readonly CheckBox _alignWithCaptureTimestampsCheckBox;
    private readonly CheckBox _enableCadenceTelemetryCheckBox;
    private readonly CheckBox _enablePacedInvalidationCheckBox;
    private readonly CheckBox _disablePacedInvalidationCheckBox;
    private readonly CheckBox _enableCaptureBackpressureCheckBox;
    private readonly CheckBox _enablePumpCadenceAdaptationCheckBox;
    private readonly CheckBox _enableCompositorCaptureCheckBox;
    private bool _suppressPacingCheckboxUpdates;

    /// <summary>
    /// Gets the selected launch parameters.
    /// </summary>
    public LaunchParameters? SelectedParameters { get; private set; }

    /// <summary>
    /// Gets the current launcher settings.
    /// </summary>
    public LauncherSettings? CurrentSettings { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LauncherForm"/> class.
    /// </summary>
    /// <param name="initialSettings">The initial settings to populate the form with.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="initialSettings"/> is null.</exception>
    public LauncherForm(LauncherSettings initialSettings)
    {
        if (initialSettings is null)
        {
            throw new ArgumentNullException(nameof(initialSettings));
        }

        Text = "Tractus HtmlToNdi Launcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 0,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

        Controls.Add(table);

        _ndiNameTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(table, "NDI Source Name", _ndiNameTextBox);

        _portNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(table, "HTTP Port", _portNumericUpDown);

        _urlTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(table, "Startup URL", _urlTextBox);

        _widthNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(table, "Width", _widthNumericUpDown);

        _heightNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(table, "Height", _heightNumericUpDown);

        _frameRateTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(table, "Frame Rate (fps)", _frameRateTextBox);

        _enableBufferingCheckBox = new CheckBox
        {
            Text = "Enable paced output buffer",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Output Buffer", _enableBufferingCheckBox);

        _bufferDepthNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 60,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(table, "Buffer Depth (frames)", _bufferDepthNumericUpDown);

        _allowLatencyExpansionCheckBox = new CheckBox
        {
            Text = "Play queued frames during recovery (variable latency)",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Latency Expansion", _allowLatencyExpansionCheckBox);

        _telemetryNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3600,
            DecimalPlaces = 1,
            Increment = 0.5M,
            Dock = DockStyle.Fill,
        };
        AddRow(table, "Telemetry Interval (s)", _telemetryNumericUpDown);

        _alignWithCaptureTimestampsCheckBox = new CheckBox
        {
            Text = "Align paced output to capture timestamps",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Capture Alignment", _alignWithCaptureTimestampsCheckBox);

        _enableCadenceTelemetryCheckBox = new CheckBox
        {
            Text = "Log capture/output cadence metrics",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Cadence Telemetry", _enableCadenceTelemetryCheckBox);

        _enablePacedInvalidationCheckBox = new CheckBox
        {
            Text = "Request Chromium frames from paced sender",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Paced Invalidation", _enablePacedInvalidationCheckBox);

        _disablePacedInvalidationCheckBox = new CheckBox
        {
            Text = "Force periodic Chromium invalidation",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Disable Pacing", _disablePacedInvalidationCheckBox);

        _enableCaptureBackpressureCheckBox = new CheckBox
        {
            Text = "Pause Chromium when buffer is ahead",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Capture Backpressure", _enableCaptureBackpressureCheckBox);

        _enablePumpCadenceAdaptationCheckBox = new CheckBox
        {
            Text = "Adapt Chromium pump using cadence telemetry",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Pump Cadence Adaptation", _enablePumpCadenceAdaptationCheckBox);

        _enableCompositorCaptureCheckBox = new CheckBox
        {
            Text = "Capture frames from Chromium compositor",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Compositor Capture", _enableCompositorCaptureCheckBox);

        _windowlessFrameRateTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(table, "Windowless Frame Rate", _windowlessFrameRateTextBox);

        _disableGpuVsyncCheckBox = new CheckBox
        {
            Text = "Disable GPU VSync",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "GPU VSync", _disableGpuVsyncCheckBox);

        _disableFrameRateLimitCheckBox = new CheckBox
        {
            Text = "Disable frame rate limit",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(table, "Frame Rate Limit", _disableFrameRateLimitCheckBox);

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
            Padding = new Padding(12, 6, 12, 6)
        };
        launchButton.Click += (_, _) => OnLaunch();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6)
        };

        buttonPanel.Controls.Add(launchButton);
        buttonPanel.Controls.Add(cancelButton);

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Panel(), 0, table.RowCount);
        table.Controls.Add(buttonPanel, 1, table.RowCount);
        table.RowCount++;

        AcceptButton = launchButton;
        CancelButton = cancelButton;

        _enableBufferingCheckBox.CheckedChanged += (_, _) => UpdateBufferingDependentControls();
        _enablePacedInvalidationCheckBox.CheckedChanged += OnEnablePacedInvalidationCheckedChanged;
        _disablePacedInvalidationCheckBox.CheckedChanged += OnDisablePacedInvalidationCheckedChanged;

        ApplySettings(initialSettings);
    }

    private void ApplySettings(LauncherSettings settings)
    {
        _ndiNameTextBox.Text = settings.NdiName;
        _portNumericUpDown.Value = Math.Clamp(settings.Port, (int)_portNumericUpDown.Minimum, (int)_portNumericUpDown.Maximum);
        _urlTextBox.Text = settings.Url;
        _widthNumericUpDown.Value = Math.Clamp(settings.Width, (int)_widthNumericUpDown.Minimum, (int)_widthNumericUpDown.Maximum);
        _heightNumericUpDown.Value = Math.Clamp(settings.Height, (int)_heightNumericUpDown.Minimum, (int)_heightNumericUpDown.Maximum);
        _frameRateTextBox.Text = settings.FrameRate;
        _enableBufferingCheckBox.Checked = settings.EnableBuffering;
        _bufferDepthNumericUpDown.Value = Math.Clamp(
            settings.BufferDepth <= 0 ? 1 : settings.BufferDepth,
            (int)_bufferDepthNumericUpDown.Minimum,
            (int)_bufferDepthNumericUpDown.Maximum);
        _bufferDepthNumericUpDown.Enabled = settings.EnableBuffering;
        _allowLatencyExpansionCheckBox.Checked = settings.AllowLatencyExpansion;
        _allowLatencyExpansionCheckBox.Enabled = settings.EnableBuffering;
        var telemetryValue = (decimal)Math.Clamp(settings.TelemetryIntervalSeconds, (double)_telemetryNumericUpDown.Minimum, (double)_telemetryNumericUpDown.Maximum);
        _telemetryNumericUpDown.Value = telemetryValue;
        _alignWithCaptureTimestampsCheckBox.Checked = settings.AlignWithCaptureTimestamps;
        _enableCadenceTelemetryCheckBox.Checked = settings.EnableCadenceTelemetry;
        _suppressPacingCheckboxUpdates = true;
        _enablePacedInvalidationCheckBox.Checked = settings.EnablePacedInvalidation;
        _disablePacedInvalidationCheckBox.Checked = settings.DisablePacedInvalidation;
        _suppressPacingCheckboxUpdates = false;
        _enableCaptureBackpressureCheckBox.Checked = settings.EnableCaptureBackpressure;
        _enablePumpCadenceAdaptationCheckBox.Checked = settings.EnablePumpCadenceAdaptation;
        _enableCompositorCaptureCheckBox.Checked = settings.EnableCompositorCapture;
        _windowlessFrameRateTextBox.Text = settings.WindowlessFrameRateOverride ?? string.Empty;
        _disableGpuVsyncCheckBox.Checked = settings.DisableGpuVsync;
        _disableFrameRateLimitCheckBox.Checked = settings.DisableFrameRateLimit;

        UpdateBufferingDependentControls();
    }

    private void UpdateBufferingDependentControls()
    {
        var bufferingEnabled = _enableBufferingCheckBox.Checked;
        _bufferDepthNumericUpDown.Enabled = bufferingEnabled;
        _allowLatencyExpansionCheckBox.Enabled = bufferingEnabled;
        _enablePacedInvalidationCheckBox.Enabled = bufferingEnabled;
        _disablePacedInvalidationCheckBox.Enabled = true;
        _enablePumpCadenceAdaptationCheckBox.Enabled = bufferingEnabled;

        var backpressureAllowed = bufferingEnabled
            && _enablePacedInvalidationCheckBox.Checked
            && !_disablePacedInvalidationCheckBox.Checked;
        _enableCaptureBackpressureCheckBox.Enabled = backpressureAllowed;
        if (!backpressureAllowed)
        {
            _enableCaptureBackpressureCheckBox.Checked = false;
        }
    }

    private void OnEnablePacedInvalidationCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressPacingCheckboxUpdates)
        {
            return;
        }

        if (_enablePacedInvalidationCheckBox.Checked)
        {
            _suppressPacingCheckboxUpdates = true;
            _disablePacedInvalidationCheckBox.Checked = false;
            _suppressPacingCheckboxUpdates = false;
        }

        UpdateBufferingDependentControls();
    }

    private void OnDisablePacedInvalidationCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressPacingCheckboxUpdates)
        {
            return;
        }

        if (_disablePacedInvalidationCheckBox.Checked)
        {
            _suppressPacingCheckboxUpdates = true;
            _enablePacedInvalidationCheckBox.Checked = false;
            _enableCaptureBackpressureCheckBox.Checked = false;
            _suppressPacingCheckboxUpdates = false;
        }

        UpdateBufferingDependentControls();
    }

    private void OnLaunch()
    {
        var ndiName = _ndiNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ndiName))
        {
            MessageBox.Show(this, "Please enter an NDI source name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _ndiNameTextBox.Focus();
            return;
        }

        var url = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Please enter a valid absolute URL.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _urlTextBox.Focus();
            return;
        }

        var frameRateText = _frameRateTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(frameRateText))
        {
            frameRateText = "60";
        }

        var settings = new LauncherSettings
        {
            NdiName = ndiName,
            Port = (int)_portNumericUpDown.Value,
            Url = url,
            Width = (int)_widthNumericUpDown.Value,
            Height = (int)_heightNumericUpDown.Value,
            FrameRate = frameRateText,
            EnableBuffering = _enableBufferingCheckBox.Checked,
            BufferDepth = (int)_bufferDepthNumericUpDown.Value,
            TelemetryIntervalSeconds = (double)_telemetryNumericUpDown.Value,
            AlignWithCaptureTimestamps = _alignWithCaptureTimestampsCheckBox.Checked,
            EnableCadenceTelemetry = _enableCadenceTelemetryCheckBox.Checked,
            WindowlessFrameRateOverride = string.IsNullOrWhiteSpace(_windowlessFrameRateTextBox.Text)
                ? null
                : _windowlessFrameRateTextBox.Text.Trim(),
            DisableGpuVsync = _disableGpuVsyncCheckBox.Checked,
            DisableFrameRateLimit = _disableFrameRateLimitCheckBox.Checked,
            AllowLatencyExpansion = _allowLatencyExpansionCheckBox.Checked,
            EnablePacedInvalidation = _enableBufferingCheckBox.Checked && _enablePacedInvalidationCheckBox.Checked,
            DisablePacedInvalidation = _disablePacedInvalidationCheckBox.Checked,
            EnableCaptureBackpressure = _enableBufferingCheckBox.Checked && _enableCaptureBackpressureCheckBox.Checked,
            EnablePumpCadenceAdaptation = _enableBufferingCheckBox.Checked && _enablePumpCadenceAdaptationCheckBox.Checked,
            EnableCompositorCapture = _enableCompositorCaptureCheckBox.Checked
        };

        try
        {
            SelectedParameters = LaunchParameters.FromSettings(settings);
            CurrentSettings = settings;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void AddRow(TableLayoutPanel table, string labelText, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            Padding = new Padding(0, 4, 8, 4)
        };

        table.Controls.Add(label, 0, table.RowCount);
        table.Controls.Add(control, 1, table.RowCount);
        table.RowCount++;
    }
}
