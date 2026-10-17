using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CoreTextRange = QuickTranslator.AiEditor.Core.Traditional.TextRange;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>Vẽ vùng alignment độc lập với focus/selection gốc của WPF TextBox.</summary>
internal sealed class TextBoxRangeHighlightAdorner : Adorner
{
    private static readonly Brush HighlightBrush = CreateBrush(Color.FromArgb(105, 37, 99, 235));
    private static readonly Pen HighlightBorder = CreatePen(Color.FromArgb(190, 29, 78, 216));
    private readonly TextBox _textBox;
    private CoreTextRange? _range;

    public TextBoxRangeHighlightAdorner(TextBox textBox) : base(textBox)
    {
        _textBox = textBox;
        IsHitTestVisible = false;
        _textBox.SizeChanged += (_, _) => InvalidateVisual();
        _textBox.TextChanged += (_, _) => InvalidateVisual();
        _textBox.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => InvalidateVisual()), handledEventsToo: true);
    }

    public CoreTextRange? Range => _range;

    public void SetRange(CoreTextRange? range)
    {
        CoreTextRange? valid = range is { Length: > 0 } value && value.Start >= 0 && value.End <= _textBox.Text.Length
            ? value : null;
        if (_range == valid) return;
        _range = valid;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_range is not { Length: > 0 } range || _textBox.Text.Length == 0) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        foreach (Rect rectangle in BuildRectangles(range))
            drawingContext.DrawRoundedRectangle(HighlightBrush, HighlightBorder, rectangle, 2, 2);
        drawingContext.Pop();
    }

    private IReadOnlyList<Rect> BuildRectangles(CoreTextRange range)
    {
        var result = new List<Rect>();
        int end = Math.Min(range.End, _textBox.Text.Length);
        for (int index = Math.Clamp(range.Start, 0, end); index < end; index++)
        {
            char character = _textBox.Text[index];
            if (character is '\r' or '\n') continue;
            Rect leading = _textBox.GetRectFromCharacterIndex(index, trailingEdge: false);
            Rect trailing = _textBox.GetRectFromCharacterIndex(index, trailingEdge: true);
            if (leading.IsEmpty) continue;
            double left = Math.Min(leading.X, trailing.IsEmpty ? leading.X : trailing.X);
            double right = Math.Max(leading.X, trailing.IsEmpty ? leading.Right : trailing.X);
            double height = Math.Max(leading.Height, trailing.IsEmpty ? 0 : trailing.Height);
            if (right - left < 1 && index + 1 <= _textBox.Text.Length)
            {
                Rect next = _textBox.GetRectFromCharacterIndex(index + 1, trailingEdge: false);
                if (!next.IsEmpty && Math.Abs(next.Y - leading.Y) < 1) right = Math.Max(right, next.X);
            }
            if (right - left < 1) right = left + Math.Max(2, _textBox.FontSize * 0.45);
            if (height < 1) height = Math.Max(2, _textBox.FontSize * 1.35);
            AddOrMerge(result, new Rect(left, leading.Y, right - left, height));
        }
        return result;
    }

    private static void AddOrMerge(List<Rect> rectangles, Rect next)
    {
        if (rectangles.Count > 0)
        {
            Rect previous = rectangles[^1];
            if (Math.Abs(previous.Y - next.Y) < 1 && next.Left <= previous.Right + 1)
            {
                rectangles[^1] = Rect.Union(previous, next);
                return;
            }
        }
        rectangles.Add(next);
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color)
    {
        var pen = new Pen(CreateBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
