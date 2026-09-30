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
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MyApp;

public sealed partial class MainPage : Page
{
    public static MainPage? Current { get; private set; }

    public MainViewModel ViewModel { get; }

    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _dirtyCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _isLoadingNote;

    private MarkerType _listType = MarkerType.Bullet;

    private EditorMode _mode = EditorMode.RichText;

    private readonly record struct MarkdownState(string Text, int SelectionStart, int SelectionEnd);
    private readonly Stack<MarkdownState> _markdownUndo = new();
    private readonly Stack<MarkdownState> _markdownRedo = new();
    private readonly DispatcherTimer _markdownTypingTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private MarkdownState _markdownCurrent = new(string.Empty, 0, 0);
    private bool _markdownTyping;
    private bool _isHighlighting;
    private FontFamily? _richTextFont;
    private static readonly FontFamily MarkdownFont = new("Cascadia Mono, Consolas");
    private static readonly Regex MarkdownListPrefix = new(@"^(\s*)([-*+] |\d+\. )");

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
        _dirtyCheckTimer.Tick += (s, e) =>
        {
            _dirtyCheckTimer.Stop();
            UpdateDirtyState();
            if (!Editor.AutoSave || ActiveTab?.IsDirty != true) return;

            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        };

        Editor.PropertyChanged += EditorSettings_PropertyChanged;
        Loaded += MainPage_Loaded;

