using ScreenSaverOverlay.Settings;

namespace ScreenSaverOverlay.Overlay;

/// <summary>
/// The PIN authentication screen shown when the user returns to a locked session. It covers
/// one monitor with an opaque panel and owns the foreground, so it's the only window that can
/// take input. Entering the correct PIN raises <see cref="Unlocked"/>; the tray app then tears
/// the whole session down. This is an in-app lock, not an OS lock — it does not block
/// Ctrl+Alt+Del / Task Manager.
/// </summary>
public sealed class LockScreenForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _pin;
    private readonly Label _hint;

    /// <summary>Raised once when the user enters the correct PIN.</summary>
    public event EventHandler? Unlocked;

    public LockScreenForm(AppSettings settings, Rectangle bounds)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(12, 14, 20);
        KeyPreview = true;
        Text = "Locked";
        Bounds = bounds;

        var card = new TableLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.FromArgb(24, 28, 38),
            Padding = new Padding(40, 32, 40, 32),
        };

        var title = new Label
        {
            Text = "🔒  잠금됨",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };
        var subtitle = new Label
        {
            Text = "계속하려면 PIN을 입력하세요",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Color.FromArgb(170, 180, 200),
            Font = new Font("Segoe UI", 10.5f),
            Margin = new Padding(0, 0, 0, 20),
        };

        _pin = new TextBox
        {
            Anchor = AnchorStyles.None,
            UseSystemPasswordChar = true,
            Width = 240,
            Font = new Font("Segoe UI", 16f),
            TextAlign = HorizontalAlignment.Center,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 14),
        };
        _pin.KeyDown += OnPinKeyDown;

        var unlock = new Button
        {
            Text = "해제",
            Anchor = AnchorStyles.None,
            AutoSize = true,
            MinimumSize = new Size(240, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(70, 120, 200),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        };
        unlock.FlatAppearance.BorderSize = 0;
        unlock.Click += (_, _) => TryUnlock();

        _hint = new Label
        {
            Text = "",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Color.FromArgb(230, 120, 120),
            Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(0, 2, 0, 0),
        };

        card.Controls.Add(title);
        card.Controls.Add(subtitle);
        card.Controls.Add(_pin);
        card.Controls.Add(unlock);
        card.Controls.Add(_hint);
        Controls.Add(card);

        // Keep the card centred as the form takes its monitor-sized bounds.
        Layout += (_, _) => CenterCard(card);
        Shown += (_, _) => { CenterCard(card); _pin.Focus(); };
    }

    private void CenterCard(Control card) =>
        card.Location = new Point((ClientSize.Width - card.Width) / 2,
                                  (ClientSize.Height - card.Height) / 2);

    /// <summary>Bring the lock screen to the foreground and focus the PIN box.</summary>
    public void Present()
    {
        Show();
        TopMost = true;
        BringToFront();
        Activate();
        _pin.Focus();
    }

    private void OnPinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            TryUnlock();
        }
    }

    private void TryUnlock()
    {
        if (_settings.VerifyPin(_pin.Text))
        {
            Unlocked?.Invoke(this, EventArgs.Empty);
            return;
        }
        _pin.Clear();
        _hint.Text = "PIN이 올바르지 않습니다";
        _pin.Focus();
    }
}
