using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Tractus.HtmlToNdi.Launcher;

internal sealed class LauncherForm : Form
{
    private readonly TextBox _applicationPathTextBox;
    private readonly TextBox _ndiNameTextBox;
    private readonly NumericUpDown _portNumericUpDown;
    private readonly TextBox _urlTextBox;
    private readonly NumericUpDown _widthNumericUpDown;
    private readonly NumericUpDown _heightNumericUpDown;
    private readonly TextBox _frameRateTextBox;
    private readonly CheckBox _enableBufferingCheckBox;
    private readonly NumericUpDown _bufferDepthNumericUpDown;
    private readonly NumericUpDown _telemetryIntervalNumericUpDown;
    private readonly NumericUpDown _windowlessFrameRateNumericUpDown;
    private readonly CheckBox _disableGpuVsyncCheckBox;
    private readonly CheckBox _disableFrameRateLimitCheckBox;
    private readonly CheckBox _debugLoggingCheckBox;
    private readonly CheckBox _quietLoggingCheckBox;
    private readonly Button _launchButton;
    private readonly Button _browseButton;

    private LauncherSettings _settings;

    public LauncherForm()
    {
        Text = "Tractus HTML to NDI Launcher";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;

        _applicationPathTextBox = new TextBox { Width = 320 };
        _browseButton = new Button
        {
            Text = "Browse...",
            AutoSize = true
        };
        _browseButton.Click += OnBrowseClick;

        _ndiNameTextBox = new TextBox { Width = 200 };
        _portNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = 9999
        };
        _urlTextBox = new TextBox { Width = 320 };
        _widthNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 16384,
            Value = 1920
        };
        _heightNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 16384,
            Value = 1080
        };
        _frameRateTextBox = new TextBox { Width = 120 };
        _enableBufferingCheckBox = new CheckBox { Text = "Enable output buffer" };
        _enableBufferingCheckBox.CheckedChanged += (_, _) => UpdateBufferDepthState();
        _bufferDepthNumericUpDown = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 30,
            Value = 3
        };
        _telemetryIntervalNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3600,
            Value = 10
        };
        _windowlessFrameRateNumericUpDown = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 240,
            DecimalPlaces = 0,
            Value = 0
        };
        _disableGpuVsyncCheckBox = new CheckBox { Text = "Disable GPU VSync" };
        _disableFrameRateLimitCheckBox = new CheckBox { Text = "Disable frame rate limit" };
        _debugLoggingCheckBox = new CheckBox { Text = "Enable debug logging" };
        _quietLoggingCheckBox = new CheckBox { Text = "Quiet console logging" };

        _launchButton = new Button
        {
            Text = "Launch",
            AutoSize = true
        };
        _launchButton.Click += OnLaunchClicked;

        AcceptButton = _launchButton;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 0,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddLabeledControl(string labelText, Control control, Control? trailingControl = null)
        {
            var rowIndex = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 6, 6)
            }, 0, rowIndex);

            control.Margin = new Padding(0, 4, 6, 4);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(control, 1, rowIndex);

            if (trailingControl != null)
            {
                trailingControl.Margin = new Padding(0, 4, 0, 4);
                trailingControl.Anchor = AnchorStyles.Left;
                layout.Controls.Add(trailingControl, 2, rowIndex);
            }
            else
            {
                layout.SetColumnSpan(control, 2);
            }
        }

        AddLabeledControl("Application", _applicationPathTextBox, _browseButton);
        AddLabeledControl("NDI name", _ndiNameTextBox);
        AddLabeledControl("HTTP port", _portNumericUpDown);
        AddLabeledControl("Start URL", _urlTextBox);
        AddLabeledControl("Width", _widthNumericUpDown);
        AddLabeledControl("Height", _heightNumericUpDown);
        AddLabeledControl("Frame rate", _frameRateTextBox);
        AddLabeledControl("Enable buffering", _enableBufferingCheckBox);
        AddLabeledControl("Buffer depth", _bufferDepthNumericUpDown);
        AddLabeledControl("Telemetry interval (s)", _telemetryIntervalNumericUpDown);
        AddLabeledControl("Windowless frame rate", _windowlessFrameRateNumericUpDown);
        AddLabeledControl(string.Empty, _disableGpuVsyncCheckBox);
        AddLabeledControl(string.Empty, _disableFrameRateLimitCheckBox);
        AddLabeledControl(string.Empty, _debugLoggingCheckBox);
        AddLabeledControl(string.Empty, _quietLoggingCheckBox);

        layout.Controls.Add(_launchButton, 2, layout.RowCount++);
        layout.SetCellPosition(_launchButton, new TableLayoutPanelCellPosition(2, layout.RowCount - 1));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _launchButton.Margin = new Padding(0, 10, 0, 0);

        Controls.Add(layout);

        _settings = LauncherSettings.Load();
        ApplySettingsToUi();
        UpdateBufferDepthState();
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = Path.GetFileName(_applicationPathTextBox.Text),
            InitialDirectory = GetInitialDirectory()
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _applicationPathTextBox.Text = dialog.FileName;
        }
    }

    private string GetInitialDirectory()
    {
        var current = _applicationPathTextBox.Text;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private void ApplySettingsToUi()
    {
        _applicationPathTextBox.Text = _settings.ApplicationPath ?? SuggestExecutablePath();
        _ndiNameTextBox.Text = _settings.NdiName;
        _portNumericUpDown.Value = ClampToRange(_portNumericUpDown, _settings.HttpPort);
        _urlTextBox.Text = _settings.StartUrl;
        _widthNumericUpDown.Value = ClampToRange(_widthNumericUpDown, _settings.Width);
        _heightNumericUpDown.Value = ClampToRange(_heightNumericUpDown, _settings.Height);
        _frameRateTextBox.Text = _settings.FrameRate ?? "60";
        _enableBufferingCheckBox.Checked = _settings.EnableBuffering;
        _bufferDepthNumericUpDown.Value = ClampToRange(_bufferDepthNumericUpDown, _settings.BufferDepth);
        _telemetryIntervalNumericUpDown.Value = ClampToRange(_telemetryIntervalNumericUpDown, _settings.TelemetryIntervalSeconds);
        _windowlessFrameRateNumericUpDown.Value = ClampToRange(_windowlessFrameRateNumericUpDown, _settings.WindowlessFrameRate ?? 0);
        _disableGpuVsyncCheckBox.Checked = _settings.DisableGpuVsync;
        _disableFrameRateLimitCheckBox.Checked = _settings.DisableFrameRateLimit;
        _debugLoggingCheckBox.Checked = _settings.EnableDebugLogging;
        _quietLoggingCheckBox.Checked = _settings.QuietConsoleLogging;
    }

    private static decimal ClampToRange(NumericUpDown control, decimal value)
    {
        if (value < control.Minimum) return control.Minimum;
        if (value > control.Maximum) return control.Maximum;
        return value;
    }

    private string SuggestExecutablePath()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Tractus.HtmlToNdi.exe"),
            Path.Combine(baseDirectory, "..", "Tractus.HtmlToNdi.exe"),
            Path.Combine(baseDirectory, "..", "Tractus.HtmlToNdi", "bin", "Release", "net8.0", "Tractus.HtmlToNdi.exe"),
            Path.Combine(baseDirectory, "..", "Tractus.HtmlToNdi", "bin", "Debug", "net8.0", "Tractus.HtmlToNdi.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private void UpdateBufferDepthState()
    {
        _bufferDepthNumericUpDown.Enabled = _enableBufferingCheckBox.Checked;
    }

    private LauncherSettings GatherSettingsFromUi()
    {
        return new LauncherSettings
        {
            ApplicationPath = _applicationPathTextBox.Text.Trim(),
            NdiName = _ndiNameTextBox.Text.Trim(),
            HttpPort = (int)_portNumericUpDown.Value,
            StartUrl = _urlTextBox.Text.Trim(),
            Width = (int)_widthNumericUpDown.Value,
            Height = (int)_heightNumericUpDown.Value,
            FrameRate = _frameRateTextBox.Text.Trim(),
            EnableBuffering = _enableBufferingCheckBox.Checked,
            BufferDepth = (int)_bufferDepthNumericUpDown.Value,
            TelemetryIntervalSeconds = (int)_telemetryIntervalNumericUpDown.Value,
            WindowlessFrameRate = _windowlessFrameRateNumericUpDown.Value == 0 ? null : (int?)_windowlessFrameRateNumericUpDown.Value,
            DisableGpuVsync = _disableGpuVsyncCheckBox.Checked,
            DisableFrameRateLimit = _disableFrameRateLimitCheckBox.Checked,
            EnableDebugLogging = _debugLoggingCheckBox.Checked,
            QuietConsoleLogging = _quietLoggingCheckBox.Checked
        };
    }

    private void OnLaunchClicked(object? sender, EventArgs e)
    {
        try
        {
            var updatedSettings = GatherSettingsFromUi();

            if (!File.Exists(updatedSettings.ApplicationPath))
            {
                MessageBox.Show(this, "The selected Tractus.HtmlToNdi executable could not be found.", "Executable missing", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(updatedSettings.NdiName))
            {
                MessageBox.Show(this, "Please enter an NDI source name.", "NDI name required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Uri.TryCreate(updatedSettings.StartUrl, UriKind.Absolute, out _))
            {
                MessageBox.Show(this, "Please enter a valid absolute URL.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings = updatedSettings;
            LauncherSettings.Save(_settings);

            StartApplicationProcess(updatedSettings);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to launch Tractus.HtmlToNdi.\n\n{ex.Message}", "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartApplicationProcess(LauncherSettings settings)
    {
        var startInfo = new ProcessStartInfo(settings.ApplicationPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(settings.ApplicationPath) ?? Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add($"--ndiname={settings.NdiName}");
        startInfo.ArgumentList.Add($"--port={settings.HttpPort}");
        startInfo.ArgumentList.Add($"--url={settings.StartUrl}");
        startInfo.ArgumentList.Add($"--w={settings.Width}");
        startInfo.ArgumentList.Add($"--h={settings.Height}");

        if (!string.IsNullOrWhiteSpace(settings.FrameRate))
        {
            startInfo.ArgumentList.Add($"--fps={settings.FrameRate}");
        }

        if (settings.EnableBuffering)
        {
            startInfo.ArgumentList.Add("--enable-output-buffer");
        }

        if (settings.BufferDepth > 0)
        {
            startInfo.ArgumentList.Add($"--buffer-depth={settings.BufferDepth}");
        }

        if (settings.TelemetryIntervalSeconds > 0)
        {
            startInfo.ArgumentList.Add($"--telemetry-interval={settings.TelemetryIntervalSeconds}");
        }

        if (settings.WindowlessFrameRate is { } windowlessRate && windowlessRate > 0)
        {
            startInfo.ArgumentList.Add($"--windowless-frame-rate={windowlessRate}");
        }

        if (settings.DisableGpuVsync)
        {
            startInfo.ArgumentList.Add("--disable-gpu-vsync");
        }

        if (settings.DisableFrameRateLimit)
        {
            startInfo.ArgumentList.Add("--disable-frame-rate-limit");
        }

        if (settings.EnableDebugLogging)
        {
            startInfo.ArgumentList.Add("-debug");
        }

        if (settings.QuietConsoleLogging)
        {
            startInfo.ArgumentList.Add("-quiet");
        }

        Process.Start(startInfo);
    }
}
