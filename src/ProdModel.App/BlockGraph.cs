using System.Drawing.Drawing2D;
using System.Text;

namespace ProdModel.App;

/// <summary>Состояние блока на схеме — влияет на цвет полоски и рамку.</summary>
internal enum BlockKind
{
    Input,      // исходный факт базы
    Derived,    // выведен правилом
    Proven,     // цель доказана
    Failed,     // ветвь не доказана
    Cyclic,     // ветвь замкнулась циклом и была переиграна
    Rejected    // попытка отброшена
}

/// <summary>
/// Блок схемы вывода: факт (или подцель) на своём слое. Модель не зависит от движка —
/// её наполняют и режим CLIPS (из блока #GRAPH), и прямой/обратный вывод на C#.
/// </summary>
internal sealed class BlockNode
{
    public const int W = 200;
    public const int H = 52;

    /// <summary>Ключ для рёбер: посылки ссылаются на него.</summary>
    public required string Key { get; init; }

    /// <summary>Полное имя факта — делится на префикс (группа) и тело.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Правая верхняя плашка: cf, номер шага, состояние. Пусто — не рисуется.</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>Слой вывода: блоки одного слоя стоят в одной колонке.</summary>
    public int Layer { get; init; }

    /// <summary>Нижняя строка: правило, стоимость, пояснение.</summary>
    public string Footer { get; init; } = string.Empty;

    public IReadOnlyList<string> Premises { get; init; } = Array.Empty<string>();

    public BlockKind Kind { get; init; } = BlockKind.Derived;

    public bool IsGoal { get; set; }

    /// <summary>Текст подсказки при наведении; null — соберётся из полей.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Цвет полоски; null — по префиксу имени.</summary>
    public Color? Accent { get; init; }

    public bool IsInput => Kind == BlockKind.Input;

    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// Блок-схема вывода: колонки по слоям, блоки-факты, рёбра «посылка → вывод».
/// Колесо мыши — зум от курсора, перетаскивание — панорама, двойной клик — вписать целиком.
/// Одна и та же панель рисует и CLIPS, и прямой/обратный вывод на C#.
/// </summary>
internal class BlockGraphPanel : Control
{
    private const int GapY = 8;
    private const int GapX = 40;
    private const int MarginX = 26;
    private const int TopPad = 30;          // место подписи слоя над колонкой
    private const int CaptionW = 54;        // колонка подписей слоёв в вертикальном виде
    private const float MinZoom = 0.12f;
    private const float MaxZoom = 4f;
    private const float TextZoom = 0.3f;    // ниже этого масштаба текст нечитаем

    private static readonly Font FontPrefix = new("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font FontBadge = new("Consolas", 9.5f, FontStyle.Bold, GraphicsUnit.Point);

    private IReadOnlyList<BlockNode> _nodes = Array.Empty<BlockNode>();
    private readonly Dictionary<string, BlockNode> _index = new(StringComparer.Ordinal);
    private List<int> _layers = new();
    private bool _vertical;   // слои идут сверху вниз — для длинных цепочек в узкой панели

    private string _summary = string.Empty;
    private Color _summaryColor = Theme.TextMuted;
    private string _emptyHint = "Схема появится после запуска вывода.";
    private IReadOnlyList<(string Label, Color Color)> _legend = Array.Empty<(string, Color)>();

    private int _contentW;
    private int _contentH;
    private float _zoom = 1f;
    private PointF _pan = PointF.Empty;
    private bool _needsFit = true;

    private bool _dragging;
    private bool _moved;
    private Point _dragStart;
    private PointF _panAtDrag;

    private BlockNode? _hover;
    private Point _hoverPoint;

    public BlockGraphPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.UiSmall;
    }

    /// <summary>Сколько блоков сейчас в схеме (для тестов).</summary>
    internal int NodeCount => _nodes.Count;

    internal IReadOnlyList<BlockNode> Nodes => _nodes;

    internal float TestZoom => _zoom;

    internal Size TestContent => new(_contentW, _contentH);

    /// <summary>Слои идут сверху вниз (длинная цепочка), а не колонками слева направо.</summary>
    internal bool TestVertical => _vertical;

