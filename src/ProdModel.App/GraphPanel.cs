using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ProdModel.Core;

namespace ProdModel.App;

/// <summary>Узел графа: факт/подцель дерева вывода либо отвергнутая ветвь (чип).</summary>
internal sealed class GraphNode
{
    public int Id { get; set; }
    public required string Text { get; init; }
    public string SubText { get; set; } = string.Empty;
    public GraphState State { get; set; } = GraphState.Normal;
    public List<GraphNode> Children { get; } = new();

    public int X { get; set; }
    public int Y { get; set; }
    public int Depth { get; set; }
    public int W { get; set; } = 268;
    public int H { get; set; } = 54;

    /// <summary>Отвергнутая ветвь (рисуется красным пунктиром сбоку).</summary>
    public bool Rejected { get; set; }

    public string? Tooltip { get; set; }
}

internal enum GraphState
{
    Normal,
    Input,
    Derived,
    Proven,
    Failed,
    Cyclic,
    Negated
}

/// <summary>
/// Панель визуализации дерева вывода: узлы-факты, рёбра-обоснования, отвергнутые
/// ветви (циклы) в виде красных чипов с подсказкой при наведении.
/// Поддерживает зум колесом мыши (от курсора) и панорамирование перетаскиванием;
/// двойной клик по пустому месту сбрасывает вид.
/// </summary>
internal sealed class GraphPanel : Control
{
    private const int MainW = 250;
    private const int MainH = 46;
    private const int ChipW = 216;
    private const int ChipH = 46;
    private const int GapX = 12;
    private const int GapY = 14;
    private const int ChipGapY = 8;
    private const int ChipOffsetX = 26;

    private const float MinZoom = 0.15f;
    private const float MaxZoom = 50f;      // было 6f — на широких деревьях упор в fit
    private const float ZoomStep = 1.25f;   // было 1.15f — быстрее набирается
    private const int DragThreshold = 4;

    private GraphNode? _root;
    private readonly List<(GraphNode Parent, GraphNode Child, bool IsOr)> _edges = new();
    private int _visible = int.MaxValue;
    private int _contentWidth;
    private int _contentHeight;

    // Вид: зум и панорама поверх «fit to panel».
    private float _zoom = 1f;
    private PointF _pan = PointF.Empty;

    // Состояние перетаскивания.
    private bool _isPanning;
    private bool _didPan;
    private Point _panStart;
    private PointF _panAtStart;

    private GraphNode? _hoverNode;
    private Point _hoverPoint;

    public event Action<GraphNode>? NodeClicked;

