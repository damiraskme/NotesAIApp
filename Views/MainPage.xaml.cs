using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using MyApp.Messages;
using MyApp.Models;
using MyApp.Models.Settings;
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

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Editor.AutoSaveDelaySeconds) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        Editor.PropertyChanged += EditorSettings_PropertyChanged;
        Loaded += MainPage_Loaded;

        ThemeTextBrush.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (s, dp) => OnThemeTextColorChanged());

        NoteTextBox.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(NoteTextBox_PointerWheelChanged), true);
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (LaunchOptions.IsNewWindow)
        {
            ViewModel.Tabs.Add(new NoteTab());
            ActivateTab(ViewModel.Tabs[0]);
            FocusEditor();
            return;
        }

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

    private EditorSettings Editor => ViewModel.Settings.Editor;

    private void EditorSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorSettings.AutoSave) when Editor.AutoSave:
                SaveNoteContent();
                break;
            case nameof(EditorSettings.AutoSave):
                _autoSaveTimer.Stop();
                break;
            case nameof(EditorSettings.AutoSaveDelaySeconds):
                _autoSaveTimer.Interval = TimeSpan.FromSeconds(Editor.AutoSaveDelaySeconds);
                break;
        }
    }

    private void NoteBoxControl(object sender, RoutedEventArgs e)
    {
        if (_isLoadingNote) return;

        UpdateToolbarState();
        UpdateDirtyState();

        if (!Editor.AutoSave || ActiveTab?.IsDirty != true) return;

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
            if (Editor.AutoSave && old.FilePath is not null && old.IsDirty)
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

    public void NewTab(string extension = ".rtf")
    {
        var tab = new NoteTab { DefaultExtension = extension };
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
        bool isClean;
        if (tab.Rtf is null)
        {
            string content = ViewModel.LoadNote(tab.FilePath) ?? string.Empty;
            NoteTextBox.Document.SetText(tab.IsPlainTextFile ? TextSetOptions.None : TextSetOptions.FormatRtf, content);
            if (content.Length == 0) ResetFormatting();
            isClean = true;
        }
        else
        {
            NoteTextBox.Document.SetText(TextSetOptions.FormatRtf, tab.Rtf);
            isClean = !tab.IsDirty;
        }

        ApplyThemeTextColor();
        if (isClean)
        {
            NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out string saved);
            tab.SavedRtf = saved;
        }
        NoteTextBox.Document.ClearUndoRedoHistory();
        NoteTextBox.Document.Selection.SetRange(tab.SelectionStart, tab.SelectionEnd);
        _isLoadingNote = false;

        UpdateDirtyState();
        UpdateToolbarState();
    }

    private static SolidColorBrush ThemeTextBrush => (SolidColorBrush)Application.Current.Resources["NoteTextBrush"];

    private void ApplyThemeTextColor()
    {
        var document = NoteTextBox.Document;
        Windows.UI.Color color = ThemeTextBrush.Color;

        ITextCharacterFormat defaultFormat = document.GetDefaultCharacterFormat();
        defaultFormat.ForegroundColor = color;
        document.SetDefaultCharacterFormat(defaultFormat);

        document.GetRange(0, StoryLength()).CharacterFormat.ForegroundColor = color;
        document.Selection.CharacterFormat.ForegroundColor = color;
    }

    private void OnThemeTextColorChanged()
    {
        if (ActiveTab is not NoteTab tab) return;

        UpdateDirtyState();
        bool wasClean = !tab.IsDirty;

        _isLoadingNote = true;
        ApplyThemeTextColor();
        if (wasClean)
        {
            NoteTextBox.Document.GetText(TextGetOptions.FormatRtf, out string saved);
            tab.SavedRtf = saved;
        }
        _isLoadingNote = false;

        UpdateDirtyState();
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
            DefaultFileExtension = tab.Extension,
        };
        foreach (var (name, extension) in SaveFileTypes.OrderBy(t => t.Extension == tab.Extension ? 0 : 1))
        {
            picker.FileTypeChoices.Add(name, new List<string> { extension });
        }

        PickFileResult? result = await picker.PickSaveFileAsync();
        FocusEditor();
        if (result is null) return false;

        tab.FilePath = result.Path;
        bool saved = WriteActiveTab();
        ViewModel.AddRecentFile(result.Path);
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

        await OpenPathAsync(result.Path);
    }

    public Task OpenPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            ViewModel.RemoveRecentFile(path);
            ShowInfo(InfoBarSeverity.Warning, $"File not found: {path}");
            return Task.CompletedTask;
        }

        ViewModel.AddRecentFile(path);

        NoteTab? existing = ViewModel.Tabs.FirstOrDefault(
            t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateTab(existing);
            FocusEditor();
            return Task.CompletedTask;
        }

        UpdateDirtyState();
        if (ActiveTab is { FilePath: null, IsDirty: false } blank)
        {
            blank.FilePath = path;
            blank.Rtf = null;
            LoadActiveTab();
        }
        else
        {
            var tab = new NoteTab { FilePath = path };
            ViewModel.Tabs.Add(tab);
            ActivateTab(tab);
        }

        ViewModel.SaveSession();
        FocusEditor();
        return Task.CompletedTask;
    }

    public async Task SaveAllAsync()
    {
        NoteTab? showing = ActiveTab;
        UpdateDirtyState();
        foreach (NoteTab tab in ViewModel.Tabs.Where(t => t.IsDirty).ToList())
        {
            ActivateTab(tab);
            if (!await SaveAsync()) break;
        }

        if (showing is not null && ViewModel.Tabs.Contains(showing)) ActivateTab(showing);
        FocusEditor();
    }

    public async Task CloseActiveTabAsync()
    {
        if (ActiveTab is NoteTab tab) await CloseTabAsync(tab);
    }

    private static readonly (string Name, string Extension)[] SaveFileTypes =
    {
        ("Rich Text", ".rtf"),
        ("Plain Text", ".txt"),
        ("Markdown", ".md"),
    };

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
            RequestedTheme = ActualTheme,
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
        UpdateEditState();
    }

    private static bool IsList(MarkerType type) => type is not (MarkerType.None or MarkerType.Undefined);

    private void UpdateEditState()
    {
        ViewModel.HasSelection = NoteTextBox.Document.Selection.Length != 0;
        ViewModel.CanUndo = NoteTextBox.Document.CanUndo();
    }

    public void Undo()
    {
        if (NoteTextBox.Document.CanUndo()) NoteTextBox.Document.Undo();
        FocusEditor();
    }

    public void Cut()
    {
        NoteTextBox.Document.Selection.Cut();
        FocusEditor();
    }

    public void Copy()
    {
        NoteTextBox.Document.Selection.Copy();
        FocusEditor();
    }

    public void Paste()
    {
        NoteTextBox.Document.Selection.Paste(0);
        FocusEditor();
    }

    public void Delete()
    {
        if (NoteTextBox.Document.Selection.Length != 0)
        {
            NoteTextBox.Document.Selection.Delete(TextRangeUnit.Character, 1);
        }
        FocusEditor();
    }

    public void SelectAll()
    {
        NoteTextBox.Document.Selection.SetRange(0, StoryLength());
        FocusEditor();
    }

    public void ClearFormatting()
    {
        var selection = NoteTextBox.Document.Selection;
        selection.CharacterFormat.Bold = FormatEffect.Off;
        selection.CharacterFormat.Italic = FormatEffect.Off;
        selection.CharacterFormat.Underline = UnderlineType.None;
        selection.CharacterFormat.Strikethrough = FormatEffect.Off;
        selection.ParagraphFormat.ListType = MarkerType.None;
        AfterFormatting();
    }

    public async Task DefineWithBingAsync()
    {
        var range = NoteTextBox.Document.Selection.GetClone();
        if (range.Length == 0) range.Expand(TextRangeUnit.Word);
        range.GetText(TextGetOptions.None, out string word);
        word = word.Trim();
        FocusEditor();
        if (word.Length == 0) return;

        await Launcher.LaunchUriAsync(new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString("define " + word)));
    }

    private int StoryLength()
    {
        NoteTextBox.Document.GetText(TextGetOptions.None, out string text);
        return text.Length;
    }


    public void ShowFind(bool replace)
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceToggle.IsChecked = replace;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        FindStatus.Visibility = Visibility.Collapsed;

        var selection = NoteTextBox.Document.Selection;
        if (selection.Length != 0)
        {
            selection.GetText(TextGetOptions.None, out string selected);
            if (!selected.Contains('\r')) FindBox.Text = selected;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
        });
    }

    public bool IsFindOpen => FindBar.Visibility == Visibility.Visible;

    public void CloseFind()
    {
        NoteTextBox.Focus(FocusState.Programmatic);
        FindBar.Visibility = Visibility.Collapsed;
        FocusEditor();
    }

    private void FindBar_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        CloseFind();
        e.Handled = true;
    }

    public void FindNext() => Find(forward: true);

    public void FindPrevious() => Find(forward: false);

    private FindOptions CurrentFindOptions => MatchCaseToggle.IsChecked == true ? FindOptions.Case : FindOptions.None;

    private bool Find(bool forward)
    {
        string query = FindBox.Text;
        if (query.Length == 0)
        {
            ShowFind(ReplaceToggle.IsChecked == true);
            return false;
        }

        var document = NoteTextBox.Document;
        var selection = document.Selection;
        int length = StoryLength();

        int start = forward ? selection.EndPosition : selection.StartPosition;
        var range = document.GetRange(start, start);
        if (range.FindText(query, forward ? length : -length, CurrentFindOptions) == 0)
        {
            int wrap = forward ? 0 : length;
            range = document.GetRange(wrap, wrap);
            if (range.FindText(query, forward ? length : -length, CurrentFindOptions) == 0)
            {
                SetFindStatus($"Cannot find \"{query}\"");
                return false;
            }
        }

        selection.SetRange(range.StartPosition, range.EndPosition);
        selection.ScrollIntoView(PointOptions.None);
        FindStatus.Visibility = Visibility.Collapsed;
        return true;
    }

    public void ReplaceOne()
    {
        string query = FindBox.Text;
        if (query.Length == 0) return;

        var selection = NoteTextBox.Document.Selection;
        selection.GetText(TextGetOptions.None, out string selected);
        var comparison = MatchCaseToggle.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (selection.Length != 0 && string.Equals(selected, query, comparison))
        {
            selection.SetText(TextSetOptions.None, ReplaceBox.Text);
            selection.Collapse(false);
        }

        FindNext();
    }

    public void ReplaceAll()
    {
        string query = FindBox.Text;
        if (query.Length == 0) return;

        var document = NoteTextBox.Document;
        int count = 0;
        document.BeginUndoGroup();
        var range = document.GetRange(0, 0);
        while (range.FindText(query, StoryLength(), CurrentFindOptions) != 0)
        {
            range.SetText(TextSetOptions.None, ReplaceBox.Text);
            range.Collapse(false);
            count++;
        }
        document.EndUndoGroup();

        SetFindStatus(count == 0 ? $"Cannot find \"{query}\"" : $"Replaced {count} occurrence{(count == 1 ? "" : "s")}");
    }

    private void SetFindStatus(string message)
    {
        FindStatus.Text = message;
        FindStatus.Visibility = Visibility.Visible;
    }

    private void ReplaceToggle_Click(object sender, RoutedEventArgs e)
    {
        ReplaceRow.Visibility = ReplaceToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FindBox.Focus(FocusState.Programmatic);
    }

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            Find(forward: !shift);
            e.Handled = true;
        }
    }

    private void ReplaceBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ReplaceOne();
            e.Handled = true;
        }
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => FindStatus.Visibility = Visibility.Collapsed;

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceOne();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    public async Task GoToLineAsync()
    {
        NoteTextBox.Document.GetText(TextGetOptions.None, out string text);
        string[] lines = text.TrimEnd('\r').Split('\r');
        int caret = NoteTextBox.Document.Selection.StartPosition;
        int currentLine = text[..Math.Min(caret, text.Length)].Count(c => c == '\r') + 1;

        var lineBox = new NumberBox
        {
            Header = $"Line number (1 - {lines.Length})",
            Minimum = 1,
            Maximum = lines.Length,
            Value = currentLine,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Go to line",
            Content = lineBox,
            PrimaryButtonText = "Go to",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };
        dialog.Opened += (s, e) => lineBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !double.IsNaN(lineBox.Value))
        {
            int line = Math.Clamp((int)lineBox.Value, 1, lines.Length);
            int position = lines.Take(line - 1).Sum(l => l.Length + 1);
            NoteTextBox.Document.Selection.SetRange(position, position);
            NoteTextBox.Document.Selection.ScrollIntoView(PointOptions.None);
        }
        FocusEditor();
    }


    private const double MinZoom = 0.1;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.1;
    private double _zoom = 1.0;

    public void ZoomIn() => SetZoom(_zoom + ZoomStep);

    public void ZoomOut() => SetZoom(_zoom - ZoomStep);

    public void ResetZoom() => SetZoom(1.0);

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(Math.Round(zoom, 1), MinZoom, MaxZoom);
        ApplyZoom();
        ZoomIndicator.Text = $"{_zoom * 100:0}%";
        ZoomIndicator.Visibility = _zoom == 1.0 ? Visibility.Collapsed : Visibility.Visible;
        FocusEditor();
    }

    private void ApplyZoom()
    {
        if (EditorHost.ActualWidth == 0 || EditorHost.ActualHeight == 0) return;

        EditorZoom.ScaleX = _zoom;
        EditorZoom.ScaleY = _zoom;
        NoteTextBox.Width = EditorHost.ActualWidth / _zoom;
        NoteTextBox.Height = EditorHost.ActualHeight / _zoom;
    }

    private void EditorHost_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    private void NoteTextBox_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        int delta = e.GetCurrentPoint(NoteTextBox).Properties.MouseWheelDelta;
        if (delta > 0) ZoomIn();
        else if (delta < 0) ZoomOut();
        e.Handled = true;
    }

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