    /// <summary>
    /// Показать схему: nodes — блоки, summary — строка итога слева внизу,
    /// emptyHint — что писать на пустой схеме, legend — подписи цветов.
    /// </summary>
    public void SetNodes(
        IReadOnlyList<BlockNode>? nodes,
        string? summary = null,
        string? emptyHint = null,
        IReadOnlyList<(string Label, Color Color)>? legend = null,
        Color? summaryColor = null)
    {
        _nodes = nodes ?? Array.Empty<BlockNode>();
        _summary = summary ?? string.Empty;
        _summaryColor = summaryColor ?? Theme.TextMuted;
        if (emptyHint is not null) _emptyHint = emptyHint;
        _legend = legend ?? Array.Empty<(string, Color)>();

        BuildLayout();
        _needsFit = true;
        Fit();
        Invalidate();
    }

    // ================================================================ раскладка ===

    private void BuildLayout()
    {
        _index.Clear();
        foreach (var node in _nodes) _index[node.Key] = node;

        _layers = _nodes.Select(n => n.Layer).Distinct().OrderBy(l => l).ToList();
        _vertical = ChooseOrientation();

        var maxRows = 1;
        for (var column = 0; column < _layers.Count; column++)
        {
            // Порядок внутри слоя: сначала по алфавиту, затем «подтягиваем» узел
            // к середине его посылок — так рёбра почти не пересекаются.
            var ordered = _nodes
                .Where(n => n.Layer == _layers[column])
                .OrderBy(n => n.Title, StringComparer.Ordinal)
                .Select((node, i) => (node, key: Barycenter(node) ?? i * (BlockNode.H + GapY)))
                .OrderBy(pair => pair.key)
                .ThenBy(pair => pair.node.Title, StringComparer.Ordinal)
                .ToList();

            for (var row = 0; row < ordered.Count; row++)
            {
                if (_vertical)
                {
                    ordered[row].node.X = MarginX + CaptionW + row * (BlockNode.W + GapX);
                    ordered[row].node.Y = TopPad + column * (BlockNode.H + GapY);
                }
                else
                {
                    ordered[row].node.X = MarginX + column * (BlockNode.W + GapX);
                    ordered[row].node.Y = TopPad + row * (BlockNode.H + GapY);
                }
            }

            maxRows = Math.Max(maxRows, ordered.Count);
        }

        if (_vertical)
        {
            _contentW = MarginX * 2 + CaptionW + maxRows * (BlockNode.W + GapX);
            _contentH = TopPad + _layers.Count * (BlockNode.H + GapY) + MarginX;
        }
        else
        {
            _contentW = MarginX * 2 + Math.Max(1, _layers.Count) * BlockNode.W
                        + Math.Max(0, _layers.Count - 1) * GapX;
            _contentH = TopPad + maxRows * (BlockNode.H + GapY) + MarginX;
        }
    }

    /// <summary>
    /// Длинная тонкая цепочка (11 слоёв по 1–2 факта) в узкой панели не влезает по ширине,
    /// но отлично влезает сверху вниз. Поворачиваем, только если так заметно крупнее.
    /// </summary>
    private bool ChooseOrientation()
    {
        if (_nodes.Count == 0 || Width <= 0 || Height <= 0 || _layers.Count == 0) return false;

        var maxRows = _nodes.GroupBy(n => n.Layer).Max(g => g.Count());
        var layers = _layers.Count;

        var wideW = MarginX * 2 + layers * BlockNode.W + (layers - 1) * GapX;
        var wideH = TopPad + maxRows * (BlockNode.H + GapY) + MarginX;

        var tallW = MarginX * 2 + CaptionW + maxRows * (BlockNode.W + GapX);
        var tallH = TopPad + layers * (BlockNode.H + GapY) + MarginX;

        var horizontal = Math.Min(Width / (float)wideW, Height / (float)wideH);
        var vertical = Math.Min(Width / (float)tallW, Height / (float)tallH);

        return vertical > horizontal * 1.15f;
    }

