using System.Reflection;
using System.Windows.Forms;
using ProdModel.App;
using ProdModel.Core;
using Xunit;

namespace ProdModel.UiTests;

/// <summary>
/// Регрессии обратного вывода: движок (глубина, мемоизация) и интерфейс
/// (переключение режима во время анимации, выбор строки результата).
/// </summary>
public sealed class BackwardRegressionTests
{
    private const string MainKb = "deadlock_kb.txt";

    private static string KbPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "data", name);

    private static object? Field(object target, string name)
        => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);

    private static void Call(object target, string name, params object?[] args)
        => target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    private static WorkingMemory InitialMemory(KnowledgeBase kb)
    {
        var memory = new WorkingMemory();
        foreach (var fact in kb.Facts)
            memory.AddInput(new Fact(fact.Atom, FactOrigin.Input) { DisplayName = fact.DisplayName });
        return memory;
    }

    private static KnowledgeBase LoadKb(string label, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prodmodel_test_{label}.txt");
        File.WriteAllText(path, text);
        return KbLoader.Load(path);
    }

    private static string ChainKb(int links, bool firstIsFact)
    {
        var facts = firstIsFact ? "a" : "x";
        var rules = string.Join("\n", Enumerable.Range(1, links)
            .Select(i => (i == 1 ? "a" : $"b{i - 1}") + $" -> b{i}"));

        return $"ИМЯ: chain\nФАКТЫ:\n{facts}\nПРАВИЛА:\n{rules}\nЦЕЛИ:\nb{links}\n";
    }

    [Fact]
    public void Solve_ProvesChainThatEndsWithInputFactBeyondDepthLimit()
    {
        var kb = LoadKb("chain_fact", ChainKb(links: 15, firstIsFact: true));
        var result = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("b15"),
            new BackwardOptions { MaxDepth = 14, MaxNodes = 100_000, UseMemo = false });

        // Последний узел — исходный факт, он замыкает ветвь независимо от глубины.
        Assert.True(result.Proven, $"ожидалось доказательство, получено {result.Root.State}: {result.Root.Note}");
    }

    [Fact]
    public void Solve_StillReportsDepthLimitWhenChainHasNoFact()
    {
        var kb = LoadKb("chain_no_fact", ChainKb(links: 3, firstIsFact: false));
        var result = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("b3"),
            new BackwardOptions { MaxDepth = 1, MaxNodes = 100_000, UseMemo = false });

        Assert.False(result.Proven);
        Assert.Equal(SearchState.DepthLimit, result.Root.State);
    }

    [Fact]
    public void Solve_ReusedMemoIsDroppedWhenMemoryChanges()
    {
        var kbA = LoadKb("memo_a", "ИМЯ: memo_a\nФАКТЫ:\na\nПРАВИЛА:\na -> g\nЦЕЛИ:\ng\n");
        var kbB = LoadKb("memo_b", "ИМЯ: memo_b\nФАКТЫ:\na\nПРАВИЛА:\nЦЕЛИ:\ng\n");

        var engine = new BackwardEngine();
        var proven = engine.Solve(kbA, InitialMemory(kbA), new Predicate("g"),
            new BackwardOptions { ReuseMemo = true });
        var unproven = engine.Solve(kbB, InitialMemory(kbB), new Predicate("g"),
            new BackwardOptions { ReuseMemo = true });

        Assert.True(proven.Proven);
        Assert.False(unproven.Proven, "мемоизация прошлой памяти не должна применяться к новой");
    }

    [Fact]
    public void Solve_ReusedMemoIsKeptWithinSameMemory()
    {
        var kb = LoadKb("memo_same", "ИМЯ: memo_same\nФАКТЫ:\na\nПРАВИЛА:\na -> g\nЦЕЛИ:\ng\n");
        var memory = InitialMemory(kb);
        var engine = new BackwardEngine();

        var first = engine.Solve(kb, memory, new Predicate("g"), new BackwardOptions { ReuseMemo = true });
        var second = engine.Solve(kb, memory, new Predicate("g"), new BackwardOptions { ReuseMemo = true });

        Assert.True(first.Proven);
        Assert.True(second.Proven);
        Assert.True(second.MemoHits > 0, "вторая проверка той же цели должна попадать в мемо");
    }

    /// <summary>
    /// Первая продукция цели ведёт в тупиковую цепочку, вторая — сразу к факту.
    /// Поиск по критерию наилучшего узла раскрывает дешёвую ветвь параллельно
    /// и не обходит тупик целиком, поэтому раскрытых узлов меньше, чем при
    /// последовательном переборе одной ветви за раз.
    /// </summary>
    [Fact]
    public void Solve_SwitchesToCheaperNeighbourBranch()
    {
        var kb = LoadKb(
            "branch_switch",
            "ИМЯ: branch_switch\n" +
            "ФАКТЫ:\nb1\n" +
            "ПРАВИЛА:\na2 -> a1\na3 -> a2\na1 -> g\nb1 -> g\n" +
            "ЦЕЛИ:\ng\n");

        var greedy = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("g"),
            new BackwardOptions { AttemptsPerNode = 1, UseMemo = false });

        var bestFirst = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("g"),
            new BackwardOptions { AttemptsPerNode = 2, UseMemo = false });

        Assert.True(greedy.Proven);
        Assert.True(bestFirst.Proven);

        Assert.True(
            bestFirst.Expanded < greedy.Expanded,
            $"ожидалось сокращение перебора: {bestFirst.Expanded} < {greedy.Expanded}");
    }

    [Fact]
    public void Solve_ReportsCostOfProvenProof()
    {
        var kb = LoadKb(
            "proof_cost",
            "ИМЯ: proof_cost\nФАКТЫ:\na\nПРАВИЛА:\na -> b1\nb1 -> b2\nb2 -> b3\nЦЕЛИ:\nb3\n");

        var result = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("b3"),
            new BackwardOptions { UseMemo = false });

        Assert.True(result.Proven);

        // Три продукции по одной посылке: 3 × (1 + 0.1 × 1).
        Assert.Equal(3.3, result.ProofCost, 3);
    }

    /// <summary>
    /// Первая продукция цели замыкается сама на себя, вторая опирается на цепочку
    /// фактов. Цикл должен быть предотвращён (ветвь отброшена), а цель — всё равно
    /// доказана переигровкой по второй продукции.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Solve_PreventsCycleAndStillProvesGoalByAnotherRule(int attemptsPerNode)
    {
        var kb = LoadKb(
            "cycle_escape",
            "ИМЯ: cycle_escape\n" +
            "ФАКТЫ:\nb1\nb2\n" +
            "ПРАВИЛА:\nc -> g\ng -> c\nd1, d2 -> g\ne1 -> d1\ne2 -> d2\nb1 -> e1\nb2 -> e2\n" +
            "ЦЕЛИ:\ng\n");

        var result = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("g"),
            new BackwardOptions { AttemptsPerNode = attemptsPerNode, UseMemo = false });

        Assert.True(result.Proven, $"ожидалось доказательство, получено {result.Root.State}: {result.Root.Note}");
        Assert.True(result.CycleHits > 0, "цикл должен быть обнаружен");

        var prevented = Assert.Single(result.Root.RejectedAttempts);
        Assert.True(prevented.CyclePrevented);
        Assert.Contains("цикл", prevented.Reason);

        // Замкнувшаяся ветвь не попадает в доказательство.
        Assert.DoesNotContain(result.Steps, s => s.State == SearchState.Cyclic);
    }

    /// <summary>
    /// Цикл есть, но альтернативной продукции нет: вывод останавливается
    /// с честной пометкой, а не раскрывается бесконечно.
    /// </summary>
    [Fact]
    public void Solve_PureCycleWithoutAlternativeIsReportedAsPrevented()
    {
        var kb = LoadKb(
            "cycle_only",
            "ИМЯ: cycle_only\nФАКТЫ:\nx\nПРАВИЛА:\nc -> g\ng -> c\nЦЕЛИ:\ng\n");

        var result = new BackwardEngine().Solve(kb, InitialMemory(kb), new Predicate("g"),
            new BackwardOptions { UseMemo = false });

        Assert.False(result.Proven);
        Assert.Equal(SearchState.Cyclic, result.Root.State);
        Assert.True(result.CycleHits > 0);
        Assert.Contains("цикл предотвращён", result.FailureReason);
    }

    [StaFact]
    public void LoadingKb_DoesNotRunBackwardInBackground()
    {
        using var form = new MainForm(Array.Empty<string>());
        form.TestLoadKb(KbPath(MainKb));

        // Выбор цели в списке не должен запускать обратный вывод сам по себе:
        // иначе загрузка базы строила дерево, которого никто не просил.
        Assert.Empty(form.TestBackward);
        Assert.Equal(0, form.TestRevealTotal);
        Assert.Null(form.TestGraphTree);
    }

    [StaFact]
    public void SwitchingModeDuringAnimation_StopsTimer()
    {
        using var form = new MainForm(Array.Empty<string>());
        form.TestLoadKb(KbPath(MainKb));
        form.TestSetMode(backward: true);
        form.TestSelectBackwardGoal(0);
        form.TestRunBackward();

        Call(form, "StartAnimation");
        Assert.True((bool)Field(form, "_animRunning")!);

        form.TestSetMode(backward: false);
        Assert.False((bool)Field(form, "_animRunning")!, "переключение режима должно снимать анимацию");

        var forwardStep = (int)Field(form, "_fwdReveal")!;
        Call(form, "OnTimerTick");
        Assert.Equal(forwardStep, (int)Field(form, "_fwdReveal")!);
    }

    [StaFact]
    public void SelectingResultRowMidAnimation_ShowsThatGoal()
    {
        using var form = new MainForm(Array.Empty<string>());
        form.TestLoadKb(KbPath(MainKb));
        form.TestSetMode(backward: true);
        form.TestSelectBackwardGoal(0);
        form.TestRunBackward();

        var grid = (DataGridView)Field(form, "_lstResults")!;
        while (grid.Rows.Count < 3 && form.TestReveal < form.TestRevealTotal)
            form.TestStepBackward();

        var results = form.TestBackward;
        Assert.True(grid.Rows.Count >= 3, $"ожидалось не меньше трёх строк, получено {grid.Rows.Count}");

        typeof(MainForm)
            .GetField("_suppressResultSelect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(form, false);

        grid.ClearSelection();
        grid.Rows[1].Selected = true;

        var explanation = form.TestExplanation;
        Assert.Contains(results[1].Goal.ToString(), explanation);
        Assert.DoesNotContain(results[2].Goal.ToString(), explanation);
    }

    [StaFact]
    public void RunningBackward_KeepsForwardReveal()
    {
        using var form = new MainForm(Array.Empty<string>());
        form.TestLoadKb(KbPath(MainKb));
        form.TestRunForward();
        form.TestStepForward();
        form.TestStepForward();

        var forwardStep = (int)Field(form, "_fwdReveal")!;
        Assert.True(forwardStep > 0);

        // Обратный вывод запускается, пока активен прямой режим (так бывает при
        // загрузке базы): раньше он обнулял счётчик раскрытия прямого графа.
        form.TestRunBackward();
        form.TestStepBackward();

        Assert.True((int)Field(form, "_bwdReveal")! > 0);
        Assert.Equal(forwardStep, (int)Field(form, "_fwdReveal")!);
    }
}
