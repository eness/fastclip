using System.Drawing;
using System.Windows.Forms;
using FastClip.Interop;
using FastClip.Models;

namespace FastClip.Forms;

internal sealed class HotkeySettingsForm : Form
{
    private readonly CheckBox _ctrlCheckBox;
    private readonly CheckBox _shiftCheckBox;
    private readonly CheckBox _altCheckBox;
    private readonly CheckBox _winCheckBox;
    private readonly ComboBox _keyComboBox;
    public HotkeyRegistration SelectedHotkey { get; private set; }

    public HotkeySettingsForm(HotkeyRegistration currentHotkey)
    {
        SelectedHotkey = currentHotkey;
        Text = "Change Hot Key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 210);

        var titleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(20, 18),
            Text = "Select a new hotkey"
        };

        _ctrlCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(23, 52),
            Text = "Ctrl"
        };

        _shiftCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(95, 52),
            Text = "Shift"
        };

        _altCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(175, 52),
            Text = "Alt"
        };

        _winCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(235, 52),
            Text = "Win"
        };

        var keyLabel = new Label
        {
            AutoSize = true,
            Location = new Point(20, 92),
            Text = "Key"
        };

        _keyComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(23, 114),
            Width = 274
        };

        foreach (var key in SupportedKeys)
        {
            _keyComboBox.Items.Add(key);
        }

        var okButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(141, 160),
            Width = 75
        };
        okButton.Click += (_, args) => OnSave(args);

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(222, 160),
            Width = 75
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.Add(titleLabel);
        Controls.Add(_ctrlCheckBox);
        Controls.Add(_shiftCheckBox);
        Controls.Add(_altCheckBox);
        Controls.Add(_winCheckBox);
        Controls.Add(keyLabel);
        Controls.Add(_keyComboBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        _ctrlCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_CONTROL) != 0;
        _shiftCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_SHIFT) != 0;
        _altCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_ALT) != 0;
        _winCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_WIN) != 0;
        _keyComboBox.SelectedItem = currentHotkey.Key;
        if (_keyComboBox.SelectedIndex < 0)
        {
            _keyComboBox.SelectedItem = Keys.V;
        }
    }

    private void OnSave(EventArgs args)
    {
        var modifiers = 0u;

        if (_ctrlCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_CONTROL;
        }

        if (_shiftCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_SHIFT;
        }

        if (_altCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_ALT;
        }

        if (_winCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_WIN;
        }

        if (modifiers == 0)
        {
            MessageBox.Show(this, "Select at least one modifier key.", "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        if (_keyComboBox.SelectedItem is not Keys selectedKey)
        {
            MessageBox.Show(this, "Select a key.", "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        SelectedHotkey = new HotkeyRegistration(HotkeyRegistration.Default.Id, modifiers, selectedKey);
    }

    private static readonly Keys[] SupportedKeys =
    [
        Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G, Keys.H, Keys.I, Keys.J, Keys.K, Keys.L, Keys.M,
        Keys.N, Keys.O, Keys.P, Keys.Q, Keys.R, Keys.S, Keys.T, Keys.U, Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z,
        Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9,
        Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12
    ];
}
