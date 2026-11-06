using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using ZynqForth.Board;
using ZynqForth.Themes;
using ZynqForth.ViewModels;

namespace ZynqForth.Views;

public partial class MainWindow : Window, IUserDialogs
{
    /// <summary>The terminal keeps this much text; older output is cut in large steps.</summary>
    private const int TerminalKeep = 200_000;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.Dialogs = this;
        viewModel.TerminalText += AppendTerminal;
        viewModel.Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Title))
            {
                UpdateTitle();
            }
        };
        UpdateTitle();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowPattern))
            {
                UpdateEditorWidth();
            }
        };
        UpdateEditorWidth();

        InputBindings.Add(new KeyBinding(viewModel.Editor.SaveCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(viewModel.RunEditorCommand, Key.F5, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenManual), Key.F1, ModifierKeys.None));
        Loaded += (_, _) => InputBox.Focus();
        SourceInitialized += (_, _) => UpdateTitleBar();
        Skins.Changed += UpdateTitleBar;
        Closed += (_, _) => Skins.Changed -= UpdateTitleBar;
    }

    public MainViewModel ViewModel { get; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ViewModel.Editor.ConfirmDiscard() || !ViewModel.Patterns.ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        ViewModel.Shutdown();
        base.OnClosing(e);
    }

    private void UpdateTitle() => Title = $"{ViewModel.Editor.Title} — MAX1000-FORTH";

    /// <summary>The timing diagram wants room: the editor column takes more of it beside the terminal.</summary>
    private void UpdateEditorWidth()
    {
        EditorColumn.Width = new GridLength(ViewModel.ShowPattern ? 1.8 : 1, GridUnitType.Star);
        TerminalColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    /// <summary>A dark window frame for the dark skins (Windows 10 20H1 and later).</summary>
    private void UpdateTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = ViewModel.SelectedSkin == Skins.IdeLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void AppendTerminal(string text)
    {
        bool atEnd = TerminalBox.VerticalOffset + TerminalBox.ViewportHeight >= TerminalBox.ExtentHeight - 2;
        TerminalBox.AppendText(text);
        if (TerminalBox.Text.Length > TerminalKeep + TerminalKeep / 4)
        {
            TerminalBox.Text = TerminalBox.Text[^TerminalKeep..];
        }

        if (atEnd)
        {
            TerminalBox.ScrollToEnd();
        }
    }

    private void Manual_Click(object sender, RoutedEventArgs e) => OpenManual();

    /// <summary>
    /// Opens the MAX1000-FORTH manual built into the app in the PDF viewer. It is written to
    /// %LocalAppData%\MAX1000-FORTH when missing or different; if a viewer still holds an older
    /// copy open, the new one gets a numbered name.
    /// </summary>
    private void OpenManual()
    {
        const string resource = "Max1000Forth.Manual.pdf";
        const string name = "MAX1000-FORTH Manual";
        try
        {
            using Stream? manual = typeof(MainWindow).Assembly.GetManifestResourceStream(resource);
            if (manual is null)
            {
                throw new FileNotFoundException("This build of the app contains no manual.");
            }

            byte[] content = new byte[manual.Length];
            manual.ReadExactly(content);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAX1000-FORTH");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + ".pdf");
            for (int n = 2; File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(content); n++)
            {
                try
                {
                    File.WriteAllBytes(path, content);
                }
                catch (IOException) when (n < 10)
                {
                    path = Path.Combine(folder, $"{name} ({n}).pdf");
                }
            }

            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, content);
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(this, $"The manual could not be opened: {exception.Message}", "MAX1000-FORTH",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearTerminal_Click(object sender, RoutedEventArgs e)
    {
        TerminalBox.Clear();
        InputBox.Focus();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ViewModel.SendInputCommand.Execute(null);
                TerminalBox.ScrollToEnd();
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel.History(-1);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                ViewModel.History(+1);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void EditorBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            RunSelection();
            e.Handled = true;
        }
    }

    private void RunSelection_Click(object sender, RoutedEventArgs e) => RunSelection();

    /// <summary>Sends the selection, or the caret's line, and moves the caret to the next line.</summary>
    private void RunSelection()
    {
        string text = EditorBox.SelectedText;
        if (text.Length == 0)
        {
            int line = EditorBox.GetLineIndexFromCharacterIndex(EditorBox.CaretIndex);
            if (line < 0)
            {
                return;
            }

            text = EditorBox.GetLineText(line).TrimEnd('\r', '\n');
            if (line + 1 < EditorBox.LineCount)
            {
                EditorBox.CaretIndex = EditorBox.GetCharacterIndexFromLineIndex(line + 1);
            }
        }

        if (ViewModel.RunEditorCommand.CanExecute(text))
        {
            ViewModel.RunEditorCommand.Execute(text);
        }
    }

    private async void See_Click(object sender, RoutedEventArgs e)
    {
        if (WordsList.SelectedItem is not ForthWord word)
        {
            MessageBox.Show(this, "Select a word in the dictionary first.", "MAX1000-FORTH",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Decompiled? d = await ViewModel.SeeAsync(word);
        if (d is not null)
        {
            new SeeWindow(d, this, source =>
            {
                string text = ViewModel.Editor.Text;
                ViewModel.Editor.Text = text + (text.Length == 0 || text.EndsWith('\n') ? "" : "\r\n") + source.Replace("\n", "\r\n") + "\r\n";
            }).Show();
        }
    }

    private void WordsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WordsList.SelectedItem is ForthWord word)
        {
            ViewModel.InsertWordCommand.Execute(word);
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }
    }

    string? IUserDialogs.OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    string? IUserDialogs.SaveFile(string title, string filter, string suggestedName)
    {
        string extension = Path.GetExtension(suggestedName);
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = filter, FileName = suggestedName, DefaultExt = extension.Length > 0 ? extension : ".fs",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    bool IUserDialogs.Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    bool? IUserDialogs.YesNoCancel(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
}
