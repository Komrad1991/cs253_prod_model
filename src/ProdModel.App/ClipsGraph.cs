using System.Globalization;
using System.Text;

namespace ProdModel.App;

/// <summary>
/// Факт из машиночитаемого блока <c>#GRAPH … #END</c>, который печатает CLIPS-программа.
/// Блок содержит всё, что нужно для блок-схемы: слой, cf, правило и посылки.
/// </summary>
internal sealed class ClipsNode
{
    public const int W = 220;
    public const int H = 54;

    public required string Name { get; init; }
    public double Cf { get; init; }
    public int Layer { get; init; }
    public string Rule { get; init; } = "input";
    public double RuleCf { get; init; } = 1.0;
    public IReadOnlyList<string> Premises { get; init; } = Array.Empty<string>();
    public bool IsGoal { get; set; }

    /// <summary>Входной факт базы (не выведен ни одним правилом).</summary>
    public bool IsInput => Rule == "input";

    // Координаты раскладки — заполняются панелью перед отрисовкой.
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Разобранный слепок вывода: факты, связи посылок и цели.</summary>
internal sealed class ClipsGraph
{
    private readonly List<ClipsNode> _nodes = new();
    private readonly Dictionary<string, ClipsNode> _byName = new(StringComparer.Ordinal);
    private readonly List<(string Name, double? Cf)> _goals = new();

    public IReadOnlyList<ClipsNode> Nodes => _nodes;

    /// <summary>Цели программы: cf = null, если цель не доказана.</summary>
    public IReadOnlyList<(string Name, double? Cf)> Goals => _goals;

    public int ProvedGoals => _goals.Count(g => g.Cf.HasValue);

    public int MaxLayer => _nodes.Count == 0 ? 0 : _nodes.Max(n => n.Layer);

    /// <summary>
    /// Разбирает последний блок #GRAPH из журнала CLIPS.
    /// Возвращает null, если блока нет (программа его не печатает) или он пуст.
    /// </summary>
    public static ClipsGraph? Parse(string? log)
    {
        if (string.IsNullOrEmpty(log)) return null;

        var lines = log.Replace("\r\n", "\n").Split('\n');

        var start = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Trim() != "#GRAPH") continue;
            start = i;
            break;
        }
        if (start < 0) return null;

        var graph = new ClipsGraph();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "#END") break;
            if (line.Length == 0) continue;

            var parts = line.Split('|');
            if (parts.Length >= 6 && parts[0] == "N") graph.AddNode(parts);
            else if (parts.Length >= 2 && parts[0] == "G") graph.AddGoal(parts);
        }

        return graph._nodes.Count == 0 ? null : graph;
    }

    private void AddNode(string[] parts)
    {
        var name = parts[3];
        if (name.Length == 0 || _byName.ContainsKey(name)) return;

        var node = new ClipsNode
        {
            Layer = ParseInt(parts[1]),
            Cf = ParseDouble(parts[2]),
            Name = name,
            Rule = parts[4],
            RuleCf = ParseDouble(parts[5]),
            Premises = parts.Length > 6 && parts[6] != "-"
                ? parts[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>()
        };

        _byName[name] = node;
        _nodes.Add(node);
    }

    private void AddGoal(string[] parts)
    {
        var name = parts[1];
        if (name.Length == 0) return;

        var hasCf = parts.Length > 2 && parts[2] != "-";
        var cf = hasCf ? ParseDouble(parts[2]) : (double?)null;

        // В модели [-1..1] отрицательный cf — свидетельство против цели.
        var proved = cf is > 0;
        _goals.Add((name, proved ? cf : null));
        if (proved && _byName.TryGetValue(name, out var node)) node.IsGoal = true;
    }

    /// <summary>Узлы в области видимости: вся база, все доказанные цели или дерево одной цели.</summary>
    public IReadOnlyList<ClipsNode> Scope(bool goalsOnly, string? goal = null)
    {
        if (goal is not null) return Tree(new[] { goal });
        if (!goalsOnly) return _nodes;
        return Tree(_goals.Where(g => g.Cf.HasValue).Select(g => g.Name));
    }

    private List<ClipsNode> Tree(IEnumerable<string> roots)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<ClipsNode>();
        foreach (var root in roots)
            if (_byName.TryGetValue(root, out var node)) stack.Push(node);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!keep.Add(node.Name)) continue;
            foreach (var premise in node.Premises)
                if (_byName.TryGetValue(premise, out var parent)) stack.Push(parent);
        }

        return _nodes.Where(n => keep.Contains(n.Name)).ToList();
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
}

/// <summary>
/// Блок-схема вывода CLIPS: тонкая обёртка над <see cref="BlockGraphPanel"/>.
/// Здесь только перевод фактов CLIPS в блоки — раскладка, зум и отрисовка общие для всех режимов.
/// </summary>
internal sealed class ClipsGraphPanel : BlockGraphPanel
{
    private static readonly (string Label, Color Color)[] Legend =
    {
        ("входные", Theme.TextMuted),
        ("threat", Theme.Red),
        ("need", Theme.Amber),
        ("candidate", Theme.Purple),
        ("pick", Theme.Accent),
        ("recommended", Theme.Green)
    };

    private ClipsGraph? _graph;
    private List<ClipsNode> _scope = new();

    internal ClipsGraph? Graph => _graph;

    internal IReadOnlyList<ClipsNode> TestNodes => _scope;

    /// <summary>Перестроить схему: goalsOnly — только деревья доказанных целей, goal — одна цель.</summary>
    public void SetGraph(ClipsGraph? graph, bool goalsOnly, string? goal = null)
    {
        _graph = graph;
        _scope = graph is null ? new List<ClipsNode>() : graph.Scope(goalsOnly, goal).ToList();

        if (graph is null)
        {
            SetNodes(null, legend: Legend,
                emptyHint: "Блок-схема появится после запуска программы, печатающей блок #GRAPH.\r\n" +
                           "В примерах certainty*.clp он выводится в конце отчёта.");
            return;
        }

        // cf — в плашку, правило — в нижнюю строку: блок читается как «что → чем выведено».
        var nodes = _scope.Select(node => new BlockNode
        {
            Key = node.Name,
            Title = node.Name,
            Badge = node.Cf.ToString("0.000", CultureInfo.InvariantCulture),
            Layer = node.Layer,
            Footer = node.IsInput ? "входной факт" : $"{node.Rule} · правило {node.RuleCf:0.00}",
            Premises = node.Premises,
            IsGoal = node.IsGoal,
            Kind = node.IsInput ? BlockKind.Input : BlockKind.Derived,
            Tooltip = TooltipFor(node)
        }).ToList();

        var allProved = graph.Goals.Count > 0 && graph.ProvedGoals == graph.Goals.Count;
        SetNodes(nodes,
            summary: graph.Goals.Count == 0 ? string.Empty : $"Цели: {graph.ProvedGoals} из {graph.Goals.Count}",
            emptyHint: "Для выбранного режима в выводе нет фактов.",
            legend: Legend,
            summaryColor: allProved ? Theme.Green : Theme.Amber);
    }

    private static string TooltipFor(ClipsNode node)
    {
        var text = new StringBuilder();
        text.AppendLine(node.Name);
        text.AppendLine($"cf {node.Cf:0.000} · слой {node.Layer} · {node.Rule} (правило {node.RuleCf:0.00})");
        if (node.Premises.Count > 0) text.Append("из: " + string.Join(" ", node.Premises));
        return text.ToString().TrimEnd();
    }
}