using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AeroxDpi
{
    internal static class Program
    {
        internal static readonly uint ShowMessage = RegisterWindowMessage("AeroxDpi.Show.1");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static void Main()
        {
            bool first;
            using (Mutex mutex = new Mutex(true, "Local\\AeroxDpi.SmallWindow.1", out first))
            {
                if (!first)
                {
                    PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new DpiWindow());
            }
        }
    }

    internal sealed class DpiWindow : Form
    {
        private static readonly Color Surface = Color.FromArgb(25, 28, 32);
        private static readonly Color Muted = Color.FromArgb(163, 170, 180);
        private readonly Label title;
        private readonly Label summary;
        private readonly Label footnote;
        private readonly PresetButton[] buttons = new PresetButton[5];
        private readonly NotifyIcon tray;
        private readonly ToolTip tips = new ToolTip();
        private readonly System.Windows.Forms.Timer connectionTimer;
        private readonly ToolStripMenuItem shortcutStatus;
        private DpiHotkeys hotkeys;
        private DpiToast dpiToast;
        private bool busy;
        private bool connected;
        private bool checkingConnection;
        private bool quitAfterOperation;
        private int preferredIndex = -1;
        private bool restorePending;
        private int restoreFailures;
        private DateTime nextRestoreAttemptUtc = DateTime.MinValue;
        private long operationGeneration;
        private string saveError;
        private int? requestedManualIndex;
        private bool requestedShowPopup;
        private int inFlightIndex = -1;
        private bool drainingRequests;

        internal DpiWindow()
        {
            Text = AppInfo.Name;
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(306, 146);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Surface;
            ForeColor = Color.WhiteSmoke;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = true;
            KeyPreview = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            title = LabelAt(AppInfo.Name, 14, 10, 214, 20, 9F, Muted);
            title.MouseDown += DragWindow;
            tips.SetToolTip(title, "Drag to move this window");
            Button minimize = WindowButton("\u2212", 244, "Hide to tray", delegate { Hide(); });
            Button close = WindowButton("\u00d7", 272, "Exit", delegate { Close(); });
            Controls.Add(minimize);
            Controls.Add(close);

            summary = LabelAt("Choose a DPI", 14, 39, 278, 29, 14F, Color.WhiteSmoke);
            summary.MouseDown += DragWindow;
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                PresetButton button = new PresetButton();
                button.Text = AeroxDevice.Presets[i].ToString();
                button.AccessibleName = AeroxDevice.Presets[i] + " DPI";
                button.AccessibleDescription = "Select sensitivity level " + (i + 1);
                button.SetBounds(14 + i * 56, 77, 50, 32);
                button.TabIndex = i;
                button.Click += async delegate { await SelectPreset(index); };
                tips.SetToolTip(button, "Ctrl+Alt+" + (i + 1));
                buttons[i] = button;
                Controls.Add(button);
            }
            footnote = LabelAt("Checking mouse\u2026", 14, 120, 280, 17, 8F, Muted);
            tips.SetToolTip(footnote, "This app shows the DPI it last sent. Changes made with the mouse button or SteelSeries GG are not tracked.");

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Show DPI window", null, delegate { Reveal(); });
            menu.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                ToolStripMenuItem presetItem = new ToolStripMenuItem(AeroxDevice.Presets[i] + " DPI");
                presetItem.ShortcutKeyDisplayString = "Ctrl+Alt+" + (i + 1);
                presetItem.Click += async delegate { await SelectPreset(index); };
                menu.Items.Add(presetItem);
            }
            menu.Items.Add(new ToolStripSeparator());
            shortcutStatus = new ToolStripMenuItem("Keyboard shortcuts");
            menu.Items.Add(shortcutStatus);
            UpdateShortcutStatus();
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = AppInfo.Name + " \u00b7 choose a level";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Reveal(); };

            connectionTimer = new System.Windows.Forms.Timer();
            connectionTimer.Interval = 3000;
            connectionTimer.Tick += async delegate { await CheckConnection(); };
            Shown += async delegate
            {
                DockInCorner();
                await CheckConnection();
                if (!IsDisposed) connectionTimer.Start();
            };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            };
            MouseDown += DragWindow;

            int? savedDpi = DpiPreferences.Load();
            if (savedDpi.HasValue)
            {
                preferredIndex = Array.IndexOf(AeroxDevice.Presets, savedDpi.Value);
                restorePending = preferredIndex >= 0;
                summary.Text = "Saved \u00b7 " + savedDpi.Value + " DPI";
                footnote.Text = "Restoring when your mouse is ready";
                tray.Text = AppInfo.Name + " \u00b7 saved " + savedDpi.Value + " DPI";
            }
        }

        private Label LabelAt(string text, int x, int y, int width, int height, float size, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.SetBounds(x, y, width, height);
            label.Font = new Font("Segoe UI", size);
            label.ForeColor = color;
            label.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(label);
            return label;
        }

        private Button WindowButton(string text, int x, string tooltip, EventHandler action)
        {
            Button button = new Button();
            button.Text = text;
            button.SetBounds(x, 5, 24, 26);
            button.Font = new Font("Segoe UI", 13F);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 59, 65);
            button.ForeColor = Muted;
            button.TabStop = false;
            button.AccessibleName = tooltip;
            button.Click += action;
            tips.SetToolTip(button, tooltip);
            return button;
        }

        private async Task CheckConnection()
        {
            if (busy || checkingConnection || drainingRequests || requestedManualIndex.HasValue) return;
            checkingConnection = true;
            long generation = operationGeneration;
            try
            {
                bool present;
                try { present = await Task.Run(() => AeroxDevice.IsConnected()); }
                catch (Exception error)
                {
                    if (IsDisposed || Disposing || busy || generation != operationGeneration) return;
                    footnote.Text = "Couldn't check mouse connection";
                    tips.SetToolTip(footnote, error.Message);
                    return;
                }
                if (IsDisposed || Disposing || busy || generation != operationGeneration) return;
                bool wasConnected = connected;
                connected = present;
                title.Text = AppInfo.Name;
                foreach (PresetButton button in buttons) button.Enabled = connected;
                if (!connected)
                {
                    restorePending = preferredIndex >= 0;
                    if (wasConnected)
                    {
                        restoreFailures = 0;
                        nextRestoreAttemptUtc = DateTime.MinValue;
                    }
                    summary.Text = preferredIndex >= 0 ? "Waiting \u00b7 " + AeroxDevice.Presets[preferredIndex] + " DPI" : "Mouse disconnected";
                    tray.Text = AppInfo.Name + " \u00b7 mouse disconnected";
                    footnote.Text = "Connect your wired Aerox 5";
                    foreach (PresetButton button in buttons) button.Selected = false;
                    return;
                }
                if (!wasConnected && preferredIndex >= 0)
                {
                    restorePending = true;
                    restoreFailures = 0;
                    nextRestoreAttemptUtc = DateTime.MinValue;
                }
                if (restorePending)
                {
                    if (DateTime.UtcNow >= nextRestoreAttemptUtc)
                        await SelectPreset(preferredIndex, false);
                    else
                        footnote.Text = "Waiting to retry your saved DPI";
                }
                else
                    ShowSelectionNote();
            }
            finally { checkingConnection = false; }
        }

        private async Task SelectPreset(int index)
        {
            await RequestPreset(index, false);
        }

        private async Task RequestPreset(int index, bool showPopup)
        {
            if (quitAfterOperation || IsDisposed || Disposing) return;
            requestedManualIndex = index;
            requestedShowPopup = showPopup;
            await DrainManualRequests();
        }

        private async Task DrainManualRequests()
        {
            if (busy || drainingRequests || quitAfterOperation || IsDisposed || Disposing) return;
            drainingRequests = true;
            try
            {
                while (requestedManualIndex.HasValue && !quitAfterOperation && !IsDisposed && !Disposing)
                {
                    int index = requestedManualIndex.Value;
                    bool showPopup = requestedShowPopup;
                    requestedManualIndex = null;
                    requestedShowPopup = false;
                    await SelectPreset(index, true, showPopup);
                }
            }
            finally { drainingRequests = false; }
        }

        private async Task SelectPreset(int index, bool remember)
        {
            await SelectPreset(index, remember, false);
        }

        private async Task SelectPreset(int index, bool remember, bool showPopup)
        {
            if (busy) return;
            busy = true;
            inFlightIndex = index;
            operationGeneration++;
            foreach (PresetButton button in buttons) button.Enabled = false;
            summary.Text = "Setting " + AeroxDevice.Presets[index] + " DPI\u2026";
            try
            {
                try { await Task.Run(() => AeroxDevice.SelectPreset(index)); }
                catch (Exception error)
                {
                    if (IsDisposed || Disposing) return;
                    foreach (PresetButton button in buttons) button.Selected = false;
                    if (!remember)
                    {
                        restoreFailures++;
                        int delaySeconds = Math.Min(30, 3 * (1 << Math.Min(4, restoreFailures - 1)));
                        nextRestoreAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                        summary.Text = "Saved \u00b7 " + AeroxDevice.Presets[index] + " DPI";
                        footnote.Text = "Couldn't apply \u00b7 will retry shortly";
                        tray.Text = AppInfo.Name + " \u00b7 waiting to restore DPI";
                    }
                    else
                    {
                        summary.Text = "Couldn't set DPI";
                        footnote.Text = "Reconnect mouse and try again";
                        tray.Text = AppInfo.Name + " \u00b7 couldn't set DPI";
                        if (!Visible)
                        {
                            tray.BalloonTipTitle = "Couldn't set DPI";
                            tray.BalloonTipText = error.Message;
                            tray.ShowBalloonTip(4000);
                        }
                    }
                    tips.SetToolTip(footnote, error.Message);
                    return;
                }
                preferredIndex = index;
                restorePending = false;
                restoreFailures = 0;
                connected = true;
                if (remember)
                {
                    try
                    {
                        await Task.Run(() => DpiPreferences.Save(AeroxDevice.Presets[index]));
                        saveError = null;
                    }
                    catch (Exception error) { saveError = error.Message; }
                }
                if (IsDisposed || Disposing) return;
                title.Text = AppInfo.Name;
                summary.Text = "Last set \u00b7 " + AeroxDevice.Presets[index] + " DPI";
                tray.Text = AppInfo.Name + " \u00b7 last set " + AeroxDevice.Presets[index] + " DPI";
                for (int i = 0; i < buttons.Length; i++) buttons[i].Selected = i == index;
                ShowSelectionNote();
                if (showPopup && !quitAfterOperation && !requestedManualIndex.HasValue)
                {
                    if (dpiToast == null || dpiToast.IsDisposed) dpiToast = new DpiToast();
                    Rectangle? avoidBounds = Visible && WindowState != FormWindowState.Minimized ? (Rectangle?)Bounds : null;
                    dpiToast.ShowDpi(AeroxDevice.Presets[index], index + 1, buttons.Length, avoidBounds);
                }
            }
            finally
            {
                busy = false;
                inFlightIndex = -1;
                if (!IsDisposed && !Disposing)
                {
                    foreach (PresetButton button in buttons) button.Enabled = connected;
                    if (quitAfterOperation) Close();
                    else if (requestedManualIndex.HasValue && !drainingRequests)
                        BeginInvoke(new Action(async delegate { await DrainManualRequests(); }));
                }
            }
        }

        private void ShowSelectionNote()
        {
            footnote.Text = saveError == null ? "Mouse-button changes aren't tracked" : "Applied \u00b7 couldn't save for startup";
            tips.SetToolTip(footnote, saveError ?? "Your last saved DPI is restored when this app starts and when the mouse reconnects. Mouse-button and GG changes are not tracked.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                quitAfterOperation = true;
                requestedManualIndex = null;
                requestedShowPopup = false;
                if (dpiToast != null) dpiToast.Hide();
                if (busy)
                {
                    e.Cancel = true;
                    Hide();
                }
            }
            base.OnFormClosing(e);
        }

        private void DockInCorner()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)Math.Round(12 * DeviceDpi / 96.0);
            Location = new Point(area.Right - Width - gap, area.Bottom - Height - gap);
        }

        private void Reveal()
        {
            Show();
            WindowState = FormWindowState.Normal;
            if (!Screen.AllScreens.AnyIntersects(Bounds)) DockInCorner();
            Activate();
        }

        protected override void WndProc(ref Message message)
        {
            if ((uint)message.Msg == Program.ShowMessage) { Reveal(); return; }
            if (message.Msg == DpiHotkeys.Message)
            {
                HandleHotkey(message.WParam.ToInt32());
                return;
            }
            base.WndProc(ref message);
        }

        private async void HandleHotkey(int id)
        {
            if (hotkeys == null || quitAfterOperation || IsDisposed || Disposing) return;
            int currentIndex = requestedManualIndex ?? (inFlightIndex >= 0 ? inFlightIndex : preferredIndex);
            int index;
            if (hotkeys.TryResolve(id, currentIndex, AeroxDevice.Presets.Length, out index))
                await RequestPreset(index, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeys = new DpiHotkeys(Handle);
            UpdateShortcutStatus();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
            base.OnHandleDestroyed(e);
        }

        private void UpdateShortcutStatus()
        {
            if (shortcutStatus == null) return;
            while (shortcutStatus.DropDownItems.Count > 0)
            {
                ToolStripItem oldItem = shortcutStatus.DropDownItems[0];
                shortcutStatus.DropDownItems.RemoveAt(0);
                oldItem.Dispose();
            }
            shortcutStatus.Text = "Keyboard shortcuts";
            string[] labels = {
                "Ctrl+Alt+Up: next level",
                "Ctrl+Alt+Down: previous level",
                "Ctrl+Alt+1: 700 DPI", "Ctrl+Alt+2: 800 DPI", "Ctrl+Alt+3: 900 DPI",
                "Ctrl+Alt+4: 1000 DPI", "Ctrl+Alt+5: 1100 DPI"
            };
            foreach (string label in labels)
                shortcutStatus.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
            if (hotkeys != null && hotkeys.ConflictDescription.Length > 0)
            {
                shortcutStatus.Text = "Keyboard shortcuts (some unavailable)";
                shortcutStatus.DropDownItems.Add(new ToolStripSeparator());
                foreach (string shortcut in hotkeys.UnavailableShortcuts)
                    shortcutStatus.DropDownItems.Add(new ToolStripMenuItem("Unavailable: " + shortcut) { Enabled = false });
                shortcutStatus.ToolTipText = "Another app may be using: " + hotkeys.ConflictDescription;
            }
            else
                shortcutStatus.ToolTipText = "These shortcuts work while the app is hidden in the tray.";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(65, 70, 78)))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                connectionTimer.Stop();
                connectionTimer.Dispose();
                if (dpiToast != null) dpiToast.Dispose();
                tray.Visible = false;
                tray.ContextMenuStrip.Dispose();
                tray.Dispose();
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }
    }

    internal static class Screens
    {
        internal static bool AnyIntersects(this Screen[] screens, Rectangle bounds)
        {
            foreach (Screen screen in screens) if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            return false;
        }
    }

    internal sealed class PresetButton : Button
    {
        private bool selected;
        private bool hovering;
        internal bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        internal PresetButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill = selected ? Color.FromArgb(255, 135, 69) : hovering ? Color.FromArgb(66, 72, 80) : Color.FromArgb(43, 48, 55);
            Color ink = selected ? Color.FromArgb(30, 24, 20) : Enabled ? Color.WhiteSmoke : Color.FromArgb(120, 127, 138);
            e.Graphics.Clear(Parent.BackColor);
            using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), ink, fill);
        }
    }
}