    /// <summary>Средняя координата посылок узла вдоль оси слоёв (null — посылок нет).</summary>
    private float? Barycenter(BlockNode node)
    {
        var sum = 0f;
        var count = 0;
        foreach (var premise in node.Premises)
        {
            if (!_index.TryGetValue(premise, out var parent)) continue;
            if (parent.Layer >= node.Layer) continue;
            sum += _vertical ? parent.X : parent.Y;
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    private void Fit()
    {
        if (_nodes.Count == 0 || Width <= 0 || Height <= 0)
        {
            _zoom = 1f;
            _pan = PointF.Empty;
            return;
        }

        var sx = (Width - 18f) / _contentW;
        var sy = (Height - 18f) / _contentH;
        _zoom = Math.Clamp(Math.Min(1f, Math.Min(sx, sy)), MinZoom, MaxZoom);
        Center();
        _needsFit = false;
    }

    private void Center() =>
        _pan = new PointF((Width - _contentW * _zoom) / 2f, (Height - _contentH * _zoom) / 2f);

    // ================================================================ отрисовка ===

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_nodes.Count == 0)
        {
            DrawCentered(g, _emptyHint);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var state = g.Save();
        g.TranslateTransform(_pan.X, _pan.Y);
        g.ScaleTransform(_zoom, _zoom);

        DrawEdges(g);
        DrawLayerCaptions(g);
        foreach (var node in _nodes) DrawBlock(g, node, ReferenceEquals(node, _hover));

        g.Restore(state);

        DrawLegend(g);
        DrawHint(g);
        DrawTooltip(g);
    }

    private void DrawEdges(Graphics g)
    {
        foreach (var node in _nodes)
        {
            using var pen = new Pen(Color.FromArgb(96, AccentOf(node)), 1.4f);
            foreach (var premise in node.Premises)
            {
                if (!_index.TryGetValue(premise, out var parent)) continue;

                if (_vertical)
                {
                    var x1 = parent.X + BlockNode.W / 2f;
                    var y1 = parent.Y + BlockNode.H;
                    var x2 = node.X + BlockNode.W / 2f;
                    var y2 = node.Y;
                    var dy = Math.Max(18f, (y2 - y1) * 0.45f);
                    g.DrawBezier(pen, x1, y1, x1, y1 + dy, x2, y2 - dy, x2, y2);
                    continue;
                }

                var ax = parent.X + BlockNode.W;
                var ay = parent.Y + BlockNode.H / 2f;
                var bx = node.X;
                var by = node.Y + BlockNode.H / 2f;
                var dx = Math.Max(24f, (bx - ax) * 0.45f);

                g.DrawBezier(pen, ax, ay, ax + dx, ay, bx - dx, by, bx, by);
            }
        }
    }

    private void DrawLayerCaptions(Graphics g)
    {
        if (_zoom < TextZoom) return;   // при мелком масштабе подписи нечитаемы — только шум

        using var brush = new SolidBrush(Theme.TextMuted);

        for (var i = 0; i < _layers.Count; i++)
        {
            var text = _layers[i] == 0 ? "слой 0 · входные" : "слой " + _layers[i];

            if (_vertical)
            {
                var y = TopPad + i * (BlockNode.H + GapY);
                using var format = new StringFormat { Alignment = StringAlignment.Far };
                g.DrawString(text, Font, brush, new RectangleF(0, y + BlockNode.H / 2f - 8, MarginX + CaptionW - 8, 16), format);
                continue;
            }

            var x = MarginX + i * (BlockNode.W + GapX);
            g.DrawString(text, Font, brush, x + 2, TopPad - 20);
        }
    }

    private void DrawBlock(Graphics g, BlockNode node, bool hover)
    {
        var rect = new Rectangle(node.X, node.Y, BlockNode.W, BlockNode.H);
        var color = AccentOf(node);
        var (prefix, body) = Split(node.Title);

        using var path = Rounded(rect, 7);
        using var fill = new SolidBrush(hover ? Theme.Panel : Theme.PanelAlt);
        g.FillPath(fill, path);

        // Цветная полоска слева — группа факта или состояние ветви.
        var clip = g.Clip;
        g.SetClip(path, CombineMode.Intersect);
        using (var stripe = new SolidBrush(color)) g.FillRectangle(stripe, rect.X, rect.Y, 3, rect.Height);
        g.Clip = clip;

        var dashed = node.Kind is BlockKind.Cyclic or BlockKind.Failed or BlockKind.Rejected;
        using (var border = new Pen(node.IsGoal ? Theme.Green : Color.FromArgb(130, color), node.IsGoal ? 1.8f : 1f))
        {
            if (dashed) border.DashStyle = DashStyle.Dash;
            g.DrawPath(border, path);
        }

        // На мелком масштабе текст превращается в кашу — рисуем только сами блоки.
        if (_zoom < TextZoom) return;

        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var prefixBrush = new SolidBrush(color);
        using var nameBrush = new SolidBrush(Theme.Text);
        using var mutedBrush = new SolidBrush(Theme.TextMuted);

        var prefixText = prefix.ToUpperInvariant();
        if (node.IsGoal) prefixText = "★ " + prefixText;
        g.DrawString(prefixText, FontPrefix, prefixBrush, new RectangleF(rect.X + 11, rect.Y + 4, rect.Width - 100, 13), format);

        g.DrawString(body, Theme.MonoSmall, nameBrush,
            new RectangleF(rect.X + 11, rect.Y + 18, rect.Width - 22, 15), format);

        if (node.Footer.Length > 0)
            g.DrawString(node.Footer, Font, mutedBrush, new RectangleF(rect.X + 11, rect.Y + 34, rect.Width - 24, 14), format);

        if (node.Badge.Length == 0) return;

        using var badgeFormat = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        using var badgeBrush = new SolidBrush(color);
        g.DrawString(node.Badge, FontBadge, badgeBrush,
            new RectangleF(rect.X + rect.Width - 88, rect.Y + 4, 76, 16), badgeFormat);
    }

    private void DrawLegend(Graphics g)
    {
        if (_summary.Length > 0)
        {
            var y = Height - 38;
            if (y > 40)
            {
                using var summaryBrush = new SolidBrush(_summaryColor);
                g.DrawString(_summary, Font, summaryBrush, 12, y);
            }
        }

        if (_legend.Count == 0) return;

        var row = Height - 20;
        if (row < 40) return;

        var x = 10f;
        using var label = new SolidBrush(Theme.TextMuted);
        foreach (var (text, color) in _legend)
        {
            using var chip = new SolidBrush(color);
            g.FillRectangle(chip, x, row + 4, 8, 8);
            g.DrawString(text, Font, label, x + 12, row);
            x += 12 + g.MeasureString(text, Font).Width + 12;
        }
    }

    private void DrawHint(Graphics g)
    {
        if (Height < 120) return;

        const string text = "колесо — зум · перетаскивание — панорама · двойной клик — вписать";
        using var brush = new SolidBrush(Theme.TextMuted);
        var size = g.MeasureString(text, Font);
        g.DrawString(text, Font, brush, Width - size.Width - 12, 8);
    }

    private void DrawTooltip(Graphics g)
    {
        if (_hover is null) return;

        var text = _hover.Tooltip;
        if (string.IsNullOrWhiteSpace(text)) text = DefaultTooltip(_hover);

        using var format = new StringFormat();
        var size = g.MeasureString(text, Font, 380, format);
        var w = (int)Math.Ceiling(size.Width) + 22;
        var h = (int)Math.Ceiling(size.Height) + 16;

        var x = _hoverPoint.X + 18;
        var y = _hoverPoint.Y + 20;
        if (x + w > Width - 8) x = Math.Max(8, _hoverPoint.X - w - 14);
        if (y + h > Height - 8) y = Math.Max(8, _hoverPoint.Y - h - 14);

        var rect = new Rectangle(x, y, w, h);
        using var path = Rounded(rect, 8);
        using var back = new SolidBrush(Color.FromArgb(244, 28, 34, 42));
        using var border = new Pen(Theme.Border);
        using var brush = new SolidBrush(Theme.Text);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(back, path);
        g.DrawPath(border, path);
        g.DrawString(text, Font, brush, rect.X + 11, rect.Y + 8, format);
    }

    private static string DefaultTooltip(BlockNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(node.Title);
        sb.AppendLine($"слой {node.Layer}" + (node.Badge.Length > 0 ? $" · {node.Badge}" : string.Empty));
        if (node.Footer.Length > 0) sb.AppendLine(node.Footer);
        if (node.Premises.Count > 0) sb.Append("из: " + string.Join(" ", node.Premises));
        return sb.ToString().TrimEnd();
    }

    private void DrawCentered(Graphics g, string message)
    {
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var brush = new SolidBrush(Theme.TextMuted);
        g.DrawString(message, Theme.Ui, brush, new RectangleF(12, 12, Width - 24, Height - 24), format);
    }

    // ================================================================ мышь ===

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_nodes.Count == 0) return;

