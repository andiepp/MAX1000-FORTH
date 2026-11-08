using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ZynqForth.Board;
using ZynqForth.Protocol;
using ZynqForth.Themes;

namespace ZynqForth.ViewModels;

/// <summary>One data stack item, top of stack first in the list.</summary>
public sealed record StackItem(int Position, short Signed, ushort Raw)
{
    public string Hex => "$" + Raw.ToString("X4");
}

public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] DefiningWords =
        [":", "create", "variable", "constant", "forget", "immediate", "marker"];

    private readonly Preferences preferences;
    private readonly DispatcherTimer flushTimer;
    private readonly DispatcherTimer statusTimer;
    private readonly DispatcherTimer boardTimer;
    private readonly DispatcherTimer afterLineTimer;
    private readonly Lock terminalLock = new();
    private readonly StringBuilder terminalPending = new();
    private readonly List<string> history = [];
    private readonly List<ForthWord> allWords = [];

    private ForthLink? link;
    private BoardClient? client;
    private string? selectedPort;
    private string selectedSkin;
    private bool showBoard;
    private bool showPattern;
    private bool isConnected;
    private int busyCount;
    private bool polling;
    private bool boardPolling;
    private bool refreshWordsAfterLine;
    private int historyIndex;
    private string inputLine = "";
    private string connectionText = "Not connected";
    private string statusText = "Choose the board's port and connect.";
    private bool statusIsError;
    private string forthState = "";
    private string sourceSummary = "";
    private bool sourceStored;
    private bool sourceAutostart;
    private string wordFilter = "";
    private string wordsSummary = "";
    private string stackSummary = "";
    private BoardState? board;
    private Max1000State? max1000Board;
    private BoardKind boardKind;
    private double progress;

    public MainViewModel(Preferences preferences)
    {
        this.preferences = preferences;
        selectedSkin = Skins.Normalize(preferences["skin"]);
        showBoard = preferences["board"] is { } b ? b == "on" : Skins.ShowsBoard(selectedSkin);
        // the board type of the last connection, so the board view does not jump at the next one
        boardKind = preferences["boardkind"] == "max1000" ? BoardKind.Max1000 : BoardKind.Zynq;
        showPattern = preferences["editor"] == "pattern" && boardKind == BoardKind.Zynq;

        Editor.Message += SetStatus;
        Patterns.Message += SetStatus;
        Words = CollectionViewSource.GetDefaultView(allWords);
        Words.Filter = item => item is ForthWord word &&
                               (WordFilter.Length == 0 || word.Name.Contains(WordFilter, StringComparison.OrdinalIgnoreCase));

        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectCommand = new RelayCommand(() => _ = ToggleConnectionAsync(), () => IsConnected || SelectedPort is not null);
        RestartCommand = new RelayCommand(p => _ = RestartAsync(p as string == "skip"), _ => CanTalk);
        SendInputCommand = new RelayCommand(SendInput, () => IsConnected);
        RunEditorCommand = new RelayCommand(p => _ = RunTextAsync(p as string ?? Editor.Text), _ => CanTalk);
        RefreshWordsCommand = new RelayCommand(() => _ = RefreshWordsAsync(), () => CanTalk);
        RefreshStackCommand = new RelayCommand(() => _ = RefreshStackAsync(), () => CanTalk);
        StoreSourceCommand = new RelayCommand(() => _ = StoreSourceAsync(), () => CanTalk);
        LoadSourceCommand = new RelayCommand(() => _ = LoadSourceAsync(), () => CanTalk && SourceStored);
        EraseSourceCommand = new RelayCommand(() => _ = EraseSourceAsync(), () => CanTalk && SourceStored);
        InsertWordCommand = new RelayCommand(p => { if (p is ForthWord w) { InputLine = (InputLine + " " + w.Name).TrimStart(); } });
        PatternLoadCommand = new RelayCommand(() => { if (Patterns.ToForth(start: true) is { } forth) { _ = RunTextAsync(forth); } },
                                              () => CanTalk && !Patterns.HasErrors);
        PatternStartCommand = new RelayCommand(() => client?.SendLine("pg-start"), () => IsConnected);
        PatternStopCommand = new RelayCommand(() => client?.SendLine("pg-stop"), () => IsConnected);
        PatternToEditorCommand = new RelayCommand(PatternToEditor, () => !Patterns.HasErrors);

        flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => FlushTerminal(),
                                         Dispatcher.CurrentDispatcher);
        statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => _ = PollStatusAsync(),
                                          Dispatcher.CurrentDispatcher) { IsEnabled = false };
        boardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _ = PollBoardAsync(),
                                         Dispatcher.CurrentDispatcher) { IsEnabled = false };
        afterLineTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => AfterLine(),
                                             Dispatcher.CurrentDispatcher) { IsEnabled = false };

        RefreshPorts();
        string? port = preferences["port"];
        SelectedPort = port is not null && Ports.Contains(port) ? port : Ports.LastOrDefault();
    }

    /// <summary>Forth's output, cleaned for display, on the UI thread.</summary>
    public event Action<string>? TerminalText;

    public EditorViewModel Editor { get; } = new();

    public PatternEditorViewModel Patterns { get; } = new();

    public ObservableCollection<string> Ports { get; } = [];

    public IReadOnlyList<string> SkinNames => Skins.Names;

    public ICollectionView Words { get; }

    public ObservableCollection<StackItem> Stack { get; } = [];

    public RelayCommand RefreshPortsCommand { get; }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand RestartCommand { get; }

    public RelayCommand SendInputCommand { get; }

    public RelayCommand RunEditorCommand { get; }

    public RelayCommand RefreshWordsCommand { get; }

    public RelayCommand RefreshStackCommand { get; }

    public RelayCommand StoreSourceCommand { get; }

    public RelayCommand LoadSourceCommand { get; }

    public RelayCommand EraseSourceCommand { get; }

    public RelayCommand InsertWordCommand { get; }

    public RelayCommand PatternLoadCommand { get; }

    public RelayCommand PatternStartCommand { get; }

    public RelayCommand PatternStopCommand { get; }

    public RelayCommand PatternToEditorCommand { get; }

    public IUserDialogs? Dialogs
    {
        get => Editor.Dialogs;
        set
        {
            Editor.Dialogs = value;
            Patterns.Dialogs = value;
        }
    }

    public string? SelectedPort
    {
        get => selectedPort;
        set => Set(ref selectedPort, value);
    }

    public string SelectedSkin
    {
        get => selectedSkin;
        set
        {
            value = Skins.Normalize(value);
            if (!Set(ref selectedSkin, value))
            {
                return;
            }

            preferences["skin"] = value;
            if (Application.Current is { } application)
            {
                Skins.Apply(application, value);
            }

            if (Skins.ShowsBoard(value))
            {
                ShowBoard = true;
            }
        }
    }

    public bool ShowBoard
    {
        get => showBoard;
        set
        {
            if (Set(ref showBoard, value))
            {
                preferences["board"] = value ? "on" : "off";
                UpdateBoardTimer();
            }
        }
    }

    /// <summary>The editor area shows the pattern editor instead of the source.</summary>
    public bool ShowPattern
    {
        get => showPattern;
        set
        {
            if (Set(ref showPattern, value))
            {
                preferences["editor"] = value ? "pattern" : "source";
                OnPropertyChanged(nameof(ShowSource));
                UpdateBoardTimer();
            }
        }
    }

    public bool ShowSource
    {
        get => !showPattern;
        set => ShowPattern = !value;
    }

    /// <summary>The board is read while it is shown, and for the pattern editor's playing step.</summary>
    private void UpdateBoardTimer() => boardTimer.IsEnabled = (ShowBoard || ShowPattern) && IsConnected;

    /// <summary>The board of the current (or last) connection is a Trenz Electronic MAX1000 (MAX1000-FORTH).</summary>
    public bool IsMax1000 => boardKind == BoardKind.Max1000;

    /// <summary>The board is an Arty Z7-20: board view and pattern generator of ZYNQ-FORTH.</summary>
    public bool IsZynqBoard => boardKind == BoardKind.Zynq;

    private void SetBoardKind(BoardKind kind)
    {
        if (boardKind == kind)
        {
            return;
        }

        boardKind = kind;
        preferences["boardkind"] = kind == BoardKind.Max1000 ? "max1000" : "zynq";
        OnPropertyChanged(nameof(IsMax1000));
        OnPropertyChanged(nameof(IsZynqBoard));
        if (kind == BoardKind.Max1000)
        {
            ShowPattern = false;     // the MAX1000 has no pattern generator
        }
    }

    public bool IsConnected
    {
        get => isConnected;
        private set
        {
            if (Set(ref isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectLabel));
                OnPropertyChanged(nameof(CanTalk));
            }
        }
    }

    /// <summary>Connected and no long operation (store, run) running.</summary>
    public bool CanTalk => IsConnected && busyCount == 0;

    public string ConnectLabel => IsConnected ? "Disconnect" : "Connect";

    public string ConnectionText
    {
        get => connectionText;
        private set => Set(ref connectionText, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    public bool StatusIsError
    {
        get => statusIsError;
        private set => Set(ref statusIsError, value);
    }

    public string ForthState
    {
        get => forthState;
        private set => Set(ref forthState, value);
    }

    public string InputLine
    {
        get => inputLine;
        set => Set(ref inputLine, value);
    }

    public string SourceSummary
    {
        get => sourceSummary;
        private set => Set(ref sourceSummary, value);
    }

    public bool SourceStored
    {
        get => sourceStored;
        private set => Set(ref sourceStored, value);
    }

    /// <summary>The saved source's autostart flag; setting it writes the flash.</summary>
    public bool SourceAutostart
    {
        get => sourceAutostart;
        set
        {
            if (sourceAutostart == value)
            {
                return;
            }

            sourceAutostart = value;
            OnPropertyChanged();
            _ = SetAutostartAsync(value);
        }
    }

    /// <summary>Autostart for the next Store (when no source is stored yet).</summary>
    public bool StoreWithAutostart { get; set; } = true;

    public string WordFilter
    {
        get => wordFilter;
        set
        {
            if (Set(ref wordFilter, value ?? ""))
            {
                Words.Refresh();
            }
        }
    }

    public string WordsSummary
    {
        get => wordsSummary;
        private set => Set(ref wordsSummary, value);
    }

    public string StackSummary
    {
        get => stackSummary;
        private set => Set(ref stackSummary, value);
    }

    public BoardState? Board
    {
        get => board;
        private set => Set(ref board, value);
    }

    /// <summary>The MAX1000's I/O state (null for an Arty Z7 or when not connected).</summary>
    public Max1000State? Max1000Board
    {
        get => max1000Board;
        private set => Set(ref max1000Board, value);
    }

    /// <summary>0..1 while a source is sent, else 0.</summary>
    public double Progress
    {
        get => progress;
        private set => Set(ref progress, value);
    }

    // ---------------------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------------------

    public async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            Disconnect("Disconnected.");
            return;
        }

        if (SelectedPort is not { } port)
        {
            return;
        }

        var newLink = new ForthLink(new SerialTransport(port));
        newLink.TerminalReceived += OnTerminalBytes;
        newLink.Faulted += exception => Application.Current?.Dispatcher.BeginInvoke(() =>
            Disconnect($"Connection lost: {exception.Message}", error: true));
        try
        {
            newLink.Open();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            newLink.Dispose();
            SetStatus($"Cannot open {port}: {exception.Message}", true);
            return;
        }

        link = newLink;
        client = new BoardClient(newLink);
        IsConnected = true;
        flushTimer.Start();
        try
        {
            BoardVersion version = await client.VersionAsync();
            preferences["port"] = port;
            SetBoardKind(version.Kind);
            if (version.Kind == BoardKind.Max1000)
            {
                client.InputWindow = 4 * 1024;
                client.RestartByCtrlR = true;
            }

            ConnectionText = $"{port} · {version.Product} firmware {version.Firmware} · kernel image {version.KernelWords} cells";
            SetStatus("Connected. Type Forth below, or run the editor's text with F5.", false);
            client.SendLine("");    // Forth answers with its prompt
            statusTimer.Start();
            UpdateBoardTimer();
            await PollStatusAsync();
            await RefreshWordsAsync();
            await RefreshStackAsync();
        }
        catch (Exception exception) when (exception is LinkException or BoardRefusedException)
        {
            Disconnect($"No answer on {port}. Is the board running MAX1000-FORTH?", error: true);
        }
    }

    private void Disconnect(string message, bool error = false)
    {
        statusTimer.Stop();
        boardTimer.Stop();
        afterLineTimer.Stop();
        link?.Dispose();
        link = null;
        client = null;
        IsConnected = false;
        busyCount = 0;
        ConnectionText = "Not connected";
        ForthState = "";
        Board = null;
        Max1000Board = null;
        Patterns.PlayingStep = null;
        FlushTerminal();
        SetStatus(message, error);
    }

    private void RefreshPorts()
    {
        string? keep = SelectedPort;
        Ports.Clear();
        foreach (string name in SerialTransport.PortNames())
        {
            Ports.Add(name);
        }

        SelectedPort = keep is not null && Ports.Contains(keep) ? keep : Ports.LastOrDefault();
    }

    // ---------------------------------------------------------------------------------
    // Terminal
    // ---------------------------------------------------------------------------------

    private void OnTerminalBytes(ReadOnlyMemory<byte> bytes)
    {
        lock (terminalLock)
        {
            foreach (byte b in bytes.Span)
            {
                switch (b)
                {
                    case (byte)'\n':
                    case (byte)'\t':
                        terminalPending.Append((char)b);
                        break;
                    case 8 or 127:
                        if (terminalPending.Length > 0)
                        {
                            terminalPending.Length--;
                        }

                        break;
                    case >= 32 and < 127:
                        terminalPending.Append((char)b);
                        break;
                    default:
                        break;      // CR and other control characters
                }
            }
        }
    }

    private void FlushTerminal()
    {
        string text;
        lock (terminalLock)
        {
            if (terminalPending.Length == 0)
            {
                return;
            }

            text = terminalPending.ToString();
            terminalPending.Clear();
        }

        TerminalText?.Invoke(text);
    }

    private void SendInput()
    {
        if (client is null)
        {
            return;
        }

        string line = InputLine;
        if (line.Length > 0 && (history.Count == 0 || history[^1] != line))
        {
            history.Add(line);
        }

        historyIndex = history.Count;
        InputLine = "";
        client.SendLine(line);
        string[] tokens = line.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        refreshWordsAfterLine |= tokens.Any(t => DefiningWords.Contains(t));
        afterLineTimer.Stop();
        afterLineTimer.Start();
    }

    /// <summary>Steps through the typed lines (Up = -1, Down = +1).</summary>
    public void History(int step)
    {
        if (history.Count == 0)
        {
            return;
        }

        historyIndex = Math.Clamp(historyIndex + step, 0, history.Count);
        InputLine = historyIndex < history.Count ? history[historyIndex] : "";
    }

    private void AfterLine()
    {
        afterLineTimer.Stop();
        _ = RefreshStackAsync();
        if (refreshWordsAfterLine)
        {
            refreshWordsAfterLine = false;
            _ = RefreshWordsAsync();
        }
    }

    // ---------------------------------------------------------------------------------
    // Requests
    // ---------------------------------------------------------------------------------

    private async Task RestartAsync(bool skipAutostart)
    {
        await Guard(async c =>
        {
            await c.RestartAsync(skipAutostart);
            SetStatus(skipAutostart ? "Forth restarted without the saved source." : "Forth restarted.", false);
            await Task.Delay(400);
            await WaitForInputAsync(c);
            await RefreshAllAsync(c);
        });
    }

    private async Task RunTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await Guard(async c =>
        {
            int lines = text.Split('\n').Length;
            SetStatus($"Sending {lines} line(s) to Forth…", false);
            await c.SendTextAsync(text, new Progress<double>(p => Progress = p));
            await WaitForInputAsync(c);
            Progress = 0;
            SetStatus($"Sent {lines} line(s).", false);
            await RefreshAllAsync(c);
        }, longRunning: true);
        Progress = 0;
    }

    private async Task StoreSourceAsync()
    {
        if (Dialogs is not null && SourceStored &&
            !Dialogs.Confirm("Store source", $"Replace the source saved on the board ({SourceSummary}) with the editor's text?"))
        {
            return;
        }

        bool autostart = SourceStored ? SourceAutostart : StoreWithAutostart;
        await Guard(async c =>
        {
            SetStatus("Writing the source to the board's flash…", false);
            await c.StoreAsync(Editor.Text, autostart);
            SetStatus("Source saved on the board" + (autostart ? "; it runs at every start." : "."), false);
            await PollStatusCoreAsync(c);
        }, longRunning: true);
    }

    private async Task LoadSourceAsync()
    {
        if (!Editor.ConfirmDiscard())
        {
            return;
        }

        await Guard(async c =>
        {
            string? source = await c.SourceAsync();
            if (source is null)
            {
                SetStatus("No source is saved on the board.", true);
                return;
            }

            Editor.Load(source, null);
            SetStatus("Loaded the board's saved source into the editor.", false);
        });
    }

    private async Task EraseSourceAsync()
    {
        if (Dialogs is not null && !Dialogs.Confirm("Erase source", "Erase the source saved on the board?"))
        {
            return;
        }

        await Guard(async c =>
        {
            await c.EraseAsync();
            SetStatus("The board's saved source is erased.", false);
            await PollStatusCoreAsync(c);
        }, longRunning: true);
    }

    private async Task SetAutostartAsync(bool on)
    {
        await Guard(async c =>
        {
            await c.AutostartAsync(on);
            SetStatus(on ? "Autostart on: the saved source runs at every start." : "Autostart off.", false);
            await PollStatusCoreAsync(c);
        }, longRunning: true);
    }

    /// <summary>
    /// Decompiles a word: reads the dictionary again (addresses change as words are added),
    /// then the word's code with the <c>mem</c> request (firmware 1.4 and later).
    /// </summary>
    /// <summary>A message in the status line, from the window (help, See).</summary>
    public void ShowMessage(string text, bool error = false) => SetStatus(text, error);

    public async Task<Decompiled?> SeeAsync(ForthWord word)
    {
        Decompiled? result = null;
        await Guard(async c =>
        {
            WordList list = await c.WordsAsync();
            ForthWord? current = list.Words.FirstOrDefault(w => w.Name == word.Name && !w.Hidden);
            if (current is null)
            {
                SetStatus($"{word.Name} is no longer in the dictionary.", true);
                return;
            }

            try
            {
                result = await Decompiler.SeeAsync(c, list, current);
                SetStatus($"{word.Name}: {(Decompiler.CodeEnd(list, current) - current.CodeAddress) / 2} cells decompiled.", false);
            }
            catch (BoardRefusedException e) when (e.Reason == "unknown")
            {
                SetStatus("See needs MAX1000-FORTH firmware 1.4 or later on the board.", true);
            }
        });
        return result;
    }

    private Task RefreshWordsAsync() => Guard(RefreshWordsCoreAsync);

    private Task RefreshStackAsync() => Guard(RefreshStackCoreAsync);

    private async Task RefreshAllAsync(BoardClient c)
    {
        await PollStatusCoreAsync(c);
        await RefreshWordsCoreAsync(c);
        await RefreshStackCoreAsync(c);
    }

    private async Task RefreshWordsCoreAsync(BoardClient c)
    {
        WordList list = await c.WordsAsync();
        allWords.Clear();
        allWords.AddRange(list.Words.Where(w => !w.Hidden));
        Words.Refresh();
        WordsSummary = $"{allWords.Count} words · HERE ${list.Here:X4}";
    }

    private async Task RefreshStackCoreAsync(BoardClient c)
    {
        DataStack stack;
        try
        {
            stack = await c.StackAsync();
        }
        catch (BoardRefusedException)
        {
            return;     // busy: Forth is between two lines; the next refresh catches it
        }

        Stack.Clear();
        for (int i = stack.Items.Count - 1; i >= 0; i--)
        {
            ushort raw = stack.Items[i];
            Stack.Add(new StackItem(stack.Items.Count - 1 - i, unchecked((short)raw), raw));
        }

        StackSummary = stack.Items.Count == 0 ? "empty" : $"depth {stack.Items.Count}, top first";
    }

    private async Task PollStatusAsync()
    {
        if (polling || client is null || busyCount > 0)
        {
            return;
        }

        polling = true;
        try
        {
            await PollStatusCoreAsync(client);
        }
        catch (Exception exception) when (exception is LinkException or BoardRefusedException)
        {
            ForthState = "no answer";
        }
        finally
        {
            polling = false;
        }
    }

    private async Task PollStatusCoreAsync(BoardClient c)
    {
        BoardStatus status = await c.StatusAsync();
        ForthState = (status.Running ? "running" : "held") +
                     (status.InputPending > 0 ? $" · {status.InputPending} bytes queued" : "") +
                     $" · starts {status.Starts}";
        SourceStored = status.Source == SourceState.Stored;
        sourceAutostart = status.Autostart;
        OnPropertyChanged(nameof(SourceAutostart));
        SourceSummary = status.Source switch
        {
            SourceState.Stored => $"{status.SourceLength} bytes, CRC {status.SourceCrc:X8}",
            SourceState.Damaged => "damaged (CRC mismatch)",
            _ => "none",
        };
    }

    private async Task PollBoardAsync()
    {
        if (boardPolling || client is null || busyCount > 0)
        {
            return;
        }

        boardPolling = true;
        try
        {
            if (IsMax1000)
            {
                Max1000Board = await client.Max1000BoardAsync();
                return;
            }

            Board = await client.BoardAsync();
            Patterns.PlayingStep = Board.Pattern is { Running: true } pg ? pg.Step : null;
        }
        catch (Exception exception) when (exception is LinkException or BoardRefusedException or FormatException)
        {
        }
        finally
        {
            boardPolling = false;
        }
    }

    /// <summary>Waits until Forth has read everything queued for it (at most a minute).</summary>
    private static async Task WaitForInputAsync(BoardClient c)
    {
        for (int i = 0; i < 600; i++)
        {
            if ((await c.StatusAsync()).InputPending == 0)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>Runs a request sequence, reporting failures in the status line.</summary>
    private async Task Guard(Func<BoardClient, Task> action, bool longRunning = false)
    {
        if (client is not { } c)
        {
            return;
        }

        if (longRunning)
        {
            busyCount++;
            OnPropertyChanged(nameof(CanTalk));
        }

        try
        {
            await action(c);
        }
        catch (Exception exception) when (exception is LinkException or BoardRefusedException or ArgumentException)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            if (longRunning && busyCount > 0)
            {
                busyCount--;
                OnPropertyChanged(nameof(CanTalk));
            }

            CommandManagerInvalidate();
        }
    }

    /// <summary>The pattern's Forth into the source editor (appended when it holds text).</summary>
    private void PatternToEditor()
    {
        if (Patterns.ToForth(start: true) is not { } forth)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            Editor.Load(forth, null);
        }
        else
        {
            Editor.Text = Editor.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + forth;
        }

        ShowPattern = false;
        SetStatus("The pattern is in the source editor as Forth: Run all loads it, Store editor saves it on the board.", false);
    }

    private void SetStatus(string message, bool error)
    {
        StatusText = message;
        StatusIsError = error;
    }

    private static void CommandManagerInvalidate() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    public void Shutdown()
    {
        if (IsConnected)
        {
            Disconnect("Closed.");
        }
    }
}
