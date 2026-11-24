using ProdModel.App;
using Xunit;

namespace ProdModel.UiTests;

/// <summary>
/// Вид «Слои» в режимах прямого и обратного вывода: блоки по слоям,
/// как в режиме CLIPS, но построенные из данных C#-движка.
/// </summary>
public sealed class BlockGraphTests
{
    private const string MainKb = "deadlock_kb.txt";

    private static string KbPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "data", name);

    private static MainForm NewForm()
    {
        var form = new MainForm(Array.Empty<string>());
        form.TestLoadKb(KbPath(MainKb));
        form.Size = new Size(1500, 950);
        return form;
    }

    [StaFact]
    public void Tree_view_is_default_and_blocks_are_hidden()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        Assert.Equal(0, form.TestGraphViewIndex);
        Assert.False(form.TestBlockPanel.Visible);

        form.TestSetGraphView(1);
        Application.DoEvents();

        Assert.Equal(1, form.TestGraphViewIndex);
        Assert.True(form.TestBlockPanel.Visible);
        Assert.Equal(0, form.TestBlockNodes);   // вывод ещё не запускали

        form.Hide();
    }

    [StaFact]
    public void Forward_blocks_show_layers_rules_and_proof_scope()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestShowAllForward();
        form.TestSetGraphView(1);
        Application.DoEvents();

        // По умолчанию — доказательство фокуса: это поддерево, а не вся память.
        var proof = form.TestBlockNodesList;
        Assert.True(proof.Count >= 4, "блоков в доказательстве: " + proof.Count);
        Assert.True(Layers(proof) >= 4, "слоёв в доказательстве: " + Layers(proof));

        // Каждый выведенный блок знает своё правило и свои посылки.
        var derived = proof.Where(n => n.Kind == BlockKind.Derived).ToList();
        Assert.NotEmpty(derived);
        Assert.All(derived, n => Assert.StartsWith("правило", n.Footer));
        Assert.All(derived, n => Assert.StartsWith("шаг", n.Badge));

        // Входные факты — без правила, слой 0.
        var inputs = proof.Where(n => n.Kind == BlockKind.Input).ToList();
        Assert.NotEmpty(inputs);
        Assert.All(inputs, n => Assert.Equal(0, n.Layer));
        Assert.All(inputs, n => Assert.Equal("входной факт", n.Footer));

        // «Все факты» — вся рабочая память к шагу: блоков заметно больше.
        var proofCount = form.TestBlockNodes;
        form.TestSetBlockScope(1);
        Application.DoEvents();

        Assert.True(form.TestBlockNodes > proofCount * 3,
            $"вся память {form.TestBlockNodes} против доказательства {proofCount}");
        Assert.True(Layers(form.TestBlockNodesList) >= 4, "слоёв вывода должно быть не меньше четырёх");

        form.Hide();
    }

    [StaFact]
    public void Forward_blocks_follow_the_animation_step()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestSetGraphView(1);
        form.TestSetBlockScope(1);
        form.TestStepForward();
        Application.DoEvents();

        var afterOne = form.TestBlockNodes;
        Assert.True(afterOne > 0, "после первого шага блоки уже есть");

        form.TestShowAllForward();
        Application.DoEvents();

        Assert.True(form.TestBlockNodes > afterOne,
            $"к концу вывода блоков больше: {form.TestBlockNodes} против {afterOne}");

        form.Hide();
    }

    [StaFact]
    public void Backward_blocks_put_the_goal_right_of_its_premises()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestSetMode(backward: true);
        form.TestShowAllBackward();
        form.TestSetGraphView(1);
        Application.DoEvents();

        var nodes = form.TestBlockNodesList;
        Assert.True(nodes.Count >= 4, "блоков в дереве цели: " + nodes.Count);
        Assert.True(Layers(nodes) >= 3, "слоёв в дереве цели: " + Layers(nodes));

        // Цель — крайняя правая колонка, её посылки лежат левее.
        var goal = nodes.Single(n => n.IsGoal);
        Assert.Equal(Layers(nodes) - 1, goal.Layer);
        Assert.All(nodes.Where(n => n.Premises.Contains(goal.Key)), n => Assert.True(n.Layer < goal.Layer));

        // У доказанной цели есть рёбра «посылка → вывод».
        Assert.NotEmpty(goal.Premises);
        Assert.All(goal.Premises, key => Assert.Contains(nodes, n => n.Key == key));

        form.Hide();
    }

    [StaFact]
    public void Backward_blocks_mark_prevented_cycles()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestSetMode(backward: true);
        Assert.True(form.TestSelectBackwardGoalByName("i_answers_Knockdown"));
        form.TestShowAllBackward();
        form.TestSetGraphView(1);
        Application.DoEvents();

        var cycles = form.TestBlockNodesList.Where(n => n.Kind == BlockKind.Cyclic).ToList();

        // В этой цели поиск замыкается на себя: цикл предотвращён и виден на схеме.
        Assert.NotEmpty(cycles);
        Assert.All(cycles, n => Assert.Equal("⟲", n.Badge));
        Assert.All(cycles, n => Assert.Equal("цикл предотвращён", n.Footer));

        form.Hide();
    }

    [StaFact]
    public void Blocks_are_laid_out_in_lanes_without_overlaps()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestShowAllForward();
        form.TestSetGraphView(1);
        form.TestSetBlockScope(1);
        Application.DoEvents();

        var nodes = form.TestBlockNodesList;
        Assert.NotEmpty(nodes);

        // Слои — дорожки: в горизонтальной раскладке это колонки (общий X),
        // в вертикальной — строки (общий Y). Внутри дорожки блоки не слипаются.
        var vertical = form.TestBlockPanel.TestVertical;
        foreach (var layer in nodes.GroupBy(n => n.Layer))
        {
            var lane = vertical ? layer.Select(n => n.Y) : layer.Select(n => n.X);
            var across = vertical ? layer.Select(n => n.X) : layer.Select(n => n.Y);
            Assert.Single(lane.Distinct());
            Assert.Equal(layer.Count(), across.Distinct().Count());
        }

        // Блоки не пересекаются между собой.
        for (var i = 0; i < nodes.Count; i++)
        for (var j = i + 1; j < nodes.Count; j++)
            Assert.False(Overlaps(nodes[i], nodes[j]),
                $"блоки пересекаются: {nodes[i].Key} и {nodes[j].Key}");

        // Отрисовка не должна падать и оставляет след на битмапе.
        var panel = form.TestBlockPanel;
        using var bitmap = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(bitmap, new Rectangle(0, 0, panel.Width, panel.Height));

        var painted = 0;
        for (var x = 0; x < bitmap.Width; x += 5)
        for (var y = 0; y < bitmap.Height; y += 5)
            if (bitmap.GetPixel(x, y).ToArgb() != bitmap.GetPixel(0, bitmap.Height - 1).ToArgb()) painted++;

        Assert.True(painted > 100, "на схеме что-то нарисовано: пикселей " + painted);

        form.Hide();
    }

    [StaFact]
    public void Deep_proof_chain_is_turned_vertically_to_stay_readable()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestShowAllForward();
        form.TestSetGraphView(1);
        Application.DoEvents();

        // Доказательство цели — длинная цепочка из ~11 слоёв по 1–2 факта.
        var nodes = form.TestBlockNodesList;
        Assert.True(Layers(nodes) >= 8, "слоёв в доказательстве: " + Layers(nodes));

        // Колонками такая цепочка не влезает по ширине (zoom < 0.3 → текст пропадает),
        // поэтому раскладка поворачивается: слои идут сверху вниз, текст остаётся видимым.
        Assert.True(form.TestBlockPanel.TestVertical, "длинная цепочка должна раскладываться по строкам");
        Assert.True(form.TestBlockPanel.TestZoom >= 0.3f,
            "масштаб доказательства: " + form.TestBlockPanel.TestZoom.ToString("0.000"));

        form.Hide();
    }

    [StaFact]
    public void Switching_modes_relabels_the_scope_switcher()
    {
        using var form = NewForm();
        form.Show();
        Application.DoEvents();

        form.TestSetGraphView(1);
        form.TestSetBlockScope(1);
        Application.DoEvents();

        // В обратном режиме «всё» — это все цели, а не все факты.
        form.TestSetMode(backward: true);
        Application.DoEvents();

        Assert.Equal(1, form.TestGraphViewIndex);
        Assert.Equal(1, form.TestBlockScopeIndex);

        form.TestShowAllBackward();
        Application.DoEvents();

        Assert.True(form.TestBlockNodes > 0, "в режиме «все цели» блоки есть");

        form.Hide();
    }

    private static int Layers(IReadOnlyList<BlockNode> nodes)
        => nodes.Count == 0 ? 0 : nodes.Max(n => n.Layer) + 1;

    private static bool Overlaps(BlockNode a, BlockNode b)
        => a.X < b.X + BlockNode.W && b.X < a.X + BlockNode.W
           && a.Y < b.Y + BlockNode.H && b.Y < a.Y + BlockNode.H;
}
