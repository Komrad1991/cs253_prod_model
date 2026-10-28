using System.Drawing;
using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ProdModel.App;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(19, 23, 28);
    public static readonly Color Panel = Color.FromArgb(25, 30, 37);
    public static readonly Color PanelAlt = Color.FromArgb(31, 37, 46);
    public static readonly Color Border = Color.FromArgb(47, 55, 66);
    public static readonly Color Text = Color.FromArgb(229, 235, 243);
    public static readonly Color TextMuted = Color.FromArgb(148, 158, 171);
    public static readonly Color Accent = Color.FromArgb(86, 156, 214);
    public static readonly Color AccentDim = Color.FromArgb(52, 92, 128);
    public static readonly Color Green = Color.FromArgb(88, 190, 130);
    public static readonly Color Amber = Color.FromArgb(226, 178, 84);
    public static readonly Color Red = Color.FromArgb(238, 106, 106);
    public static readonly Color Purple = Color.FromArgb(184, 142, 240);

    public static readonly Font Ui = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font UiBold = new("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font UiSmall = new("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Mono = new("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font MonoSmall = new("Consolas", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font Title = new("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point);

    public static Color CfColor(string text)
    {
        if (!double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var cf))
            return Text;

        return cf >= 0.8 ? Green : cf >= 0.5 ? Amber : TextMuted;
    }
}

internal static class Rounding
{
    public static GraphicsPath Rect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || rect.Width < radius * 2 || rect.Height < radius * 2)
        {
            path.AddRectangle(rect);
            return path;
        }

        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Панель-карточка со скруглённой рамкой — общий контейнер для колонок.</summary>
internal sealed class CardPanel : Panel
{
    [DefaultValue(10)]
    public int CornerRadius { get; set; } = 10;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.Panel;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Stroke { get; set; } = Theme.Border;

    public CardPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Background;
        Padding = new Padding(10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounding.Rect(rect, CornerRadius);
        using var brush = new SolidBrush(Fill);
        using var pen = new Pen(Stroke);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);

        base.OnPaint(e);
    }
}

/// <summary>Сегментированный переключатель вкладок: «пилюли» в стиле остальных панелей.</summary>
internal sealed class SegmentBar : Control
{
    private readonly List<string> _items = new();
    private int _selected;
    private int _hover = -1;

    public event EventHandler? SelectedIndexChanged;

    public SegmentBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Height = 38;
        Font = Theme.Ui;
        Cursor = Cursors.Hand;
    }

    public void Add(string text)
    {
        _items.Add(text);
        Invalidate();
    }

    [DefaultValue(0)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_items.Count == 0) return;
            var index = Math.Max(0, Math.Min(_items.Count - 1, value));
            if (index == _selected) return;

            _selected = index;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<Rectangle> LayoutPills()
    {
        var rects = new List<Rectangle>(_items.Count);
        var x = 2;

        foreach (var item in _items)
        {
            var width = TextRenderer.MeasureText(item, Theme.Ui).Width + 26;
            rects.Add(new Rectangle(x, 5, width, 26));
            x += width + 4;
        }

        return rects;
    }

    private int IndexAt(Point point)
    {
        var rects = LayoutPills();
        for (var i = 0; i < rects.Count; i++)
        {
            if (rects[i].Contains(point)) return i;
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        using (var divider = new Pen(Theme.Border))
        {
            g.DrawLine(divider, 0, Height - 1, Width, Height - 1);
        }

        var rects = LayoutPills();
        for (var i = 0; i < _items.Count && i < rects.Count; i++)
        {
            var rect = rects[i];
            var selected = i == _selected;
            var hovered = i == _hover && !selected;

            if (selected || hovered)
            {
                using var path = Rounding.Rect(rect, 7);
                using var brush = new SolidBrush(selected ? Theme.AccentDim : Theme.PanelAlt);
                g.FillPath(brush, path);
            }

            if (selected)
            {
                using var underline = new Pen(Theme.Accent, 2f);
                g.DrawLine(underline, rect.Left + 10, rect.Bottom - 3, rect.Right - 10, rect.Bottom - 3);
            }

            var textRect = new Rectangle(rect.X + 8, rect.Y, rect.Width - 16, rect.Height);
            TextRenderer.DrawText(g, _items[i], selected ? Theme.UiBold : Theme.Ui, textRect,
                selected ? Color.White : Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.Location);
        if (index == _hover) return;

        _hover = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var index = IndexAt(e.Location);
        if (index >= 0) SelectedIndex = index;
    }
}

/// <summary>Таблица с тёмным оформлением (заголовки и ячейки рисует DataGridView).</summary>
internal sealed class DarkGrid : DataGridView
{
    public DarkGrid()
    {
        DoubleBuffered = true;
        BackgroundColor = Theme.Panel;
        GridColor = Color.FromArgb(37, 44, 53);
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AllowUserToOrderColumns = false;
        RowHeadersVisible = false;
        MultiSelect = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        ColumnHeadersHeight = 30;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        EnableHeadersVisualStyles = false;
        ShowCellToolTips = true;
        ScrollBars = ScrollBars.Vertical;
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        RowTemplate.Height = 26;
        Dock = DockStyle.Fill;
        Font = Theme.Ui;

        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.TextMuted,
            SelectionBackColor = Theme.PanelAlt,
            SelectionForeColor = Theme.TextMuted,
            Font = Theme.UiBold,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0)
        };

        DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            SelectionBackColor = Theme.AccentDim,
            SelectionForeColor = Color.White,
            Font = Theme.Ui,
            Padding = new Padding(8, 0, 0, 0),
            WrapMode = DataGridViewTriState.False
        };

        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(22, 27, 33),
            SelectionBackColor = Theme.AccentDim,
            SelectionForeColor = Color.White
        };
    }

    public void AddColumn(string header, int weight)
    {
        Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            FillWeight = weight,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            Resizable = DataGridViewTriState.False
        });
    }

    public DataGridViewRow AddRow(params string[] cells)
    {
        var index = Rows.Add(cells);
        return Rows[index];
    }

    protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
    {
        base.OnCellFormatting(e);
        if (e.ColumnIndex == 1 && e.Value is string text)
        {
            e.CellStyle!.ForeColor = Theme.CfColor(text);
            if (Rows[e.RowIndex].Selected) e.CellStyle.ForeColor = Color.White;
        }
    }
}

