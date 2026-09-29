using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MyApp;

public sealed partial class PanelSplitter : Grid
{
    private double _startX;
    private bool _dragging;
    private readonly Rectangle _line;

    public event EventHandler? DragStarted;

    public event EventHandler<double>? DragDelta;

    public event EventHandler? DragCompleted;

    public PanelSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        _line = new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = (Brush)Application.Current.Resources["NoteBorderBrush"],
        };
        Children.Add(_line);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (s, e) => EndDrag();
        PointerCaptureLost += (s, e) => EndDrag();
        PointerEntered += (s, e) => _line.Width = 3;
        PointerExited += (s, e) => { if (!_dragging) _line.Width = 1; };
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        CapturePointer(e.Pointer);
        _startX = e.GetCurrentPoint(null).Position.X;
        _dragging = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        DragDelta?.Invoke(this, e.GetCurrentPoint(null).Position.X - _startX);
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _line.Width = 1;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
