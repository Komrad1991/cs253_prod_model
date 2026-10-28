using System.ComponentModel;
using System.Drawing;

namespace ProdModel.App;

/// <summary>Выбор состава противника: от 1 до 6 героев из базы знаний.</summary>
internal sealed class CompositionForm : Form
{
    private readonly CheckedListBox _list = new();
    private readonly Label _hint = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Selected { get; private set; } = Array.Empty<string>();

    public CompositionForm(IEnumerable<string> heroes, IEnumerable<string> selected)
    {
        var chosen = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);

        Text = "Состав противника";
        Size = new Size(380, 600);
        MinimumSize = new Size(320, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "Выберите от 1 до 6 вражеских героев:",
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui,
            Padding = new Padding(2, 6, 0, 0)
        };

        _list.Dock = DockStyle.Fill;
        _list.CheckOnClick = true;
        _list.BackColor = Theme.Panel;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.Font = Theme.Ui;
        _list.IntegralHeight = false;

        foreach (var hero in heroes.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            _list.Items.Add(hero, chosen.Contains(hero));
        }

        _list.ItemCheck += OnItemCheck;

        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        card.Controls.Add(_list);
        card.Controls.Add(header);

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 26;
        _hint.ForeColor = Theme.TextMuted;
        _hint.Font = Theme.UiSmall;
        _hint.Padding = new Padding(12, 6, 0, 0);
        _hint.Text = $"Выбрано: {_list.CheckedItems.Count} из 6";

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Background,
            Padding = new Padding(0, 8, 10, 0)
        };

        var cancel = MakeButton("Отмена");
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var ok = MakeButton("Применить");
        ok.Click += (_, _) => Confirm();

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(card);
        Controls.Add(_hint);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) => _list.Focus();
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (e.NewValue == CheckState.Checked && _list.CheckedItems.Count >= 6)
        {
            e.NewValue = CheckState.Unchecked;
        }

        BeginInvoke(UpdateHint);
    }

    private void UpdateHint()
    {
        var count = _list.CheckedItems.Count;
        _hint.Text = $"Выбрано: {count} из 6";
        _hint.ForeColor = count is >= 1 and <= 6 ? Theme.TextMuted : Theme.Amber;
    }

    private void Confirm()
    {
        var picked = _list.CheckedItems.Cast<string>().ToArray();
        if (picked.Length is < 1 or > 6)
        {
            MessageBox.Show(this, "Нужно выбрать от 1 до 6 героев.", "Состав противника",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Selected = picked;
        DialogResult = DialogResult.OK;
        Close();
    }

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
            Width = TextRenderer.MeasureText(text, Theme.Ui).Width + 30,
            Margin = new Padding(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Theme.AccentDim;
        return button;
    }
}
