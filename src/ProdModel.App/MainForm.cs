using System.Drawing;
using ProdModel.Core;

namespace ProdModel.App;

internal sealed class MainForm : Form
{
    private enum AnimMode
    {
        None,
        Forward,
        Backward
    }

    private KnowledgeBase? _kb;
    private string? _kbPath;
    private ForwardResult? _forward;
    private BuildPlan? _plan;
    private KnowledgeBase? _kbBase;
    private readonly List<string> _enemies = new();
    private readonly Dictionary<string, double> _enemyCf = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BackwardResult> _backward = new();
    private readonly Dictionary<string, DataGridViewRow> _factRows = new(StringComparer.OrdinalIgnoreCase);

    private int _step;
    private int _reveal;
    private int _revealMax;
    private AnimMode _anim;
    private bool _bulk;
    private bool _suppress;
    private Fact? _currentFact;

    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly Button _btnOpen = MakeButton("Открыть БЗ…");
    private readonly Button _btnComposition = MakeButton("Состав…");
    private readonly Button _btnForward = MakeButton("Прямой вывод ▶");
    private readonly Button _btnBackward = MakeButton("Обратный вывод");
    private readonly Button _btnStep = MakeButton("Шаг");
    private readonly Button _btnAll = MakeButton("Всё сразу");
    private readonly Button _btnReset = MakeButton("Сброс");
    private readonly CheckBox _chkFullTree = new()
    {
        Text = "Всё дерево (+циклы)",
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        Font = Theme.Ui,
        Margin = new Padding(4, 8, 12, 4),
        Cursor = Cursors.Hand
    };
    private readonly ToolTip _tips = new() { InitialDelay = 400, AutoPopDelay = 12000 };
    private readonly ComboBox _cmbGoals = new();
    private readonly NumericUpDown _numSpeed = new();
    private readonly Label _lblKb = new();
    private readonly Label _lblAnswer = new();
    private readonly Label _lblCenter = new();
    private readonly Label _lblRight = new();

    private readonly DarkGrid _lstFacts = new();
    private readonly DarkGrid _lstRules = new();
    private readonly DarkGrid _lstResults = new();
    private readonly DarkGrid _lstBuild = new();
    private readonly RichTextBox _txtExplain = new();
    private readonly GraphPanel _graph = new();
    private readonly SegmentBar _segments = new();

    private readonly ToolStripStatusLabel _statusLeft = new();
    private readonly ToolStripStatusLabel _statusMid = new();
    private readonly ToolStripStatusLabel _statusRight = new();

    private string[]? _startupArgs;

    public MainForm(string[]? args = null)
    {
        _startupArgs = args;
        BuildUi();
        WireEvents();

        var start = FindDataFile(Path.Combine("data", "deadlock_kb.txt"));

        var kbArg = GetArg(args, "--kb");
        if (kbArg is not null)
        {
            var resolved = ResolveKbPath(kbArg);
            if (resolved is not null) start = resolved;
        }

        // входные данные: состав противника файлом или списком
        var inputArg = GetArg(args, "--input");
        if (inputArg is not null)
        {
            var resolvedInput = ResolveKbPath(inputArg);
            if (resolvedInput is not null)
            {
                var input = KbLoader.Load(resolvedInput);
                _enemies.Clear();
                _enemyCf.Clear();
                foreach (var fact in input.FactsFor("enemy"))
                {
                    var name = fact.Atom.Args[0].Name;
                    _enemies.Add(name);
                    _enemyCf[name] = fact.Cf;
                }
            }
        }

        var enemiesArg = GetArg(args, "--enemies");
        if (enemiesArg is not null)
        {
            _enemies.Clear();
            _enemyCf.Clear();
            _enemies.AddRange(enemiesArg.Split(new[] { ',', ';', ' ' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (start is not null) LoadKb(start);
        else ShowExplanation("База знаний не загружена.\nНажмите «Открыть БЗ…» и выберите файл data/deadlock_kb.txt.");
    }

    private void ApplyStartupArgs()
    {
        if (_startupArgs is null || _startupArgs.Length == 0) return;

        var segment = GetArg(_startupArgs, "--segment");
        if (segment is not null)
        {
            var index = segment.ToLowerInvariant() switch
            {
                "facts" => 0,
                "rules" => 1,
                "results" => 2,
                "build" => 3,
                _ => -1
            };

            if (index >= 0)
            {
                _segments.SelectedIndex = index;
                ShowSegment();
            }
        }

        if (_startupArgs.Contains("--full", StringComparer.OrdinalIgnoreCase)) _chkFullTree.Checked = true;

        var goalArg = GetArg(_startupArgs, "--goal");
        if (goalArg is not null)
        {
            if (int.TryParse(goalArg, out var index) && index >= 0 && index < _cmbGoals.Items.Count)
            {
                _cmbGoals.SelectedIndex = index;
            }
            else if (ParseGoalArg(goalArg) is { } goal)
            {
                _cmbGoals.Items.Add(goal);
                _cmbGoals.SelectedIndex = _cmbGoals.Items.Count - 1;
            }
        }

        if (_startupArgs.Contains("--forward", StringComparer.OrdinalIgnoreCase)) RevealAllForward();

        if (_startupArgs.Contains("--backward", StringComparer.OrdinalIgnoreCase))
        {
            RunBackward();
        }
    }

    /// <summary>Разбор цели из аргумента командной строки: «pick(MetalSkin, anti_bullet)».</summary>
    private static GoalDefinition? ParseGoalArg(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        var open = trimmed.IndexOf('(');
        if (open < 0) return new GoalDefinition { Pattern = new Predicate(trimmed, Array.Empty<Arg>()) };

        var name = trimmed[..open].Trim();
        if (name.Length == 0) return null;

        var inner = trimmed[(open + 1)..].TrimEnd(')');
        var args = inner
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.StartsWith('?') ? Arg.Var(a[1..]) : Arg.Const(a))
            .ToArray();

        return new GoalDefinition { Pattern = new Predicate(name, args) };
    }

    private static string? GetArg(string[]? args, string name)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Путь к БЗ из аргументов: сначала как есть (от текущего каталога),
    /// затем поиском вверх от каталога сборки — чтобы профиль запуска VS
    /// работал и когда рабочим каталогом является bin.
    /// </summary>
    private static string? ResolveKbPath(string path)
    {
        if (File.Exists(path)) return Path.GetFullPath(path);

        var found = FindDataFile(path);
        if (found is not null) return found;

        return FindDataFile(path.Replace('/', '\\').TrimStart('\\'));
    }

    // ------------------------------------------------------------------ UI ---

    private void BuildUi()
    {
        SuspendLayout();

        Text = "Продукционная модель · Deadlock: герои, предметы, контр-предметы";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        Size = new Size(1600, 1000);
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 110, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 4) };

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel
        };

        _cmbGoals.Width = 330;
        _cmbGoals.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbGoals.FlatStyle = FlatStyle.Flat;
        _cmbGoals.BackColor = Theme.PanelAlt;
        _cmbGoals.ForeColor = Theme.Text;
        _cmbGoals.Font = Theme.Ui;
        _cmbGoals.Margin = new Padding(0, 4, 12, 4);

        _numSpeed.Minimum = 20;
        _numSpeed.Maximum = 1500;
        _numSpeed.Increment = 20;
        _numSpeed.Value = 160;
        _numSpeed.Width = 68;
        _numSpeed.BackColor = Theme.PanelAlt;
        _numSpeed.ForeColor = Theme.Text;
        _numSpeed.BorderStyle = BorderStyle.FixedSingle;
        _numSpeed.Margin = new Padding(0, 4, 12, 4);

        row1.Controls.AddRange(new Control[]
        {
            _btnOpen,
            _btnComposition,
            _btnForward,
            _btnBackward,
            _btnStep,
            _btnAll,
            _btnReset,
            _chkFullTree,
            MakeLabel("Цель:"),
            _cmbGoals,
            MakeLabel("Задержка, мс:"),
            _numSpeed
        });

        _tips.SetToolTip(_chkFullTree,
            "Показывать отвергнутые ветви полным поддеревом — вместе с предотвращёнными циклами.\n" +
            "Наведите на красный узел, чтобы увидеть причину отбраковки.");

        _lblKb.Dock = DockStyle.Top;
        _lblKb.Height = 24;
        _lblKb.ForeColor = Theme.TextMuted;
        _lblKb.Font = Theme.UiSmall;
        _lblKb.Text = "База не загружена";
        _lblKb.Padding = new Padding(2, 4, 0, 0);

        _lblAnswer.Dock = DockStyle.Top;
        _lblAnswer.Height = 26;
        _lblAnswer.ForeColor = Theme.Green;
        _lblAnswer.Font = new Font(Theme.UiBold.FontFamily, 10f, FontStyle.Bold, GraphicsUnit.Point);
        _lblAnswer.Text = "Контрпик: —";
        _lblAnswer.Padding = new Padding(2, 4, 0, 0);
        _lblAnswer.AutoEllipsis = true;

        top.Controls.Add(_lblAnswer);
        top.Controls.Add(_lblKb);
        top.Controls.Add(row1);

        var outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 3,
            BackColor = Theme.Border,
            Panel1MinSize = 25,
            Panel2MinSize = 25
        };

