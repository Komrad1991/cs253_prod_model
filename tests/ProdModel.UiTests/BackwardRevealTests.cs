using ProdModel.App;
using ProdModel.Core;
using Xunit;

namespace ProdModel.UiTests;

/// <summary>
/// Тесты интерфейса: создают настоящую форму и проверяют состояние графа,
/// а не пиксели. Запуск: dotnet test tests\ProdModel.UiTests
/// </summary>
public sealed class BackwardRevealTests
{
    private const string MainKb = "deadlock_kb.txt";
    private const string CycleGoal = "i_answers_Knockdown";
    private const string BranchingGoal = "second_pick_Curse_anti_immunity";

    private static string KbPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "data", name);

    private static MainForm NewForm(string kb = MainKb)
    {
        var form = new MainForm(Array.Empty<string>());
        form.TestLoadKb(KbPath(kb));
        return form;
    }

    private static MainForm NewBackwardForm(string goal)
    {
        var form = NewForm();
        form.TestSetMode(backward: true);
        Assert.True(form.TestSelectBackwardGoalByName(goal), $"цель «{goal}» не найдена в списке целей");
        form.TestRunBackward();
        return form;
    }

    private static int CountNodes(GraphNode? node)
    {
        if (node is null) return 0;
        var total = 1;
        foreach (var child in node.Children)
            if (!child.Rejected) total += CountNodes(child);
        return total;
    }

    private static List<GraphNode> Flatten(GraphNode? node)
    {
        var list = new List<GraphNode>();
        if (node is null) return list;
        list.Add(node);
        foreach (var child in node.Children)
            if (!child.Rejected) list.AddRange(Flatten(child));
        return list;
    }

    /// <summary>Все узлы графа, включая красные чипы предотвращённых циклов.</summary>
    private static List<GraphNode> FlattenAll(GraphNode? node)
    {
        var list = new List<GraphNode>();
        if (node is null) return list;
        list.Add(node);
        foreach (var child in node.Children) list.AddRange(FlattenAll(child));
        return list;
    }

    private static List<SearchNode> BreadthFirst(SearchNode root)
    {
        var list = new List<SearchNode>();
        var queue = new Queue<SearchNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            list.Add(node);
            foreach (var child in node.Children) queue.Enqueue(child);
        }
        return list;
    }

    [StaFact]
    public void Backward_StartsFromGoalOnly()
    {
        using var form = NewBackwardForm(BranchingGoal);

        Assert.Equal(1, form.TestReveal);
        Assert.True(form.TestRevealTotal > 1, "дерево доказательства должно содержать больше одного узла");

        var tree = form.TestGraphTree;
        Assert.NotNull(tree);
        Assert.Equal(BranchingGoal, tree!.Text);
        Assert.Empty(tree.Children);
    }

    [StaFact]
    public void Backward_GrowsOneNodePerStep()
    {
        using var form = NewBackwardForm(BranchingGoal);
        var total = form.TestRevealTotal;

        for (var step = 2; step <= total; step++)
        {
            form.TestStepBackward();

            Assert.Equal(step, form.TestReveal);
            Assert.Equal(step, CountNodes(form.TestGraphTree));
        }

        Assert.Equal(total, CountNodes(form.TestGraphTree));
    }

    [StaFact]
    public void Backward_RevealOrderIsBreadthFirst()
    {
        using var form = NewBackwardForm(BranchingGoal);
        var result = Assert.Single(form.TestBackward);
        var expected = BreadthFirst(result.Root);

        Assert.Equal(expected.Count, form.TestRevealTotal);

        for (var step = 1; step <= expected.Count; step++)
        {
            if (step > 1) form.TestStepBackward();

            var revealed = Flatten(form.TestGraphTree).Select(n => n.Text).OrderBy(t => t, StringComparer.Ordinal);
            var wanted = expected.Take(step).Select(n => n.Label).OrderBy(t => t, StringComparer.Ordinal);

            Assert.Equal(wanted, revealed);
        }
    }

    [StaFact]
    public void Backward_ShowAllRevealsWholeTree()
    {
        using var form = NewBackwardForm(BranchingGoal);

        form.TestShowAllBackward();

        Assert.Equal(form.TestRevealTotal, form.TestReveal);
        Assert.Equal(form.TestRevealTotal, CountNodes(form.TestGraphTree));
        Assert.Contains(BranchingGoal, form.TestStatus);
    }

    [StaFact]
    public void Backward_ExplanationFollowsReveal()
    {
        using var form = NewBackwardForm(BranchingGoal);

        Assert.Contains(BranchingGoal, form.TestExplanation);
        Assert.Contains("Шаг 1 из", form.TestExplanation);

        form.TestShowAllBackward();

        Assert.Contains("ЦЕЛЬ ДОКАЗАНА", form.TestExplanation);
    }

    [StaFact]
    public void Backward_CycleIsPreventedAndGoalStillProven()
    {
        using var form = NewBackwardForm(CycleGoal);

        form.TestShowAllBackward();

        var result = Assert.Single(form.TestBackward);

        // Цикл встретился и был предотвращён, но цель всё равно доказана:
        // поиск переиграл её по следующей продукции.
        Assert.True(result.CycleHits > 0, "цель-цикл должна давать хотя бы одно попадание в цикл");
        Assert.True(result.Proven, "после предотвращения цикла цель должна быть доказана другой продукцией");

        Assert.Contains(result.Root.RejectedAttempts, a => a.CyclePrevented);

        var chips = FlattenAll(form.TestGraphTree).Where(n => n.Rejected).ToList();

        Assert.NotEmpty(chips);

        foreach (var chip in chips)
        {
            Assert.Equal(GraphState.Cyclic, chip.State);
            Assert.Contains("цикл предотвращён", chip.SubText);
        }

        Assert.Contains("цикл предотвращён", form.TestExplanation);
        Assert.Contains("ПРЕДОТВРАЩЁННЫЕ ЦИКЛЫ", form.TestExplanation);
    }

    [StaFact]
    public void Backward_AllGoalsNeverShrink()
    {
        using var form = NewForm();
        form.TestSetMode(backward: true);
        form.TestSelectBackwardGoal(0);
        form.TestRunBackward();

        var total = form.TestRevealTotal;
        Assert.True(total > 1);

        for (var step = 2; step <= total; step++)
        {
            form.TestStepBackward();

            Assert.Equal(step, form.TestReveal);
            Assert.NotNull(form.TestGraphTree);
        }

        Assert.Equal(total, form.TestReveal);
    }

    [StaFact]
    public void Forward_ShowAllReachesEveryFiring()
    {
        using var form = NewForm();
        form.TestSetMode(backward: false);
        form.TestRunForward();
        form.TestShowAllForward();

        Assert.True(form.TestRevealTotal > 0, "прямой вывод должен дать хотя бы один шаг");
        Assert.Equal(form.TestRevealTotal, form.TestReveal);
        Assert.True(form.TestForwardFacts > 0);

        var tree = form.TestGraphTree;
        Assert.NotNull(tree);
        Assert.NotEmpty(tree!.Children);
    }

    [StaFact]
    public void ModeSwitch_RebuildsGraphForNewMode()
    {
        using var form = NewForm();

        form.TestSetMode(backward: false);
        form.TestRunForward();
        form.TestShowAllForward();
        var forwardNodes = CountNodes(form.TestGraphTree);
        Assert.True(forwardNodes > 1);

        form.TestSetMode(backward: true);
        form.TestSelectBackwardGoalByName(BranchingGoal);
        form.TestShowAllBackward();
        var backwardNodes = CountNodes(form.TestGraphTree);
        Assert.True(backwardNodes > 1);
        Assert.NotEqual(forwardNodes, backwardNodes);

        form.TestSetMode(backward: false);
        Assert.Equal(forwardNodes, CountNodes(form.TestGraphTree));

        form.TestSetMode(backward: true);
        Assert.Equal(backwardNodes, CountNodes(form.TestGraphTree));
    }
}
