using System.ComponentModel;
using System.Drawing;

namespace ProdModel.App;

/// <summary>Выбор предмета для обратного запроса «предмет → лучшие цели».</summary>
internal sealed class ItemPickerForm : Form
{
    private readonly ComboBox _cmb = new();
    private readonly Label _hint = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedItem { get; private set; } = string.Empty;

    public ItemPickerForm(IEnumerable<string> items, string? preselected = null)
    {
        Text = "Обратный запрос: предмет → враги";
        Size = new Size(460, 230);
        MinimumSize = new Size(380, 210);
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
            Height = 42,
            Text = "Выберите предмет — покажу героев, против которых он работает лучше всего:",
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui,
            Padding = new Padding(2, 6, 0, 0)
        };

        _cmb.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmb.FlatStyle = FlatStyle.Flat;
        _cmb.BackColor = Theme.PanelAlt;
        _cmb.ForeColor = Theme.Text;
        _cmb.Font = Theme.Ui;
        _cmb.Dock = DockStyle.Top;

        foreach (var item in items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            _cmb.Items.Add(item);

        var preselectedIndex = -1;
        if (!string.IsNullOrEmpty(preselected))
        {
            for (var i = 0; i < _cmb.Items.Count; i++)
            {
                if (string.Equals(_cmb.Items[i]?.ToString(), preselected, StringComparison.OrdinalIgnoreCase))
                {
                    preselectedIndex = i;
                    break;
                }
            }
        }

        if (_cmb.Items.Count > 0)
            _cmb.SelectedIndex = preselectedIndex >= 0 ? preselectedIndex : 0;

        _cmb.SelectedIndexChanged += (_, _) => UpdateHint();
        UpdateHint();

        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        card.Controls.Add(_cmb);
        card.Controls.Add(header);

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 26;
        _hint.ForeColor = Theme.TextMuted;
        _hint.Font = Theme.UiSmall;
        _hint.Padding = new Padding(14, 6, 0, 0);

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

        var ok = MakeButton("Показать");
        ok.Click += (_, _) => Confirm();

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(card);
        Controls.Add(_hint);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) => _cmb.Focus();
    }

    private void UpdateHint()
    {
        if (_cmb.SelectedItem is string item)
        {
            _hint.Text = $"Выбран: {item}";
            _hint.ForeColor = Theme.TextMuted;
        }
        else
        {
            _hint.Text = "Предмет не выбран";
            _hint.ForeColor = Theme.Amber;
        }
    }

    private void Confirm()
    {
        if (_cmb.SelectedItem is not string item || string.IsNullOrWhiteSpace(item))
        {
            MessageBox.Show(this, "Выберите предмет из списка.", "Обратный запрос",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SelectedItem = item;
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