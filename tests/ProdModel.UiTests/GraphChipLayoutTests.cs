using ProdModel.App;
using Xunit;

namespace ProdModel.UiTests;

/// <summary>
/// Раскладка красных чипов «цикл предотвращён»: чип не должен наезжать на
/// узлы своего слоя — он ставится за последним узлом строки.
/// </summary>
public sealed class GraphChipLayoutTests
{
    private static GraphNode Node(string text) => new() { Text = text };

    [StaFact]
    public void Chip_IsPlacedRightOfItsRow_AndDoesNotOverlapSiblings()
    {
        var root = Node("root");

        var left = Node("left");
        var right = Node("right");
        root.Children.Add(left);
        root.Children.Add(right);

        // У правого узла — предотвращённый цикл (красный чип).
        var chip = Node("cycle");
        chip.Rejected = true;
        right.Children.Add(chip);

        using var panel = new GraphPanel();
        panel.SetTree(root, resetView: true);

        var rowRight = Math.Max(left.X + left.W, right.X + right.W);

        Assert.True(chip.X >= rowRight, "чип должен стоять правее последнего узла своего слоя");
        Assert.False(Overlaps(chip, left), "чип не должен наезжать на левый узел");
        Assert.False(Overlaps(chip, right), "чип не должен наезжать на правый узел");
        Assert.Equal(left.Y, right.Y);
    }

    [StaFact]
    public void Chips_OfOneNode_StackWithoutOverlappingEachOther()
    {
        var root = Node("root");

        var first = Node("cycle-1");
        var second = Node("cycle-2");
        first.Rejected = true;
        second.Rejected = true;
        root.Children.Add(first);
        root.Children.Add(second);

        using var panel = new GraphPanel();
        panel.SetTree(root, resetView: true);

        Assert.Equal(first.X, second.X);
        Assert.True(second.Y >= first.Y + first.H, "чипы одного узла идут стопкой");
        Assert.False(Overlaps(first, root), "чип не должен наезжать на свой узел");
        Assert.False(Overlaps(second, root), "чип не должен наезжать на свой узел");
    }

    private static bool Overlaps(GraphNode a, GraphNode b)
        => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
}
