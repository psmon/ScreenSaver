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
    private readonly List<EffectRow> _effectRows = new();
    private TrackBar _opacity = null!;
    private TextBox _colorHex = null!;
    private Button _colorPick = null!;
    private NumericUpDown _delay = null!;
    private NumericUpDown _idle = null!;
    private CheckBox _hostedAuto = null!;
    private CheckBox _autoStart = null!;
    private Label _opacityValue = null!;

    /// <summary>Fired with the current (unsaved) settings whenever the user hits Preview.</summary>
    public event EventHandler<AppSettings>? LivePreviewRequested;

    /// <summary>Fired with the saved settings when the user clicks Save.</summary>
    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings.Clone().Normalized();
        BuildUi();
        LoadValues();
    }

    private TableLayoutPanel _layout = null!;
    private int _row;

    private void BuildUi()
    {
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Screensaver Overlay — 설정";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        MinimumSize = new Size(520, 420);
        ClientSize = new Size(680, 720);
        // Final size/position is clamped to the screen in OnLoad; the dialog is freely resizable.

        // Root: scrollable content on top (fills), button bar pinned at the bottom.
        // Using a 2-row TableLayoutPanel avoids dock-order clipping entirely.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = new Padding(20, 16, 20, 16),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        // AutoSize label column so labels never wrap, even at high DPI.
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddSection("화면보호기");

        // --- base screensaver picker -------------------------------------
        var saverPanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        _screenSaverCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, DropDownWidth = 420 };
        _screenSaverCombo.Items.Add(new SaverItem("(Windows 기본값 사용)", ""));
        foreach (var s in ScreenSaverCatalog.Enumerate())
            _screenSaverCombo.Items.Add(new SaverItem(s.DisplayName, s.Path));
        var saverBrowse = new Button { Text = "찾기…", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        saverBrowse.Click += OnBrowseSaver;
        saverPanel.Controls.Add(_screenSaverCombo);
        saverPanel.Controls.Add(saverBrowse);
        AddRow("화면보호기", saverPanel);

        AddSection("오버레이 효과 (여러 개 동시 선택 가능 · 각자 개수·크기·속도)");
        AddFullRow(BuildEffectsTable());

        _opacity = new TrackBar { Minimum = 1, Maximum = 255, TickFrequency = 32, Width = 240, AutoSize = false, Height = 36 };
        _opacityValue = new Label { AutoSize = true, Margin = new Padding(10, 8, 0, 0), MinimumSize = new Size(54, 0) };
        _opacity.Scroll += (_, _) => UpdateSliderLabels();
        AddRow("불투명도", WithValue(_opacity, _opacityValue));

        var colorPanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        _colorHex = new TextBox { Width = 150, PlaceholderText = "#RRGGBB (비우면 무지개)" };
        _colorPick = new Button { Text = "색 선택…", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _colorPick.Click += OnPickColor;
        colorPanel.Controls.Add(_colorHex);
        colorPanel.Controls.Add(_colorPick);
        AddRow("색상", colorPanel);

        AddSection("자동 작동");

        _hostedAuto = new CheckBox
        {
            Text = "유휴 시 화면보호기 + 오버레이 자동 실행\n(Windows 자체 화면보호기는 비활성화)",
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 6),
        };
        AddRow("자동 모드", _hostedAuto);

        _idle = new NumericUpDown { Minimum = 5, Maximum = 7200, Increment = 10, Width = 100 };
        AddRow("시작 유휴시간 (초)", _idle);

        _delay = new NumericUpDown { Minimum = 0, Maximum = 600, Width = 100 };
        AddRow("오버레이 지연 (초)", _delay);

        AddSection("상주 / 바로가기");

        _autoStart = new CheckBox { Text = "Windows 시작 시 자동 실행 (상주 / 서비스)", AutoSize = true };
        AddRow("자동 시작", _autoStart);

        var shortcutBtn = new Button { Text = "바탕화면 바로가기 만들기", AutoSize = true };
        shortcutBtn.Click += OnCreateShortcut;
        AddRow("", shortcutBtn);

        scroll.Controls.Add(_layout);
        root.Controls.Add(scroll, 0, 0);

        // --- button bar ---------------------------------------------------
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(14, 10, 14, 10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var save = new Button { Text = "저장", AutoSize = true, MinimumSize = new Size(96, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, MinimumSize = new Size(96, 30), DialogResult = DialogResult.Cancel };
        var preview = new Button { Text = "미리보기", AutoSize = true, MinimumSize = new Size(96, 30) };
        save.Click += OnSave;
        preview.Click += (_, _) => LivePreviewRequested?.Invoke(this, Collect());
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(preview);
        root.Controls.Add(buttons, 0, 1);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void AddSection(string title)
    {
        var header = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(60, 90, 150),
            Margin = new Padding(0, _row == 0 ? 0 : 14, 0, 4),
        };
        _layout.Controls.Add(header, 0, _row);
        _layout.SetColumnSpan(header, 2);
        _row++;
    }

    private void AddRow(string label, Control control)
    {
        _layout.Controls.Add(new Label
        {
            Text = label,
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(3, 9, 8, 3),
        }, 0, _row);
        control.Margin = control.Margin == Padding.Empty ? new Padding(3, 6, 3, 6) : control.Margin;
        control.Anchor = AnchorStyles.Left;
        _layout.Controls.Add(control, 1, _row);
        _row++;
    }

    private static Control WithValue(Control main, Control value)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        p.Controls.Add(main);
        p.Controls.Add(value);
        return p;
    }

    // a control that spans both columns of the outer layout
    private void AddFullRow(Control c)
    {
        c.Margin = c.Margin == Padding.Empty ? new Padding(3, 4, 3, 8) : c.Margin;
        _layout.Controls.Add(c, 0, _row);
        _layout.SetColumnSpan(c, 2);
        _row++;
    }

    /// <summary>One row of per-effect controls (enable + count + size + speed).</summary>
    private TableLayoutPanel BuildEffectsTable()
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4, Margin = new Padding(0, 2, 0, 2),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label Head(string s) => new() { Text = s, AutoSize = true, Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(90, 90, 110), Margin = new Padding(3, 2, 14, 4) };
        t.Controls.Add(Head("효과 (체크 = 사용)"), 0, 0);
        t.Controls.Add(Head("개수"), 1, 0);
        t.Controls.Add(Head("크기"), 2, 0);
        t.Controls.Add(Head("속도"), 3, 0);

        int r = 1;
        foreach (var (id, name) in EffectRegistry.Available)
        {
            var row = new EffectRow
            {
                EffectId = id,
                Enable = new CheckBox { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 16, 3) },
                Count = new NumericUpDown { Minimum = 1, Maximum = 500, Width = 64, Margin = new Padding(3, 4, 10, 4) },
                Size = new NumericUpDown { Minimum = 4, Maximum = 1000, Increment = 4, Width = 74, Margin = new Padding(3, 4, 10, 4) },
                Speed = new NumericUpDown { Minimum = 0.05M, Maximum = 20M, Increment = 0.1M, DecimalPlaces = 2, Width = 64, Margin = new Padding(3, 4, 10, 4) },
            };
            row.Enable.CheckedChanged += (_, _) => row.SyncEnabled();
            t.Controls.Add(row.Enable, 0, r);
            t.Controls.Add(row.Count, 1, r);
            t.Controls.Add(row.Size, 2, r);
            t.Controls.Add(row.Speed, 3, r);
            _effectRows.Add(row);
            r++;
        }
        return t;
    }

    private void UpdateSliderLabels()
    {
        _opacityValue.Text = $"{_opacity.Value} / 255";
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Only shrink if the form genuinely exceeds a sane working area (guard against bogus
        // tiny values). The content scrolls and the button bar is pinned, so any size works.
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (wa.Width >= 640 && wa.Height >= 480)
        {
            int w = Math.Min(Width, wa.Width - 60);
            int h = Math.Min(Height, wa.Height - 60);
            Size = new Size(w, h);
            Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);
        }
    }

    private void LoadValues()
    {
        SelectSaver(_settings.ScreenSaverPath);

        foreach (var row in _effectRows)
        {
            var layer = _settings.Layers.FirstOrDefault(
                l => string.Equals(l.EffectId, row.EffectId, StringComparison.OrdinalIgnoreCase));
            row.Enable.Checked = layer is { Enabled: true };
            row.Count.Value = Math.Clamp(layer?.Count ?? 3, (int)row.Count.Minimum, (int)row.Count.Maximum);
            row.Size.Value = Math.Clamp(layer?.Size ?? 100, (int)row.Size.Minimum, (int)row.Size.Maximum);
            row.Speed.Value = Math.Clamp((decimal)(layer?.Speed ?? 1.0), row.Speed.Minimum, row.Speed.Maximum);
            row.SyncEnabled();
        }

        _opacity.Value = Math.Clamp(_settings.Opacity, _opacity.Minimum, _opacity.Maximum);
        _colorHex.Text = _settings.ColorHex;
        _hostedAuto.Checked = _settings.HostedAutoMode;
        _idle.Value = Math.Clamp(_settings.IdleSeconds, (int)_idle.Minimum, (int)_idle.Maximum);
        _delay.Value = Math.Clamp(_settings.StartDelaySeconds, (int)_delay.Minimum, (int)_delay.Maximum);
        _autoStart.Checked = _settings.AutoStart || AutoStartManager.IsEnabled();
        UpdateSliderLabels();
    }

    private AppSettings Collect()
    {
        var s = _settings.Clone();
        s.ScreenSaverPath = (_screenSaverCombo.SelectedItem as SaverItem)?.Path ?? s.ScreenSaverPath;

        s.Layers = _effectRows.Select(row => new EffectLayer
        {
            EffectId = row.EffectId,
            Enabled = row.Enable.Checked,
            Count = (int)row.Count.Value,
            Size = (int)row.Size.Value,
            Speed = (double)row.Speed.Value,
        }).ToList();
        // keep the legacy single-effect fields pointing at the first enabled layer (back-compat)
        var first = s.Layers.FirstOrDefault(l => l.Enabled) ?? s.Layers.FirstOrDefault();
        if (first is not null) { s.EffectId = first.EffectId; s.Count = first.Count; s.Size = first.Size; s.Speed = first.Speed; }

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

    private sealed class EffectRow
    {
        public string EffectId = "";
        public CheckBox Enable = null!;
        public NumericUpDown Count = null!;
        public NumericUpDown Size = null!;
        public NumericUpDown Speed = null!;

        public void SyncEnabled() => Count.Enabled = Size.Enabled = Speed.Enabled = Enable.Checked;
    }

    private sealed record SaverItem(string Name, string Path)
    {
        public override string ToString() => Name;
    }
}
