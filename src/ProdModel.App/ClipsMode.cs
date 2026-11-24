using System.Text;
using CLIPSNET;

namespace ProdModel.App;

/// <summary>
/// Режим «CLIPS»: демонстрация примеров на настоящей машине вывода CLIPS.
/// Слева — текст программы (*.clp), справа — вывод движка, факты и агенда.
/// Кнопки «Рестарт» и «Дальше» повторяют логику примера CLIPSFormsExample:
/// Run() до останова по (halt) и чтение сообщений из слота messages факта ioproxy.
/// В списке слева можно переключить пример: persons.clp (ЭС «Тиндер»),
/// certainty.clp (та же БЗ Deadlock с CF в [0..1]) или certainty_signed.clp
/// (та же БЗ с CF в [-1..1]: отрицательный cf — свидетельство против факта).
/// certainty_demo.clp и certainty_demo_signed.clp — короткие учебные примеры
/// той же модели CF на одной цепочке вывода.
/// heckerman_cf.clp — пример из статьи Хекермана «The Certainty-Factor Model»:
/// воспроизводит её числа и сравнивает варианты объединения CF.
/// Список «CF: …» в шапке переключает способ объединения и сразу пересчитывает
/// вывод; для программ без режимов он недоступен.
/// </summary>
internal sealed class ClipsModePanel : Panel
{
    private const string DefaultProgram = "persons.clp";

    /// <summary>CLIPS отдаёт слоты как байты UTF-8, прочитанные как cp1251 — тем же способом их и разворачиваем.</summary>
    private static readonly Encoding SlotEncoding = CreateSlotEncoding();

    private readonly RichTextBox _code = new();
    private readonly RichTextBox _out = new();
    private readonly SegmentBar _views = new();
    private readonly Panel _toolbar;
    private readonly StringBuilder _log = new();

    // Блок-схема вывода: строится из машиночитаемого блока #GRAPH в журнале CLIPS.
    private readonly ClipsGraphPanel _graph = new();
    private readonly SegmentBar _graphModes = new();
    private readonly ComboBox _graphGoals = UiCombo.Make(300);
    private readonly Panel _graphHost = new() { Dock = DockStyle.Fill, BackColor = Theme.PanelAlt };
    private bool _syncingGoals;

    private readonly ComboBox _programs = UiCombo.Make(168);
    private readonly ComboBox _cfMode = UiCombo.Make(300);
    private readonly ToolTip _tip = new();
    private bool _cfModeSupported;
    private readonly Button _btnRestart;
    private readonly Button _btnNext;
    private readonly Button _btnOpen;
    private readonly Button _btnSave;
    private Label? _codeCaption;

    private CLIPSNET.Environment? _env;
    private CaptureRouter? _router;
    private string _path = string.Empty;
    private int _step;
    private bool _finished;
    private bool _started;

    public event EventHandler? StatusChanged;

    public ClipsModePanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        Padding = new Padding(6);
        DoubleBuffered = true;

        _btnRestart = UiButton.Make("⟲ Рестарт");
        _btnNext = UiButton.Make("▶ Дальше");
        _btnOpen = UiButton.Make("Открыть…");
        _btnSave = UiButton.Make("Сохранить как…");

        _toolbar = BuildToolbar();
        Controls.Add(BuildSplit());

