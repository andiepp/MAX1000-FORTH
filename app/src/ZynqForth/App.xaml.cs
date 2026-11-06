using System.Windows;
using ZynqForth.Themes;
using ZynqForth.ViewModels;
using ZynqForth.Views;

namespace ZynqForth;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preferences = Preferences.Load();
        var viewModel = new MainViewModel(preferences);
        Skins.Apply(this, viewModel.SelectedSkin);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();

        // ZynqForth.exe [--skin <name>] [--connect [COMn]] [file.fs] [file.pattern]
        string[] args = e.Args;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--skin" && i + 1 < args.Length)
            {
                viewModel.SelectedSkin = args[++i];
            }
            else if (args[i] == "--connect")
            {
                if (i + 1 < args.Length && args[i + 1].StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.SelectedPort = args[++i].ToUpperInvariant();
                }

                _ = viewModel.ToggleConnectionAsync();
            }
            else if (System.IO.File.Exists(args[i]) &&
                     args[i].EndsWith(".pattern", StringComparison.OrdinalIgnoreCase))
            {
                viewModel.Patterns.OpenFile(args[i]);
                viewModel.ShowPattern = true;
            }
            else if (System.IO.File.Exists(args[i]))
            {
                viewModel.Editor.OpenFile(args[i]);
            }
        }
    }
}