        ThemeTextBrush.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (s, dp) => OnThemeTextColorChanged());
        foreach (SolidColorBrush brush in MarkupBrushes.Values)
        {
            brush.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (s, dp) => RefreshMarkdownHighlight());
        }
        _markdownTypingTimer.Tick += (s, e) =>
        {
            _markdownTypingTimer.Stop();
            _markdownTyping = false;
        };

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
        if (MainWindow.Current?.IsSettingsOpen == true) return;
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
        if (_isLoadingNote || _isHighlighting) return;
        if (_mode == EditorMode.Markdown) OnMarkdownTextChanged();

        UpdateToolbarState();
        _dirtyCheckTimer.Stop();
        _dirtyCheckTimer.Start();
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

    public void NewTab(string extension = TextPackService.Extension)
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

    private void LoadTextPack(NoteTab tab)
    {
        try
        {
            TextPackContent content = TextPackService.Load(tab.FilePath!);
            double maxWidth = Math.Max(48, NoteTextBox.ActualWidth - NoteTextBox.Padding.Left - NoteTextBox.Padding.Right - 24);
            tab.KnownAssets = RichTextMarkdown.Load(NoteTextBox.Document, content.Markdown, content.Assets, maxWidth, XamlRoot?.RasterizationScale ?? 1.0);
            tab.OriginalAssets = content.Assets;
        }
        catch (Exception ex)
        {
            NoteTextBox.Document.SetText(TextSetOptions.None, string.Empty);
            ShowInfo(InfoBarSeverity.Error, $"Could not open {Path.GetFileName(tab.FilePath)}: {ex.Message}");
        }
        if (StoryLength() <= 1) ResetFormatting();
    }

    private bool WriteTextPack(NoteTab tab, string path)
    {
        try
        {
            TextPackContent content = RichTextMarkdown.Save(NoteTextBox.Document, tab.KnownAssets, tab.OriginalAssets);
            TextPackService.Save(path, content);
            tab.OriginalAssets = content.Assets;
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save {path}: {ex.Message}");
            return false;
        }

        tab.SavedSnapshot = DocumentSnapshot(tab);
        tab.IsDirty = false;
        return true;
    }

    private void LoadActiveTab()
    {
        if (ActiveTab is not NoteTab tab) return;

        _isLoadingNote = true;
        ApplyEditorMode(tab.Mode);
        bool isClean;
        if (tab.Rtf is null && tab.IsTextPack && tab.FilePath is not null && File.Exists(tab.FilePath))
        {
            LoadTextPack(tab);
            isClean = true;
        }
        else if (tab.Rtf is null)
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
            tab.SavedSnapshot = DocumentSnapshot(tab);
        }
        if (tab.IsTextPack) ViewModel.StatusFileType = "Note";
        ResetMarkdownHistory();
        NoteTextBox.Document.ClearUndoRedoHistory();
        NoteTextBox.Document.Selection.SetRange(tab.SelectionStart, tab.SelectionEnd);
        _isLoadingNote = false;

        UpdateDirtyState();
        UpdateToolbarState();
    }

    private static SolidColorBrush ThemeTextBrush => (SolidColorBrush)Application.Current.Resources["NoteTextBrush"];

    private static readonly Dictionary<MarkupKind, SolidColorBrush> MarkupBrushes = new()
    {
        [MarkupKind.Syntax] = (SolidColorBrush)Application.Current.Resources["NoteMarkupBrush"],
        [MarkupKind.Bold] = (SolidColorBrush)Application.Current.Resources["NoteBoldMarkupBrush"],
        [MarkupKind.Italic] = (SolidColorBrush)Application.Current.Resources["NoteItalicMarkupBrush"],
        [MarkupKind.Strikethrough] = (SolidColorBrush)Application.Current.Resources["NoteStrikeMarkupBrush"],
    };

    private string DocumentSnapshot(NoteTab tab)
    {
        var options = tab.Mode == EditorMode.RichText ? TextGetOptions.FormatRtf : TextGetOptions.None;
        NoteTextBox.Document.GetText(options, out string snapshot);
        return snapshot;
    }

    private string PlainText()
    {
        NoteTextBox.Document.GetText(TextGetOptions.None, out string text);
        return text;
    }

    private void HighlightMarkdown()
    {
        var document = NoteTextBox.Document;
        string text = PlainText();

        _isHighlighting = true;
        document.BatchDisplayUpdates();
        document.GetRange(0, text.Length).CharacterFormat.ForegroundColor = ThemeTextBrush.Color;
        foreach (MarkupSpan span in MarkdownSyntax.FindMarkup(text))
        {
            document.GetRange(span.Start, span.Start + span.Length).CharacterFormat.ForegroundColor = MarkupBrushes[span.Kind].Color;
        }
        document.ApplyDisplayUpdates();
        document.ClearUndoRedoHistory();
        _isHighlighting = false;
    }

    private void RefreshMarkdownHighlight()
    {
        if (_mode != EditorMode.Markdown || ActiveTab is null) return;

        _isLoadingNote = true;
        HighlightMarkdown();
        _isLoadingNote = false;
    }

    private MarkdownState CaptureMarkdownState(string text) =>
        new(text, NoteTextBox.Document.Selection.StartPosition, NoteTextBox.Document.Selection.EndPosition);

    private void ResetMarkdownHistory()
    {
        _markdownUndo.Clear();
        _markdownRedo.Clear();
        _markdownTyping = false;
        _markdownTypingTimer.Stop();
        _markdownCurrent = CaptureMarkdownState(PlainText());
        if (_mode == EditorMode.Markdown) HighlightMarkdown();
    }

    private void OnMarkdownTextChanged()
    {
        string text = PlainText();
        if (text == _markdownCurrent.Text) return;

        if (!_markdownTyping)
        {
            _markdownUndo.Push(_markdownCurrent);
            _markdownRedo.Clear();
            _markdownTyping = true;
        }
        _markdownTypingTimer.Stop();
        _markdownTypingTimer.Start();

        _markdownCurrent = CaptureMarkdownState(text);
        HighlightMarkdown();
    }

    private void BeginMarkdownEdit()
    {
        _markdownTyping = false;
        _markdownTypingTimer.Stop();
    }

    private void RestoreMarkdownState(MarkdownState state)
    {
        _markdownCurrent = state;
        BeginMarkdownEdit();

        _isLoadingNote = true;
        NoteTextBox.Document.SetText(TextSetOptions.None, state.Text.EndsWith('\r') ? state.Text[..^1] : state.Text);
        ApplyThemeTextColor();
        HighlightMarkdown();
        NoteTextBox.Document.Selection.SetRange(state.SelectionStart, state.SelectionEnd);
        _isLoadingNote = false;

        _markdownCurrent = CaptureMarkdownState(PlainText());
        UpdateDirtyState();
        UpdateToolbarState();
    }

    public bool IsMarkdownMode => _mode == EditorMode.Markdown;

    public void Redo()
    {
        if (_mode == EditorMode.Markdown)
        {
            if (_markdownRedo.Count > 0)
            {
                _markdownUndo.Push(CaptureMarkdownState(PlainText()));
                RestoreMarkdownState(_markdownRedo.Pop());
            }
        }
        else if (NoteTextBox.Document.CanRedo())
        {
            NoteTextBox.Document.Redo();
        }
        FocusEditor();
    }

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
        if (_mode == EditorMode.Markdown) HighlightMarkdown();
        if (wasClean) tab.SavedSnapshot = DocumentSnapshot(tab);
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

        tab.IsDirty = DocumentSnapshot(tab) != tab.SavedSnapshot;
    }

    public async Task<bool> SaveAsync()
    {
        if (ActiveTab?.FilePath is null) return await SaveAsAsync();
        return WriteActiveTab();
    }

    private string? DefaultSaveFolder =>
        Editor.DefaultSaveFolder is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    public async Task<bool> SaveAsAsync()
    {
        if (ActiveTab is not NoteTab tab) return false;

        var picker = new FileSavePicker(MainWindow.Current.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = tab.FilePath is null ? "Note" : Path.GetFileNameWithoutExtension(tab.FilePath),
            DefaultFileExtension = tab.Extension,
        };
        if (DefaultSaveFolder is string saveFolder) picker.SuggestedFolder = saveFolder;
        foreach (var (name, extension) in SaveFileTypes.OrderBy(t => t.Extension == tab.Extension ? 0 : 1))
        {
            picker.FileTypeChoices.Add(name, new List<string> { extension });
        }

        PickFileResult? result = await picker.PickSaveFileAsync();
        FocusEditor();
        if (result is null) return false;

        EditorMode previousMode = tab.Mode;
        bool wasTextPack = tab.IsTextPack;
        tab.FilePath = result.Path;
        bool saved = WriteActiveTab();
        if (saved && (tab.Mode != previousMode || tab.IsTextPack != wasTextPack))
        {
            tab.Rtf = null;
            LoadActiveTab();
        }
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
        if (DefaultSaveFolder is string openFolder) picker.SuggestedStartFolder = openFolder;
        picker.FileTypeFilter.Add(TextPackService.Extension);
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
        ("Note (TextPack)", TextPackService.Extension),
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

        if (tab.IsTextPack) return WriteTextPack(tab, path);

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

        tab.SavedSnapshot = DocumentSnapshot(tab);
        tab.IsDirty = false;
        return true;
    }

    private void ApplyEditorMode(EditorMode mode)
    {
        _mode = mode;
        _richTextFont ??= NoteTextBox.FontFamily;

        bool rich = mode == EditorMode.RichText;
        bool markdown = mode == EditorMode.Markdown;
        NoteTextBox.FontFamily = markdown ? MarkdownFont : _richTextFont;
        NoteTextBox.ClipboardCopyFormat = rich ? RichEditClipboardFormat.AllFormats : RichEditClipboardFormat.PlainText;

        Toolbar?.SetMode(mode);
        ViewModel.StatusFileType = mode switch
        {
            EditorMode.PlainText => "Plain text",
            EditorMode.Markdown => "Markdown",
            _ => "Rich text",
        };

        ViewModel.IsRichText = rich;
    }

    public void ToggleBold()
    {
        if (_mode == EditorMode.Markdown) ToggleMarkdownWrap("**");
        else if (_mode == EditorMode.RichText) NoteTextBox.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;
        AfterFormatting();
    }

    public void ToggleItalic()
    {
        if (_mode == EditorMode.Markdown) ToggleMarkdownWrap("*");
        else if (_mode == EditorMode.RichText) NoteTextBox.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;
        AfterFormatting();
    }

    public void ToggleUnderline()
    {
        if (_mode == EditorMode.RichText)
        {
            var format = NoteTextBox.Document.Selection.CharacterFormat;
            format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        }
        AfterFormatting();
    }

    public void ToggleStrikethrough()
    {
        if (_mode == EditorMode.Markdown) ToggleMarkdownWrap("~~");
        else if (_mode == EditorMode.RichText) NoteTextBox.Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle;
        AfterFormatting();
    }

    private static FormattingToolbar? Toolbar => MainWindow.Current?.FormattingToolbar;

    public void ToggleList()
    {
        if (_mode == EditorMode.Markdown)
        {
            ToggleMarkdownList(_listType, forceApply: false);
            AfterFormatting();
            return;
        }
        ApplyList(IsList(NoteTextBox.Document.Selection.ParagraphFormat.ListType) ? MarkerType.None : _listType);
    }

    private void ToggleMarkdownWrap(string marker)
    {
        BeginMarkdownEdit();
        var document = NoteTextBox.Document;
        var selection = document.Selection;
        int start = selection.StartPosition;
        int end = selection.EndPosition;
        int m = marker.Length;
        selection.GetText(TextGetOptions.None, out string text);
        while (text.EndsWith('\r'))
        {
            text = text[..^1];
            end--;
        }
        selection.SetRange(start, end);

        if (start >= m)
        {
            document.GetRange(start - m, start).GetText(TextGetOptions.None, out string before);
            document.GetRange(end, end + m).GetText(TextGetOptions.None, out string after);
            if (before == marker && after == marker)
            {
                document.GetRange(end, end + m).SetText(TextSetOptions.None, string.Empty);
                document.GetRange(start - m, start).SetText(TextSetOptions.None, string.Empty);
                selection.SetRange(start - m, end - m);
                return;
            }
        }

        if (text.Length > 2 * m && text.StartsWith(marker) && text.EndsWith(marker))
        {
            string inner = text[m..^m];
            selection.SetText(TextSetOptions.None, inner);
            selection.SetRange(start, start + inner.Length);
            return;
        }

        selection.SetText(TextSetOptions.None, marker + text + marker);
        selection.SetRange(start + m, start + m + text.Length);
    }

    private void ToggleMarkdownList(MarkerType style, bool forceApply)
    {
        BeginMarkdownEdit();
        var range = NoteTextBox.Document.Selection.GetClone();
        range.Expand(TextRangeUnit.Paragraph);
        range.GetText(TextGetOptions.None, out string text);
        if (text.EndsWith('\r'))
        {
            range.MoveEnd(TextRangeUnit.Character, -1);
            text = text[..^1];
        }

        string[] lines = text.Split('\r');
        bool allListed = lines.Where(l => l.Trim().Length > 0).All(l => MarkdownListPrefix.IsMatch(l));
        bool remove = allListed && !forceApply;

        int number = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            string content = MarkdownListPrefix.Replace(lines[i], "$1");
            if (remove || content.Trim().Length == 0)
            {
                lines[i] = remove ? content : lines[i];
                continue;
            }

            Match indent = Regex.Match(content, @"^\s*");
            string prefix = style == MarkerType.Arabic ? $"{number++}. " : "- ";
            lines[i] = indent.Value + prefix + content[indent.Length..];
        }

        string result = string.Join('\r', lines);
        range.SetText(TextSetOptions.None, result);
        NoteTextBox.Document.Selection.SetRange(range.StartPosition, range.StartPosition + result.Length);
    }

    public void SetListStyle(MarkerType style)
    {
        _listType = style;
        Toolbar?.SetListStyle(style);

        if (_mode == EditorMode.Markdown)
        {
            ToggleMarkdownList(_listType, forceApply: true);
            AfterFormatting();
            return;
        }
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

    private void NoteTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_mode == EditorMode.Markdown && !_isHighlighting && !_isLoadingNote)
        {
            string text = PlainText();
            if (text == _markdownCurrent.Text) _markdownCurrent = CaptureMarkdownState(text);
        }
        UpdateToolbarState();
    }

    private void UpdateToolbarState()
    {
        UpdateEditState();
        if (_mode != EditorMode.RichText)
        {
            Toolbar?.SetState(false, false, false, false, false);
            return;
        }

        var character = NoteTextBox.Document.Selection.CharacterFormat;
        Toolbar?.SetState(
            character.Bold == FormatEffect.On,
            character.Italic == FormatEffect.On,
            character.Underline is not (UnderlineType.None or UnderlineType.Undefined),
            character.Strikethrough == FormatEffect.On,
            IsList(NoteTextBox.Document.Selection.ParagraphFormat.ListType));
    }

    private static bool IsList(MarkerType type) => type is not (MarkerType.None or MarkerType.Undefined);

    public bool TryChangeListLevel(int delta)
    {
        if (_mode != EditorMode.RichText) return false;

        var document = NoteTextBox.Document;
        var selection = document.Selection;
        if (!IsList(document.GetRange(selection.StartPosition, selection.StartPosition).ParagraphFormat.ListType)) return false;

        float step = RichTextMarkdown.ListIndentStep;
        float minIndent = ListGroupMinIndent(selection.StartPosition);
        int end = Math.Max(selection.StartPosition, selection.EndPosition);
        int position = selection.StartPosition;
        while (true)
        {
            ITextRange paragraph = document.GetRange(position, position);
            paragraph.Expand(TextRangeUnit.Paragraph);
            ITextParagraphFormat format = paragraph.ParagraphFormat;
            if (IsList(format.ListType))
            {
                float left = format.LeftIndent + delta * step;
                if (left >= minIndent - 0.5f && left <= minIndent + 8 * step + 0.5f)
                {
                    format.SetIndents(format.FirstLineIndent, left, format.RightIndent);
                }
            }

            if (paragraph.EndPosition >= end || paragraph.EndPosition <= position) break;
            position = paragraph.EndPosition;
        }

        AfterFormatting();
        return true;
    }

    private float ListGroupMinIndent(int position)
    {
        var document = NoteTextBox.Document;
        float min = float.MaxValue;

        ITextRange current = document.GetRange(position, position);
        current.Expand(TextRangeUnit.Paragraph);

        int up = current.StartPosition;
        while (true)
        {
            ITextRange paragraph = document.GetRange(up, up);
            paragraph.Expand(TextRangeUnit.Paragraph);
            ITextParagraphFormat format = paragraph.ParagraphFormat;
            if (!IsList(format.ListType)) break;
            min = Math.Min(min, format.LeftIndent);
            if (paragraph.StartPosition == 0) break;
            up = paragraph.StartPosition - 1;
        }

        int length = StoryLength();
        int down = current.EndPosition;
        while (down < length)
        {
            ITextRange paragraph = document.GetRange(down, down);
            paragraph.Expand(TextRangeUnit.Paragraph);
            ITextParagraphFormat format = paragraph.ParagraphFormat;
            if (!IsList(format.ListType)) break;
            min = Math.Min(min, format.LeftIndent);
            if (paragraph.EndPosition <= down) break;
            down = paragraph.EndPosition;
        }

        return min == float.MaxValue ? 0 : min;
    }

    private static readonly System.Text.RegularExpressions.Regex WordPattern = new(@"\S+");

    private void UpdateStatus()
    {
        string text = RichTextMarkdown.StripTags(PlainText());
        if (text.EndsWith('\r')) text = text[..^1];

        int caret = Math.Min(NoteTextBox.Document.Selection.EndPosition, text.Length);
        int lineStart = caret == 0 ? 0 : text.LastIndexOf('\r', caret - 1) + 1;
        int line = text.AsSpan(0, caret).Count('\r') + 1;
        ViewModel.StatusPosition = $"Ln {line}, Col {caret - lineStart + 1}";

        int words = WordPattern.Count(text);
        int characters = text.Length - text.Count(c => c == '\r');
        ViewModel.StatusCounts = $"{words} {(words == 1 ? "word" : "words")}, {characters} {(characters == 1 ? "character" : "characters")}";
    }

    private void UpdateEditState()
    {
        UpdateStatus();
        ViewModel.HasSelection = NoteTextBox.Document.Selection.Length != 0;
        ViewModel.CanUndo = _mode == EditorMode.Markdown ? _markdownUndo.Count > 0 : NoteTextBox.Document.CanUndo();
    }

    public void Undo()
    {
        if (_mode == EditorMode.Markdown)
        {
            if (_markdownUndo.Count > 0)
            {
                _markdownRedo.Push(CaptureMarkdownState(PlainText()));
                RestoreMarkdownState(_markdownUndo.Pop());
            }
        }
        else if (NoteTextBox.Document.CanUndo())
        {
            NoteTextBox.Document.Undo();
        }
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

    public async void Paste()
    {
        await PasteAsync(Clipboard.GetContent());
        FocusEditor();
    }

    private async void NoteTextBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content = Clipboard.GetContent();
        bool mayHoldImage = content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems);
        if (_mode == EditorMode.RichText && !mayHoldImage) return;

        e.Handled = true;
        await PasteAsync(content);
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    private async Task PasteAsync(DataPackageView content)
    {
        var images = new List<IRandomAccessStreamReference>();
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            images.Add(await content.GetBitmapAsync());
        }
        else if (content.Contains(StandardDataFormats.StorageItems))
        {
            foreach (IStorageItem item in await content.GetStorageItemsAsync())
            {
                if (item is StorageFile file && ImageExtensions.Contains(file.FileType.ToLowerInvariant()))
                {
                    images.Add(RandomAccessStreamReference.CreateFromFile(file));
                }
            }
        }

        if (images.Count > 0)
        {
            if (_mode != EditorMode.RichText)
            {
                ShowInfo(InfoBarSeverity.Informational, "Images can only be pasted into rich text (.rtf) notes.");
                return;
            }

            foreach (IRandomAccessStreamReference image in images)
            {
                await InsertImageAsync(image);
            }
            return;
        }

        if (_mode == EditorMode.RichText) NoteTextBox.Document.Selection.Paste(0);
        else await PastePlainTextAsync(content);
    }

    private async Task InsertImageAsync(IRandomAccessStreamReference image)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await image.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            double width = decoder.OrientedPixelWidth / scale;
            double height = decoder.OrientedPixelHeight / scale;
            double maxWidth = Math.Max(48, NoteTextBox.ActualWidth - NoteTextBox.Padding.Left - NoteTextBox.Padding.Right - 24);
            if (width > maxWidth)
            {
                height *= maxWidth / width;
                width = maxWidth;
            }

            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var png = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            png.Seek(0);

            var selection = NoteTextBox.Document.Selection;
            int position = selection.StartPosition;
            selection.InsertImage(
                (int)Math.Round(width), (int)Math.Round(height), 0,
                VerticalCharacterAlignment.Baseline, "Pasted image", png);
            selection.SetRange(position + 1, position + 1);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not paste the image: {ex.Message}");
        }
    }

    private async Task PastePlainTextAsync(DataPackageView content)
    {
        if (!content.Contains(StandardDataFormats.Text)) return;

        string text = await content.GetTextAsync();
        var selection = NoteTextBox.Document.Selection;
        selection.SetText(TextSetOptions.None, text.Replace("\r\n", "\r").Replace('\n', '\r'));
        selection.Collapse(false);
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
        if (_mode != EditorMode.RichText) return;

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
        ViewModel.StatusZoom = ZoomIndicator.Text;
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

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        PythonInfoBar.Severity = severity;
        PythonInfoBar.Message = message;
        PythonInfoBar.IsOpen = true;
    }
}