        _programs.SelectedIndexChanged += (_, _) => SelectProgramFromList();
        _cfMode.Items.AddRange(CfModes.Select(m => (object)m.Label).ToArray());
        _cfMode.SelectedIndex = 0;
        _cfMode.Enabled = false;
        _tip.SetToolTip(_cfMode,
            "Формула объединения CF. Действует в примере heckerman_cf.clp: выбор перезапускает вывод.");
        _cfMode.SelectedIndexChanged += (_, _) => OnCfModeChanged();
        _btnRestart.Click += (_, _) => Restart();
        _btnNext.Click += (_, _) => Next();
        _btnOpen.Click += (_, _) => OpenProgram();
        _btnSave.Click += (_, _) => SaveProgram();
        _views.SelectedIndexChanged += (_, _) => RenderView();
    }

    // ================================================================ разметка ===

    /// <summary>Панель с кнопками режима — её MainForm ставит во вторую строку шапки.</summary>
    public Control Toolbar => _toolbar;

    /// <summary>Имя загруженной программы (для подписи в шапке).</summary>
    public string ProgramName => Path.GetFileName(_path);

    internal string TestOutput => _out.Text;

    internal int TestStep => _step;

    internal string TestProgram => _code.Text;

    /// <summary>0 — вывод, 1 — факты, 2 — агенда, 3 — граф.</summary>
    internal void TestSelectView(int index) => _views.SelectedIndex = index;

    /// <summary>Сколько блоков в построенной схеме.</summary>
    internal int TestGraphNodes => _graph.NodeCount;

    /// <summary>0 — только цели, 1 — все факты базы.</summary>
    internal void TestSelectGraphMode(int index) => _graphModes.SelectedIndex = index;

    /// <summary>Сам контрол схемы — для проверок отрисовки.</summary>
    internal Control TestGraphView => _graph;

    internal ClipsGraphPanel TestGraph => _graph;

    /// <summary>0 — «Все цели», 1.. — конкретная цель.</summary>
    internal void TestSelectGraphGoal(int index) => _graphGoals.SelectedIndex = index;

    internal string[] TestGraphGoalList() => _graphGoals.Items.Cast<string>().ToArray();

    /// <summary>Выбрать пример из списка по имени файла.</summary>
    internal void TestSelectProgram(string fileName)
    {
        FillProgramList();
        var index = _programs.Items.IndexOf(fileName);
        if (index >= 0) _programs.SelectedIndex = index;
    }

    /// <summary>Имена примеров в выпадающем списке.</summary>
    internal string[] TestProgramList() => _programs.Items.Cast<string>().ToArray();

    /// <summary>Выбрать вариант объединения CF в списке режимов.</summary>
    internal void TestSelectCfMode(int index) => _cfMode.SelectedIndex = index;

    /// <summary>Доступен ли список режимов для текущей программы.</summary>
    internal bool TestCfModeEnabled => _cfMode.Enabled;

    /// <summary>Подписи вариантов объединения CF.</summary>
    internal string[] TestCfModeList() => _cfMode.Items.Cast<string>().ToArray();

    /// <summary>Строка состояния для нижней панели окна.</summary>
    public string Status
    {
        get
        {
            if (_env is null) return "CLIPS: не запущен";
            var name = Path.GetFileName(_path);
            return _finished
                ? $"CLIPS: шаг {_step} · {name} · агенда пуста, вывод завершён"
                : $"CLIPS: шаг {_step} · {name}";
        }
    }

    private Panel BuildToolbar()
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

        row.Controls.AddRange(new Control[]
        {
            _programs, _cfMode, _btnRestart, _btnNext, _btnOpen, _btnSave
        });

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        host.Controls.Add(row);
        return host;
    }

    private Control BuildSplit()
    {
        StyleEditor(_code);
        _code.ReadOnly = false;
        _code.AcceptsTab = true;

        StyleEditor(_out);
        _out.ReadOnly = true;

        var codeCard = new CardPanel { Dock = DockStyle.Fill, CornerRadius = 8, Padding = new Padding(0) };
        _codeCaption = Caption("ПРОГРАММА CLIPS · " + DefaultProgram);
        codeCard.Controls.Add(_code);
        codeCard.Controls.Add(_codeCaption);

        _views.Add("Вывод");
        _views.Add("Факты");
        _views.Add("Агенда");
        _views.Add("Граф");
        _views.Dock = DockStyle.Top;
        _views.Height = 34;
        _views.BackColor = Theme.Panel;

        _graphModes.Add("Цели");
        _graphModes.Add("Все факты");
        _graphModes.Height = 32;
        _graphModes.Width = _graphModes.PreferredWidth;
        _graphModes.BackColor = Theme.Panel;
        _graphModes.SelectedIndexChanged += (_, _) => RebuildGraph();
        _graphModes.SelectedIndex = 0;

        // Список целей: дерево одной цели читается, вся база — только обзорно.
        _graphGoals.Height = 26;
        _graphGoals.Margin = new Padding(14, 3, 0, 0);
        _graphGoals.Items.Add("Все цели");
        _graphGoals.SelectedIndex = 0;
        _graphGoals.SelectedIndexChanged += (_, _) => { if (!_syncingGoals) RebuildGraph(); };

        var graphBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel,
            Padding = new Padding(4, 1, 0, 0)
        };
        graphBar.Controls.Add(_graphModes);
        graphBar.Controls.Add(_graphGoals);

        _graph.Dock = DockStyle.Fill;
        _graphHost.Controls.Add(_graph);
        _graphHost.Controls.Add(graphBar);
        _graphHost.Visible = false;

        var outCard = new CardPanel { Dock = DockStyle.Fill, CornerRadius = 8, Padding = new Padding(0) };
        outCard.Controls.Add(_out);
        outCard.Controls.Add(_graphHost);
        outCard.Controls.Add(_views);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor = Theme.Background
        };
        split.Panel1.Controls.Add(codeCard);
        split.Panel2.Controls.Add(outCard);

        // Пропорции задаём один раз, когда панель получила настоящую ширину:
        // до раскладки SplitterDistance выходит за границы и бросает исключение.
        var applied = false;
        void Apply()
        {
            if (applied) return;
            var limit = split.Width - split.Panel2MinSize - split.SplitterWidth;
            if (limit <= split.Panel1MinSize) return;
            split.SplitterDistance = Math.Clamp((int)(split.Width * 0.52), split.Panel1MinSize, limit);
            applied = true;
        }
        split.SizeChanged += (_, _) => Apply();
        VisibleChanged += (_, _) => { if (Visible) Apply(); };

        return split;
    }

    private static Label Caption(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 26,
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(10, 0, 0, 0),
        ForeColor = Theme.TextMuted,
        Font = Theme.UiSmall,
        BackColor = Theme.Panel
    };

    private static void StyleEditor(RichTextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.Font = Theme.Mono;
        box.BackColor = Theme.PanelAlt;
        box.ForeColor = Theme.Text;
        box.BorderStyle = BorderStyle.None;
        box.WordWrap = false;
        box.ScrollBars = RichTextBoxScrollBars.Both;
        box.DetectUrls = false;
        box.HideSelection = false;
    }

    // ================================================================ движок ===

    /// <summary>Первое открытие режима: грузим программу по умолчанию и делаем Reset.</summary>
    public void EnsureReady()
    {
        if (_started) return;
        _started = true;

        FillProgramList();

        var path = FindProgram(DefaultProgram);
        if (path is null)
        {
            Append("Файл data\\" + DefaultProgram + " не найден.");
            RenderView();
            return;
        }

        LoadProgram(path);
    }

    /// <summary>Список примеров из папки data — переключение без диалога открытия файла.</summary>
    private void FillProgramList()
    {
        var folder = DataFolder();
        var names = folder is null
            ? Array.Empty<string>()
            : Directory.GetFiles(folder, "*.clp").Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToArray();

        if (names.Length == 0) names = new[] { DefaultProgram };

        _programs.Items.Clear();
        _programs.Items.AddRange(names);
        var index = Array.IndexOf(names, DefaultProgram);
        _programs.SelectedIndex = index >= 0 ? index : 0;
    }

    private void SelectProgramFromList()
    {
        if (_programs.SelectedItem is not string name) return;
        if (string.Equals(name, ProgramName, StringComparison.OrdinalIgnoreCase)) return;

        var path = FindProgram(name);
        if (path is null)
        {
            Append("Файл data\\" + name + " не найден.");
            RenderView();
            return;
        }

        _started = true;
        LoadProgram(path);
    }

    /// <summary>Читает файл программы, обновляет подписи и делает Restart.</summary>
    private void LoadProgram(string path)
    {
        try
        {
            _code.Text = File.ReadAllText(path);
            _path = path;
            UpdateCaptions();
            Restart();
        }
        catch (Exception ex)
        {
            Append("Не удалось открыть файл: " + ex.Message);
            RenderView();
        }
    }

    /// <summary>Подпись над редактором зависит от выбранного примера.</summary>
    private void UpdateCaptions()
    {
        var name = ProgramName;
        if (_codeCaption is not null) _codeCaption.Text = "ПРОГРАММА CLIPS · " + name;
    }

    /// <summary>Clear + Load + Reset — как кнопка «Рестарт» в примере.</summary>
    public void Restart()
    {
        if (!EnsureEngine()) return;

        _step = 0;
        _finished = false;
        _log.Clear();

        try
        {
            _env!.Clear();
            _env.LoadFromString(_code.Text);
            _env.Reset();

            // Reset возвращает defglobal к значениям из файла, поэтому режим
            // применяем после него — и только если программа его знает.
            _cfModeSupported =
                Count("(if (member$ parallel (get-defglobal-list)) then 1 else 0)") > 0;
            _cfMode.Enabled = _cfModeSupported;
            if (_cfModeSupported) ApplyCfMode();

            Append("Выполнены команды Clear и Reset.");
            Append("Правил: " + CountRules() + " · фактов: " + CountFacts() + ".");
        }
        catch (Exception ex)
        {
            Append("Ошибка загрузки программы: " + ex.Message);
        }

        RenderView();
        RaiseStatus();
    }

    /// <summary>Один запуск Run() до останова по (halt) — как кнопка «Дальше» в примере.</summary>
    public void Next()
    {
        if (!EnsureEngine()) return;
        if (!_started) EnsureReady();
        if (_env is null || _router is null) return;

        _router.Clear();
        var factsBefore = CountFacts();
        try
        {
            _env.Run();
        }
        catch (Exception ex)
        {
            Append("Ошибка выполнения: " + ex.Message);
            RenderView();
            return;
        }

        _step++;
        var trace = _router.Output.TrimEnd();
        _finished = trace.Length == 0;
        var factsAfter = CountFacts();

        // Run() прогоняет всю агенду до (halt) — так же, как в CLIPSFormsExample.
        // Поэтому в заголовке видно, сколько фактов реально добавило это нажатие.
        Append(string.Empty);
        Append($"── Шаг {_step} · выведено фактов: {factsAfter - factsBefore} · всего: {factsAfter} " +
               new string('─', 14));
        Append(_finished ? "Сработавших правил нет — агенда пуста." : trace);

        foreach (var message in ReadProxyMessages().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Append("ЭС: " + message);

        // Так же, как в примере: гасим сообщения, чтобы следующая итерация началась с чистого листа.
        // Только если программа вообще пользуется фактом-посредником ioproxy.
        Release(Evaluate("(if (> (length$ (find-all-facts ((?f ioproxy)) TRUE)) 0) " +
                         "then (assert (clearmessage)) else FALSE)"));

        RenderView();
        RaiseStatus();
    }

    /// <summary>
    /// Варианты объединения CF, которые можно переключить на лету.
    /// Подпись — сама формула, а тройка defglobal задаёт, какая функция её считает.
    /// </summary>
    private static readonly (string Label, string Parallel, string Serial, string And)[] CfModes =
    {
        ("cf1+cf2−cf1·cf2 (уравнение 1)", "standard", "clamp", "min"),
        ("λ1·λ2 → cf (уравнения 4–6)", "lambda", "clamp", "min"),
        ("cf1+cf2 (обрезка до ±1)", "linear", "clamp", "min"),
        ("max(cf1, cf2)", "strongest", "clamp", "min"),
        ("cf_пр·cf_п без отсечки", "standard", "open", "min"),
        ("cf_п<0.2 → 0, иначе cf_пр·cf_п", "standard", "deadzone", "min"),
        ("Π cf_i вместо min", "standard", "clamp", "product"),
    };

    /// <summary>Пишет выбранный режим в defglobal программы (после Reset).</summary>
    private void ApplyCfMode()
    {
        var mode = CfModes[Math.Clamp(_cfMode.SelectedIndex, 0, CfModes.Length - 1)];
        Release(Evaluate("(bind ?*parallel* " + mode.Parallel + ")"));
        Release(Evaluate("(bind ?*serial* " + mode.Serial + ")"));
        Release(Evaluate("(bind ?*and-mode* " + mode.And + ")"));
    }

    /// <summary>Смена режима перезапускает пример и сразу считает его заново.</summary>
    private void OnCfModeChanged()
    {
        if (!_started || _env is null || !_cfModeSupported) return;

        Restart();
        if (_cfModeSupported) Next();
    }

    private bool EnsureEngine()
    {
        if (_env is not null) return true;

        try
        {
            _env = new CLIPSNET.Environment();
            _router = new CaptureRouter(_env, new[] { Router.STDOUT });
            Append("Машина вывода CLIPS загружена (CLIPSCLRWrapper).");
            return true;
        }
        catch (Exception ex)
        {
            _env = null;
            _router = null;
            Append("Не удалось загрузить CLIPS: " + ex.Message);
            Append("Режим требует сборки x86 — проверьте PlatformTarget проекта.");
            RenderView();
            return false;
        }
    }

    /// <summary>
    /// Сообщения, которые ЭС положила в слот messages факта ioproxy.
    /// Забираем их одной строкой целиком: на .NET-сторону попадает один объект-значение,
    /// который мы сразу освобождаем (обёртка держит нативные указатели на факты).
    /// </summary>
    private string ReadProxyMessages()
    {
        var value = Evaluate(
            "(if (> (length$ (find-all-facts ((?f ioproxy)) TRUE)) 0) " +
            "then (implode$ (fact-slot-value (nth$ 1 (find-all-facts ((?f ioproxy)) TRUE)) messages)) " +
            "else \"\")");
        try
        {
            return value is LexemeValue lexeme ? DecodeSlot(lexeme.Value) : string.Empty;
        }
        finally { Release(value); }
    }

    /// <summary>Eval с перехватом ошибок; полученное значение надо освободить через <see cref="Release"/>.</summary>
    private PrimitiveValue? Evaluate(string command)
    {
        if (_env is null) return null;
        try { return _env.Eval(command); }
        catch { return null; }
    }

    /// <summary>
    /// Освобождает значение обёртки сразу. Иначе финализатор FactAddressValue дёрнет
    /// ReleaseFact уже после Dispose среды и процесс упадёт с AccessViolation.
    /// </summary>
    private static void Release(PrimitiveValue? value) => value?.Dispose();

    private string CaptureEval(string command)
    {
        if (_env is null || _router is null) return "Машина вывода не запущена.";

        _router.Clear();
        try { Release(_env.Eval(command)); }
        catch (Exception ex) { return ex.Message; }
        return _router.Output;
    }

    private int CountFacts() => Count("(length$ (get-fact-list))");
    private int CountRules() => Count("(length$ (get-defrule-list))");

    private int Count(string command)
    {
        var value = Evaluate(command);
        try { return value is IntegerValue number ? (int)number.Value : 0; }
        finally { Release(value); }
    }

    // ================================================================ вывод ===

    private void Append(string line)
    {
        _log.AppendLine(line);
    }

    private void RenderView()
    {
        var graphView = _views.SelectedIndex == 3;
        _out.Visible = !graphView;
        _graphHost.Visible = graphView;

        if (graphView)
        {
            RebuildGraph();
            return;
        }

        _out.Text = _views.SelectedIndex switch
        {
            1 => CaptureEval("(facts)"),
            2 => CaptureEval("(agenda)"),
            _ => _log.ToString()
        };

        _out.SelectionStart = _out.TextLength;
        _out.ScrollToCaret();
    }

    /// <summary>Строит блок-схему из последнего блока #GRAPH в журнале вывода.</summary>
    private void RebuildGraph()
    {
        var graph = ClipsGraph.Parse(_log.ToString());
        SyncGoalList(graph);

        var goalsOnly = _graphModes.SelectedIndex <= 0;
        var goal = goalsOnly && _graphGoals.SelectedIndex > 0 ? _graphGoals.SelectedItem as string : null;
        _graph.SetGraph(graph, goalsOnly, goal);
    }

    /// <summary>Обновляет список целей, сохраняя выбранную цель, если она ещё есть.</summary>
    private void SyncGoalList(ClipsGraph? graph)
    {
        var names = graph?.Goals.Where(g => g.Cf.HasValue).Select(g => g.Name).ToList() ?? new List<string>();
        var current = _graphGoals.SelectedItem as string;

        var same = _graphGoals.Items.Count == names.Count + 1;
        for (var i = 0; i < names.Count && same; i++)
            same = Equals(_graphGoals.Items[i + 1], names[i]);
        if (same) return;

        _syncingGoals = true;
        try
        {
            _graphGoals.Items.Clear();
            _graphGoals.Items.Add("Все цели");
            foreach (var name in names) _graphGoals.Items.Add(name);

            // По умолчанию — первая цель: её дерево читается лучше, чем все сразу.
            _graphGoals.SelectedIndex = current is not null && names.Contains(current)
                ? names.IndexOf(current) + 1
                : names.Count > 0 ? 1 : 0;
        }
        finally { _syncingGoals = false; }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

    // ================================================================ файлы ===

    private void OpenProgram()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Открыть файл кода CLIPS",
            Filter = "CLIPS files (*.clp)|*.clp|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        _started = true;
        LoadProgram(dialog.FileName);
    }

    private void SaveProgram()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Сохранить файл как…",
            Filter = "CLIPS files (*.clp)|*.clp|Все файлы (*.*)|*.*",
            FileName = Path.GetFileName(_path)
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, _code.Text);
            _path = dialog.FileName;
            RaiseStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), ex.Message, "Ошибка сохранения",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? FindProgram(string fileName)
    {
        var folder = DataFolder();
        if (folder is null) return null;
        var path = Path.Combine(folder, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Папка с примерами: рядом с exe (копируется сборкой) или в корне репозитория.</summary>
    private static string? DataFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data"),
            "data"
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static Encoding CreateSlotEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    private static string DecodeSlot(string value)
    {
        try { return Encoding.UTF8.GetString(SlotEncoding.GetBytes(value)); }
        catch { return value; }
    }

    // ================================================================ служебное ===

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _router?.Dispose();
            _env?.Dispose();
            _router = null;
            _env = null;
        }
        base.Dispose(disposing);
    }
}