    public GraphPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ResizeRedraw = true;
        Font = Theme.UiSmall;
    }

    private void DrawTooltip(Graphics g, GraphNode node, Point point)
    {
        var text = node.Tooltip;
        if (string.IsNullOrWhiteSpace(text)) return;

        using var format = new StringFormat { Trimming = StringTrimming.None };
        var size = g.MeasureString(text, Theme.UiSmall, 360, format);
        var w = (int)Math.Ceiling(size.Width) + 22;
        var h = (int)Math.Ceiling(size.Height) + 16;

        var x = point.X + 20;
        var y = point.Y + 22;

        if (x + w > Width - 8) x = Math.Max(8, point.X - w - 14);
        if (y + h > Height - 8) y = Math.Max(8, point.Y - h - 14);
        if (x + w > Width - 8) x = Math.Max(8, Width - w - 8);
        if (y + h > Height - 8) y = Math.Max(8, Height - h - 8);

        var rect = new Rectangle(x, y, w, h);
        using var path = RoundedRect(rect, 8);
        using var back = new SolidBrush(Color.FromArgb(242, 28, 34, 42));
        using var border = new Pen(node.Rejected ? Color.FromArgb(150, 84, 84) : Theme.Border);
        g.FillPath(back, path);
        g.DrawPath(border, path);

        if (node.Rejected)
        {
            using var accent = new SolidBrush(Theme.Red);
            g.FillRectangle(accent, rect.X + 1, rect.Y + 1, 3, rect.Height - 2);
        }

        using var textBrush = new SolidBrush(Theme.Text);
        g.DrawString(text, Theme.UiSmall, textBrush,
            new RectangleF(rect.X + 12, rect.Y + 8, rect.Width - 20, rect.Height - 14), format);
    }

    /// <summary>
    /// Задать новое дерево. По умолчанию вид (зум/панорама) сохраняется, чтобы
    /// анимация не «дёргала» картинку на каждом шаге; сброс — через <see cref="ResetView"/>.
    /// </summary>
    public void SetTree(GraphNode? root, bool resetView = false)
    {
        _root = root;
        _visible = int.MaxValue;
        if (resetView) ResetView();
        Relayout();
        Invalidate();
    }

    public void SetTreeAnimated(GraphNode? root, int visibleCount)
    {
        _root = root;
        _visible = visibleCount;
        ResetView();
        Relayout();
        Invalidate();
    }

    public void Reveal(int visibleCount)
    {
        _visible = visibleCount;
        Invalidate();
    }

    public int VisibleCount => _visible;

    /// <summary>Текущее дерево — для автотестов и диагностики.</summary>
    internal GraphNode? Tree => _root;

    /// <summary>Показывать отвергнутые ветви полным поддеревом (вместе с предотвращёнными циклами).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool FullTree { get; set; }

    public void ResetView()
    {
        _zoom = 1f;
        _pan = PointF.Empty;
        _isPanning = false;
        _didPan = false;
        Invalidate();
    }

    // ------------------------------------------------------------ раскладка ---

    private void Relayout()
    {
        _edges.Clear();
        _contentWidth = 0;
        _contentHeight = 0;

        if (_root is null) return;

        foreach (var node in AllNodes(_root))
        {
            node.W = node.Rejected && !FullTree ? ChipW : MainW;
            node.H = node.Rejected && !FullTree ? ChipH : MainH;
        }

        AssignDepth(_root);

        var order = PostOrder(_root, FullTree);
        var leaf = 0;

        foreach (var node in order)
        {
            var children = FullTree ? node.Children : node.Children.Where(c => !c.Rejected).ToList();
            if (children.Count == 0)
            {
                node.X = leaf * (MainW + GapX);
                leaf++;
            }
            else
            {
                node.X = (children[0].X + children[^1].X) / 2;
            }

            node.Y = node.Depth * (MainH + GapY);
        }

        foreach (var node in AllNodes(_root))
        {
            foreach (var child in node.Children)
            {
                if (!child.Rejected || FullTree) _edges.Add((node, child, child.Rejected));

                _contentWidth = Math.Max(_contentWidth, child.X + child.W);
                _contentHeight = Math.Max(_contentHeight, child.Y + child.H);
            }

            _contentWidth = Math.Max(_contentWidth, node.X + node.W);
            _contentHeight = Math.Max(_contentHeight, node.Y + node.H);
        }

        if (FullTree) return;

        // компактный режим: отвергнутые ветви — чипами сбоку от узла.
        // Чипы ставим за последним узлом своей строки, иначе они наезжают
        // на соседние узлы того же слоя.
        var rowRight = new Dictionary<int, int>();
        var chipCursor = new Dictionary<int, int>();

        foreach (var node in AllNodes(_root).Where(n => !n.Rejected))
        {
            var right = node.X + node.W;

            if (!rowRight.TryGetValue(node.Depth, out var current) || right > current)
                rowRight[node.Depth] = right;
        }

        foreach (var node in AllNodes(_root).Where(n => !n.Rejected))
        {
            var chips = node.Children.Where(c => c.Rejected).ToList();
            if (chips.Count == 0) continue;

            var totalHeight = chips.Count * ChipH + (chips.Count - 1) * ChipGapY;
            var y = node.Y + (node.H - totalHeight) / 2;

            if (!chipCursor.TryGetValue(node.Depth, out var x))
            {
                x = rowRight[node.Depth] + ChipOffsetX;
                chipCursor[node.Depth] = x;
            }

            foreach (var chip in chips)
            {
                chip.X = x;
                chip.Y = y;
                y += ChipH + ChipGapY;

                _edges.Add((node, chip, true));
                _contentWidth = Math.Max(_contentWidth, chip.X + chip.W);
                _contentHeight = Math.Max(_contentHeight, chip.Y + chip.H);
            }

            chipCursor[node.Depth] = x + ChipW + ChipOffsetX;
        }
    }

    /// <summary>
    /// Проставляет <see cref="GraphNode.Depth"/> сверху вниз: корень — 0, его дети — 1 и т. д.
    /// Без этого все узлы получали Y = 0 и рисовались одной строкой друг поверх друга.
    /// </summary>
    private static void AssignDepth(GraphNode root)
    {
        var seen = new HashSet<GraphNode>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<GraphNode>();

        root.Depth = 0;
        seen.Add(root);
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var child in node.Children)
            {
                if (!seen.Add(child)) continue;
                child.Depth = node.Depth + 1;
                queue.Enqueue(child);
            }
        }
    }

    private static List<GraphNode> PostOrder(GraphNode root, bool includeRejected)
    {
        var order = new List<GraphNode>();
        var stack = new Stack<(GraphNode Node, bool Done)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (node, done) = stack.Pop();
            if (done)
            {
                order.Add(node);
                continue;
            }

            stack.Push((node, true));
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                if (includeRejected || !node.Children[i].Rejected) stack.Push((node.Children[i], false));
            }
        }

        return order;
    }

    private static IEnumerable<GraphNode> AllNodes(GraphNode root)
    {
        var stack = new Stack<GraphNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    /// <summary>Базовый масштаб «вписать в панель» с учётом пользовательского зума.</summary>
    private float FitScale()
    {
        var fitScaleX = (Width - 32f) / Math.Max(1, _contentWidth);
        var fitScaleY = (Height - 24f) / Math.Max(1, _contentHeight);
        return Math.Min(1f, Math.Min(fitScaleX, fitScaleY)) * _zoom;
    }

    /// <summary>Смещение без пользовательской панорамы — используется как «якорь» для зума.</summary>
    private (float Scale, float OffsetX, float OffsetY) TransformBase()
    {
        var scale = FitScale();

        var offsetX = (Width - _contentWidth * scale) / 2f;
        var scaledHeight = _contentHeight * scale;
        var offsetY = scaledHeight <= Height - 16
            ? (Height - scaledHeight) / 2f
            : 14f;

        return (scale, offsetX, offsetY);
    }

    /// <summary>Полное преобразование: базовое + пользовательская панорама.</summary>
    private (float Scale, float OffsetX, float OffsetY) Transform()
    {
        var (scale, ox, oy) = TransformBase();
        return (scale, ox + _pan.X, oy + _pan.Y);
    }

    // ------------------------------------------------------------ отрисовка ---

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        if (_root is null)
        {
            DrawEmpty(g);
            return;
        }

        var (scale, offsetX, offsetY) = Transform();
        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(scale, scale);

        foreach (var (parent, child, isOr) in _edges)
        {
            if (child.Id > _visible) continue;
            DrawEdge(g, parent, child, isOr);
        }

        foreach (var node in AllNodes(_root))
        {
            if (node.Rejected || node.Id > _visible) continue;
            DrawNode(g, node);
        }

        // отвергнутые (альтернативные) ветви — поверх, чтобы подсказка и пунктир читались
        foreach (var node in AllNodes(_root))
        {
            if (!node.Rejected || node.Id > _visible) continue;
            DrawNode(g, node);
        }

        g.ResetTransform();

        if (_hoverNode is not null)
        {
            DrawTooltip(g, _hoverNode, _hoverPoint);
        }
    }

    private void DrawEmpty(Graphics g)
    {
        const string text = "Дерево вывода появится здесь.";
        var rect = new RectangleF(20, Height / 2f - 30, Width - 40, 60);
        TextRenderer.DrawText(g, text, Theme.Ui, Rectangle.Round(rect), Theme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private void DrawEdge(Graphics g, GraphNode parent, GraphNode child, bool isOr)
    {
        if (isOr)
        {
            var orColor = child.State == GraphState.Cyclic ? Theme.Red : Theme.Amber;
            using var orPen = new Pen(orColor, 1.6f)
            {
                DashStyle = DashStyle.Custom,
                DashPattern = new[] { 3.5f, 3f }
            };

            var vertical = child.Y >= parent.Y + parent.H - 4;
            using var orPath = new GraphicsPath();

            if (vertical)
            {
                var ax1 = parent.X + parent.W / 2f;
                var ay1 = parent.Y + parent.H;
                var ax2 = child.X + child.W / 2f;
                var ay2 = child.Y;
                var aMidY = (ay1 + ay2) / 2f;
                orPath.AddBezier(ax1, ay1, ax1, aMidY, ax2, aMidY, ax2, ay2);
            }
            else
            {
                var osx = parent.X + parent.W;
                var osy = parent.Y + parent.H / 2f;
                var oex = child.X;
                var oey = child.Y + child.H / 2f;
                orPath.AddBezier(osx, osy, osx + 26, osy, oex - 26, oey, oex, oey);
            }

            g.DrawPath(orPen, orPath);
            return;
        }

        var color = child.State switch
        {
            GraphState.Proven => Theme.Green,
            GraphState.Cyclic => Theme.Red,
            GraphState.Failed => Color.FromArgb(110, 120, 132),
            GraphState.Negated => Theme.Purple,
            _ => Color.FromArgb(90, 110, 130)
        };

        using var pen = new Pen(color, 1.7f);
        var x1 = parent.X + parent.W / 2f;
        var y1 = parent.Y + parent.H;
        var x2 = child.X + child.W / 2f;
        var y2 = child.Y;

        using var path = new GraphicsPath();
        var midY = (y1 + y2) / 2f;
        path.AddBezier(x1, y1, x1, midY, x2, midY, x2, y2);
        g.DrawPath(pen, path);
    }

    private void DrawNode(Graphics g, GraphNode node)
    {
        var rect = new Rectangle(node.X, node.Y, node.W, node.H);

        var (fill, border, textColor, subColor) = node.Rejected
            ? node.State == GraphState.Cyclic
                ? (Color.FromArgb(56, 30, 32), Theme.Red, Color.FromArgb(238, 170, 170), Color.FromArgb(180, 120, 120))
                : (Color.FromArgb(54, 45, 28), Color.FromArgb(198, 152, 72), Color.FromArgb(240, 214, 160), Color.FromArgb(180, 154, 112))
            : node.State switch
            {
                GraphState.Input => (Color.FromArgb(32, 58, 44), Theme.Green, Theme.Text, Theme.TextMuted),
                GraphState.Derived => (Theme.PanelAlt, Theme.Accent, Theme.Text, Theme.TextMuted),
                GraphState.Proven => (Color.FromArgb(34, 62, 48), Theme.Green, Theme.Text, Theme.TextMuted),
                GraphState.Failed => (Color.FromArgb(42, 36, 36), Color.FromArgb(120, 104, 104), Theme.Text, Theme.TextMuted),
                GraphState.Cyclic => (Color.FromArgb(66, 34, 34), Theme.Red, Theme.Text, Theme.TextMuted),
                GraphState.Negated => (Color.FromArgb(46, 38, 60), Theme.Purple, Theme.Text, Theme.TextMuted),
                _ => (Theme.PanelAlt, Theme.Border, Theme.Text, Theme.TextMuted)
            };

        using (var path = RoundedRect(rect, 8))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, node.Rejected ? 1.1f : 1.2f))
        {
            if (node.Rejected)
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = new[] { 4f, 3f };
            }

            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        using var textBrush = new SolidBrush(textColor);
        using var subBrush = new SolidBrush(subColor);
        using var titleFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center
        };
        using var subFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center
        };

        var titleHeight = node.H >= 50 ? 26f : 22f;
        g.DrawString(node.Text, Theme.MonoSmall, textBrush,
            new RectangleF(rect.X + 9, rect.Y + 3, rect.Width - 18, titleHeight), titleFormat);

        g.DrawString(node.SubText, Theme.UiSmall, subBrush,
            new RectangleF(rect.X + 9, rect.Y + 3 + titleHeight, rect.Width - 18, node.H - titleHeight - 6), subFormat);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ------------------------------------------------------------ мышь ---

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_didPan) { _didPan = false; return; }

        var node = HitTest(e.Location);
        if (node is not null && !node.Rejected) NodeClicked?.Invoke(node);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_root is null) return;

        _isPanning = true;
        _didPan = false;
        _panStart = e.Location;
        _panAtStart = _pan;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _isPanning = false;
        Cursor = _hoverNode is not null && !_didPan ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isPanning)
        {
            var dx = e.X - _panStart.X;
            var dy = e.Y - _panStart.Y;
            if (!_didPan && Math.Abs(dx) + Math.Abs(dy) > DragThreshold)
            {
                _didPan = true;
                _hoverNode = null;
            }

            if (_didPan)
            {
                _pan = new PointF(_panAtStart.X + dx, _panAtStart.Y + dy);
                if (Cursor != Cursors.SizeAll) Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            }
        }

        var node = HitTest(e.Location);
        _hoverPoint = e.Location;

        if (node is null)
        {
            Cursor = Cursors.Default;
            if (_hoverNode is not null)
            {
                _hoverNode = null;
                Invalidate();
            }
            return;
        }

        Cursor = Cursors.Hand;

        if (!ReferenceEquals(node, _hoverNode))
        {
            _hoverNode = node;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverNode is null) return;

        _hoverNode = null;
        Invalidate();
    }

    /// <summary>Колесо — зум относительно курсора. Двойной клик по пустому месту — сброс вида.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_root is null) return;

        var factor = e.Delta > 0 ? ZoomStep : 1f / ZoomStep;
        var newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001f) return;

        // Содержимое под курсором до зума.
        var (s1, ox1, oy1) = Transform();
        var cx = (e.X - ox1) / s1;
        var cy = (e.Y - oy1) / s1;

        _zoom = newZoom;

        // Смещение без панорамы при новом масштабе; подбираем pan так,
        // чтобы (cx, cy) снова оказалось под курсором.
        var (s2, bx2, by2) = TransformBase();
        _pan = new PointF(
            e.X - cx * s2 - bx2,
            e.Y - cy * s2 - by2
        );

        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_root is null) return;

        // Реагируем только на пустое место, чтобы случайный двойной клик
        // по узлу не сбрасывал вид.
        var pt = PointToClient(MousePosition);
        if (HitTest(pt) is not null) return;

        _zoom = 1f;
        _pan = PointF.Empty;
        Invalidate();
    }

    private GraphNode? HitTest(Point point)
    {
        if (_root is null) return null;

        var (scale, offsetX, offsetY) = Transform();
        var x = (point.X - offsetX) / scale;
        var y = (point.Y - offsetY) / scale;

        GraphNode? found = null;
        foreach (var node in AllNodes(_root))
        {
            if (node.Id > _visible) continue;
            if (!new RectangleF(node.X, node.Y, node.W, node.H).Contains((float)x, (float)y)) continue;
            if (found is null || node.Rejected) found = node;
        }

        return found;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
        Invalidate();
    }
}