        var inner = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 3,
            BackColor = Theme.Border,
            Panel1MinSize = 25,
            Panel2MinSize = 25
        };

        outer.Panel2.Controls.Add(inner);
        outer.Panel2.BackColor = Theme.Background;
        outer.Panel1.BackColor = Theme.Background;
        inner.Panel1.BackColor = Theme.Background;
        inner.Panel2.BackColor = Theme.Background;

        BuildLeft(outer.Panel1);
        BuildCenter(inner.Panel1);
        BuildRight(inner.Panel2);

        var status = new StatusStrip
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.TextMuted,
            SizingGrip = false,
            Font = Theme.UiSmall
        };
        _statusLeft.Text = "Готово";
        _statusLeft.ForeColor = Theme.TextMuted;
        _statusMid.Spring = true;
        _statusMid.TextAlign = ContentAlignment.MiddleLeft;
        _statusMid.ForeColor = Theme.TextMuted;
        _statusRight.ForeColor = Theme.TextMuted;
        status.Items.AddRange(new ToolStripItem[] { _statusLeft, _statusMid, _statusRight });

        Controls.Add(outer);
        Controls.Add(top);
        Controls.Add(status);

        Shown += (_, _) =>
        {
            try
            {
                outer.Panel1MinSize = 330;
                outer.Panel2MinSize = 520;
                inner.Panel1MinSize = 380;
                inner.Panel2MinSize = 330;

                outer.SplitterDistance = 410;
                inner.SplitterDistance = Math.Max(430, inner.Width - 430);

                ApplyStartupArgs();
            }
            catch (InvalidOperationException)
            {
                // размеры ещё не стабилизировались — не критично
            }
        };

        ResumeLayout(true);
    }

    private void BuildLeft(Control host)
    {
        _segments.Add("Факты");
        _segments.Add("Правила");
        _segments.Add("Результаты");
        _segments.Add("Сборка");
        _segments.Dock = DockStyle.Top;
        _segments.Height = 40;

        _lstFacts.AddColumn("Факт", 42);
        _lstFacts.AddColumn("cf", 10);
        _lstFacts.AddColumn("Слой", 13);
        _lstFacts.AddColumn("Источник", 35);

        _lstRules.AddColumn("Правило", 44);
        _lstRules.AddColumn("cf", 10);
        _lstRules.AddColumn("Заключение", 46);

        _lstResults.AddColumn("Цель", 66);
        _lstResults.AddColumn("cf", 16);
        _lstResults.AddColumn("Слои", 18);

        _lstBuild.AddColumn("Предмет", 38);
        _lstBuild.AddColumn("Слот", 20);
        _lstBuild.AddColumn("cf", 14);
        _lstBuild.AddColumn("Потребность", 38);

        _lstRules.Visible = false;
        _lstResults.Visible = false;
        _lstBuild.Visible = false;

        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 8, 8, 8)
        };

        card.Controls.Add(_lstFacts);
        card.Controls.Add(_lstRules);
        card.Controls.Add(_lstResults);
        card.Controls.Add(_lstBuild);
        card.Controls.Add(_segments);

        host.Controls.Add(card);
    }

    private void ShowSegment()
    {
        var index = _segments.SelectedIndex;
        _lstFacts.Visible = index == 0;
        _lstRules.Visible = index == 1;
        _lstResults.Visible = index == 2;
        _lstBuild.Visible = index == 3;

        if (index == 3) ShowPlan();

        if (index == 2 && _lstResults.Rows.Count == 0 && !_suppress)
        {
            ShowExplanation("Обратный вывод ещё не выполнялся.\nВыберите цель сверху и нажмите «Обратный вывод».");
        }
    }

    private void BuildCenter(Control host)
    {
        _lblCenter.Dock = DockStyle.Top;
        _lblCenter.Height = 28;
        _lblCenter.Font = Theme.UiBold;
        _lblCenter.ForeColor = Theme.Text;
        _lblCenter.TextAlign = ContentAlignment.MiddleLeft;
        _lblCenter.Padding = new Padding(10, 0, 0, 0);
        _lblCenter.BackColor = Theme.Panel;
        _lblCenter.Text = "Дерево вывода";
        _lblCenter.AutoEllipsis = true;

        _graph.Dock = DockStyle.Fill;

        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        card.Controls.Add(_graph);
        card.Controls.Add(_lblCenter);

        host.Controls.Add(card);
    }

    private void BuildRight(Control host)
    {
        _lblRight.Dock = DockStyle.Top;
        _lblRight.Height = 28;
        _lblRight.Font = Theme.UiBold;
        _lblRight.ForeColor = Theme.Text;
        _lblRight.TextAlign = ContentAlignment.MiddleLeft;
        _lblRight.Padding = new Padding(10, 0, 0, 0);
        _lblRight.BackColor = Theme.Panel;
        _lblRight.Text = "Объяснение";

        _txtExplain.Dock = DockStyle.Fill;
        _txtExplain.BackColor = Theme.Background;
        _txtExplain.ForeColor = Theme.Text;
        _txtExplain.Font = Theme.Mono;
        _txtExplain.BorderStyle = BorderStyle.None;
        _txtExplain.ReadOnly = true;
        _txtExplain.WordWrap = true;
        _txtExplain.ScrollBars = RichTextBoxScrollBars.Vertical;
        _txtExplain.DetectUrls = false;

        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        card.Controls.Add(_txtExplain);
        card.Controls.Add(_lblRight);

        host.Controls.Add(card);
    }

    private void WireEvents()
    {
        _btnOpen.Click += (_, _) => OpenKbDialog();
        _btnComposition.Click += (_, _) => ChooseComposition();
        _chkFullTree.CheckedChanged += (_, _) => OnFullTreeChanged();
        _btnForward.Click += (_, _) => ToggleForward();
        _btnBackward.Click += (_, _) => RunBackward();
        _btnStep.Click += (_, _) => StepForward();
        _btnAll.Click += (_, _) => RevealAllForward();
        _btnReset.Click += (_, _) =>
        {
            if (_kbPath is not null) LoadKb(_kbPath);
        };
        _timer.Tick += (_, _) => OnTick();

        _lstFacts.SelectionChanged += (_, _) => ShowSelectedFact();
        _lstRules.SelectionChanged += (_, _) => ShowSelectedRule();
        _lstResults.SelectionChanged += (_, _) => ShowSelectedResult();
        _lstBuild.SelectionChanged += (_, _) => ShowPlan();
        _segments.SelectedIndexChanged += (_, _) => ShowSegment();

        _graph.NodeClicked += node =>
        {
            if (_factRows.TryGetValue(node.Text, out var row))
            {
                row.Selected = true;
                _lstFacts.CurrentCell = row.Cells[0];
                _lstFacts.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 3);
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) ToggleForward();
            if (e.KeyCode == Keys.F6) StepForward();
        };
    }

    // ------------------------------------------------------------- загрузка ---

    private void OpenKbDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "База знаний (*.txt)|*.txt|Все файлы (*.*)|*.*",
            Title = "Открыть базу знаний или сценарий",
            InitialDirectory = _kbPath is not null
                ? Path.GetDirectoryName(_kbPath)
                : FindDataFile("data") ?? AppContext.BaseDirectory
        };

        if (dialog.ShowDialog(this) == DialogResult.OK) LoadKb(dialog.FileName);
    }

    private void LoadKb(string path)
    {
        try
        {
            _kbBase = KbLoader.Load(path);
            _kbPath = path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка загрузки базы знаний", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_enemies.Count == 0) _enemies.AddRange(DefaultComposition(_kbBase));
        ApplyComposition();
    }

    /// <summary>Подставляет выбранный состав врагов в базу знаний и пересчитывает вывод.</summary>
    private void ApplyComposition()
    {
        if (_kbBase is null) return;

        var known = _kbBase.GetSort("hero")?.Values;
        if (known is { Count: > 0 })
        {
            _enemies.RemoveAll(e => !known.Contains(e, StringComparer.OrdinalIgnoreCase));
        }

        if (_enemies.Count > 6) _enemies.RemoveRange(6, _enemies.Count - 6);
        _enemies.Sort(StringComparer.OrdinalIgnoreCase);

        _kb = _kbBase.WithEnemies(_enemies.Select(name =>
            (Name: name, Cf: _enemyCf.TryGetValue(name, out var cf) ? cf : 0.9)));

        StopAnimation();
        _forward = null;
        _plan = null;
        _backward.Clear();
        _step = 0;
        _factRows.Clear();
        _lstFacts.Rows.Clear();
        _lstRules.Rows.Clear();
        _lstResults.Rows.Clear();
        _lstBuild.Rows.Clear();
        _graph.SetTree(null);

        _suppress = true;

        _cmbGoals.Items.Clear();
        foreach (var goal in _kb.Goals) _cmbGoals.Items.Add(goal);
        if (_cmbGoals.Items.Count > 0) _cmbGoals.SelectedIndex = 0;

        foreach (var rule in _kb.Rules)
        {
            var row = _lstRules.AddRow(rule.Id, Certainty.Format(rule.Cf), rule.ConclusionText);
            row.Tag = rule;
        }

        _lblKb.Text = $"{_kb.Name}   ·   {Path.GetFileName(_kbPath)}   ·   состав: {DescribeComposition()}   ·   фактов {_kb.Facts.Count}, правил {_kb.Rules.Count}";

        ComputeForward();
        _lstFacts.CurrentCell = null;
        _lstFacts.ClearSelection();
        _suppress = false;

        UpdateAnswer();
        _lblCenter.Text = "Прямой вывод: рабочая память (входные факты)";
        ShowExplanation(IntroText());
    }

    private void ChooseComposition()
    {
        if (_kbBase is null) return;

        var heroes = _kbBase.GetSort("hero")?.Values ?? new List<string>();
        using var dialog = new CompositionForm(heroes, _enemies);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _enemies.Clear();
        _enemyCf.Clear();
        _enemies.AddRange(dialog.Selected);
        ApplyComposition();
    }

    private string DescribeComposition()
        => _enemies.Count == 0 ? "не задан" : string.Join(", ", _enemies) + $" — {_enemies.Count}";

    private static IEnumerable<string> DefaultComposition(KnowledgeBase kb)
    {
        var heroes = kb.GetSort("hero")?.Values ?? new List<string>();
        var preferred = new[] { "Haze", "Seven", "Abrams" };
        return preferred.Where(p => heroes.Contains(p, StringComparer.OrdinalIgnoreCase)).Take(3);
    }

    private void UpdateAnswer()
    {
        var best = _plan?.BestCounter;

        if (best is null)
        {
            _lblAnswer.ForeColor = Theme.TextMuted;
            _lblAnswer.Text = _plan is null
                ? "Контрпик: —"
                : "Контрпик: под выбранный состав не выведено ни одного предмета";
            return;
        }

        _lblAnswer.ForeColor = Theme.Green;
        _lblAnswer.Text = $"Лучший контрпик: {best.Item}   ·   cf {Certainty.Format(best.Cf)}   ·   закрывает {best.Need}   ·   {best.Slot}, {best.Cost}";
    }

    private string IntroText()
    {
        var warnings = _kb!.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToList();
        var text = $"БЗ: {_kb.Name}\n" +
                   $"Файл знаний: {string.Join(", ", _kb.Sources.Select(Path.GetFileName))}\n" +
                   $"Состав противника: {DescribeComposition()}   (выбирается кнопкой «Состав…», 1–6 героев)\n" +
                   $"Фактов: {_kb.Facts.Count}, правил: {_kb.Rules.Count}, целей: {_kb.Goals.Count}\n\n" +
                   "«Прямой вывод ▶» (F5) — факты появляются по шагам, видно каждое срабатывание.\n" +
                   "«Обратный вывод» — доказывает выбранную цель и показывает дерево доказательства.\n" +
                   "«Сборка» — итоговый набор предметов и второй контр-предмет.\n";

        if (warnings.Count > 0)
        {
            text += "\nЗамечания к базе:\n";
            foreach (var diagnostic in warnings.Take(6)) text += "  " + diagnostic + "\n";
        }

        return text;
    }
    private void ComputeForward()
    {
        if (_kb is null) return;

        _forward = new ForwardChainer().Run(_kb);
        _factRows.Clear();
        _lstFacts.Rows.Clear();

        var suppressBefore = _suppress;
        _suppress = true;
        foreach (var fact in _forward.Memory.Facts)
        {
            if (fact.IsDerived) continue;
            AddFactRow(fact);
        }
        _suppress = suppressBefore;

        _plan = new BuildPlanner().Build(_forward, _kb);
        FillBuild();
        UpdateStatus();
    }

    // -------------------------------------------------------- прямой вывод ---

    private void ToggleForward()
    {
        if (_kb is null) return;
        if (_forward is null) ComputeForward();
        if (_forward is null) return;

        if (_anim == AnimMode.Forward)
        {
            StopAnimation();
            return;
        }

        if (_step >= _forward.Firings.Count)
        {
            _step = 0;
            _factRows.Clear();
            _lstFacts.Rows.Clear();
            foreach (var fact in _forward.Memory.Facts.Where(f => !f.IsDerived)) AddFactRow(fact);
            _graph.SetTree(null);
        }

        _anim = AnimMode.Forward;
        _timer.Interval = Math.Max(20, (int)_numSpeed.Value);
        _timer.Start();
        _btnForward.Text = "Стоп ■";
        _lblCenter.Text = "Прямой вывод: пошаговое срабатывание продукций";
    }

    private void StepForward()
    {
        if (_kb is null) return;
        if (_forward is null) ComputeForward();
        if (_forward is null) return;

        if (_step >= _forward.Firings.Count)
        {
            StopAnimation();
            return;
        }

        ApplyFiring(_forward.Firings[_step]);
        _step++;
        UpdateStatus();
    }

    private void ApplyFiring(Firing firing)
    {
        foreach (var conclusion in firing.Conclusions) UpsertFactRow(conclusion);

        var last = firing.Conclusions.LastOrDefault();
        if (last is not null && !_bulk)
        {
            ShowFactGraph(last);
            ShowExplanation(TextExplainer.ExplainFiring(firing) + "\n" + TextExplainer.ExplainFact(last));
        }
    }

    private void RevealAllForward()
    {
        if (_kb is null) return;
        if (_forward is null) ComputeForward();
        if (_forward is null) return;

        StopAnimation();
        _bulk = true;
        while (_step < _forward.Firings.Count)
        {
            ApplyFiring(_forward.Firings[_step]);
            _step++;
        }
        _bulk = false;

        foreach (var fact in _forward.Memory.Facts) UpsertFactRow(fact);

        var best = _forward.Memory.Facts
            .Where(f => f.Atom.Name.Equals("recommended", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Cf)
            .FirstOrDefault();

        if (best is not null)
        {
            ShowFactGraph(best);
            _lblCenter.Text = $"Лучшая рекомендация: {best.Text}   (cf={Certainty.Format(best.Cf)}, слой {best.Depth})";
        }
        else
        {
            _graph.SetTree(null);
            _lblCenter.Text = $"Прямой вывод завершён: {_forward.Memory.Count} фактов, {_forward.Firings.Count} срабатываний";
        }

        ShowExplanation(BuildForwardSummary());
        UpdateStatus();
    }

    private string BuildForwardSummary()
    {
        if (_forward is null) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ПРЯМОЙ ВЫВОД — ИТОГ");
        sb.AppendLine($"Срабатываний продукций: {_forward.Firings.Count}");
        sb.AppendLine($"Фактов в рабочей памяти: {_forward.Memory.Count} (входных {_forward.InputCount}, выведено {_forward.DerivedCount})");
        sb.AppendLine($"Слоёв вывода: {_forward.Layers}, проходов: {_forward.Passes}");
        sb.AppendLine($"Заблокировано повторных срабатываний: {_forward.BlockedRepeats}");
        sb.AppendLine($"Циклических поддержек (не учтены в cf): {_forward.CircularSupports}");
        sb.AppendLine($"Время: {_forward.ElapsedMs} мс" + (_forward.LimitReached ? "  ⚠ достигнут лимит фактов" : string.Empty));
        sb.AppendLine();
        sb.AppendLine("Факты по слоям:");
        for (var layer = 0; layer <= _forward.Layers; layer++)
        {
            var facts = _forward.FactsByLayer(layer).ToList();
            if (facts.Count == 0) continue;
            sb.AppendLine($"  слой {layer} ({facts.Count}): " + string.Join(", ", facts.Take(8).Select(f => f.Text)) +
                          (facts.Count > 8 ? ", …" : string.Empty));
        }

        if (_plan is not null)
        {
            sb.AppendLine();
            sb.Append(_plan.Explain());
        }

        return sb.ToString();
    }

    // -------------------------------------------------------- обратный вывод ---

    private void RunBackward()
    {
        if (_kb is null) return;
        if (_cmbGoals.SelectedItem is not GoalDefinition goal) return;
        if (_forward is null) ComputeForward();

        StopAnimation();
        Cursor = Cursors.WaitCursor;
        _backward.Clear();
        _lstResults.Rows.Clear();

        try
        {
            var facts = _forward!.Memory.InputOnly();
            var engine = new BackwardEngine();
            var instances = BackwardSolver.EnumerateInstances(goal, _kb);

            foreach (var instance in instances)
            {
                var result = engine.Solve(_kb, facts, instance);
                if (result.Proven && result.Cf >= 0.1) _backward.Add(result);
            }

            _backward.Sort((a, b) => b.Cf.CompareTo(a.Cf));

            foreach (var result in _backward.Take(400))
            {
                var row = _lstResults.AddRow(result.Goal.ToString(), Certainty.Format(result.Cf), result.Layers.ToString());
                row.Tag = result;
            }

            _lblCenter.Text = $"Обратный вывод: доказано {_backward.Count} целей из {instances.Count} подстановок";

            if (_backward.Count == 0)
            {
                ShowExplanation($"Цель {goal} не доказана ни при одной подстановке.\n" +
                                "Проверьте, что в сценарии есть факты enemy(...) и что цель соответствует заключению правил.");
            }
            else
            {
                _segments.SelectedIndex = 2;
                _lstResults.Rows[0].Selected = true;
                _lstResults.CurrentCell = _lstResults.Rows[0].Cells[0];
                _lstResults.Focus();
                ShowBackward(_backward[0]);
            }
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        UpdateStatus();

        if (_backward.Count > 0)
        {
            _statusLeft.Text = $"Обратный вывод: доказано {_backward.Count} целей";
            _statusMid.Text = $"Правил: {_kb.Rules.Count}   ·   цель: {_cmbGoals.SelectedItem}";
        }
    }

    private void ShowSelectedResult()
    {
        if (_suppress) return;
        if (_lstResults.SelectedRows.Count == 0) return;
        if (_lstResults.SelectedRows[0].Tag is not BackwardResult result) return;
        ShowBackward(result);
    }

    private void ShowBackward(BackwardResult result)
    {
        var fullTree = _chkFullTree.Checked;
        _graph.FullTree = fullTree;
        var root = BuildBackwardGraph(result.Root, fullTree);
        _revealMax = MaxId(root);
        _reveal = 0;

        var rejected = CountRejected(result.Root);
        var cycles = CountCycles(result.Root);

        _graph.SetTreeAnimated(root, 0);

        var suffix = rejected > 0
            ? $"   ·   отклонено ветвей: {rejected}" +
              (cycles > 0 ? $", из них циклов: {cycles}" : string.Empty) +
              (fullTree ? string.Empty : "   ·   есть отклонённые ветви (галочка «Всё дерево»)")
            : string.Empty;

        _lblCenter.Text = $"Доказательство цели: {result.Goal}   (cf={Certainty.Format(result.Cf)}, слоёв {result.Layers})" + suffix;
        ShowExplanation(TextExplainer.ExplainBackward(result, includeRejected: true));

        StopAnimation();
        _anim = AnimMode.Backward;
        _timer.Interval = Math.Max(20, (int)_numSpeed.Value / 3);
        _timer.Start();
    }

    /// <summary>Галочка «Всё дерево»: перерисовать текущее доказательство с отвергнутыми ветвями.</summary>
    private void OnFullTreeChanged()
    {
        _graph.FullTree = _chkFullTree.Checked;

        if (_lstResults.SelectedRows.Count > 0 && _lstResults.SelectedRows[0].Tag is BackwardResult result)
        {
            ShowBackward(result);
            return;
        }

        if (_currentFact is not null && _lstFacts.SelectedRows.Count > 0)
        {
            ShowFactGraph(_currentFact);
        }
    }

    private void OnTick()
    {
        if (_anim == AnimMode.Forward)
        {
            if (_forward is null || _step >= _forward.Firings.Count)
            {
                StopAnimation();
                UpdateStatus();
                return;
            }

            StepForward();
            return;
        }

        if (_anim == AnimMode.Backward)
        {
            _reveal += 2;
            if (_reveal >= _revealMax + 1)
            {
                _reveal = _revealMax + 1;
                _graph.Reveal(int.MaxValue);
                StopAnimation();
                return;
            }

            _graph.Reveal(_reveal);
        }
    }

    private void StopAnimation()
    {
        _timer.Stop();
        _anim = AnimMode.None;
        _btnForward.Text = "Прямой вывод ▶";
    }

    private static GraphNode BuildBackwardGraph(SearchNode root, bool expandRejected)
    {
        var map = new Dictionary<SearchNode, GraphNode>();
        var order = new List<SearchNode>();
        var stack = new Stack<(SearchNode Node, bool Done)>();
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
            foreach (var child in node.Children) stack.Push((child, false));
        }

        foreach (var node in order)
        {
            var graph = new GraphNode
            {
                Id = node.Id,
                Text = node.Negated ? "НЕ " + node.Goal : node.Goal.ToString(),
                SubText = SubText(node),
                State = MapState(node),
                Tooltip = TooltipFor(node)
            };

            foreach (var child in node.Children)
            {
                if (map.TryGetValue(child, out var childGraph)) graph.Children.Add(childGraph);
            }

            map[node] = graph;
        }

        var built = map[root];

        var depthStack = new Stack<(GraphNode Node, int Depth)>();
        depthStack.Push((built, 0));
        while (depthStack.Count > 0)
        {
            var (node, depth) = depthStack.Pop();
            node.Depth = depth;
            foreach (var child in node.Children) depthStack.Push((child, depth + 1));
        }

        // Отвергнутые ветви (циклы, тупики): чипы сбоку или полное поддерево.
        var nextId = map.Values.Max(g => g.Id);
        var budget = 600;

        foreach (var (search, graph) in map)
        {
            if (search.RejectedAttempts.Count == 0) continue;

            foreach (var attempt in search.RejectedAttempts)
            {
                if (expandRejected)
                {
                    foreach (var child in attempt.Children)
                    {
                        var subtree = BuildRejectedNode(child, graph.Depth + 1, attempt, ref nextId, ref budget);
                        if (subtree is not null) graph.Children.Add(subtree);
                    }

                    continue;
                }

                var failing = attempt.Children.FirstOrDefault(c => !c.Proven) ?? attempt.Children.FirstOrDefault();
                if (failing is null) continue;

                var isCycle = failing.State == SearchState.Cyclic;
                var bindings = attempt.Bindings.Count > 0
                    ? "\nПодстановка: " + string.Join(", ", attempt.Bindings.Select(kv => $"?{kv.Key}={kv.Value}"))
                    : string.Empty;

                var chip = new GraphNode
                {
                    Id = ++nextId,
                    Text = (isCycle ? "⟲ " : "✕ ") + (failing.Negated ? "НЕ " : "") + failing.Goal,
                    SubText = $"{attempt.Rule.Id} · отклонено",
                    State = isCycle ? GraphState.Cyclic : GraphState.Failed,
                    Rejected = true,
                    Depth = graph.Depth + 1,
                    Tooltip = $"Отклонённая ветвь · правило {attempt.Rule.Id}\n" +
                              (attempt.Rule.Why.Length > 0 ? attempt.Rule.Why + "\n" : string.Empty) +
                              $"\nПричина: {attempt.Reason}{bindings}"
                };

                graph.Children.Add(chip);
            }
        }

        return built;
    }

    /// <summary>Полное поддерево отвергнутой попытки — с циклом, который был предотвращён.</summary>
    private static GraphNode? BuildRejectedNode(SearchNode node, int depth, RejectedAttempt attempt, ref int nextId, ref int budget)
    {
        if (budget <= 0) return null;
        budget--;

        var isCycle = node.State == SearchState.Cyclic;
        var note = string.IsNullOrWhiteSpace(node.Note) ? string.Empty : "\n" + node.Note;
        var bindings = attempt.Bindings.Count > 0
            ? "\nПодстановка: " + string.Join(", ", attempt.Bindings.Select(kv => $"?{kv.Key}={kv.Value}"))
            : string.Empty;

        var graph = new GraphNode
        {
            Id = ++nextId,
            Text = (isCycle ? "⟲ " : "✕ ") + (node.Negated ? "НЕ " : string.Empty) + node.Goal,
            SubText = isCycle ? $"цикл предотвращён · {attempt.Rule.Id}" : $"{attempt.Rule.Id} · отклонено",
            State = MapState(node),
            Rejected = true,
            Depth = depth,
            Tooltip = $"Отклонённая ветвь · правило {attempt.Rule.Id}\n" +
                      (attempt.Rule.Why.Length > 0 ? attempt.Rule.Why + "\n" : string.Empty) +
                      $"\n{node.Goal} — {StateText(node)}{note}{bindings}"
        };

        foreach (var child in node.Children)
        {
            var childGraph = BuildRejectedNode(child, depth + 1, attempt, ref nextId, ref budget);
            if (childGraph is not null) graph.Children.Add(childGraph);
        }

        return graph;
    }

    private static string StateText(SearchNode node) => node.State switch
    {
        SearchState.Cyclic => "цикл предотвращён",
        SearchState.Failed => "не доказано",
        SearchState.Proven => "доказано, но правило не применилось",
        SearchState.DepthLimit => "предел глубины",
        _ => "в процессе"
    };

    private static int CountCycles(SearchNode root)
    {
        var count = 0;
        var stack = new Stack<SearchNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.State == SearchState.Cyclic) count++;
            foreach (var child in node.Children) stack.Push(child);
            foreach (var attempt in node.RejectedAttempts)
            {
                foreach (var child in attempt.Children) stack.Push(child);
            }
        }

        return count;
    }

    private static int CountRejected(SearchNode root)
    {
        var count = 0;
        var stack = new Stack<SearchNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            count += node.RejectedAttempts.Count;
            foreach (var child in node.Children) stack.Push(child);
        }

        return count;
    }

    private static string TooltipFor(SearchNode node)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine((node.Negated ? "НЕ " : string.Empty) + node.Goal);
        sb.AppendLine($"cf={Certainty.Format(node.Cf)} · {node.State}");

        if (node.Fact is not null) sb.AppendLine("факт рабочей памяти");
        if (node.Rule is not null)
        {
            sb.AppendLine($"правило {node.Rule.Id}" +
                          (string.IsNullOrWhiteSpace(node.Rule.Why) ? string.Empty : " — " + node.Rule.Why));
        }
        if (!string.IsNullOrWhiteSpace(node.Note)) sb.AppendLine(node.Note);
        if (node.Truncated) sb.AppendLine("ветвь оборвана ограничением поиска");

        return sb.ToString().TrimEnd();
    }

    private static string SubText(SearchNode node)
    {
        if (node.Fact is not null) return $"факт базы · cf={Certainty.Format(node.Cf)}";
        if (node.Rule is not null) return $"{node.Rule.Id} · cf={Certainty.Format(node.Cf)}";
        return $"cf={Certainty.Format(node.Cf)}";
    }

    private static GraphState MapState(SearchNode node) => node.State switch
    {
        SearchState.Proven => GraphState.Proven,
        SearchState.Cyclic => GraphState.Cyclic,
        SearchState.Failed => GraphState.Failed,
        SearchState.DepthLimit => GraphState.Failed,
        _ => GraphState.Derived
    };

    private static int MaxId(GraphNode root)
    {
        var max = 0;
        var stack = new Stack<GraphNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Id > max) max = node.Id;
            foreach (var child in node.Children) stack.Push(child);
        }
        return max;
    }

    // --------------------------------------------------------------- факты ---

    private void AddFactRow(Fact fact)
    {
        var row = _lstFacts.AddRow(fact.Text, Certainty.Format(fact.Cf), fact.Depth.ToString(),
            fact.IsDerived ? fact.Rule?.Id ?? "вывод" : "входной");
        _factRows[fact.Text] = row;
    }

    private void UpsertFactRow(Fact fact)
    {
        if (_factRows.TryGetValue(fact.Text, out var row))
        {
            row.Cells[1].Value = Certainty.Format(fact.Cf);
            row.Cells[2].Value = fact.Depth.ToString();
            row.Cells[3].Value = fact.IsDerived ? fact.Rule?.Id ?? "вывод" : "входной";
            return;
        }

        AddFactRow(fact);
    }

    private void ShowSelectedFact()
    {
        if (_suppress || _forward is null) return;
        if (_lstFacts.SelectedRows.Count == 0) return;
        if (!_lstFacts.Focused) return;

        var text = _lstFacts.SelectedRows[0].Cells[0].Value?.ToString();
        if (string.IsNullOrEmpty(text)) return;

        var fact = _forward.Memory.Facts.FirstOrDefault(f => string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase));
        if (fact is null) return;

        ShowFactGraph(fact);
        ShowExplanation(TextExplainer.ExplainFact(fact));
    }

    private void ShowFactGraph(Fact fact)
    {
        _currentFact = fact;
        var root = BuildFactGraph(fact);
        _graph.SetTree(root);
        _lblCenter.Text = "Обоснование факта: " + fact.Text + $"   (cf={Certainty.Format(fact.Cf)}, слой {fact.Depth})";
    }

    private static string FactTooltip(Fact fact)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(fact.Text);
        sb.AppendLine($"cf={Certainty.Format(fact.Cf)} · слой {fact.Depth}");

        if (fact.IsDerived && fact.Rule is not null)
        {
            sb.AppendLine($"правило {fact.Rule.Id}" +
                          (string.IsNullOrWhiteSpace(fact.Rule.Why) ? string.Empty : " — " + fact.Rule.Why));
        }
        else
        {
            sb.AppendLine("входной факт");
        }

        return sb.ToString().TrimEnd();
    }

    private GraphNode BuildFactGraph(Fact fact)
    {
        var created = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        var root = new GraphNode
        {
            Id = 0,
            Text = fact.Text,
            SubText = fact.IsDerived ? $"{fact.Rule?.Id} · cf={Certainty.Format(fact.Cf)}" : $"вход · cf={Certainty.Format(fact.Cf)}",
            State = fact.IsDerived ? GraphState.Derived : GraphState.Input,
            Tooltip = FactTooltip(fact)
        };

        created[fact.Text] = root;
        var nextId = 1;
        var stack = new Stack<(Fact Fact, GraphNode Node, int Depth)>();
        stack.Push((fact, root, 0));

        while (stack.Count > 0)
        {
            var (current, node, depth) = stack.Pop();
            node.Depth = depth;

            foreach (var premise in current.Premises)
            {
                if (created.TryGetValue(premise.Text, out _))
                {
                    node.Children.Add(new GraphNode
                    {
                        Id = nextId++,
                        Text = "↺ " + premise.Text,
                        SubText = "повтор — не раскрывается",
                        State = GraphState.Cyclic,
                        Depth = depth + 1,
                        Tooltip = "Цикл: факт уже встречался в этой цепочке, ветвь не раскрывается."
                    });
                    continue;
                }

                var child = new GraphNode
                {
                    Id = nextId++,
                    Text = premise.Text,
                    SubText = premise.IsDerived
                        ? $"{premise.Rule?.Id} · cf={Certainty.Format(premise.Cf)}"
                        : $"вход · cf={Certainty.Format(premise.Cf)}",
                    State = premise.IsDerived ? GraphState.Derived : GraphState.Input,
                    Tooltip = FactTooltip(premise)
                };

                created[premise.Text] = child;
                node.Children.Add(child);
                stack.Push((premise, child, depth + 1));
            }
        }

        return root;
    }

    private void ShowSelectedRule()
    {
        if (_suppress) return;
        if (_lstRules.SelectedRows.Count == 0) return;
        if (_lstRules.SelectedRows[0].Tag is not Rule rule) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Правило {rule.Id}");
        sb.AppendLine($"Коэффициент уверенности правила: {Certainty.Format(rule.Cf)}");
        sb.AppendLine();
        sb.AppendLine("ЕСЛИ:");
        foreach (var condition in rule.Conditions)
        {
            sb.AppendLine($"   {condition}");
        }
        sb.AppendLine("ТО:");
        foreach (var conclusion in rule.Conclusions)
        {
            sb.AppendLine($"   {conclusion}");
        }
        if (!string.IsNullOrWhiteSpace(rule.Why))
        {
            sb.AppendLine();
            sb.AppendLine("Смысл правила:");
            sb.AppendLine("   " + rule.Why);
        }
        if (rule.SourceFile is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Источник: {Path.GetFileName(rule.SourceFile)}:{rule.Line}");
        }

        var firings = _forward?.Firings.Where(f => ReferenceEquals(f.Rule, rule)).Take(5).ToList() ?? new List<Firing>();
        if (firings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Срабатывания в последнем прямом выводе:");
            foreach (var firing in firings)
            {
                sb.AppendLine($"   #{firing.Step} слой {firing.Layer} → {string.Join(", ", firing.Conclusions.Select(c => c.Text))} cf={Certainty.Format(firing.Cf)}");
            }
        }

        _lblCenter.Text = "Правило " + rule.Id;
        _graph.SetTree(null);
        ShowExplanation(sb.ToString());
    }

    private void FillBuild()
    {
        _lstBuild.Rows.Clear();
        if (_plan is null) return;

        foreach (var item in _plan.Chosen)
        {
            var row = _lstBuild.AddRow(item.Item, item.Slot, Certainty.Format(item.Cf),
                item.Need + (item.Active ? " · активный" : string.Empty));
            row.Tag = item;
        }

        foreach (var item in _plan.SecondWave)
        {
            var row = _lstBuild.AddRow(item.Item, item.Slot, Certainty.Format(item.Cf),
                item.Need + (item.Active ? " · активный" : string.Empty) +
                (item.Reason is null ? string.Empty : " · " + item.Reason));
            row.Tag = item;
        }
    }

    private void ShowPlan()
    {
        if (_plan is null)
        {
            ShowExplanation("Сборка ещё не рассчитана: выполните прямой вывод.");
            return;
        }

        _lblCenter.Text = "Сборка предметов под состав противника";
        _graph.SetTree(null);
        ShowExplanation(_plan.Explain());
    }

    // --------------------------------------------------------- объяснения ---

    private void ShowExplanation(string text)
    {
        _txtExplain.SuspendLayout();
        _txtExplain.Clear();
        AppendText(text);
        _txtExplain.SelectionStart = 0;
        _txtExplain.SelectionLength = 0;
        _txtExplain.ResumeLayout();
    }

    private void AppendText(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            _txtExplain.SelectionStart = _txtExplain.TextLength;
            _txtExplain.SelectionLength = 0;
            _txtExplain.SelectionColor = ColorFor(line);
            _txtExplain.AppendText(line + "\n");
        }
    }

    private static Color ColorFor(string line)
    {
        var trimmed = line.TrimStart();

        if (line.Contains("ЦЕЛЬ ДОКАЗАНА", StringComparison.OrdinalIgnoreCase)) return Theme.Green;
        if (line.Contains("НЕ ДОКАЗАНА", StringComparison.OrdinalIgnoreCase)) return Theme.Red;
        if (trimmed.StartsWith("✔")) return Theme.Green;
        if (trimmed.StartsWith("✘")) return Theme.Red;
        if (trimmed.StartsWith("⟲")) return Theme.Purple;
        if (trimmed.StartsWith("⚠")) return Theme.Amber;
        if (trimmed.StartsWith("Шаг")) return Theme.Accent;
        if (trimmed.StartsWith("Расчёт")) return Theme.Amber;
        if (trimmed.StartsWith("СБОРКА") || trimmed.StartsWith("ПРЯМОЙ ВЫВОД")) return Theme.Accent;
        return Theme.Text;
    }

    private void UpdateStatus()
    {
        if (_kb is null)
        {
            _statusLeft.Text = "База не загружена";
            return;
        }

        var total = _forward?.Memory.Count ?? _kb.Facts.Count;
        var derived = _forward?.DerivedCount ?? 0;
        var layers = _forward?.Layers ?? 0;
        var steps = _forward?.Firings.Count ?? 0;

        _statusLeft.Text = $"Фактов: {_factRows.Count} из {total} (выведено {derived})";
        _statusMid.Text = $"Правил: {_kb.Rules.Count}   ·   слоёв: {layers}   ·   шагов: {_step}/{steps}" +
                          (_forward?.LimitReached == true ? "   ·   ⚠ лимит" : string.Empty);
        _statusRight.Text = _forward is null ? string.Empty : $"{_forward.ElapsedMs} мс";
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.TextMuted,
        Font = Theme.Ui,
        Margin = new Padding(6, 9, 6, 4),
        BackColor = Color.Transparent
    };

    private static Button MakeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Height = 30,
            AutoSize = false,
            Width = TextRenderer.MeasureText(text, Theme.Ui).Width + 26,
            Margin = new Padding(0, 3, 8, 3),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Theme.AccentDim;
        button.FlatAppearance.MouseDownBackColor = Theme.Accent;
        return button;
    }

    private static string? FindDataFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(candidate) && relative == "data") return candidate;
        }
        return null;
    }
}