        var target = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2f : 1f / 1.2f), MinZoom, MaxZoom);
        if (Math.Abs(target - _zoom) < 0.0001f) return;

        // Точка под курсором остаётся на месте при зуме.
        var cx = (e.X - _pan.X) / _zoom;
        var cy = (e.Y - _pan.Y) / _zoom;
        _zoom = target;
        _pan = new PointF(e.X - cx * _zoom, e.Y - cy * _zoom);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _nodes.Count == 0) return;

        _dragging = true;
        _moved = false;
        _dragStart = e.Location;
        _panAtDrag = _pan;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            if (Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) > 3) _moved = true;
            if (_moved)
            {
                _pan = new PointF(_panAtDrag.X + e.X - _dragStart.X, _panAtDrag.Y + e.Y - _dragStart.Y);
                Cursor = Cursors.SizeAll;
                Invalidate();
            }
            return;
        }

        var hit = HitTest(e.Location);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Invalidate();
        }
        if (hit is not null) _hoverPoint = e.Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_nodes.Count == 0) return;
        Fit();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_needsFit) Fit();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        Invalidate();
    }

    /// <summary>Блок под точкой экрана (с учётом зума и панорамы).</summary>
    private BlockNode? HitTest(Point point)
    {
        if (_nodes.Count == 0) return null;

        var x = (point.X - _pan.X) / _zoom;
        var y = (point.Y - _pan.Y) / _zoom;

        foreach (var node in _nodes)
            if (x >= node.X && x <= node.X + BlockNode.W && y >= node.Y && y <= node.Y + BlockNode.H)
                return node;

        return null;
    }

    // ================================================================ оформление ===

    private static Color AccentOf(BlockNode node)
    {
        if (node.Accent is { } accent) return accent;

        return node.Kind switch
        {
            BlockKind.Cyclic => Theme.Red,
            BlockKind.Failed => Theme.TextMuted,
            BlockKind.Rejected => Theme.Red,
            _ => ColorFor(node.Title)
        };
    }

    /// <summary>Цвет группы фактов: по имени предиката (или по префиксу имени в CLIPS).</summary>
    internal static Color ColorFor(string name) => Split(name).Prefix switch
    {
        "warning" or "threat" => Theme.Red,
        "enemy_buys" or "enemy_answers" or "need" => Theme.Amber,
        "candidate" or "i_answers" => Theme.Purple,
        "recommended" or "second_pick" => Theme.Green,
        "pick" => Theme.Accent,
        _ => Theme.TextMuted
    };

    /// <summary>
    /// Делит имя на группу и тело: «candidate_Headhunter» → («candidate», «Headhunter»),
    /// «hero(Haze)» → («hero», «(Haze)»). Группа — самый длинный известный префикс.
    /// </summary>
    internal static (string Prefix, string Body) Split(string name)
    {
        foreach (var group in Groups)
        {
            if (name.Length <= group.Length) continue;
            if (!name.StartsWith(group, StringComparison.Ordinal)) continue;

            var next = name[group.Length];
            if (next != '_' && next != '(') continue;
            return (group, next == '_' ? name[(group.Length + 1)..] : name[group.Length..]);
        }

        var paren = name.IndexOf('(');
        if (paren > 0) return (name[..paren], name[paren..]);

        var index = name.IndexOf('_');
        return index > 0 ? (name[..index], name[(index + 1)..]) : ("факт", name);
    }

    // Длинные префиксы идут первыми: «i_answers» должен победить «i».
    private static readonly string[] Groups =
    {
        "item_provides", "item_active", "item_tier", "second_pick", "recommended",
        "enemy_answers", "enemy_buys", "candidate", "i_answers", "warning",
        "threat", "need", "pick", "hero", "item"
    };

    private static GraphicsPath Rounded(Rectangle rect, int radius)
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
}
