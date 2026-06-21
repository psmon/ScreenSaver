using ScreenSaverOverlay.Effects;
using ScreenSaverOverlay.Screensaver;
using ScreenSaverOverlay.Service;

namespace ScreenSaverOverlay.Settings;

/// <summary>
/// Settings dialog. Built in code (no designer) to keep the sample self-contained.
/// Raises <see cref="LivePreviewRequested"/> so the tray app can show the overlay while
/// the user tweaks values.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private ComboBox _screenSaverCombo = null!;
    private ComboBox _effectCombo = null!;
    private NumericUpDown _count = null!;
    private NumericUpDown _size = null!;
    private TrackBar _speed = null!;
    private TrackBar _opacity = null!;
    private TextBox _colorHex = null!;
    private Button _colorPick = null!;
    private NumericUpDown _delay = null!;
    private NumericUpDown _idle = null!;
    private CheckBox _hostedAuto = null!;
    private CheckBox _autoStart = null!;

    /// <summary>Fired with the current (unsaved) settings whenever the user hits Preview.</summary>
    public event EventHandler<AppSettings>? LivePreviewRequested;

    /// <summary>Fired with the saved settings when the user clicks Save.</summary>
    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings.Clone();
        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        Text = "Screensaver Overlay — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(470, 560);
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoSize = true,
            ColumnStyles =
            {
                new ColumnStyle(SizeType.Absolute, 140),
                new ColumnStyle(SizeType.Percent, 100),
            },
        };

        int row = 0;
        void AddRow(string label, Control control)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Anchor = AnchorStyles.Left,
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 3),
            }, 0, row);
            control.Margin = new Padding(3, 5, 3, 5);
            layout.Controls.Add(control, 1, row);
            row++;
        }

        // --- base screensaver picker -------------------------------------
        var saverPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        _screenSaverCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        _screenSaverCombo.Items.Add(new SaverItem("(Windows 기본값 사용)", ""));
        foreach (var s in ScreenSaverCatalog.Enumerate())
            _screenSaverCombo.Items.Add(new SaverItem(s.DisplayName, s.Path));
        var saverBrowse = new Button { Text = "찾기…", Width = 60 };
        saverBrowse.Click += OnBrowseSaver;
        saverPanel.Controls.Add(_screenSaverCombo);
        saverPanel.Controls.Add(saverBrowse);
        AddRow("Screensaver", saverPanel);

        _effectCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        foreach (var (id, name) in EffectRegistry.Available)
            _effectCombo.Items.Add(new EffectItem(id, name));
        AddRow("Overlay effect", _effectCombo);

        _count = new NumericUpDown { Minimum = 1, Maximum = 500, Width = 90 };
        AddRow("Count", _count);

        _size = new NumericUpDown { Minimum = 4, Maximum = 1000, Increment = 4, Width = 90 };
        AddRow("Size (px)", _size);

        _speed = new TrackBar { Minimum = 5, Maximum = 400, TickFrequency = 50, Width = 250 };
        AddRow("Speed (x0.01)", _speed);

        _opacity = new TrackBar { Minimum = 1, Maximum = 255, TickFrequency = 32, Width = 250 };
        AddRow("Opacity", _opacity);

        var colorPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        _colorHex = new TextBox { Width = 120, PlaceholderText = "#RRGGBB or empty=rainbow" };
        _colorPick = new Button { Text = "Pick…", Width = 70 };
        _colorPick.Click += OnPickColor;
        colorPanel.Controls.Add(_colorHex);
        colorPanel.Controls.Add(_colorPick);
        AddRow("Color", colorPanel);

        _hostedAuto = new CheckBox
        {
            Text = "유휴 시 화면보호기+오버레이 자동 실행 (Windows 자체 화면보호기는 비활성화)",
            AutoSize = true,
        };
        AddRow("Auto mode", _hostedAuto);

        _idle = new NumericUpDown { Minimum = 5, Maximum = 7200, Increment = 10, Width = 90 };
        AddRow("Idle to start (s)", _idle);

        _delay = new NumericUpDown { Minimum = 0, Maximum = 600, Width = 90 };
        AddRow("Overlay delay (s)", _delay);

        var residencyPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = Padding.Empty };
        _autoStart = new CheckBox { Text = "Start with Windows (stay resident / service)", AutoSize = true };
        var shortcutBtn = new Button { Text = "Create desktop shortcut", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        shortcutBtn.Click += OnCreateShortcut;
        residencyPanel.Controls.Add(_autoStart);
        residencyPanel.Controls.Add(shortcutBtn);
        AddRow("Residency", residencyPanel);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            Height = 56,
        };
        var save = new Button { Text = "Save", Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var preview = new Button { Text = "Preview", Width = 90 };
        save.Click += OnSave;
        preview.Click += (_, _) => LivePreviewRequested?.Invoke(this, Collect());
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(preview);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void LoadValues()
    {
        SelectSaver(_settings.ScreenSaverPath);

        for (int i = 0; i < _effectCombo.Items.Count; i++)
        {
            if (_effectCombo.Items[i] is EffectItem item &&
                string.Equals(item.Id, _settings.EffectId, StringComparison.OrdinalIgnoreCase))
            {
                _effectCombo.SelectedIndex = i;
                break;
            }
        }
        if (_effectCombo.SelectedIndex < 0 && _effectCombo.Items.Count > 0)
            _effectCombo.SelectedIndex = 0;

        _count.Value = Math.Clamp(_settings.Count, (int)_count.Minimum, (int)_count.Maximum);
        _size.Value = Math.Clamp(_settings.Size, (int)_size.Minimum, (int)_size.Maximum);
        _speed.Value = Math.Clamp((int)Math.Round(_settings.Speed * 100), _speed.Minimum, _speed.Maximum);
        _opacity.Value = Math.Clamp(_settings.Opacity, _opacity.Minimum, _opacity.Maximum);
        _colorHex.Text = _settings.ColorHex;
        _hostedAuto.Checked = _settings.HostedAutoMode;
        _idle.Value = Math.Clamp(_settings.IdleSeconds, (int)_idle.Minimum, (int)_idle.Maximum);
        _delay.Value = Math.Clamp(_settings.StartDelaySeconds, (int)_delay.Minimum, (int)_delay.Maximum);
        _autoStart.Checked = _settings.AutoStart || AutoStartManager.IsEnabled();
    }

    private AppSettings Collect()
    {
        var s = _settings.Clone();
        s.ScreenSaverPath = (_screenSaverCombo.SelectedItem as SaverItem)?.Path ?? s.ScreenSaverPath;
        s.EffectId = (_effectCombo.SelectedItem as EffectItem)?.Id ?? s.EffectId;
        s.Count = (int)_count.Value;
        s.Size = (int)_size.Value;
        s.Speed = _speed.Value / 100.0;
        s.Opacity = _opacity.Value;
        s.ColorHex = _colorHex.Text.Trim();
        s.HostedAutoMode = _hostedAuto.Checked;
        s.IdleSeconds = (int)_idle.Value;
        s.StartDelaySeconds = (int)_delay.Value;
        s.AutoStart = _autoStart.Checked;
        return s.Normalized();
    }

    private void SelectSaver(string path)
    {
        int match = 0; // default to "(Windows 기본값)"
        for (int i = 0; i < _screenSaverCombo.Items.Count; i++)
        {
            if (_screenSaverCombo.Items[i] is SaverItem item &&
                !string.IsNullOrEmpty(item.Path) &&
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                match = i;
                break;
            }
        }
        _screenSaverCombo.SelectedIndex = match;
    }

    private void OnBrowseSaver(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Screensaver (*.scr)|*.scr",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Add (or reuse) an entry for the picked file and select it.
        for (int i = 0; i < _screenSaverCombo.Items.Count; i++)
        {
            if (_screenSaverCombo.Items[i] is SaverItem item &&
                string.Equals(item.Path, dlg.FileName, StringComparison.OrdinalIgnoreCase))
            {
                _screenSaverCombo.SelectedIndex = i;
                return;
            }
        }
        int idx = _screenSaverCombo.Items.Add(new SaverItem(Path.GetFileName(dlg.FileName), dlg.FileName));
        _screenSaverCombo.SelectedIndex = idx;
    }

    private void OnCreateShortcut(object? sender, EventArgs e)
    {
        try
        {
            ShortcutManager.CreateDesktopShortcut();
            MessageBox.Show(this, "바탕화면에 바로가기를 만들었습니다.",
                "Screensaver Overlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "바로가기 생성 실패: " + ex.Message,
                "Screensaver Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnPickColor(object? sender, EventArgs e)
    {
        using var dlg = new ColorDialog { FullOpen = true };
        var current = Collect().ResolveColor();
        if (current is { } c) dlg.Color = c;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _colorHex.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var s = Collect();
        try
        {
            AutoStartManager.Set(s.AutoStart);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not update auto-start: " + ex.Message,
                "Screensaver Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        s.Save();
        SettingsSaved?.Invoke(this, s);
    }

    private sealed record EffectItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record SaverItem(string Name, string Path)
    {
        public override string ToString() => Name;
    }
}
