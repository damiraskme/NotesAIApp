using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyApp.Models;
using MyApp.Services;
using MyApp.ViewModels;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace MyApp;

public sealed partial class MainWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public static MainWindow Current { get; private set; }
    public MainViewModel ViewModel { get; }
    private bool _closeConfirmed;
    public MainWindow()
    {
        Current = this;
        ViewModel = new MainViewModel();
        InitializeComponent();

        Title = ProjectPaths.AppName;
        AppTitleText.Text = ProjectPaths.AppName;

        ExtendsContentIntoTitleBar = true;

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(true, false);
        }

        DragRegion.SizeChanged += (s, e) => UpdateDragRegion();
        SizeChanged += (s, e) => UpdateDragRegion();
        DragRegion.Loaded += (s, e) => UpdateDragRegion();
        TabStrip.SizeChanged += (s, e) => UpdateDragRegion();

        DragRegion.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((s, e) => (RootFrame.Content as MainPage)?.FocusEditor()), true);

        TabScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(TabScroller_PointerWheelChanged), true);

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab) && ViewModel.ActiveTab is NoteTab tab)
            {
                DispatcherQueue.TryEnqueue(() => BringTabIntoView(tab));
            }
        };

        TabScroller.ViewChanged += (s, e) => UpdateTabArrows();
        TabScroller.SizeChanged += (s, e) => UpdateTabArrows();
        TabItems.SizeChanged += (s, e) => UpdateTabArrows();

        AppWindow.Closing += (s, e) =>
        {
            if (_closeConfirmed) return;
            e.Cancel = true;
            RequestClose();
        };

        AppWindow.Resize(new SizeInt32 {Width = 512, Height = 512});
        AppWindow.SetTaskbarIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        
        RootFrame.Navigate(typeof(MainPage));
    }
    
    private void UpdateDragRegion()
    {
        if (DragRegion.ActualWidth == 0 || DragRegion.ActualHeight == 0 || AutoSaveToggleSwitch.ActualWidth == 0)
            return;

        double scale = DragRegion.XamlRoot?.RasterizationScale ?? 1.0;

        var dragRegionTransform = DragRegion.TransformToVisual(null);
        var dragRegionBounds = dragRegionTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, DragRegion.ActualWidth, DragRegion.ActualHeight));

        var switchTransform = AutoSaveToggleSwitch.TransformToVisual(null);
        var switchBounds = switchTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, AutoSaveToggleSwitch.ActualWidth, AutoSaveToggleSwitch.ActualHeight));

        var tabStripTransform = TabStrip.TransformToVisual(null);
        var tabStripBounds = tabStripTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, TabStrip.ActualWidth, TabStrip.ActualHeight));

        var onTopButtonTransform = OnTopButton.TransformToVisual(null);
        var onTopButtonBounds = onTopButtonTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, OnTopButton.ActualWidth, OnTopButton.ActualHeight));

        List<RectInt32> dragRects = new List<RectInt32>();

        int rect1Width = (int)((switchBounds.Left - dragRegionBounds.Left) * scale);
        if (rect1Width > 0)
        {
            dragRects.Add(new RectInt32(
                (int)(dragRegionBounds.X * scale),
                (int)(dragRegionBounds.Y * scale),
                rect1Width,
                (int)(dragRegionBounds.Height * scale)));
        }

        int rect2X = (int)(tabStripBounds.Right * scale);
        int rect2Width = (int)((onTopButtonBounds.Left - tabStripBounds.Right) * scale);
        if (rect2Width > 0)
        {
            dragRects.Add(new RectInt32(
                rect2X,
                (int)(dragRegionBounds.Y * scale),
                rect2Width,
                (int)(dragRegionBounds.Height * scale)));
        }

        var nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        nonClientSource.SetRegionRects(NonClientRegionKind.Caption, dragRects.ToArray());
    }
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        RequestClose();
    }

    private async void RequestClose()
    {
        if (RootFrame.Content is MainPage mainPage && !await mainPage.ConfirmCloseAllAsync()) return;

        _closeConfirmed = true;
        Close();
    }
    private void StayOnTop_Click(object sender, RoutedEventArgs e)
    {
        if(AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
            if (sender is Button target)
            {
                target.Content = presenter.IsAlwaysOnTop ? "\uE77A" : "\uE718";
            }
        }
    }
    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if(RootFrame.Content is MainPage mainPage)
        {
            mainPage.SaveNoteContent();
        }
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.NewTab();
        }
    }

    private void Tab_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (RootFrame.Content is not MainPage mainPage || sender is not FrameworkElement { DataContext: NoteTab tab }) return;

        var point = e.GetCurrentPoint((UIElement)sender).Properties;
        if (point.IsLeftButtonPressed)
        {
            mainPage.ActivateTab(tab);
            BringTabIntoView(tab);
            mainPage.FocusEditor();
        }
        else if (point.IsMiddleButtonPressed)
        {
            _ = mainPage.CloseTabAsync(tab);
        }
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage && sender is FrameworkElement { DataContext: NoteTab tab })
        {
            await mainPage.CloseTabAsync(tab);
        }
    }

    private void Tab_PointerEntered(object sender, PointerRoutedEventArgs e) => SetTabHover(sender, 1);

    private void Tab_PointerExited(object sender, PointerRoutedEventArgs e) => SetTabHover(sender, 0);

    private static void SetTabHover(object sender, double opacity)
    {
        if (sender is FrameworkElement tab && tab.FindName("HoverBorder") is Border hover)
        {
            hover.Opacity = opacity;
        }
    }

    private void BringTabIntoView(NoteTab tab)
    {
        if (TabItems.ContainerFromItem(tab) is UIElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
    }

    private void ScrollTabsLeft_Click(object sender, RoutedEventArgs e) => ScrollTabs(-1);

    private void ScrollTabsRight_Click(object sender, RoutedEventArgs e) => ScrollTabs(1);

    private void ScrollTabs(int direction)
    {
        double step = TabScroller.ViewportWidth * 0.75 * direction;
        TabScroller.ChangeView(TabScroller.HorizontalOffset + step, null, null);
    }

    private void UpdateTabArrows()
    {
        bool overflows = TabScroller.ScrollableWidth > 0.5;
        var visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        ScrollTabsLeftButton.Visibility = visibility;
        ScrollTabsRightButton.Visibility = visibility;

        ScrollTabsLeftButton.IsEnabled = TabScroller.HorizontalOffset > 0.5;
        ScrollTabsRightButton.IsEnabled = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;
    }

    private void MenuItem_RefocusEditor(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.FocusEditor();
        }
    }

    private void TabScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(TabScroller).Properties.MouseWheelDelta;
        TabScroller.ChangeView(TabScroller.HorizontalOffset - delta, null, null, true);
        e.Handled = true;
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            await mainPage.SaveAsAsync();
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            await mainPage.OpenFileAsync();
        }
    }
}
