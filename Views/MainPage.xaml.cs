using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Messages;
using MyApp.Models;
using MyApp.Services;
using MyApp.ViewModels;
using Microsoft.Windows.Storage.Pickers;
using System.ComponentModel;

namespace MyApp;

public sealed partial class MainPage : Page
{
    public static MainPage? Current { get; private set; }

    public MainViewModel ViewModel { get; }

    private readonly DispatcherTimer _autoSaveTimer;
    private bool _isLoadingNote;

    private MarkerType _listType = MarkerType.Bullet;

    private NoteTab? ActiveTab => ViewModel.ActiveTab;

    public MainPage()
    {
        Current = this;
        ViewModel = MainWindow.Current.ViewModel;
        InitializeComponent();

        WeakReferenceMessenger.Default.Register<RequestSaveMessage>(this, async (recipient, message) =>
        {
            _autoSaveTimer?.Stop();
            await SaveAsync();
            FocusEditor();
        });

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += MainPage_Loaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        List<string> paths = ViewModel.LoadSession(out string? activePath);
        if (paths.Count == 0 && File.Exists(ViewModel.DefaultFilePath))
        {
            paths.Add(ViewModel.DefaultFilePath);
        }

        foreach (string path in paths)
        {
            ViewModel.Tabs.Add(new NoteTab { FilePath = path });
        }
        if (ViewModel.Tabs.Count == 0)
        {
            ViewModel.Tabs.Add(new NoteTab());
        }

        ActivateTab(ViewModel.Tabs.FirstOrDefault(t => t.FilePath == activePath) ?? ViewModel.Tabs[0]);
        FocusEditor();
    }

