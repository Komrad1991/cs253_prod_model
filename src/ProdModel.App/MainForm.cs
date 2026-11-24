using System.Drawing;
using ProdModel.Core;

namespace ProdModel.App;

internal sealed class MainForm : Form
{
    private enum Mode { Forward, Backward, Clips }

    private Mode _mode = Mode.Forward;
    private KnowledgeBase? _kb;
    private string? _kbPath;
    private ForwardResult? _forward;
    private readonly List<BackwardResult> _backward = new();

    private int _step;
    private int _fwdReveal;
    private int _bwdReveal;

    /// <summary>Сколько шагов раскрыто в текущем режиме. У каждого режима свой счётчик.</summary>
    private int _reveal
    {
        get => _mode == Mode.Forward ? _fwdReveal : _bwdReveal;
        set
        {
            if (_mode == Mode.Forward) _fwdReveal = value;
            else _bwdReveal = value;
        }
    }

    /// <summary>Режим CLIPS: своя панель вместо графа и своя строка кнопок.</summary>
    private readonly ClipsModePanel _clips = new();
    private readonly Panel _clipsControls = new();

    // Левая панель и основная область прячутся в режиме CLIPS — им нужны поля.
    private Panel? _side;
    private Panel? _main;
    private SplitContainer? _split;
    private bool _animRunning;
    private Mode _animMode;
    private bool _suppress;
    private bool _refreshing;
    private bool _suppressResultSelect;
    private Fact? _focusFact;
    private BackwardResult? _currentBackward;

    // Обратный вывод раскрывается по узлам: один шаг = один узел дерева доказательства.
    private readonly List<(BackwardResult Result, SearchNode Node)> _bwdSteps = new();
    private readonly Dictionary<SearchNode, int> _bwdOrder = new();
    private readonly List<int> _bwdStarts = new();

    private readonly System.Windows.Forms.Timer _timer = new();

    // --- переключатель режима ---
    private readonly SegmentBar _modeBar = new();

    // --- общие ---
    private readonly Button _btnOpen = MakeButton("Открыть БЗ…");
    private readonly Button _btnReset = MakeButton("Сброс");

    // --- прямой режим ---
    private readonly Button _btnFwdPlay = MakeButton("▶ Играть");
    private readonly Button _btnFwdStep = MakeButton("⏭ Шаг");
    private readonly Button _btnFwdShow = MakeButton("⏩ Всё сразу");
    private readonly ComboBox _cmbFwdGoal = new();

    // --- обратный режим ---
    private readonly Button _btnBwdPlay = MakeButton("▶ Играть");
    private readonly Button _btnBwdStep = MakeButton("⏭ Шаг");
    private readonly Button _btnBwdShow = MakeButton("⏩ Всё сразу");

    private readonly ComboBox _cmbGoals = new();
    private readonly NumericUpDown _numSpeed = new();
    private readonly Label _lblSpeed = new();
    private readonly Label _lblKb = new();
    private SegmentBar? _tabs;

    private readonly Panel _forwardControls = new();
    private readonly Panel _backwardControls = new();

    private readonly DarkGrid _lstFacts = new();
    private readonly DarkGrid _lstRules = new();
    private readonly DarkGrid _lstResults = new();
    private readonly RichTextBox _txtExplain = new();
    private readonly GraphPanel _graph = new();

    // Вторая визуализация тех же данных: блоки по слоям вывода (как в режиме CLIPS).
    private readonly BlockGraphPanel _blockGraph = new();
    private readonly SegmentBar _graphViews = new();
    private readonly SegmentBar _graphScope = new();

    private string _baseExplanation = string.Empty;

    private readonly ToolStripStatusLabel _statusLeft = new();
    private readonly ToolStripStatusLabel _statusMid = new();
    private readonly ToolStripStatusLabel _statusRight = new();

    public MainForm(string[]? args = null)
    {
        BuildUi();
        WireEvents();

        var start = FindDataFile(Path.Combine("data", "deadlock_kb.txt"));
        var kbArg = GetArg(args, "--kb");
        if (kbArg is not null)
        {
            var resolved = ResolveKbPath(kbArg);
            if (resolved is not null) start = resolved;
        }

        if (start is not null) LoadKb(start);
        else ShowExplanation("База знаний не загружена.");
    }

    // ================================================================ UI ===

    private void BuildUi()
    {
        SuspendLayout();

        Text = "Продукционная модель: прямой и обратный вывод";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        Size = new Size(1400, 900);
        MinimumSize = new Size(1100, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 4) };

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
        _modeBar.Add("CLIPS");
        _modeBar.Width = _modeBar.PreferredWidth;
        _modeBar.Height = 32;
        _modeBar.Margin = new Padding(0, 4, 14, 4);

        _lblSpeed.AutoSize = true;
        _lblSpeed.ForeColor = Theme.TextMuted;
        _lblSpeed.Text = "Скорость, мс:";
        _lblSpeed.Margin = new Padding(0, 9, 4, 0);

        _numSpeed.Minimum = 20;
        _numSpeed.Maximum = 1500;
        _numSpeed.Increment = 20;
        _numSpeed.Value = 160;
        _numSpeed.Width = 68;
        _numSpeed.BackColor = Theme.PanelAlt;
        _numSpeed.ForeColor = Theme.Text;
        _numSpeed.BorderStyle = BorderStyle.FixedSingle;
        _numSpeed.Margin = new Padding(0, 4, 12, 4);

        _lblKb.AutoSize = true;
        _lblKb.ForeColor = Theme.TextMuted;
        _lblKb.Text = "БЗ не загружена";
        _lblKb.Margin = new Padding(10, 9, 0, 0);

        row1.Controls.AddRange(new Control[]
        {
            _modeBar, _btnOpen, _btnReset, _lblSpeed, _numSpeed, _lblKb
        });

        // -------- строка 2: переключаемые панели управления --------
        BuildForwardControls();
        BuildBackwardControls();

        _forwardControls.Dock = DockStyle.Fill;
        _backwardControls.Dock = DockStyle.Fill;
        _backwardControls.Visible = false;

        // Кнопки режима CLIPS живут в той же строке, что и кнопки выводов.
        _clipsControls.Dock = DockStyle.Fill;
        _clipsControls.Visible = false;
        _clipsControls.Controls.Add(_clips.Toolbar);

        var row2 = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        row2.Controls.Add(_clipsControls);
        row2.Controls.Add(_backwardControls);
        row2.Controls.Add(_forwardControls);

        top.Controls.Add(row2);
        top.Controls.Add(row1);

        // -------- основная область: граф + объяснение + таблицы --------
        _graph.Dock = DockStyle.Fill;

        // Над графом — переключатель вида: привычное дерево или блоки по слоям.
        _graphViews.Add("Дерево");
        _graphViews.Add("Слои");
        _graphViews.Height = 30;
        _graphViews.Width = _graphViews.PreferredWidth;
        _graphViews.Margin = new Padding(0, 2, 10, 0);

