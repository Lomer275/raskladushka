using System.Diagnostics;
using Raskladushka.Core.Updates;

namespace Raskladushka.App.Shell;

public sealed class AboutForm : Form
{
    public AboutForm(AppHost host)
    {
        Text = "О программе";
        Icon = AppIcons.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12)
        };
        Controls.Add(layout);
        var titleFont = new Font(Font.FontFamily, Font.Size + 4, FontStyle.Bold);
        Disposed += (_, _) => titleFont.Dispose();
        layout.Controls.Add(new Label { AutoSize = true, Text = "Раскладушка", Font = titleFont });
        layout.Controls.Add(new Label { AutoSize = true, Text = $"Версия {Application.ProductVersion.Split('+')[0]}" });
        layout.Controls.Add(new Label { AutoSize = true, Text = "Лицензия MIT" });
        var link = new LinkLabel { AutoSize = true, Text = "github.com/Lomer275/raskladushka" };
        link.LinkClicked += (_, _) => Open(new ProcessStartInfo(UpdateCheck.RepoUrl) { UseShellExecute = true });
        layout.Controls.Add(link);
        var licenses = new Button { AutoSize = true, Text = "Сторонние лицензии" };
        licenses.Click += (_, _) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "Файл THIRD-PARTY-NOTICES.txt не найден рядом с программой.", "Раскладушка");
                return;
            }
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Open(start);
        };
        layout.Controls.Add(licenses);
        var close = new Button { AutoSize = true, Text = "Закрыть", DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        void Open(ProcessStartInfo start)
        {
            try { Process.Start(start)?.Dispose(); }
            catch (Exception error)
            {
                host.Log.Error("About open failed", error);
                MessageBox.Show(this, "Не удалось открыть.", "Раскладушка");
            }
        }
    }
}
