using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SpotifyFavoritesTool;

public sealed class TrayIconController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _showMainWindow;
    private readonly Action _showSettings;
    private readonly Action _syncTracks;
    private readonly Action _exitApplication;
    private readonly Forms.NotifyIcon _icon;

    public TrayIconController(
        Dispatcher dispatcher,
        Action showMainWindow,
        Action showSettings,
        Action syncTracks,
        Action exitApplication)
    {
        _dispatcher = dispatcher;
        _showMainWindow = showMainWindow;
        _showSettings = showSettings;
        _syncTracks = syncTracks;
        _exitApplication = exitApplication;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Spotify Favorites Tool",
            Visible = true,
            ContextMenuStrip = CreateMenu()
        };
        _icon.DoubleClick += (_, _) => Invoke(_showMainWindow);
    }

    public void Dispose()
    {
        _icon.Dispose();
    }

    private Forms.ContextMenuStrip CreateMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = TrayMenuColors.Background,
            ForeColor = TrayMenuColors.Foreground,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            MinimumSize = new Size(156, 0),
            Padding = new Forms.Padding(8, 6, 8, 6),
            Renderer = new TrayMenuRenderer()
        };

        AddMenuItem(menu, "Открыть", (_, _) => Invoke(_showMainWindow));
        AddMenuItem(menu, "Настройки", (_, _) => Invoke(_showSettings));
        AddMenuItem(menu, "Синхронизация", (_, _) => Invoke(_syncTracks));
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddMenuItem(menu, "Выход", (_, _) => Invoke(_exitApplication));
        return menu;
    }

    private static void AddMenuItem(Forms.ContextMenuStrip menu, string text, EventHandler onClick)
    {
        menu.Items.Add(new Forms.ToolStripMenuItem(text, image: null, onClick)
        {
            AutoSize = false,
            Height = 32,
            Width = 156,
            Padding = new Forms.Padding(12, 0, 12, 0),
            Margin = new Forms.Padding(0, 2, 0, 2)
        });
    }

    private void Invoke(Action action)
    {
        _dispatcher.Invoke(action);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private static class TrayMenuColors
    {
        public static readonly Color Background = Color.FromArgb(22, 27, 24);
        public static readonly Color Border = Color.FromArgb(54, 81, 66);
        public static readonly Color Foreground = Color.FromArgb(244, 248, 245);
        public static readonly Color Muted = Color.FromArgb(127, 141, 132);
        public static readonly Color HoverBackground = Color.FromArgb(32, 58, 41);
        public static readonly Color HoverBorder = Color.FromArgb(30, 215, 96);
    }

    private sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new TrayMenuColorTable())
        {
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            bounds.Width -= 1;
            bounds.Height -= 1;

            using var path = CreateRoundedRectangle(bounds, 8);
            using var border = new Pen(TrayMenuColors.Border);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(border, path);
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            bounds.Width -= 1;
            bounds.Height -= 1;

            using var path = CreateRoundedRectangle(bounds, 8);
            using var brush = new SolidBrush(TrayMenuColors.Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                return;
            }

            var menuWidth = e.ToolStrip?.ClientSize.Width ?? e.Item.Width;
            var availableWidth = Math.Max(1, menuWidth - 18);
            var bounds = new Rectangle(8, 3, availableWidth, e.Item.Height - 6);
            using var path = CreateRoundedRectangle(bounds, 7);
            using var fill = new SolidBrush(TrayMenuColors.HoverBackground);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TrayMenuColors.Foreground : TrayMenuColors.Muted;
            e.TextFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            var textHeight = TextRenderer.MeasureText(e.Text, e.TextFont).Height;
            var textY = e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height - textHeight) / 2;
            e.TextRectangle = new Rectangle(18, textY, e.Item.Width - 34, textHeight);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(48, TrayMenuColors.Border));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class TrayMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => TrayMenuColors.Background;
        public override Color ImageMarginGradientBegin => TrayMenuColors.Background;
        public override Color ImageMarginGradientMiddle => TrayMenuColors.Background;
        public override Color ImageMarginGradientEnd => TrayMenuColors.Background;
        public override Color MenuBorder => TrayMenuColors.Border;
        public override Color MenuItemBorder => TrayMenuColors.HoverBorder;
        public override Color MenuItemSelected => TrayMenuColors.HoverBackground;
        public override Color MenuItemSelectedGradientBegin => TrayMenuColors.HoverBackground;
        public override Color MenuItemSelectedGradientEnd => TrayMenuColors.HoverBackground;
    }
}
