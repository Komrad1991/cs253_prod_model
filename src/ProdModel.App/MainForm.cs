using System.Drawing;
using ProdModel.Core;

namespace ProdModel.App;

internal sealed class MainForm : Form
{
    private enum Mode { Forward, Backward }
    private enum AnimMode { None, Forward, Backward }

    private Mode _mode = Mode.Forward;
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
    private BackwardResult? _currentBackward;
    private HashSet<string>? _relevantFacts;

    private ReverseResult? _reverseResult;
    private CancellationTokenSource? _reverseCts;

    private readonly System.Windows.Forms.Timer _timer = new();

    // --- переключатель режима ---
    private readonly SegmentBar _modeBar = new();

    // --- общие ---
    private readonly Button _btnOpen = MakeButton("Открыть БЗ…");
    private readonly Button _btnComposition = MakeButton("Состав…");
    private readonly Button _btnItemTargets = MakeButton("Предмет → враги");
    private readonly Button _btnReset = MakeButton("Сброс");

    // --- прямой режим ---
    private readonly Button _btnFwdPlay = MakeButton("▶ Играть");
    private readonly Button _btnFwdStep = MakeButton("⏭ Шаг");
    private readonly Button _btnFwdShow = MakeButton("⏩ Всё сразу");
    private readonly ComboBox _cmbFwdGoal = new();
    private readonly CheckBox _chkFwdHide = new()
    {
        Text = "Скрыть лишнее",
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        Font = Theme.Ui,
        Margin = new Padding(4, 8, 12, 4),
        Cursor = Cursors.Hand
    };

    // --- обратный режим ---
    private readonly Button _btnBwdPlay = MakeButton("▶ Играть");
    private readonly Button _btnBwdStep = MakeButton("⏭ Шаг");
    private readonly Button _btnBwdShow = MakeButton("⏩ Результат");
    private readonly CheckBox _chkBwdHide = new()
    {
        Text = "Скрыть лишнее",
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

    private readonly Panel _forwardControls = new();
    private readonly Panel _backwardControls = new();

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

    // ================================================================ UI ===

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

        var top = new Panel { Dock = DockStyle.Top, Height = 148, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 4) };

