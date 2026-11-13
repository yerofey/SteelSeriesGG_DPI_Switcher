using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AeroxDpi
{
    internal sealed class DpiToast : Form
    {
        private readonly Label dpiLabel;
        private readonly Label levelLabel;
        private readonly Font dpiFont;
        private readonly Font levelFont;
        private readonly Timer hideTimer;
        private Rectangle workingArea;
        private Rectangle? avoidBounds;
        private bool positioning;
        private bool hasTarget;
        private bool resourcesDisposed;

        internal DpiToast()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(29, 30, 32);
            ForeColor = Color.FromArgb(245, 245, 245);
            ClientSize = new Size(184, 76);
            DoubleBuffered = true;
            AccessibleRole = AccessibleRole.Alert;
            dpiFont = new Font("Segoe UI", 19F, FontStyle.Bold, GraphicsUnit.Point);
            levelFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            dpiLabel = new Label { Name = "DpiValue", AutoSize = false,
                Font = dpiFont, ForeColor = ForeColor, BackColor = BackColor,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            levelLabel = new Label { Name = "DpiLevel", AutoSize = false,
                Font = levelFont, ForeColor = Color.FromArgb(177, 180, 185),
                BackColor = BackColor, TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false };
            Controls.Add(dpiLabel);
            Controls.Add(levelLabel);
            hideTimer = new Timer { Interval = 1400 };
            hideTimer.Tick += HideTimerTick;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // NOACTIVATE, TOOLWINDOW, LAYERED, TRANSPARENT.
                parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Initialize our layered window at full opacity without changing Form.Opacity.
            SetLayeredWindowAttributes(Handle, 0, 255, 0x00000002);
        }

        internal void ShowDpi(int dpi, int levelNumber, int totalLevels, Rectangle? avoidBounds)
        {
            if (IsDisposed)
                throw new ObjectDisposedException("DpiToast");
            hideTimer.Stop();
            dpiLabel.Text = dpi.ToString() + " DPI";
            levelLabel.Text = "Level " + levelNumber + " of " + totalLevels;
            Text = dpiLabel.Text + ", " + levelLabel.Text;
            AccessibleName = Text;
            dpiLabel.AccessibleName = dpiLabel.Text;
            levelLabel.AccessibleName = levelLabel.Text;
            IntPtr foreground = GetForegroundWindow();
            Screen target = foreground == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(foreground);
            workingArea = target.WorkingArea;
            this.avoidBounds = avoidBounds;
            hasTarget = true;
            // Place the handle on its target monitor before reading DeviceDpi.
            Bounds = CalculateBounds(workingArea, Size, 12, avoidBounds);
            IntPtr nativeHandle = Handle;
            ApplyLayout();
            if (!Visible)
                Show();
            ApplyLayout();
            hideTimer.Start();
        }

        private void ApplyLayout()
        {
            if (!hasTarget || positioning)
                return;
            positioning = true;
            try
            {
                double scale = DeviceDpi / 96.0;
                Func<int, int> px = delegate(int value) { return Math.Max(1, (int)Math.Round(value * scale)); };
                Bounds = CalculateBounds(workingArea, new Size(px(184), px(76)), px(12), avoidBounds);
                int left = px(17);
                int textWidth = Math.Max(0, ClientSize.Width - left - px(9));
                dpiLabel.SetBounds(left, px(9), textWidth, px(35));
                levelLabel.SetBounds(left + px(1), px(44), Math.Max(0, textWidth - px(1)), px(21));
                Invalidate();
            }
            finally { positioning = false; }
        }

        internal static Rectangle CalculateBounds(Rectangle area, Size requested, int gap, Rectangle? avoid)
        {
            int width = Math.Min(Math.Max(1, requested.Width), Math.Max(1, area.Width));
            int height = Math.Min(Math.Max(1, requested.Height), Math.Max(1, area.Height));
            int x = Math.Max(area.Left, area.Right - width - gap);
            int y = Math.Max(area.Top, area.Bottom - height - gap);
            Rectangle result = new Rectangle(x, y, width, height);
            if (avoid.HasValue && result.IntersectsWith(avoid.Value))
            {
                int above = avoid.Value.Top - height - gap;
                if (above >= area.Top && above + height <= area.Bottom)
                    result.Y = above;
            }
            result.X = Math.Max(area.Left, Math.Min(result.X, area.Right - width));
            result.Y = Math.Max(area.Top, Math.Min(result.Y, area.Bottom - height));
            return result;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyLayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var border = new Pen(Color.FromArgb(66, 68, 72)))
                e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (var stripe = new SolidBrush(Color.FromArgb(245, 126, 49)))
                e.Graphics.FillRectangle(stripe, 0, 0, Math.Max(3, (int)Math.Round(4 * DeviceDpi / 96.0)), Height);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        private void HideTimerTick(object sender, EventArgs e)
        {
            hideTimer.Stop();
            Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                if (hideTimer != null)
                {
                    hideTimer.Stop();
                    hideTimer.Dispose();
                }
                if (dpiFont != null) dpiFont.Dispose();
                if (levelFont != null) levelFont.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    }
}