    public void FocusEditor()
    {
        DispatcherQueue.TryEnqueue(() => NoteTextBox.Focus(FocusState.Programmatic));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsAutoSaveEnabled)) return;

        if (ViewModel.IsAutoSaveEnabled)
        {
            SaveNoteContent();
        }
        else
        {
            _autoSaveTimer.Stop();
        }
    }

    private void NoteBoxControl(object sender, RoutedEventArgs e)
    {
        if (_isLoadingNote) return;

        UpdateToolbarState();
        UpdateDirtyState();

        if (!ViewModel.IsAutoSaveEnabled || ActiveTab?.IsDirty != true) return;

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveTimer_Tick(object? sender, object e)
    {
        _autoSaveTimer.Stop();
        SaveNoteContent();
    }

    public void SaveNoteContent()
    {
        if (ActiveTab?.FilePath is null) return;
        WriteActiveTab();
    }

    public void ActivateTab(NoteTab tab)
    {
        if (tab == ActiveTab) return;

        if (ActiveTab is NoteTab old)
        {
            _autoSaveTimer.Stop();
            UpdateDirtyState();
            if (ViewModel.IsAutoSaveEnabled && old.FilePath is not null && old.IsDirty)
            {
                WriteActiveTab();
            }
            NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            old.Rtf = rtf;
            old.SelectionStart = NoteTextBox.Document.Selection.StartPosition;
            old.SelectionEnd = NoteTextBox.Document.Selection.EndPosition;
        }

        ViewModel.ActiveTab = tab;
        LoadActiveTab();
        ViewModel.SaveSession();
        FocusEditor();
    }

    public void NewTab()
    {
        var tab = new NoteTab();
        ViewModel.Tabs.Add(tab);
        ActivateTab(tab);
        NoteTextBox.Focus(FocusState.Programmatic);
    }

    public async Task CloseTabAsync(NoteTab tab)
    {
        if (tab == ActiveTab) UpdateDirtyState();
        if (tab.IsDirty)
        {
            ActivateTab(tab);
            bool canClose = await ConfirmDiscardChangesAsync();
            FocusEditor();
            if (!canClose) return;
        }

        int index = ViewModel.Tabs.IndexOf(tab);
        ViewModel.Tabs.Remove(tab);

        if (tab == ActiveTab)
        {
            _autoSaveTimer.Stop();
            ViewModel.ActiveTab = null;
            if (ViewModel.Tabs.Count == 0)
            {
                ViewModel.Tabs.Add(new NoteTab());
            }
            ActivateTab(ViewModel.Tabs[Math.Min(index, ViewModel.Tabs.Count - 1)]);
        }

        ViewModel.SaveSession();
        FocusEditor();
    }

    public async Task<bool> ConfirmCloseAllAsync()
    {
        NoteTab? showing = ActiveTab;
        UpdateDirtyState();
        List<NoteTab> unsaved = ViewModel.Tabs.Where(t => t.IsDirty).ToList();

        for (int i = 0; i < unsaved.Count; i++)
        {
            ActivateTab(unsaved[i]);
            int othersLeft = unsaved.Count - i - 1;

            switch (await AskAboutUnsavedAsync(unsaved[i], othersLeft))
            {
                case UnsavedChoice.Cancel:
                    return false;
                case UnsavedChoice.Save:
                    if (!await SaveAsync()) return false;
                    break;
                case UnsavedChoice.DontSave:
                    break;
                case UnsavedChoice.SaveAll:
                    foreach (NoteTab tab in unsaved.Skip(i))
                    {
                        ActivateTab(tab);
                        if (!await SaveAsync()) return false;
                    }
                    i = unsaved.Count;
                    break;
                case UnsavedChoice.DontSaveAll:
                    i = unsaved.Count;
                    break;
            }
        }

        if (showing is not null) ActivateTab(showing);
        ViewModel.SaveSession();
        return true;
    }

    private void LoadActiveTab()
    {
        if (ActiveTab is not NoteTab tab) return;

        _isLoadingNote = true;
        if (tab.Rtf is null)
        {
            string content = ViewModel.LoadNote(tab.FilePath) ?? string.Empty;
            NoteTextBox.Document.SetText(tab.IsPlainTextFile ? TextSetOptions.None : TextSetOptions.FormatRtf, content);
            if (content.Length == 0) ResetFormatting();

            NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out string saved);
            tab.SavedRtf = saved;
        }
        else
        {
            NoteTextBox.Document.SetText(TextSetOptions.FormatRtf, tab.Rtf);
        }
        NoteTextBox.Document.ClearUndoRedoHistory();
        NoteTextBox.Document.Selection.SetRange(tab.SelectionStart, tab.SelectionEnd);
        _isLoadingNote = false;

        UpdateDirtyState();
        UpdateToolbarState();
    }

    private void ResetFormatting()
    {
        var selection = NoteTextBox.Document.Selection;
        selection.ParagraphFormat.ListType = MarkerType.None;
        selection.CharacterFormat.Bold = FormatEffect.Off;
        selection.CharacterFormat.Italic = FormatEffect.Off;
        selection.CharacterFormat.Underline = UnderlineType.None;
        selection.CharacterFormat.Strikethrough = FormatEffect.Off;
    }

    private void UpdateDirtyState()
    {
        if (ActiveTab is not NoteTab tab) return;

        NoteTextBox.Document.GetText(TextGetOptions.None, out string text);
        if (tab.FilePath is null && string.IsNullOrWhiteSpace(text))
        {
            tab.IsDirty = false;
            return;
        }

        NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
        tab.IsDirty = rtf != tab.SavedRtf;
    }

    public async Task<bool> SaveAsync()
    {
        if (ActiveTab?.FilePath is null) return await SaveAsAsync();
        return WriteActiveTab();
    }

    public async Task<bool> SaveAsAsync()
    {
        if (ActiveTab is not NoteTab tab) return false;

        var picker = new FileSavePicker(MainWindow.Current.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = tab.FilePath is null ? "Note" : Path.GetFileNameWithoutExtension(tab.FilePath),
            DefaultFileExtension = ".rtf",
        };
        picker.FileTypeChoices.Add("Rich Text", new List<string> { ".rtf" });
        picker.FileTypeChoices.Add("Plain Text", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("Markdown File", new List<string> { ".md " });

        PickFileResult? result = await picker.PickSaveFileAsync();
        FocusEditor();
        if (result is null) return false;

        tab.FilePath = result.Path;
        bool saved = WriteActiveTab();
        ViewModel.SaveSession();
        return saved;
    }

    public async Task OpenFileAsync()
    {
        var picker = new FileOpenPicker(MainWindow.Current.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".rtf");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".md");

        PickFileResult? result = await picker.PickSingleFileAsync();
        FocusEditor();
        if (result is null) return;

        NoteTab? existing = ViewModel.Tabs.FirstOrDefault(
            t => string.Equals(t.FilePath, result.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateTab(existing);
            return;
        }

        UpdateDirtyState();
        if (ActiveTab is { FilePath: null, IsDirty: false } blank)
        {
            blank.FilePath = result.Path;
            blank.Rtf = null;
            LoadActiveTab();
        }
        else
        {
            var tab = new NoteTab { FilePath = result.Path };
            ViewModel.Tabs.Add(tab);
            ActivateTab(tab);
        }

        ViewModel.SaveSession();
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        UpdateDirtyState();
        if (ActiveTab is not { IsDirty: true } tab) return true;

        return await AskAboutUnsavedAsync(tab, othersLeft: 0) switch
        {
            UnsavedChoice.Save => await SaveAsync(),
            UnsavedChoice.DontSave => true,
            _ => false,
        };
    }

    private enum UnsavedChoice { Save, DontSave, Cancel, SaveAll, DontSaveAll }

    private async Task<UnsavedChoice> AskAboutUnsavedAsync(NoteTab tab, int othersLeft)
    {
        string name = tab.FilePath is null ? "this note" : Path.GetFileName(tab.FilePath);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = $"Do you want to save changes to {name}?",
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Unsaved changes",
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        UnsavedChoice? allChoice = null;
        if (othersLeft > 0)
        {
            int total = othersLeft + 1;
            var saveAll = new Button { Content = $"Save all ({total})", HorizontalAlignment = HorizontalAlignment.Stretch };
            var dontSaveAll = new Button { Content = "Don't save any", HorizontalAlignment = HorizontalAlignment.Stretch };
            saveAll.Click += (_, _) => { allChoice = UnsavedChoice.SaveAll; dialog.Hide(); };
            dontSaveAll.Click += (_, _) => { allChoice = UnsavedChoice.DontSaveAll; dialog.Hide(); };

            var allRow = new Grid { ColumnSpacing = 8 };
            allRow.ColumnDefinitions.Add(new ColumnDefinition());
            allRow.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(dontSaveAll, 1);
            allRow.Children.Add(saveAll);
            allRow.Children.Add(dontSaveAll);

            content.Children.Add(new TextBlock
            {
                Text = $"{othersLeft} more unsaved {(othersLeft == 1 ? "note" : "notes")} after this one.",
                Opacity = 0.7,
            });
            content.Children.Add(allRow);
        }

        ContentDialogResult result = await dialog.ShowAsync();
        return allChoice ?? result switch
        {
            ContentDialogResult.Primary => UnsavedChoice.Save,
            ContentDialogResult.Secondary => UnsavedChoice.DontSave,
            _ => UnsavedChoice.Cancel,
        };
    }

    private bool WriteActiveTab()
    {
        if (ActiveTab is not { FilePath: string path } tab) return false;

        string content;
        if (tab.IsPlainTextFile)
        {
            NoteTextBox.Document.GetText(TextGetOptions.None, out string text);
            content = text.TrimEnd('\r').Replace("\r", Environment.NewLine);
        }
        else
        {
            NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out content);
        }

        if (!ViewModel.SaveNote(path, content))
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save {path}");
            return false;
        }

        NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out string saved);
        tab.SavedRtf = saved;
        tab.IsDirty = false;
        return true;
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        NoteTextBox.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;
        AfterFormatting();
    }

    private void ItalicButton_Click(object sender, RoutedEventArgs e)
    {
        NoteTextBox.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;
        AfterFormatting();
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        var format = NoteTextBox.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        AfterFormatting();
    }

    private void StrikethroughButton_Click(object sender, RoutedEventArgs e)
    {
        NoteTextBox.Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle;
        AfterFormatting();
    }

    private void ListButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyList(IsList(NoteTextBox.Document.Selection.ParagraphFormat.ListType) ? MarkerType.None : _listType);
    }

    private void ListStyleItem_Click(object sender, RoutedEventArgs e)
    {
        _listType = ReferenceEquals(sender, NumberedListItem) ? MarkerType.Arabic : MarkerType.Bullet;
        BulletListIcon.Visibility = _listType == MarkerType.Bullet ? Visibility.Visible : Visibility.Collapsed;
        NumberedListIcon.Visibility = _listType == MarkerType.Arabic ? Visibility.Visible : Visibility.Collapsed;

        ApplyList(_listType);
    }

    private void ApplyList(MarkerType type)
    {
        var paragraph = NoteTextBox.Document.Selection.ParagraphFormat;
        paragraph.ListType = type;
        if (type == MarkerType.Arabic)
        {
            paragraph.ListStyle = MarkerStyle.Period;
            paragraph.ListStart = 1;
        }
        AfterFormatting();
    }

    private void AfterFormatting()
    {
        UpdateToolbarState();
        NoteTextBox.Focus(FocusState.Programmatic);
    }

    private void NoteTextBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateToolbarState();

    private void UpdateToolbarState()
    {
        var character = NoteTextBox.Document.Selection.CharacterFormat;
        BoldButton.IsChecked = character.Bold == FormatEffect.On;
        ItalicButton.IsChecked = character.Italic == FormatEffect.On;
        UnderlineButton.IsChecked = character.Underline is not (UnderlineType.None or UnderlineType.Undefined);
        StrikethroughButton.IsChecked = character.Strikethrough == FormatEffect.On;
        ListButton.IsChecked = IsList(NoteTextBox.Document.Selection.ParagraphFormat.ListType);
    }

    private static bool IsList(MarkerType type) => type is not (MarkerType.None or MarkerType.Undefined);

    private async void PythonButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPythonBusy) return;
        SetPythonBusy(true);

        try
        {
            NoteTextBox.Document.GetText(TextGetOptions.None, out string text);
            PythonResult result = await PythonService.RunAsync("process", text.TrimEnd('\r'));

            if (result.Error is not null)
            {
                ShowInfo(InfoBarSeverity.Error, result.Error);
                return;
            }

            if (result.Text is not null)
            {
                NoteTextBox.Document.SetText(TextSetOptions.None, result.Text);
            }

            if (result.Message is not null)
            {
                ShowInfo(InfoBarSeverity.Informational, result.Message);
            }
        }
        finally
        {
            SetPythonBusy(false);
        }
    }

    private void SetPythonBusy(bool busy)
    {
        ViewModel.IsPythonBusy = busy;
        PythonButton.IsEnabled = !busy;
        PythonIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        PythonProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PythonProgress.IsActive = busy;
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        PythonInfoBar.Severity = severity;
        PythonInfoBar.Message = message;
        PythonInfoBar.IsOpen = true;
    }
}