        _graphScope.Add("Доказательство");
        _graphScope.Add("Все факты");
        _graphScope.Height = 30;
        _graphScope.Width = _graphScope.PreferredWidth;
        _graphScope.Visible = false;
        _graphScope.Margin = new Padding(0, 2, 0, 0);

        var graphBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Background,
            Padding = new Padding(2, 2, 2, 0)
        };
        graphBar.Controls.AddRange(new Control[] { _graphViews, _graphScope });

        _blockGraph.Dock = DockStyle.Fill;
        _blockGraph.Visible = false;

        // Дерево и блоки делят одну область и показываются по очереди.
        var graphHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        graphHost.Controls.Add(_blockGraph);
        graphHost.Controls.Add(_graph);
        graphHost.Controls.Add(graphBar);

        _txtExplain.Dock = DockStyle.Fill;
        _txtExplain.ReadOnly = true;
        _txtExplain.BackColor = Theme.PanelAlt;
        _txtExplain.ForeColor = Theme.Text;
        _txtExplain.BorderStyle = BorderStyle.None;
        _txtExplain.Font = Theme.MonoSmall;
        _txtExplain.WordWrap = true;

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        right.Controls.Add(_txtExplain);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor = Theme.Background
        };
        split.Panel1.Controls.Add(graphHost);
        split.Panel2.Controls.Add(right);
        _split = split;

        ConfigureGrid(_lstFacts);
        ConfigureGrid(_lstRules);
        ConfigureGrid(_lstResults);

        // -------- левая панель: переключатель таблиц + карточка --------
        var sideTabs = new SegmentBar { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Background };
        sideTabs.Add("Факты");
        sideTabs.Add("Правила");
        sideTabs.Add("Результаты");
        _tabs = sideTabs;

        // Карточка с общей рамкой — та же геометрия, что и у остальных панелей.
        var sideCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 8,
            Padding = new Padding(6)
        };
        sideCard.Controls.Add(_lstFacts);
        sideCard.Controls.Add(_lstRules);
        sideCard.Controls.Add(_lstResults);

        var side = new Panel
        {
            Dock = DockStyle.Left,
            Width = 440,
            BackColor = Theme.Background,
            Padding = new Padding(6, 0, 6, 6)
        };
        side.Controls.Add(sideCard);
        side.Controls.Add(sideTabs);
        _side = side;

        sideTabs.SelectedIndexChanged += (_, _) => ShowSideTable(sideTabs.SelectedIndex);
        ShowSideTable(0);

        // Граф и режим CLIPS делят одну и ту же область и показываются по очереди.
        _clips.Visible = false;
        _main = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        _main.Controls.Add(_clips);
        _main.Controls.Add(split);

        var status = new StatusStrip { BackColor = Theme.Panel, ForeColor = Theme.TextMuted, SizingGrip = false };
        _statusLeft.Spring = false;
        _statusLeft.Text = "Готово";
        _statusMid.Spring = true;
        _statusMid.TextAlign = ContentAlignment.MiddleLeft;
        _statusRight.Spring = false;
        status.Items.AddRange(new ToolStripItem[] { _statusLeft, _statusMid, _statusRight });

        Controls.Add(_main);
        Controls.Add(side);
        Controls.Add(top);
        Controls.Add(status);

        ResumeLayout(false);
        PerformLayout();

        // Пропорции графа и объяснения — при показе формы: до раскладки реальная ширина неизвестна.
        Load += (_, _) => ApplySplitRatio(split);
    }

    /// <summary>Граф — 62% ширины, объяснение — остальное.</summary>
    private static void ApplySplitRatio(SplitContainer split)
    {
        var limit = split.Width - split.Panel2MinSize - split.SplitterWidth;

        if (limit <= split.Panel1MinSize)
            return;

        split.SplitterDistance = Math.Clamp((int)(split.Width * 0.62), split.Panel1MinSize, limit);
    }

    private void BuildForwardControls()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel,
            Padding = new Padding(0, 4, 0, 0)
        };

        _cmbFwdGoal.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbFwdGoal.FlatStyle = FlatStyle.Flat;
        _cmbFwdGoal.BackColor = Theme.PanelAlt;
        _cmbFwdGoal.ForeColor = Theme.Text;
        _cmbFwdGoal.Font = Theme.Ui;
        _cmbFwdGoal.Width = 320;
        _cmbFwdGoal.Margin = new Padding(6, 4, 12, 4);

        row.Controls.AddRange(new Control[] { _btnFwdPlay, _btnFwdStep, _btnFwdShow, _cmbFwdGoal });
        _forwardControls.Controls.Add(row);
    }

    private void BuildBackwardControls()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel,
            Padding = new Padding(0, 4, 0, 0)
        };

        _cmbGoals.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbGoals.FlatStyle = FlatStyle.Flat;
        _cmbGoals.BackColor = Theme.PanelAlt;
        _cmbGoals.ForeColor = Theme.Text;
        _cmbGoals.Font = Theme.Ui;
        _cmbGoals.Width = 320;
        _cmbGoals.Margin = new Padding(6, 4, 12, 4);

        row.Controls.AddRange(new Control[] { _btnBwdPlay, _btnBwdStep, _btnBwdShow, _cmbGoals });
        _backwardControls.Controls.Add(row);
    }

    private void ConfigureGrid(DarkGrid grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Theme.PanelAlt;
        grid.DefaultCellStyle.BackColor = Theme.PanelAlt;
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Theme.AccentDim;
        grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        grid.GridColor = Theme.Border;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextMuted;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.UiSmall;

        // Столбцы создаются здесь, до добавления строк.
        if (ReferenceEquals(grid, _lstFacts))
        {
            grid.Columns.Clear();
            grid.AddColumn("Факт", 75);
            grid.AddColumn("Источник", 25);
        }
        else if (ReferenceEquals(grid, _lstRules))
        {
            grid.Columns.Clear();
            grid.AddColumn("Правило", 12);
            grid.AddColumn("Условия", 44);
            grid.AddColumn("Заключения", 44);
        }
        else if (ReferenceEquals(grid, _lstResults))
        {
            grid.Columns.Clear();
            grid.AddColumn("Цель", 40);
            grid.AddColumn("Результат", 24);
            grid.AddColumn("Узлов", 12);
            grid.AddColumn("Циклов", 12);
            grid.AddColumn("Переключений", 12);
        }
    }

    /// <summary>Показывает в левой карточке одну из таблиц: 0 — факты, 1 — правила, 2 — результаты.</summary>
    private void ShowSideTable(int index)
    {
        _lstFacts.Visible = index == 0;
        _lstRules.Visible = index == 1;
        _lstResults.Visible = index == 2;
    }

    // ================================================================ Events ===

    private void WireEvents()
    {
        _btnOpen.Click += (_, _) => OpenKbDialog();
        _btnReset.Click += (_, _) => ResetAll();

        _modeBar.SelectedIndexChanged += (_, _) =>
        {
            // Анимация идёт по шагам одного режима — при переключении её надо снять,
            // иначе таймер продолжит раскрывать шаги уже другого режима.
            PauseAnimation();

            _mode = _modeBar.SelectedIndex switch
            {
                0 => Mode.Forward,
                1 => Mode.Backward,
                _ => Mode.Clips
            };

            var clips = _mode == Mode.Clips;
            _forwardControls.Visible = _mode == Mode.Forward;
            _backwardControls.Visible = _mode == Mode.Backward;
            _clipsControls.Visible = clips;

            // В режиме CLIPS база знаний, граф и скорость анимации не нужны.
            if (_side is not null) _side.Visible = !clips;
            if (_split is not null) _split.Visible = !clips;
            _clips.Visible = clips;
            _btnOpen.Enabled = !clips;
            _btnReset.Enabled = !clips;
            _lblSpeed.Visible = !clips;
            _numSpeed.Visible = !clips;

            UpdatePlayButtons();
            UpdateStatus();
            UpdateGraphScopeLabels();

            if (clips)
            {
                _clips.EnsureReady();
                UpdateKbLabel();
                return;
            }

            UpdateKbLabel();

            // При переключении режима сразу показываем дерево этого режима,
            // иначе на панели остаётся граф предыдущего.
            if (_mode == Mode.Forward)
            {
                BuildForwardGraph();
            }
            else
            {
                BuildBackwardGraph();
            }
        };

        _btnFwdPlay.Click += (_, _) => ToggleForward();
        _btnFwdStep.Click += (_, _) => StepForward();
        _btnFwdShow.Click += (_, _) => ShowAllForward();

        _btnBwdPlay.Click += (_, _) => ToggleBackward();
        _btnBwdStep.Click += (_, _) => StepBackward();
        _btnBwdShow.Click += (_, _) => ShowAllBackward();

        _cmbFwdGoal.SelectedIndexChanged += (_, _) => { if (!_suppress) OnForwardGoalChanged(); };
        _cmbGoals.SelectedIndexChanged += (_, _) => { if (!_suppress) OnBackwardGoalChanged(); };

        _graph.NodeClicked += OnNodeClicked;

        // Дерево ↔ блоки: обе панели живут в одной области, показываем одну.
        _graphViews.SelectedIndexChanged += (_, _) =>
        {
            var blocks = _graphViews.SelectedIndex == 1;
            _graph.Visible = !blocks;
            _blockGraph.Visible = blocks;
            _graphScope.Visible = blocks;
            RefreshGraphView();
        };

        _graphScope.SelectedIndexChanged += (_, _) =>
        {
            if (_suppress || !_blockGraph.Visible) return;
            RefreshGraphView();
        };

        _clips.StatusChanged += (_, _) => { UpdateKbLabel(); UpdateStatus(); };

        _timer.Interval = (int)_numSpeed.Value;
        _numSpeed.ValueChanged += (_, _) => _timer.Interval = (int)_numSpeed.Value;
        _timer.Tick += (_, _) => OnTimerTick();

        _lstFacts.SelectionChanged += (_, _) => OnFactSelected();
        _lstResults.SelectionChanged += (_, _) => OnBackwardSelected();
        _lstResults.MouseDown += (_, _) => _suppressResultSelect = false;
        _lstResults.KeyDown += (_, _) => _suppressResultSelect = false;

        FormClosing += (_, _) => _timer.Stop();
    }

    private void OpenKbDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файл базы знаний",
            Filter = "Файлы БЗ (*.txt;*.kb)|*.txt;*.kb|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadKb(dlg.FileName);
    }

    private void LoadKb(string path)
    {
        try
        {
            var kb = KbLoader.Load(path);
            _kb = kb;
            _kbPath = Path.GetFullPath(path);

            UpdateKbLabel();
            _statusLeft.Text = "БЗ загружена";

            ResetAll();

            _suppress = true;
            try
            {
                _cmbFwdGoal.Items.Clear();
                _cmbFwdGoal.Items.Add("(любая)");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var goal in kb.Goals)
                    if (seen.Add(goal.ToString())) _cmbFwdGoal.Items.Add(goal.ToString());
                foreach (var rule in kb.Rules)
                    foreach (var conclusion in rule.Conclusions)
                        if (seen.Add(conclusion.ToString())) _cmbFwdGoal.Items.Add(conclusion.ToString());

                _cmbFwdGoal.SelectedIndex = 0;

                _cmbGoals.Items.Clear();
                _cmbGoals.Items.Add("(все цели)");
                foreach (var goal in kb.Goals) _cmbGoals.Items.Add(goal.ToString());
                if (_cmbGoals.Items.Count > 0) _cmbGoals.SelectedIndex = 0;
            }
            finally
            {
                _suppress = false;
            }

            _txtExplain.Text = BuildKbSummary(kb);
            RefreshForwardView();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка загрузки БЗ",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetAll()
    {
        PauseAnimation();
        _step = 0;
        _fwdReveal = 0;
        _bwdReveal = 0;
        _forward = null;
        _backward.Clear();
        _focusFact = null;
        _currentBackward = null;
        _bwdSteps.Clear();
        _bwdOrder.Clear();
        _bwdStarts.Clear();

        _refreshing = true;
        try
        {
            _lstFacts.Rows.Clear();
            _lstResults.Rows.Clear();

            _lstRules.Rows.Clear();
            if (_kb is not null)
                foreach (var rule in _kb.Rules)
                    _lstRules.Rows.Add(rule.Id, rule.ConditionText, rule.ConclusionText);
        }
        finally
        {
            _refreshing = false;
        }

        _graph.SetTree(null);
        _graph.ResetView();
        ClearBlocks(_mode == Mode.Forward ? ForwardBlockHint : BackwardBlockHint);
        if (_kb is not null) _txtExplain.Text = BuildKbSummary(_kb);
        UpdateStatus();
    }

    // ================================================================ Forward ===

    private void ToggleForward()
    {
        if (_kb is null) return;

        if (_animRunning)
        {
            PauseAnimation();
            return;
        }

        if (_forward is null)
        {
            RunForward();
            if (_forward is null) return;
        }

        // анимация закончилась — начинаем просмотр заново
        if (_step >= _forward.Firings.Count)
        {
            _step = 0;
            _reveal = 0;
            _focusFact = null;
            RefreshForwardView();
        }

        StartAnimation();
    }

    private void StepForward()
    {
        if (_kb is null) return;
        PauseAnimation();
        if (_forward is null)
        {
            RunForward();
            if (_forward is null) return;
        }
        AdvanceForward();
    }

    private void ShowAllForward()
    {
        if (_kb is null) return;
        if (_forward is null) RunForward();
        if (_forward is null) return;
        PauseAnimation();
        _reveal = _forward.Firings.Count;
        _step = _reveal;
        _focusFact = _forward.Firings.Count > 0
            ? _forward.Firings[^1].Conclusions.LastOrDefault()
            : null;
        RefreshForwardView();
        ShowFactsTab();
    }

    private void RunForward()
    {
        if (_kb is null) return;

        Predicate? goal = null;
        if (_cmbFwdGoal.SelectedIndex > 0 && _cmbFwdGoal.SelectedItem is string text)
        {
            var fake = TryParsePredicate(text);
            goal = fake;
        }

        var opt = new ForwardOptions
        {
            MaxPasses = 200,
            MaxFacts = 50_000,
            MaxMatches = Matcher.DefaultMaxMatches,
            Goal = goal
        };

        _forward = new ForwardChainer().Run(_kb, opt);
        _step = 0;
        _reveal = 0;
        _focusFact = null;
        RefreshForwardView();
        UpdateStatus();
    }

    private void AdvanceForward()
    {
        if (_forward is null) return;
        if (_step >= _forward.Firings.Count)
        {
            PauseAnimation();
            return;
        }

        var firing = _forward.Firings[_step];
        _step++;
        _reveal = _step;

        var conclusion = firing.Conclusions.LastOrDefault(c => c.IsDerived)
                         ?? firing.Conclusions.LastOrDefault();
        if (conclusion is not null) _focusFact = conclusion;

        RefreshForwardView();
    }

    private void RefreshForwardView()
    {
        _refreshing = true;
        try
        {
            _lstFacts.Rows.Clear();
            foreach (var fact in RevealedFacts())
                _lstFacts.Rows.Add(fact.Text, fact.IsDerived ? $"слой {fact.Depth}" : "входной");
        }
        finally
        {
            _refreshing = false;
        }

        BuildForwardGraph();
        UpdateStatus();
    }

    /// <summary>Факты, выведенные к текущему шагу анимации (входные видны сразу).</summary>
    private IEnumerable<Fact> RevealedFacts()
    {
        if (_forward is null)
        {
            if (_kb is null) yield break;
            foreach (var fact in _kb.Facts) yield return fact;
            yield break;
        }

        foreach (var fact in _forward.Memory.Facts)
            if (RevealStep(fact) <= _reveal)
                yield return fact;
    }

    /// <summary>Шаг вывода, на котором факт появился (0 — входной).</summary>
    private static int RevealStep(Fact fact)
        => fact.IsDerived && fact.Derivations.Count > 0 ? fact.Derivations[0].Step : 0;

    private void BuildForwardGraph()
    {
        var focus = ResolveForwardFocus();

        if (focus is null)
        {
            _graph.SetTree(null);
        }
        else
        {
            var nextId = 0;
            var root = BuildProofNode(focus, ref nextId, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            _graph.SetTree(root, resetView: !_animRunning && _graph.Visible);
        }

        BuildForwardBlocks(focus);
    }

    /// <summary>Фокус прямого вывода: выбранный пользователем факт либо последний выведенный к шагу.</summary>
    private Fact? ResolveForwardFocus()
    {
        if (_forward is null) return null;

        if (_focusFact is not null && RevealStep(_focusFact) <= _reveal) return _focusFact;

        return _forward.Firings
            .Take(_reveal)
            .SelectMany(f => f.Conclusions)
            .LastOrDefault(c => c.IsDerived);
    }

    /// <summary>Блоки прямого вывода: слой — глубина факта, подпись — правило, плашка — шаг.</summary>
    private void BuildForwardBlocks(Fact? focus)
    {
        if (_forward is null)
        {
            ClearBlocks(ForwardBlockHint);
            return;
        }

        var goals = new HashSet<string>(
            _kb?.Goals.Select(g => g.Pattern.Key) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // «Все факты» — вся рабочая память к текущему шагу, иначе только доказательство фокуса.
        var facts = _graphScope.SelectedIndex == 1 || focus is null
            ? RevealedFacts().ToList()
            : ProofFacts(focus);

        var nodes = new List<BlockNode>(facts.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            if (!seen.Add(fact.Key)) continue;

            nodes.Add(new BlockNode
            {
                Key = fact.Key,
                Title = fact.Atom.ToString(),
                Badge = fact.IsDerived ? "шаг " + RevealStep(fact) : string.Empty,
                Layer = Math.Max(0, fact.Depth),
                Footer = fact.IsDerived ? $"правило {fact.Rule?.Id ?? "—"}" : "входной факт",
                Premises = fact.IsDerived
                    ? fact.Premises.Select(p => p.Key).ToArray()
                    : Array.Empty<string>(),
                IsGoal = ReferenceEquals(fact, focus) || goals.Contains(fact.Key),
                Kind = fact.IsDerived ? BlockKind.Derived : BlockKind.Input,
                Tooltip = FactTooltip(fact)
            });
        }

        var summary = $"Шаг {_step} из {_forward.Firings.Count} · фактов {nodes.Count} · " +
                      $"слоёв {LayerCount(nodes)}";

        _blockGraph.SetNodes(nodes, summary, ForwardBlockHint, BlockLegend);
    }

    /// <summary>Факты дерева доказательства фокуса; повторы отсекаются — циклы безопасны.</summary>
    private static List<Fact> ProofFacts(Fact focus)
    {
        var result = new List<Fact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<Fact>();
        stack.Push(focus);

        while (stack.Count > 0)
        {
            var fact = stack.Pop();
            if (!seen.Add(fact.Key)) continue;

            result.Add(fact);
            foreach (var premise in fact.Premises) stack.Push(premise);
        }

        return result;
    }

    /// <summary>Дерево доказательства: узел — факт, дети — посылки сработавшего правила.</summary>
    private static GraphNode BuildProofNode(Fact fact, ref int nextId, HashSet<string> path, int depth)
    {
        var node = new GraphNode
        {
            Id = nextId++,
            Text = fact.Text,
            State = fact.IsDerived ? GraphState.Derived : GraphState.Input,
            SubText = fact.IsDerived
                ? $"правило {fact.Rule?.Id ?? "—"} · слой {fact.Depth}"
                : "исходный факт",
            Tooltip = FactTooltip(fact)
        };

        if (!fact.IsDerived || depth >= 12) return node;

        if (!path.Add(fact.Key))
        {
            node.State = GraphState.Cyclic;
            node.SubText = "цикл";
            return node;
        }

        foreach (var premise in fact.Premises)
            node.Children.Add(BuildProofNode(premise, ref nextId, path, depth + 1));

        path.Remove(fact.Key);
        return node;
    }

    private static string FactTooltip(Fact fact)
    {
        if (!fact.IsDerived) return $"{fact.Text}\nвходной факт";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(fact.Text);
        sb.AppendLine($"правило {fact.Rule?.Id ?? "—"} · слой {fact.Depth}");
        if (!string.IsNullOrWhiteSpace(fact.Rule?.Why)) sb.AppendLine(fact.Rule!.Why);
        if (fact.Premises.Count > 0)
            sb.AppendLine("посылки: " + string.Join(", ", fact.Premises.Select(p => p.Text)));
        return sb.ToString();
    }

    // ================================================================ Backward ===

    private void ToggleBackward()
    {
        if (_kb is null) return;

        if (_animRunning)
        {
            PauseAnimation();
            return;
        }

        if (_backward.Count == 0)
        {
            RunBackward();
            if (_bwdSteps.Count == 0) return;
        }

        if (_bwdReveal >= _bwdSteps.Count)
        {
            _bwdReveal = 1;
            RefreshBackwardView();
        }

        StartAnimation();
    }

    private void StepBackward()
    {
        if (_kb is null) return;
        PauseAnimation();
        if (_backward.Count == 0)
        {
            RunBackward();
            if (_bwdSteps.Count == 0) return;
        }
        AdvanceBackward();
    }

    private void ShowAllBackward()
    {
        if (_kb is null) return;
        if (_backward.Count == 0) RunBackward();
        PauseAnimation();
        _bwdReveal = _bwdSteps.Count;
        RefreshBackwardView();
        ShowResultsTab();
    }

    private void RunBackward()
    {
        if (_kb is null) return;

        var opt = new BackwardOptions
        {
            MaxDepth = 14,
            MaxNodes = 30_000,
            UseMemo = true,
            ReuseMemo = true,
            MaxGoalInstances = 4000
        };

        var goals = new List<GoalDefinition>();
        if (_cmbGoals.SelectedIndex > 0 && _cmbGoals.SelectedIndex - 1 < _kb.Goals.Count)
            goals.Add(_kb.Goals[_cmbGoals.SelectedIndex - 1]);
        else
            goals.AddRange(_kb.Goals);

        // Обратный вывод идёт от исходных фактов базы, а не от памяти прямого вывода:
        // иначе любая цель уже лежит в памяти и дерево вырождается в один узел.
        var solutions = BackwardSolver.SolveAll(_kb, BuildInitialMemory(_kb), goals, opt);

        _backward.Clear();
        foreach (var group in solutions)
            _backward.AddRange(group.Results);

        // Раскрытие идёт по узлам дерева в порядке «цель → её посылки → посылки посылок»,
        // чтобы было видно, как из предмета-ответа выводятся герои, против которых он играет.
        _bwdSteps.Clear();
        _bwdOrder.Clear();
        _bwdStarts.Clear();

        foreach (var result in _backward)
        {
            _bwdStarts.Add(_bwdSteps.Count);
            foreach (var node in BreadthFirst(result.Root))
            {
                _bwdOrder[node] = _bwdSteps.Count;
                _bwdSteps.Add((result, node));
            }
        }

        _bwdReveal = _bwdSteps.Count > 0 ? 1 : 0;
        _currentBackward = null;
        RefreshBackwardView();
        UpdateStatus();
    }

    /// <summary>Обход дерева поиска по уровням: цель, её посылки, посылки посылок и т. д.</summary>
    private static IEnumerable<SearchNode> BreadthFirst(SearchNode root)
    {
        var queue = new Queue<SearchNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            foreach (var child in node.Children) queue.Enqueue(child);
        }
    }

    /// <summary>Рабочая память только из исходных фактов базы — стартовое состояние обратного вывода.</summary>
    private static WorkingMemory BuildInitialMemory(KnowledgeBase kb)
    {
        var memory = new WorkingMemory();
        foreach (var fact in kb.Facts)
            memory.AddInput(new Fact(fact.Atom, FactOrigin.Input) { DisplayName = fact.DisplayName });
        return memory;
    }

    private void AdvanceBackward()
    {
        if (_bwdSteps.Count == 0) return;
        if (_bwdReveal >= _bwdSteps.Count)
        {
            PauseAnimation();
            return;
        }
        _bwdReveal++;
        RefreshBackwardView();
    }

    private void RefreshBackwardView()
    {
        var started = 0;
        for (var i = 0; i < _bwdStarts.Count; i++)
            if (_bwdStarts[i] < _bwdReveal) started++;

        _refreshing = true;
        try
        {
            _lstResults.Rows.Clear();
            foreach (var result in _backward.Take(started))
                _lstResults.Rows.Add(
                    result.Goal.ToString(),
                    BackwardStateText(result),
                    result.NodesVisited.ToString(),
                    result.CycleHits.ToString(),
                    result.BranchSwitches.ToString());

            // Снимаем автовыбор первой строки: иначе при показе вкладки «Результаты»
            // отложенный SelectionChanged перебивает дерево графа деревом первой цели.
            _suppressResultSelect = true;
            _lstResults.ClearSelection();
            _lstResults.CurrentCell = null;
        }
        finally
        {
            _refreshing = false;
        }

        BuildBackwardGraph();
        UpdateStatus();
    }

    private void BuildBackwardGraph()
    {
        if (_bwdReveal <= 0 || _bwdSteps.Count == 0)
        {
            _graph.SetTree(null);
            ClearBlocks(BackwardBlockHint);
            return;
        }

        var index = Math.Min(_bwdReveal, _bwdSteps.Count) - 1;
        var focus = _bwdSteps[index].Result;

        // Выбор пользователя в таблице приоритетнее автоматического,
        // но шаг и подсказка должны относиться к той же цели.
        if (_currentBackward is not null)
        {
            var i = _backward.IndexOf(_currentBackward);
            if (i >= 0 && i < _bwdStarts.Count && _bwdStarts[i] < _bwdReveal)
            {
                focus = _currentBackward;
                var end = i + 1 < _bwdStarts.Count ? _bwdStarts[i + 1] : _bwdSteps.Count;
                index = Math.Min(_bwdReveal, end) - 1;
            }
        }

        var id = 0;
        var root = ConvertBackwardNode(focus.Root, ref id);
        _graph.SetTree(root, resetView: !_animRunning && _graph.Visible);

        ShowExplanation(BackwardExplanation(focus, index));
        BuildBackwardBlocks(focus);
    }

    /// <summary>
    /// Блоки обратного вывода: посылки слева, цель справа (слой = глубина до листьев минус глубина узла),
    /// поэтому стрелки идут в ту же сторону, что и в прямом выводе.
    /// </summary>
    private void BuildBackwardBlocks(BackwardResult focus)
    {
        var nodes = new List<BlockNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (_graphScope.SelectedIndex == 1)
        {
            // «Все цели» — все доказательства, раскрытые к текущему шагу.
            for (var i = 0; i < _backward.Count && i < _bwdStarts.Count; i++)
                if (_bwdStarts[i] < _bwdReveal)
                    CollectBackwardBlocks(_backward[i].Root, nodes, seen);
        }
        else
        {
            CollectBackwardBlocks(focus.Root, nodes, seen);
        }

        var summary = $"Цель: {focus.Goal} · {BackwardStateText(focus)} · узлов {nodes.Count} · " +
                      $"слоёв {LayerCount(nodes)} · переключений {focus.BranchSwitches}";

        _blockGraph.SetNodes(nodes, summary, BackwardBlockHint, BlockLegend,
            focus.Proven ? Theme.Green : Theme.Amber);
    }

    /// <summary>Раскладывает раскрытую часть дерева поиска в блоки (итеративно, без рекурсии).</summary>
    private void CollectBackwardBlocks(SearchNode root, List<BlockNode> nodes, HashSet<string> seen)
    {
        var visible = new List<SearchNode>();
        var stack = new Stack<SearchNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            visible.Add(node);
            foreach (var child in RevealedChildren(node)) stack.Push(child);
        }

        if (visible.Count == 0) return;

        var maxDepth = visible.Max(n => n.Depth);

        // Ключ узла: цель плюс правило, которым она доказана — одинаковые подцели схлопываются.
        var keys = new Dictionary<SearchNode, string>();
        foreach (var node in visible)
            keys[node] = node.Goal.Key + (node.Rule is null ? string.Empty : "@" + node.Rule.Id);

        foreach (var node in visible)
        {
            var key = keys[node];
            if (!seen.Add(key)) continue;

            nodes.Add(new BlockNode
            {
                Key = key,
                Title = node.Goal.ToString(),
                Badge = BackwardBadge(node),
                Layer = maxDepth - node.Depth,
                Footer = node.Fact is not null ? "факт из базы"
                    : node.Rule is not null ? $"правило {node.Rule.Id}"
                    : node.Note ?? string.Empty,
                Premises = RevealedChildren(node).Select(c => keys[c]).ToArray(),
                IsGoal = ReferenceEquals(node, root),
                Kind = BackwardKind(node),
                Tooltip = BackwardTooltip(node)
            });
        }

        // Предотвращённые циклы: ветвь отброшена, но на схеме видно, где поиск замкнулся.
        if (_bwdReveal < _bwdSteps.Count) return;

        foreach (var node in visible)
        {
            var number = 0;
            foreach (var attempt in node.RejectedAttempts)
            {
                if (!attempt.CyclePrevented) continue;

                var key = keys[node] + "⟲" + number++;
                if (!seen.Add(key)) continue;

                var tooltip = new System.Text.StringBuilder();
                tooltip.AppendLine("Цикл предотвращён: ветвь замкнута, поиск переиграл её по следующей продукции.");
                tooltip.AppendLine($"Продукция {attempt.Rule.Id}: {attempt.Reason}");
                foreach (var cyclic in attempt.Children) AppendCyclePath(tooltip, cyclic);

                nodes.Add(new BlockNode
                {
                    Key = key,
                    Title = attempt.Children.Count > 0 ? attempt.Children[0].Goal.ToString() : attempt.Rule.Id,
                    Badge = "⟲",
                    Layer = Math.Max(0, maxDepth - node.Depth - 1),
                    Footer = "цикл предотвращён",
                    Kind = BlockKind.Cyclic,
                    Tooltip = tooltip.ToString().TrimEnd()
                });
            }
        }
    }

    /// <summary>Дети узла, до которых дошла очередь раскрытия.</summary>
    private IEnumerable<SearchNode> RevealedChildren(SearchNode node)
    {
        foreach (var child in node.Children)
            if (_bwdOrder.TryGetValue(child, out var order) && order < _bwdReveal)
                yield return child;
    }

    private static string BackwardBadge(SearchNode node) => node.State switch
    {
        SearchState.Cyclic => "⟲ цикл",
        SearchState.DepthLimit => "предел",
        SearchState.Failed => "не доказана",
        _ => string.Empty
    };

    private static BlockKind BackwardKind(SearchNode node) => node.State switch
    {
        SearchState.Cyclic => BlockKind.Cyclic,
        SearchState.Failed or SearchState.DepthLimit => BlockKind.Failed,
        _ when node.Fact is not null => BlockKind.Input,   // ветвь замкнулась на факт базы
        _ => BlockKind.Derived
    };

    private static string BackwardTooltip(SearchNode node)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(node.Label);
        text.AppendLine($"слой поиска {node.Depth} · {node.State}");
        if (node.Rule is not null) text.AppendLine($"по правилу {node.Rule.Id}");
        if (!string.IsNullOrWhiteSpace(node.Note)) text.AppendLine(node.Note);
        if (node.Fact is not null) text.AppendLine("факт из базы — ветвь замкнута");
        foreach (var attempt in node.RejectedAttempts)
            text.AppendLine($"{(attempt.CyclePrevented ? "⟲ цикл предотвращён" : "✘ отклонено")}: " +
                            $"{attempt.Rule.Id} — {attempt.Reason}");
        return text.ToString().TrimEnd();
    }

    private static string BackwardStateText(BackwardResult result)
    {
        if (result.Proven)
            return result.CyclePrevented ? "доказана · цикл обойдён" : "доказана";

        return result.Root.State switch
        {
            SearchState.Cyclic => "цикл предотвращён, альтернатив нет",
            SearchState.DepthLimit => "предел глубины",
            _ => "не доказана"
        };
    }

    /// <summary>Пока дерево растёт — подсказка о текущем шаге, после — полный разбор.</summary>
    private string BackwardExplanation(BackwardResult focus, int index)
    {
        if (_bwdReveal >= _bwdSteps.Count)
            return TextExplainer.ExplainBackward(focus, includeRejected: true);

        var node = _bwdSteps[index].Node;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Цель: {focus.Goal}");
        sb.AppendLine($"Шаг {_bwdReveal} из {_bwdSteps.Count} — раскрываю узел");
        sb.AppendLine();
        sb.AppendLine($"  {node.Label}");
        if (node.Rule is not null) sb.AppendLine($"  по правилу {node.Rule.Id}");
        if (!string.IsNullOrWhiteSpace(node.Note)) sb.AppendLine($"  {node.Note}");
        if (node.Fact is not null) sb.AppendLine("  факт из базы — ветвь замкнута");
        sb.AppendLine();
        sb.AppendLine("Дальше: " + (node.Children.Count == 0
            ? "посылок нет"
            : string.Join(", ", node.Children.Select(c => c.Label))));
        return sb.ToString();
    }

    private GraphNode ConvertBackwardNode(SearchNode source, ref int nextId)
    {
        var state = source.State switch
        {
            SearchState.Proven => source.Negated ? GraphState.Negated : GraphState.Proven,
            SearchState.Failed => GraphState.Failed,
            SearchState.Cyclic => GraphState.Cyclic,
            SearchState.DepthLimit => GraphState.Failed,
            _ => GraphState.Normal
        };

        var sub = source.Fact is not null ? "факт из базы"
            : source.Rule is not null ? $"правило {source.Rule.Id}"
            : source.Note;

        // Отвергнутые альтернативы не рисуем чипами (они наезжают на соседние
        // уровни), но показываем их число и полный список в подсказке.
        if (source.RejectedAttempts.Count > 0)
            sub = $"{sub} · отклонено {source.RejectedAttempts.Count}";

        var tooltip = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(source.Note)) tooltip.AppendLine(source.Note);
        foreach (var attempt in source.RejectedAttempts)
            tooltip.AppendLine(
                $"{(attempt.CyclePrevented ? "⟲ цикл предотвращён" : "✘ отклонено")}: " +
                $"{attempt.Rule.Id} — {attempt.Reason}");

        var node = new GraphNode
        {
            Id = nextId++,
            Text = source.Label,
            SubText = sub ?? string.Empty,
            State = state,
            Tooltip = tooltip.Length == 0 ? null : tooltip.ToString().TrimEnd()
        };

        // Дети появляются только когда до них дошла очередь раскрытия.
        foreach (var child in source.Children)
            if (_bwdOrder.TryGetValue(child, out var order) && order < _bwdReveal)
                node.Children.Add(ConvertBackwardNode(child, ref nextId));

        // Предотвращённые циклы: ветвь отброшена, но на итоговом графе видно,
        // где именно поиск замкнулся и какая продукция сработала вместо неё.
        if (_bwdReveal >= _bwdSteps.Count)
        {
            foreach (var attempt in source.RejectedAttempts)
            {
                if (!attempt.CyclePrevented) continue;

                var head = attempt.Children.Count > 0 ? attempt.Children[0] : null;

                var chipTooltip = new System.Text.StringBuilder();
                chipTooltip.AppendLine("Цикл предотвращён: ветвь замкнута, поиск переигран по следующей продукции.");
                chipTooltip.AppendLine($"Продукция {attempt.Rule.Id}: {attempt.Reason}");

                foreach (var cyclic in attempt.Children)
                    AppendCyclePath(chipTooltip, cyclic);

                node.Children.Add(new GraphNode
                {
                    Id = nextId++,
                    Text = head?.Label ?? attempt.Rule.Id,
                    SubText = "цикл предотвращён",
                    State = GraphState.Cyclic,
                    Rejected = true,
                    Tooltip = chipTooltip.ToString().TrimEnd()
                });
            }
        }

        return node;
    }

    /// <summary>Печатает замкнувшуюся ветвь в подсказку чипа (без рекурсии).</summary>
    private static void AppendCyclePath(System.Text.StringBuilder sb, SearchNode root)
    {
        var stack = new Stack<(SearchNode Node, int Level)>();
        stack.Push((root, 1));

        while (stack.Count > 0)
        {
            var (node, level) = stack.Pop();

            sb.AppendLine(
                new string(' ', level * 2) +
                $"{node.Label} ← {(node.Rule is null ? "факт" : "правило " + node.Rule.Id)}" +
                (string.IsNullOrEmpty(node.Note) ? string.Empty : $" // {node.Note}"));

            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push((node.Children[i], level + 1));
        }
    }

    // ================================================================ Timer/UI ===

    private void OnTimerTick()
    {
        if (!_animRunning) { _timer.Stop(); return; }

        if (_animMode == Mode.Forward) AdvanceForward();
        else AdvanceBackward();

        var done = _animMode == Mode.Forward
            ? _forward is not null && _step >= _forward.Firings.Count
            : _bwdSteps.Count > 0 && _bwdReveal >= _bwdSteps.Count;

        if (done) PauseAnimation();
    }

    private void StartAnimation()
    {
        _animRunning = true;
        _animMode = _mode;
        _timer.Interval = (int)_numSpeed.Value;
        _timer.Start();
        UpdatePlayButtons();
    }

    private void PauseAnimation()
    {
        _timer.Stop();
        _animRunning = false;
        UpdatePlayButtons();
    }

    private void UpdatePlayButtons()
    {
        SetButtonText(_btnFwdPlay, _animRunning && _mode == Mode.Forward ? "⏸ Пауза" : "▶ Играть");
        SetButtonText(_btnBwdPlay, _animRunning && _mode == Mode.Backward ? "⏸ Пауза" : "▶ Играть");
    }

    private static void SetButtonText(Button button, string text)
    {
        if (button.Text == text) return;
        button.Text = text;
        button.Width = TextRenderer.MeasureText(text, Theme.Ui).Width + 30;
    }

    private void OnForwardGoalChanged() { /* перезапуск не требуется: применяется при RunForward */ }

    private void OnBackwardGoalChanged()
    {
        if (_kb is null) return;
        PauseAnimation();
        RunBackward();
    }

    private void OnFactSelected()
    {
        if (_refreshing) return;
        if (_lstFacts.SelectedRows.Count == 0 || _forward is null) return;

        var text = _lstFacts.SelectedRows[0].Cells[0].Value?.ToString();
        if (string.IsNullOrEmpty(text)) return;

        var fact = _forward.Memory.Facts.FirstOrDefault(f => f.Text == text);
        if (fact is null) return;

        _focusFact = fact;
        ShowExplanation(TextExplainer.ExplainFact(fact));
        BuildForwardGraph();
    }

    private void OnBackwardSelected()
    {
        if (_refreshing || _suppressResultSelect) return;
        if (_lstResults.SelectedRows.Count == 0 || _backward.Count == 0) return;
        var idx = _lstResults.SelectedRows[0].Index;
        if (idx < 0 || idx >= _backward.Count) return;
        _currentBackward = _backward[idx];
        BuildBackwardGraph();
    }

    private void ShowFactsTab()
    {
        if (_tabs is not null) _tabs.SelectedIndex = 0;
    }

    private void ShowResultsTab()
    {
        // Сетка при первом показе сама делает текущей первую строку и поднимает
        // SelectionChanged — это не выбор пользователя, поэтому глушим его.
        _suppressResultSelect = true;
        if (_tabs is not null) _tabs.SelectedIndex = 2;

        // Снимаем автовыбор уже после создания окна сетки, иначе клик по первой
        // строке не даёт SelectionChanged и граф не перестраивается.
        _lstResults.ClearSelection();
        _lstResults.CurrentCell = null;
    }

    private void ShowExplanation(string text)
    {
        _baseExplanation = text;
        _txtExplain.Text = text;
    }

    // ================================================================ блоки ===

    /// <summary>Подписи области блок-схемы зависят от режима: у каждого вывода своё «всё».</summary>
    private void UpdateGraphScopeLabels()
    {
        var keep = _graphScope.SelectedIndex;
        var old = _suppress;
        _suppress = true;
        try
        {
            _graphScope.SetItems(_mode == Mode.Forward
                ? new[] { "Доказательство", "Все факты" }
                : new[] { "Дерево цели", "Все цели" });
            _graphScope.SelectedIndex = keep;
        }
        finally
        {
            _suppress = old;
        }
    }

    /// <summary>Перестроить активный вид графа (дерево или блоки) для текущего режима.</summary>
    private void RefreshGraphView()
    {
        if (_mode == Mode.Forward) BuildForwardGraph();
        else BuildBackwardGraph();
    }

    private void ClearBlocks(string hint) => _blockGraph.SetNodes(null, emptyHint: hint);

    private const string ForwardBlockHint =
        "Блоки появятся по мере срабатывания правил.";

    private const string BackwardBlockHint =
        "Блоки покажут, из чего складывается доказательство цели.";

    private static readonly (string Label, Color Color)[] BlockLegend =
    {
        ("входные", Theme.TextMuted),
        ("угрозы", Theme.Red),
        ("потребности", Theme.Amber),
        ("кандидаты", Theme.Purple),
        ("выбор", Theme.Accent),
        ("рекомендация", Theme.Green)
    };

    /// <summary>Сколько слоёв занято блоками (для итоговой строки).</summary>
    private static int LayerCount(IReadOnlyList<BlockNode> nodes)
        => nodes.Count == 0 ? 0 : nodes.Max(n => n.Layer) + 1;

    /// <summary>Клик по узлу дополняет подсказку шага, а не заменяет её.</summary>
    private void OnNodeClicked(GraphNode node)
    {
        var details = string.IsNullOrWhiteSpace(node.Tooltip) ? node.Text : node.Tooltip;
        _txtExplain.Text = string.IsNullOrEmpty(_baseExplanation)
            ? details
            : _baseExplanation.TrimEnd() + Environment.NewLine + Environment.NewLine +
              "— узел —" + Environment.NewLine + details;
    }

    private string BuildKbSummary(KnowledgeBase kb)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"База: {kb.Name}");
        if (!string.IsNullOrEmpty(kb.Description)) sb.AppendLine(kb.Description);
        sb.AppendLine();
        sb.AppendLine($"Фактов: {kb.Facts.Count}");
        sb.AppendLine($"Правил: {kb.Rules.Count}");
        sb.AppendLine($"Целей: {kb.Goals.Count}");
        sb.AppendLine();
        foreach (var d in kb.Diagnostics) sb.AppendLine(d.ToString());
        return sb.ToString();
    }

    private void UpdateStatus()
    {
        _statusLeft.Text = _kb is null ? "БЗ не загружена" : $"БЗ: {Path.GetFileName(_kbPath ?? "")}";

        if (_mode == Mode.Forward)
        {
            _statusMid.Text = _forward is null
                ? "Прямой вывод: не запущен"
                : $"Прямой вывод: шаг {_step} из {_forward.Firings.Count} · фактов {RevealedFacts().Count()} из {_forward.Memory.Count}";
        }
        else if (_mode == Mode.Backward)
        {
            if (_bwdSteps.Count == 0)
            {
                _statusMid.Text = "Обратный вывод: не запущен";
            }
            else
            {
                var index = Math.Min(_bwdReveal, _bwdSteps.Count) - 1;
                var focus = index >= 0 ? _bwdSteps[index].Result : null;
                var state = focus is null ? string.Empty : BackwardStateText(focus);

                _statusMid.Text = $"Обратный вывод: шаг {_bwdReveal} из {_bwdSteps.Count} · {focus?.Goal} · {state}";
            }
        }
        else
        {
            _statusMid.Text = _clips.Status;
        }

        _statusRight.Text = _mode switch
        {
            Mode.Forward => "Прямой вывод",
            Mode.Backward => "Обратный вывод (от исходных фактов)",
            _ => "CLIPS: демонстрация примера"
        };
    }

    /// <summary>Подпись в шапке: имя базы знаний либо имя CLIPS-программы.</summary>
    private void UpdateKbLabel()
    {
        _lblKb.Text = _mode == Mode.Clips
            ? $"CLIPS: {_clips.ProgramName}"
            : _kb is null
                ? "БЗ не загружена"
                : $"БЗ: {Path.GetFileName(_kbPath ?? "")} · {_kb.Name}";
    }

    // ================================================================ helpers ===

    private static Button MakeButton(string text) => UiButton.Make(text);

    private static Predicate? TryParsePredicate(string text)
    {
        try { return ParsePredicateLoose(text); }
        catch { return null; }
    }

    private static Predicate ParsePredicateLoose(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return new Predicate("?");
        var open = text.IndexOf('(');
        if (open < 0) return new Predicate(text);
        if (!text.EndsWith(')')) throw new FormatException();
        var name = text[..open].Trim();
        var inner = text[(open + 1)..^1].Trim();
        if (inner.Length == 0) return new Predicate(name);
        var parts = inner.Split(',');
        var args = new List<Arg>();
        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            args.Add(token.StartsWith('?') ? Arg.Var(token[1..]) : Arg.Const(token));
        }
        return new Predicate(name, args);
    }

    private static string? GetArg(string[]? args, string name)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string? FindDataFile(string relativePath)
    {
        var candidates = new[]
        {
            relativePath,
            Path.Combine(AppContext.BaseDirectory, relativePath)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveKbPath(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        var candidates = new[]
        {
            path,
            Path.Combine(AppContext.BaseDirectory, path),
            Path.Combine(Environment.CurrentDirectory, path)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ============================================================ test surface ===
    // Минимальный доступ для автотестов (tests/ProdModel.UiTests): они создают
    // настоящую форму и проверяют состояние графа, не полагаясь на пиксели.

    internal GraphNode? TestGraphTree => _graph.Tree;

    internal string TestStatus => _statusMid.Text ?? string.Empty;

    internal string TestExplanation => _txtExplain.Text ?? string.Empty;

    internal int TestReveal => _reveal;

    internal int TestRevealTotal => _mode == Mode.Forward
        ? _forward?.Firings.Count ?? 0
        : _bwdSteps.Count;

    internal IReadOnlyList<BackwardResult> TestBackward => _backward;

    internal int TestForwardFacts => _forward?.Memory.Count ?? 0;

    internal void TestLoadKb(string path) => LoadKb(path);

    internal void TestSetMode(bool backward) => _modeBar.SelectedIndex = backward ? 1 : 0;

    /// <summary>0 — прямой вывод, 1 — обратный, 2 — CLIPS.</summary>
    internal void TestSetModeIndex(int index) => _modeBar.SelectedIndex = index;

    internal ClipsModePanel TestClips => _clips;

    // --- вид «Слои» (блок-схема) ---

    internal BlockGraphPanel TestBlockPanel => _blockGraph;

    internal int TestBlockNodes => _blockGraph.NodeCount;

    internal IReadOnlyList<BlockNode> TestBlockNodesList => _blockGraph.Nodes;

    internal int TestGraphViewIndex => _graphViews.SelectedIndex;

    /// <summary>0 — дерево, 1 — блоки по слоям.</summary>
    internal void TestSetGraphView(int index) => _graphViews.SelectedIndex = index;

    /// <summary>0 — доказательство выбранной цели, 1 — все факты / все цели.</summary>
    internal void TestSetBlockScope(int index) => _graphScope.SelectedIndex = index;

    internal int TestBlockScopeIndex => _graphScope.SelectedIndex;

    internal void TestSelectBackwardGoal(int index) => _cmbGoals.SelectedIndex = index;

    internal bool TestSelectBackwardGoalByName(string goal)
    {
        for (var i = 0; i < _cmbGoals.Items.Count; i++)
        {
            if (!string.Equals(_cmbGoals.Items[i]?.ToString(), goal, StringComparison.OrdinalIgnoreCase))
                continue;
            _cmbGoals.SelectedIndex = i;
            return true;
        }
        return false;
    }

    internal void TestRunBackward() => RunBackward();

    internal void TestStepBackward() => StepBackward();

    internal void TestShowAllBackward() => ShowAllBackward();

    internal void TestRunForward() => RunForward();

    internal void TestStepForward() => StepForward();

    internal void TestShowAllForward() => ShowAllForward();
}