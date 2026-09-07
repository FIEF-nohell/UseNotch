using System.Drawing;
using System.Windows.Forms;

namespace UseNotch.OverlayInputHarness;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InputTargetForm());
    }
}

internal sealed class InputTargetForm : Form
{
    private int _clickCount;
    private int _wheelCount;

    public InputTargetForm()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Bounds = area;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(44, 52, 64);
        Text = BuildTitle();
        ShowInTaskbar = true;
        TopMost = false;
        var label = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(214, 225, 239),
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Location = new Point(24, 24),
            Text = "UseNotch input verification harness. This blue-gray surface is behind the transparent overlay.",
        };
        Controls.Add(label);
        MouseDown += (_, _) =>
        {
            _clickCount++;
            Text = BuildTitle();
        };
        MouseWheel += (_, _) =>
        {
            _wheelCount++;
            Text = BuildTitle();
        };
    }

    private string BuildTitle() => $"UseNotch overlay input target | clicks={_clickCount} | wheels={_wheelCount}";
}