        // -------- строка 1: режим + общие --------
        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel
        };

        _modeBar.Add("Прямой вывод");
        _modeBar.Add("Обратный вывод");
        _modeBar.Width = 248;
        _modeBar.Height = 32;
        _modeBar.Margin = new Padding(0, 4, 14, 4);

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
            _modeBar,
            _btnOpen,
            _btnComposition,
            _btnItemTargets,
            _btnReset,
            MakeLabel("Скорость:"),
            _numSpeed
        });

        // -------- строка 2: панель режима --------
        BuildForwardControls();
        BuildBackwardControls();

        // -------- строки 3–4: подписи --------
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
        top.Controls.Add(_backwardControls);
        top.Controls.Add(_forwardControls);
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
            catch (InvalidOperationException) { /* размеры ещё не стабилизировались */ }
        };

        ResumeLayout(true);
    }

    private void BuildForwardControls()
    {
        _forwardControls.Dock = DockStyle.Top;
        _forwardControls.Height = 38;
        _forwardControls.BackColor = Theme.Panel;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel
        };

        _cmbFwdGoal.Width = 280;
        _cmbFwdGoal.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbFwdGoal.FlatStyle = FlatStyle.Flat;
        _cmbFwdGoal.BackColor = Theme.PanelAlt;
        _cmbFwdGoal.ForeColor = Theme.Text;
        _cmbFwdGoal.Font = Theme.Ui;
        _cmbFwdGoal.Margin = new Padding(0, 4, 12, 4);
        _cmbFwdGoal.Items.Add("(без цели)");
        _cmbFwdGoal.SelectedIndex = 0;

        _tips.SetToolTip(_btnFwdPlay, "Запустить/приостановить пошаговую анимацию прямого вывода (F5).");
        _tips.SetToolTip(_btnFwdStep, "Один шаг прямого вывода (F6).");
        _tips.SetToolTip(_btnFwdShow, "Показать сразу весь результат прямого вывода.");
        _tips.SetToolTip(_chkFwdHide,
            "Скрыть промежуточные факты — оставить только входные и итоговые (recommended / pick / second_pick).");
        _tips.SetToolTip(_cmbFwdGoal,
            "Цель: остановить прямой вывод, как только в памяти появится факт с этим образцом.\n" +
            "«(без цели)» — вывод до неподвижной точки.\n" +
            "Внимание: сборка предметов будет построена по частичной памяти.");

        flow.Controls.AddRange(new Control[]
        {
            _btnFwdPlay, _btnFwdStep, _btnFwdShow, _chkFwdHide,
            MakeLabel("Цель:"), _cmbFwdGoal
        });

        _forwardControls.Controls.Add(flow);
    }

    private void BuildBackwardControls()
    {
        _backwardControls.Dock = DockStyle.Top;
        _backwardControls.Height = 38;
        _backwardControls.BackColor = Theme.Panel;
        _backwardControls.Visible = false;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
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

        _tips.SetToolTip(_btnBwdPlay, "Запустить/приостановить пошаговое раскрытие дерева доказательства.");
        _tips.SetToolTip(_btnBwdStep, "Показать один следующий узел дерева.");
        _tips.SetToolTip(_btnBwdShow, "Показать дерево целиком, без анимации.");
        _tips.SetToolTip(_chkBwdHide,
            "Свернуть отвергнутые ветви в компактные чипы.\n" +
            "Снимите галочку, чтобы увидеть полные поддеревья отвергнутых попыток (включая предотвращённые циклы).");

        flow.Controls.AddRange(new Control[]
        {
            _btnBwdPlay, _btnBwdStep, _btnBwdShow, _chkBwdHide,
            MakeLabel("Цель:"), _cmbGoals
        });

        _backwardControls.Controls.Add(flow);
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

    // ========================================================== события ===

    private void WireEvents()
    {
        _modeBar.SelectedIndexChanged += (_, _) => SwitchMode(_modeBar.SelectedIndex == 0 ? Mode.Forward : Mode.Backward);

        _btnOpen.Click += (_, _) => OpenKbDialog();
        _btnComposition.Click += (_, _) => ChooseComposition();
        _btnItemTargets.Click += (_, _) => ChooseItemAndShowTargets();
        _btnReset.Click += (_, _) => { if (_kbPath is not null) LoadKb(_kbPath); };

        // прямой режим
        _btnFwdPlay.Click += (_, _) => ForwardPlay();
        _btnFwdStep.Click += (_, _) => ForwardStep(manual: true);
        _btnFwdShow.Click += (_, _) => ForwardShowResult();
        _chkFwdHide.CheckedChanged += (_, _) => { _relevantFacts = null; if (_forward is not null) RefreshFactList(); };
        _cmbFwdGoal.SelectedIndexChanged += (_, _) =>
        {
            if (_suppress) return;
            StopAnimation();
            _forward = null;
            _plan = null;
            _relevantFacts = null;
            _factRows.Clear();
            _lstFacts.Rows.Clear();
            _lstBuild.Rows.Clear();
            _graph.SetTree(null);
            _lblCenter.Text = "Цель изменена: нажмите «▶ Играть» для пересчёта";
            ShowExplanation("Цель прямого вывода изменена. Нажмите «▶ Играть», «⏭ Шаг» или «⏩ Всё сразу».");
        };

        // обратный режим
        _btnBwdPlay.Click += (_, _) => BackwardPlay();
        _btnBwdStep.Click += (_, _) => BackwardStep(manual: true);
        _btnBwdShow.Click += (_, _) => BackwardShowResult();
        _chkBwdHide.CheckedChanged += (_, _) => { if (_currentBackward is not null) ShowBackward(_currentBackward, animate: false, revealAll: true); };
        _cmbGoals.SelectedIndexChanged += (_, _) =>
        {
            if (_suppress) return;
            _currentBackward = null;
            _backward.Clear();
            _lstResults.Rows.Clear();
            _graph.SetTree(null);
            _lblCenter.Text = "Обратный вывод: выберите цель и нажмите «Играть»";
            ShowExplanation("Цель выбрана. Нажмите «▶ Играть», «⏭ Шаг» или «⏩ Результат».");
        };

        _timer.Tick += (_, _) => OnTick();

        _lstFacts.SelectionChanged += (_, _) => ShowSelectedFact();
        _lstRules.SelectionChanged += (_, _) => ShowSelectedRule();
        _lstResults.SelectionChanged += (_, _) => ShowSelectedResult();
        _lstBuild.SelectionChanged += (_, _) => ShowPlan();
        _segments.SelectedIndexChanged += (_, _) => ShowSegment();

        _graph.NodeClicked += node =>
        {
            if (_reverseResult is not null)
            {
                var heroVerdict = _reverseResult.Heroes
                    .FirstOrDefault(h => h.Hero.Equals(node.Text, StringComparison.OrdinalIgnoreCase));
                if (heroVerdict?.Proof is not null)
                {
                    ShowBackward(heroVerdict.Proof, animate: false, revealAll: true);
                    return;
                }
            }

            if (_factRows.TryGetValue(node.Text, out var row))
            {
                row.Selected = true;
                _lstFacts.CurrentCell = row.Cells[0];
                _lstFacts.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 3);
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { if (_mode == Mode.Forward) ForwardPlay(); else BackwardPlay(); }
            if (e.KeyCode == Keys.F6) { if (_mode == Mode.Forward) ForwardStep(manual: true); else BackwardStep(manual: true); }
        };
    }

    private void SwitchMode(Mode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        StopAnimation();

        _forwardControls.Visible = mode == Mode.Forward;
        _backwardControls.Visible = mode == Mode.Backward;

        if (mode == Mode.Forward && _segments.SelectedIndex == 2) _segments.SelectedIndex = 0;
        if (mode == Mode.Backward && _segments.SelectedIndex == 0 && _backward.Count > 0) _segments.SelectedIndex = 2;

        UpdateAnswer();
        UpdateStatus();

        if (mode == Mode.Forward && _lblCenter.Text.Length == 0)
            _lblCenter.Text = "Прямой вывод";
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
            if (index >= 0) { _segments.SelectedIndex = index; ShowSegment(); }
        }

        if (_startupArgs.Contains("--full", StringComparer.OrdinalIgnoreCase)) _chkBwdHide.Checked = false;
        if (_startupArgs.Contains("--hero-counter", StringComparer.OrdinalIgnoreCase)) { /* не используется */ }

        var goalArg = GetArg(_startupArgs, "--goal");
        if (goalArg is not null)
        {
            if (int.TryParse(goalArg, out var index) && index >= 0 && index < _cmbGoals.Items.Count)
                _cmbGoals.SelectedIndex = index;
            else if (ParseGoalArg(goalArg) is { } goal)
            {
                _cmbGoals.Items.Add(goal);
                _cmbGoals.SelectedIndex = _cmbGoals.Items.Count - 1;
            }
        }

        if (_startupArgs.Contains("--forward", StringComparer.OrdinalIgnoreCase))
        {
            _modeBar.SelectedIndex = 0;
            ForwardShowResult();
        }

        if (_startupArgs.Contains("--backward", StringComparer.OrdinalIgnoreCase))
        {
            _modeBar.SelectedIndex = 1;
            BackwardShowResult();
        }
    }

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
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string? ResolveKbPath(string path)
    {
        if (File.Exists(path)) return Path.GetFullPath(path);
        var found = FindDataFile(path);
        if (found is not null) return found;
        return FindDataFile(path.Replace('/', '\\').TrimStart('\\'));
    }

    // ======================================================= загрузка БЗ ===

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

    private void ApplyComposition()
    {
        if (_kbBase is null) return;

        var known = _kbBase.GetSort("hero")?.Values;
        if (known is { Count: > 0 })
            _enemies.RemoveAll(e => !known.Contains(e, StringComparer.OrdinalIgnoreCase));

        if (_enemies.Count > 6) _enemies.RemoveRange(6, _enemies.Count - 6);
        _enemies.Sort(StringComparer.OrdinalIgnoreCase);

        _kb = _kbBase.WithEnemies(_enemies.Select(name =>
            (Name: name, Cf: _enemyCf.TryGetValue(name, out var cf) ? cf : 0.9)));

        StopAnimation();
        _reverseResult = null;
        _currentBackward = null;
        _currentFact = null;
        _relevantFacts = null;
        _forward = null;
        _plan = null;
        _backward.Clear();
        _step = 0;
        _reveal = 0;
        _revealMax = 0;
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

        _cmbFwdGoal.Items.Clear();
        _cmbFwdGoal.Items.Add("(без цели)");
        foreach (var goal in _kb.Goals) _cmbFwdGoal.Items.Add(goal);
        _cmbFwdGoal.SelectedIndex = 0;

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
        UpdateStatus();
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

    // ============================================ кнопки: прямой режим ===

    private Predicate? GetForwardGoal()
    {
        if (_cmbFwdGoal.SelectedIndex <= 0) return null;
        if (_cmbFwdGoal.SelectedItem is not GoalDefinition goal) return null;
        return goal.Pattern;
    }

    private static string GoalReachedText(ForwardResult f)
    {
        if (!f.GoalReached) return string.Empty;
        var stepInfo = f.GoalReachedAtStep == 0 ? "уже во входных фактах" : $"шаг {f.GoalReachedAtStep}";
        return $"Цель достигнута: {f.ReachedGoal!.Text} cf={Certainty.Format(f.ReachedGoal.Cf)} ({stepInfo})";
    }

    private void ForwardPlay()
    {
        if (_kb is null) { ShowExplanation("Сначала загрузите базу знаний."); return; }
        if (_forward is null) ComputeForward();
        if (_forward is null) return;

        if (_forward.Firings.Count == 0)
        {
            // Анимировать нечего — показываем итог (в т.ч. случай «цель во входных»)
            ForwardShowResult();
            return;
        }

        if (_anim == AnimMode.Forward)
        {
            StopAnimation();
            return;
        }

        if (_step >= _forward.Firings.Count) ResetForwardAnimation();

        _reverseResult = null;
        _anim = AnimMode.Forward;
        _timer.Interval = Math.Max(20, (int)_numSpeed.Value);
        _timer.Start();
        _btnFwdPlay.Text = "■ Стоп";
        _lblCenter.Text = "Прямой вывод: пошаговое срабатывание продукций";
    }

    private void ForwardStep(bool manual = true)
    {
        if (_kb is null) return;
        if (_forward is null) ComputeForward();
        if (_forward is null) return;

        if (_forward.Firings.Count == 0)
        {
            UpdateStatus();
            _lblCenter.Text = _forward.GoalReached
                ? GoalReachedText(_forward)
                : "Прямой вывод: нет срабатываний";
            return;
        }

        if (manual && _anim == AnimMode.Forward) StopAnimation();
        if (_step >= _forward.Firings.Count) ResetForwardAnimation();

        ApplyFiring(_forward.Firings[_step]);
        _step++;
        UpdateStatus();

        if (_forward.GoalReached && _step >= _forward.GoalReachedAtStep)
        {
            StopAnimation();
            _lblCenter.Text = GoalReachedText(_forward);
        }
    }

    private void ForwardShowResult()
    {
        if (_kb is null) return;
        if (_forward is null) ComputeForward();
        if (_forward is null) return;

        StopAnimation();
        _reverseResult = null;
        _bulk = true;
        while (_step < _forward.Firings.Count)
        {
            ApplyFiring(_forward.Firings[_step]);
            _step++;
        }
        _bulk = false;

        foreach (var fact in _forward.Memory.Facts) UpsertFactRow(fact);

        // Цель имеет приоритет над «лучшей рекомендацией»
        if (_forward.GoalReached)
        {
            ShowFactGraph(_forward.ReachedGoal!);
            _lblCenter.Text = GoalReachedText(_forward);
            ShowExplanation(TextExplainer.ExplainFact(_forward.ReachedGoal!) + "\n\n" + BuildForwardSummary());
            UpdateStatus();
            return;
        }

        var best = _forward.Memory.Facts
            .Where(f => f.Atom.Name.Equals("recommended", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Cf)
            .FirstOrDefault();

        if (best is not null)
        {
            ShowFactGraph(best);
            _lblCenter.Text = $"Лучшая рекомендация: {best.Text}   (cf={Certainty.Format(best.Cf)}, слой {best.Depth})";
            ShowExplanation(TextExplainer.ExplainFact(best) + "\n\n" + BuildForwardSummary());
        }
        else
        {
            _graph.SetTree(null);
            _lblCenter.Text = $"Прямой вывод завершён: {_forward.Memory.Count} фактов, {_forward.Firings.Count} срабатываний";
            ShowExplanation(BuildForwardSummary());
        }

        UpdateStatus();
    }

    private void ResetForwardAnimation()
    {
        _step = 0;
        _factRows.Clear();
        _lstFacts.Rows.Clear();
        if (_forward is not null)
            foreach (var fact in _forward.Memory.Facts.Where(f => !f.IsDerived)) AddFactRow(fact);
        _graph.SetTree(null);
    }

    private void ComputeForward()
    {
        if (_kb is null) return;

        StopAnimation();

        _forward = new ForwardChainer().Run(_kb, new ForwardOptions { Goal = GetForwardGoal() });
        _relevantFacts = null;

        _suppress = true;
        RefreshFactList();
        _suppress = false;

        _plan = new BuildPlanner().Build(_forward, _kb);
        FillBuild();
        UpdateStatus();
    }

    private void RefreshFactList()
    {
        if (_forward is null) return;
        _factRows.Clear();
        _lstFacts.Rows.Clear();

        var filter = _chkFwdHide.Checked ? GetRelevantFacts() : null;

        foreach (var fact in _forward.Memory.Facts)
        {
            if (filter is not null && !filter.Contains(fact.Text)) continue;
            if (fact.IsDerived && _step == 0) continue;  // ещё не «сыграно» — не показываем
            AddFactRow(fact);
        }
    }

    /// <summary>Факты, ведущие к recommended / pick / second_pick (для режима «Скрыть лишнее»).</summary>
    private HashSet<string> GetRelevantFacts()
    {
        if (_relevantFacts is not null) return _relevantFacts;
        if (_forward is null) return _relevantFacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var relevant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Fact>();

        foreach (var fact in _forward.Memory.Facts)
            if (IsResultPredicate(fact.Atom.Name)) stack.Push(fact);

        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if (!relevant.Add(f.Text)) continue;
            foreach (var p in f.Premises) stack.Push(p);
        }

        return _relevantFacts = relevant;
    }

    private static bool IsResultPredicate(string name) =>
        name.Equals("recommended", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pick", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("second_pick", StringComparison.OrdinalIgnoreCase);

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

        if (_forward.Goal is not null)
        {
            sb.AppendLine();
            if (_forward.GoalReached)
            {
                var stepInfo = _forward.GoalReachedAtStep == 0
                    ? "уже во входных фактах"
                    : $"шаг {_forward.GoalReachedAtStep}";
                sb.AppendLine($"ЦЕЛЬ {_forward.Goal} ДОСТИГНУТА: {_forward.ReachedGoal!.Text} " +
                              $"cf={Certainty.Format(_forward.ReachedGoal.Cf)} ({stepInfo})");
            }
            else
            {
                sb.AppendLine($"ЦЕЛЬ {_forward.Goal} НЕ ДОСТИГНУТА за {_forward.Firings.Count} срабатываний");
            }
            sb.AppendLine();
        }

        if (_plan is not null)
        {
            sb.AppendLine();
            sb.Append(_plan.Explain());
        }
        return sb.ToString();
    }

    // =========================================== кнопки: обратный режим ===

    private void BackwardPlay()
    {
        if (_kb is null) { ShowExplanation("Сначала загрузите базу знаний."); return; }

        if (_anim == AnimMode.Backward)
        {
            StopAnimation();
            return;
        }

        if (_currentBackward is null)
        {
            ComputeBackward();
            if (_currentBackward is null) return;
            ShowBackward(_currentBackward, animate: true, revealAll: false);
            return;
        }

        // Если дерево уже было раскрыто до конца — начинаем анимацию с начала
        if (_reveal == int.MaxValue || _reveal >= _revealMax)
        {
            _reveal = 0;
            _graph.Reveal(0);
        }

        _anim = AnimMode.Backward;
        _timer.Interval = Math.Max(20, (int)_numSpeed.Value / 3);
        _timer.Start();
        _btnBwdPlay.Text = "■ Стоп";
    }

    private void BackwardStep(bool manual = true)
    {
        if (_kb is null) return;

        if (_currentBackward is null)
        {
            ComputeBackward();
            if (_currentBackward is null) return;
            ShowBackward(_currentBackward, animate: false, revealAll: false);
            _reveal = 0;
            _graph.Reveal(0);
            return;
        }

        if (manual && _anim == AnimMode.Backward) StopAnimation();

        // Если дошли до конца — сбрасываем на начало, иначе показываем следующий узел
        if (_reveal == int.MaxValue || _reveal >= _revealMax)
        {
            _reveal = 0;
            _graph.Reveal(0);
        }
        else
        {
            _reveal++;
            _graph.Reveal(_reveal);
        }
    }

    private void BackwardShowResult()
    {
        if (_kb is null) return;

        if (_currentBackward is null)
        {
            ComputeBackward();
            if (_currentBackward is null) return;
        }

        ShowBackward(_currentBackward, animate: false, revealAll: true);
    }

    /// <summary>Считает обратный вывод без анимации. Результаты — в списке слева.</summary>
    private void ComputeBackward()
    {
        if (_kb is null) return;
        if (_cmbGoals.SelectedItem is not GoalDefinition goal) { ShowExplanation("Выберите цель."); return; }
        if (_forward is null) ComputeForward();

        StopAnimation();
        _reverseResult = null;
        Cursor = Cursors.WaitCursor;
        _backward.Clear();
        _lstResults.Rows.Clear();
        _currentBackward = null;

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
                _graph.SetTree(null);
                UpdateAnswer();
                return;
            }

            _segments.SelectedIndex = 2;
            _lstResults.Rows[0].Selected = true;
            _lstResults.CurrentCell = _lstResults.Rows[0].Cells[0];
            _currentBackward = _backward[0];
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        UpdateAnswer();
        UpdateStatus();
    }

    private void ShowBackward(BackwardResult result, bool animate, bool revealAll)
    {
        _reverseResult = null;
        _currentBackward = result;
        _currentFact = null;
        _graph.FullTree = !_chkBwdHide.Checked;

        var root = BuildBackwardGraph(result.Root, _graph.FullTree);
        _revealMax = MaxId(root);

        var rejected = CountRejected(result.Root);
        var cycles = CountCycles(result.Root);

        StopAnimation();

        if (animate)
        {
            _reveal = 0;
            _graph.SetTreeAnimated(root, 0);
            _anim = AnimMode.Backward;
            _timer.Interval = Math.Max(20, (int)_numSpeed.Value / 3);
            _timer.Start();
            _btnBwdPlay.Text = "■ Стоп";
        }
        else if (revealAll)
        {
            _reveal = int.MaxValue;
            _graph.SetTree(root);
        }
        else
        {
            _reveal = 0;
            _graph.SetTreeAnimated(root, 0);
        }

        var suffix = rejected > 0
            ? $"   ·   отклонено ветвей: {rejected}" +
              (cycles > 0 ? $", из них циклов: {cycles}" : string.Empty) +
              (_chkBwdHide.Checked ? "   ·   снят «Скрыть лишнее» — показать все ветви" : string.Empty)
            : string.Empty;

        _lblCenter.Text = $"Доказательство цели: {result.Goal}   (cf={Certainty.Format(result.Cf)}, слоёв {result.Layers})" + suffix;
        ShowExplanation(TextExplainer.ExplainBackward(result, includeRejected: true));
        UpdateAnswer();
    }

    private void ShowSelectedResult()
    {
        if (_suppress) return;
        if (_lstResults.SelectedRows.Count == 0) return;
        if (_lstResults.SelectedRows[0].Tag is not BackwardResult result) return;
        ShowBackward(result, animate: false, revealAll: true);
    }

    private void OnTick()
    {
        if (_anim == AnimMode.Forward)
        {
            if (_forward is null || _step >= _forward.Firings.Count)
            {
                StopAnimation();
                UpdateStatus();
                if (_forward is { GoalReached: true }) _lblCenter.Text = GoalReachedText(_forward);
                return;
            }
            ForwardStep(manual: false);
            return;
        }

        if (_anim == AnimMode.Backward)
        {
            if (_reveal == int.MaxValue || _reveal >= _revealMax)
            {
                _reveal = int.MaxValue;
                _graph.Reveal(int.MaxValue);
                StopAnimation();
                return;
            }

            _reveal += 2;
            if (_reveal >= _revealMax)
            {
                _reveal = int.MaxValue;
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
        _btnFwdPlay.Text = "▶ Играть";
        _btnBwdPlay.Text = "▶ Играть";
    }

    // ======================================================== факты ===

    private void AddFactRow(Fact fact)
    {
        if (_factRows.ContainsKey(fact.Text)) return;
        var row = _lstFacts.AddRow(fact.Text, Certainty.Format(fact.Cf), fact.Depth.ToString(),
            fact.IsDerived ? fact.Rule?.Id ?? "вывод" : "входной");
        _factRows[fact.Text] = row;
    }

    private void UpsertFactRow(Fact fact)
    {
        if (_chkFwdHide.Checked)
        {
            var relevant = GetRelevantFacts();
            if (!relevant.Contains(fact.Text)) return;
        }

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
        _reverseResult = null;
        _currentFact = fact;
        _currentBackward = null;
        var root = BuildFactGraph(fact);
        _graph.FullTree = false;
        _graph.SetTree(root);
        _lblCenter.Text = "Обоснование факта: " + fact.Text + $"   (cf={Certainty.Format(fact.Cf)}, слой {fact.Depth})";
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
                if (created.ContainsKey(premise.Text))
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
        else sb.AppendLine("входной факт");

        return sb.ToString().TrimEnd();
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
        foreach (var condition in rule.Conditions) sb.AppendLine($"   {condition}");
        sb.AppendLine("ТО:");
        foreach (var conclusion in rule.Conclusions) sb.AppendLine($"   {conclusion}");

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

        _reverseResult = null;
        _lblCenter.Text = "Правило " + rule.Id;
        _graph.SetTree(null);
        ShowExplanation(sb.ToString());
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
            ShowExplanation("Обратный вывод ещё не выполнялся.\nВыберите цель сверху и нажмите «▶ Играть» или «⏩ Результат».");
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
        _reverseResult = null;
        _lblCenter.Text = "Сборка предметов под состав противника";
        _graph.SetTree(null);
        ShowExplanation(_plan.Explain());
    }

    // =========================================================== reverse ===

    private async void ChooseItemAndShowTargets()
    {
        if (_reverseCts is not null)
        {
            _reverseCts.Cancel();
            return;
        }

        if (_kbBase is null)
        {
            ShowExplanation("База знаний не загружена.\nНажмите «Открыть БЗ…» и выберите файл data/deadlock_kb.txt.");
            return;
        }

        var items = _kbBase.GetSort("item")?.Values ?? new List<string>();
        if (items.Count == 0)
        {
            ShowExplanation("В базе знаний нет объявленного сорта item — нечего выбирать.");
            return;
        }

        var preselected = _plan?.BestCounter?.Item;
        using var dialog = new ItemPickerForm(items, preselected);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var selectedItem = dialog.SelectedItem;

        _reverseCts = new CancellationTokenSource();
        _btnItemTargets.Text = "Отмена";
        _btnItemTargets.Enabled = true;
        Cursor = Cursors.WaitCursor;
        _lblCenter.Text = $"Обратный запрос: считаю {selectedItem}…";

        var progress = new Progress<string>(msg =>
        {
            _lblCenter.Text = $"Обратный запрос: {selectedItem} · {msg}";
        });

        try
        {
            var options = new ReverseQueryOptions
            {
                MaxDepth = 8,
                MaxNodes = 1_500,
                Cancellation = _reverseCts.Token,
                Progress = progress
            };

            var result = await Task.Run(() => ReverseQuery.Run(_kbBase, selectedItem, options),
                _reverseCts.Token);

            StopAnimation();
            _reverseResult = result;
            _graph.FullTree = false;
            _graph.SetTree(BuildReverseOverviewGraph(result, false, _kbBase));

            _lblCenter.Text = $"Обратный запрос: предмет {result.Item} → " +
                              $"{result.FullyCovered.Count()} полных контров, {result.Dangerous.Count()} опасных";
            ShowExplanation(result.ExplainBestTargets());

            _statusLeft.Text = $"Предмет {result.Item}: закрывает {result.FullyCovered.Count()} героев";
            _statusMid.Text = $"Опасных для предмета: {result.Dangerous.Count()}   ·   всего героев: {result.Heroes.Count}";
            _statusRight.Text = string.Empty;
        }
        catch (OperationCanceledException) { _lblCenter.Text = "Обратный запрос отменён"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Обратный запрос", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _reverseCts?.Dispose();
            _reverseCts = null;
            _btnItemTargets.Text = "Предмет → враги";
            _btnItemTargets.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private static GraphNode BuildReverseOverviewGraph(ReverseResult result, bool showHeroCounter, KnowledgeBase kb, int maxHeroes = 15)
    {
        var root = new GraphNode
        {
            Id = 0,
            Text = $"предмет {result.Item}",
            SubText = $"героев: {result.Heroes.Count} · полный контр: {result.FullyCovered.Count()} · " +
                      $"опасных: {result.Dangerous.Count()}",
            State = GraphState.Derived,
            Depth = 0,
            Tooltip = $"Обратный запрос: против каких героев {result.Item} закрывает больше всего угроз."
        };

        var heroes = result.Heroes
            .Where(h => !h.NoThreats)
            .OrderByDescending(h => h.Covered)
            .ThenByDescending(h => h.FullyCountered)
            .ThenBy(h => h.Hero, StringComparer.OrdinalIgnoreCase)
            .Take(maxHeroes)
            .ToList();

        var nextId = 1;
        foreach (var hero in heroes)
        {
            var heroNode = new GraphNode
            {
                Id = nextId++,
                Text = hero.Hero,
                SubText = hero.TargetLabel,
                State = hero.FullyCountered ? GraphState.Proven
                      : hero.Dangerous ? GraphState.Failed
                      : GraphState.Derived,
                Depth = 1,
                Tooltip = $"Герой {hero.Hero}\nПокрыто угроз: {hero.Covered} из {hero.TotalThreats}"
            };

            foreach (var tag in hero.CoveredTags)
                heroNode.Children.Add(new GraphNode
                {
                    Id = nextId++,
                    Text = tag,
                    SubText = "закрыто",
                    State = GraphState.Proven,
                    Depth = 2,
                    Tooltip = $"{hero.Hero} · {tag}: предмет {result.Item} закрывает"
                });

            foreach (var tag in hero.UncoveredTags)
                heroNode.Children.Add(new GraphNode
                {
                    Id = nextId++,
                    Text = tag,
                    SubText = "не закрыто",
                    State = GraphState.Failed,
                    Depth = 2,
                    Tooltip = $"{hero.Hero} · {tag}: предмет не защищает"
                });

            root.Children.Add(heroNode);
        }

        return root;
    }

    // ============================================ построение дерева ===

    private static GraphNode BuildBackwardGraph(SearchNode root, bool expandRejected)
    {
        var map = new Dictionary<SearchNode, GraphNode>();
        var order = new List<SearchNode>();
        var stack = new Stack<(SearchNode Node, bool Done)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (node, done) = stack.Pop();
            if (done) { order.Add(node); continue; }
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
                if (map.TryGetValue(child, out var childGraph)) graph.Children.Add(childGraph);

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

        var nextId = map.Values.Max(g => g.Id);

        // В полном режиме раскрываем все поддеревья, включая очень глубокие
        // (там и живут «закопанные» циклы). В компактном — оставляем прежний
        // небольшой лимит: там на каждую отвергнутую ветвь всё равно
        // приходится один чип, и глубокие поддеревья не разворачиваются.
        var budget = expandRejected ? 20_000 : 600;

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

                graph.Children.Add(new GraphNode
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
                });
            }
        }

        return built;
    }

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
                foreach (var child in attempt.Children) stack.Push(child);
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
            sb.AppendLine($"правило {node.Rule.Id}" +
                          (string.IsNullOrWhiteSpace(node.Rule.Why) ? string.Empty : " — " + node.Rule.Why));
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

    // ===================================================== объяснения ===

    private string IntroText()
    {
        var warnings = _kb!.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToList();
        var text = $"БЗ: {_kb.Name}\n" +
                   $"Файл знаний: {string.Join(", ", _kb.Sources.Select(Path.GetFileName))}\n" +
                   $"Состав противника: {DescribeComposition()}   (выбирается кнопкой «Состав…», 1–6 героев)\n" +
                   $"Фактов: {_kb.Facts.Count}, правил: {_kb.Rules.Count}, целей: {_kb.Goals.Count}\n\n" +
                   "Сверху переключаются два режима:\n" +
                   "  • «Прямой вывод» — факты появляются по шагам, видно каждое срабатывание;\n" +
                   "  • «Обратный вывод» — доказывает выбранную цель и строит дерево доказательства.\n\n" +
                   "В каждом режиме есть «▶ Играть / ■ Стоп», «⏭ Шаг», «⏩ Всё сразу» и «Скрыть лишнее».\n" +
                   "В прямом режиме можно задать цель — вывод остановится при её появлении.\n" +
                   "«Предмет → враги» — отдельный обратный запрос: какие герои хуже всего переносят выбранный предмет.\n";

        if (warnings.Count > 0)
        {
            text += "\nЗамечания к базе:\n";
            foreach (var diagnostic in warnings.Take(6)) text += "  " + diagnostic + "\n";
        }
        return text;
    }

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
        if (line.Contains("ЦЕЛЬ ДОСТИГНУТА", StringComparison.OrdinalIgnoreCase)) return Theme.Green;
        if (line.Contains("НЕ ДОКАЗАНА", StringComparison.OrdinalIgnoreCase)) return Theme.Red;
        if (line.Contains("НЕ ДОСТИГНУТА", StringComparison.OrdinalIgnoreCase)) return Theme.Amber;
        if (trimmed.StartsWith("✔")) return Theme.Green;
        if (trimmed.StartsWith("✘")) return Theme.Red;
        if (trimmed.StartsWith("⟲")) return Theme.Purple;
        if (trimmed.StartsWith("⚠")) return Theme.Amber;
        if (trimmed.StartsWith("Шаг")) return Theme.Accent;
        if (trimmed.StartsWith("Расчёт")) return Theme.Amber;
        if (trimmed.StartsWith("СБОРКА") || trimmed.StartsWith("ПРЯМОЙ ВЫВОД")) return Theme.Accent;
        if (trimmed.StartsWith("ПРЕДМЕТ:") || trimmed.StartsWith("ЛУЧШИЕ ЦЕЛИ")) return Theme.Accent;
        if (trimmed.StartsWith("ПРОТИВ КОГО ПЛОХ")) return Theme.Amber;
        return Theme.Text;
    }

    // ========================================================= статус ===

    private void UpdateAnswer()
    {
        // Если в прямом режиме задана цель — показываем её статус
        if (_mode == Mode.Forward && _forward is not null && _forward.Goal is not null)
        {
            if (_forward.GoalReached)
            {
                _lblAnswer.ForeColor = Theme.Green;
                var stepInfo = _forward.GoalReachedAtStep == 0 ? "уже во входных фактах"
                                                               : $"шаг {_forward.GoalReachedAtStep}";
                _lblAnswer.Text = $"Цель достигнута: {_forward.ReachedGoal!.Text} " +
                                  $"cf={Certainty.Format(_forward.ReachedGoal.Cf)} ({stepInfo})";
            }
            else
            {
                _lblAnswer.ForeColor = Theme.Amber;
                _lblAnswer.Text = $"Цель {_forward.Goal} НЕ достигнута за {_forward.Firings.Count} срабатываний";
            }
            return;
        }

        if (_mode == Mode.Backward)
        {
            if (_currentBackward is not null && _currentBackward.Proven)
            {
                _lblAnswer.ForeColor = Theme.Green;
                _lblAnswer.Text = $"Цель: {_currentBackward.Goal} → доказана, cf={Certainty.Format(_currentBackward.Cf)}";
            }
            else if (_currentBackward is not null)
            {
                _lblAnswer.ForeColor = Theme.Red;
                _lblAnswer.Text = $"Цель: {_currentBackward.Goal} → не доказана";
            }
            else
            {
                _lblAnswer.ForeColor = Theme.TextMuted;
                _lblAnswer.Text = "Обратный вывод: выберите цель и нажмите «▶ Играть»";
            }
            return;
        }

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

    private void UpdateStatus()
    {
        if (_kb is null) { _statusLeft.Text = "База не загружена"; return; }

        var total = _forward?.Memory.Count ?? _kb.Facts.Count;
        var derived = _forward?.DerivedCount ?? 0;
        var layers = _forward?.Layers ?? 0;
        var steps = _forward?.Firings.Count ?? 0;

        if (_mode == Mode.Forward)
        {
            _statusLeft.Text = $"Фактов: {_factRows.Count} из {total} (выведено {derived})";
            var goalInfo = _forward?.Goal is not null
                ? (_forward.GoalReached ? "   ·   ✔ цель" : "   ·   ✗ цель")
                : string.Empty;
            _statusMid.Text = $"Правил: {_kb.Rules.Count}   ·   слоёв: {layers}   ·   шагов: {_step}/{steps}" +
                              goalInfo +
                              (_forward?.LimitReached == true ? "   ·   ⚠ лимит" : string.Empty);
            _statusRight.Text = _forward is null ? string.Empty : $"{_forward.ElapsedMs} мс";
        }
        else
        {
            _statusLeft.Text = $"Обратный вывод: целей в списке {_backward.Count}";
            _statusMid.Text = $"Цель: {_cmbGoals.SelectedItem}   ·   узлов в дереве: {(_currentBackward?.NodesVisited ?? 0)}";
            _statusRight.Text = _currentBackward is null ? string.Empty : $"cf={Certainty.Format(_currentBackward.Cf)}";
        }
    }

    private string DescribeComposition()
        => _enemies.Count == 0 ? "не задан" : string.Join(", ", _enemies) + $" — {_enemies.Count}";

    private static IEnumerable<string> DefaultComposition(KnowledgeBase kb)
    {
        var heroes = kb.GetSort("hero")?.Values ?? new List<string>();
        var preferred = new[] { "Haze", "Seven", "Abrams" };
        return preferred.Where(p => heroes.Contains(p, StringComparer.OrdinalIgnoreCase)).Take(3);
    }

    // ========================================================= утилиты ===

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