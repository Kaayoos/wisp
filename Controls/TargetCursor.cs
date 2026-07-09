using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Wisp.Services;

namespace Wisp.Controls
{
    /// <summary>
    /// A custom "target" cursor inspired by reactbits.dev's TargetCursor: it hides the system cursor and
    /// draws a small dot plus four corner brackets that idle-spin and snap to wrap around whichever
    /// clickable element the pointer is over. Rendered in the window's adorner layer, so it sits above all
    /// content and never intercepts input. Fully toggleable and dependency-free.
    ///
    /// An element is treated as a target if it (or an ancestor) is a ButtonBase, has Cursor=Hand, or has
    /// the attached <see cref="IsTargetProperty"/> set - so most of Wisp's interactive UI lights up with no
    /// tagging, while specific containers can opt in explicitly.
    /// </summary>
    public sealed class TargetCursor
    {
        // Opt-in marker: TargetCursor.IsTarget="True" forces an element to be wrapped even if it isn't a
        // button and doesn't use the hand cursor (e.g. a custom card built from a Border).
        public static readonly DependencyProperty IsTargetProperty =
            DependencyProperty.RegisterAttached("IsTarget", typeof(bool), typeof(TargetCursor), new PropertyMetadata(false));
        public static void SetIsTarget(DependencyObject o, bool v) => o.SetValue(IsTargetProperty, v);
        public static bool GetIsTarget(DependencyObject o) => (bool)o.GetValue(IsTargetProperty);

        private readonly Window _window;
        private FrameworkElement? _root;
        private AdornerLayer? _layer;
        private CursorAdorner? _adorner;
        private Cursor? _savedCursor;
        private bool _savedForceCursor;
        private bool _enabled;

        // Motion state (root-element coordinates).
        private Point _mouse;
        private Point _center;       // smoothed reticle center
        private double _angle;       // idle spin angle, degrees
        private double _strength;    // 0 = compact + spinning, 1 = wrapped around the target
        private double _scale = 1.0; // press feedback
        private double _scaleTarget = 1.0;
        private FrameworkElement? _target;
        private bool _hasMouse;
        private long _lastTick;

        // Geometry / motion tuning.
        private const double CornerSize = 14;       // bracket box size (px)
        private const double Pad = 4;               // gap outside the wrapped element
        private const double SpinDegPerSec = 180;   // 2s per revolution, like spinDuration=2
        private const double CenterTau = 0.045;     // follow smoothing time-constant (snappy)
        private const double StrengthTau = 0.09;    // wrap-in/out ease (~hoverDuration 0.2 feel)

        public TargetCursor(Window window) { _window = window; }

        public bool IsEnabled => _enabled;

        public void Enable()
        {
            if (_enabled) return;
            _root = _window.Content as FrameworkElement;
            if (_root == null) return;
            _layer = AdornerLayer.GetAdornerLayer(_root);
            if (_layer == null) return;

            _adorner = new CursorAdorner(_root);
            _layer.Add(_adorner);

            // Hide the system cursor across the whole window (ForceCursor overrides children that set Hand).
            _savedCursor = _window.Cursor;
            _savedForceCursor = _window.ForceCursor;
            _window.Cursor = Cursors.None;
            _window.ForceCursor = true;

            _center = _mouse = Mouse.GetPosition(_root);
            _hasMouse = _root.IsMouseOver;
            _lastTick = 0;

            _window.PreviewMouseMove += OnMouseMove;
            _window.PreviewMouseDown += OnMouseDown;
            _window.PreviewMouseUp += OnMouseUp;
            _window.MouseEnter += OnMouseEnter;
            _window.MouseLeave += OnMouseLeave;
            CompositionTarget.Rendering += OnRendering;

            _enabled = true;
            _adorner.SetVisible(_hasMouse);
        }

        public void Disable()
        {
            if (!_enabled) return;
            _enabled = false;

            CompositionTarget.Rendering -= OnRendering;
            _window.PreviewMouseMove -= OnMouseMove;
            _window.PreviewMouseDown -= OnMouseDown;
            _window.PreviewMouseUp -= OnMouseUp;
            _window.MouseEnter -= OnMouseEnter;
            _window.MouseLeave -= OnMouseLeave;

            if (_layer != null && _adorner != null) _layer.Remove(_adorner);
            _adorner = null;
            _layer = null;

            // Restore the system cursor.
            _window.Cursor = _savedCursor;
            _window.ForceCursor = _savedForceCursor;

            _target = null;
            _strength = 0;
            _scale = _scaleTarget = 1.0;
        }

        private void OnMouseEnter(object sender, MouseEventArgs e) { _hasMouse = true; _adorner?.SetVisible(true); }
        private void OnMouseLeave(object sender, MouseEventArgs e) { _hasMouse = false; _adorner?.SetVisible(false); }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_root == null) return;
            _hasMouse = true;
            _adorner?.SetVisible(true);
            _mouse = e.GetPosition(_root);
            _target = FindTarget(e.OriginalSource as DependencyObject);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e) => _scaleTarget = 0.88;
        private void OnMouseUp(object sender, MouseButtonEventArgs e) => _scaleTarget = 1.0;

        /// <summary>Walks up the visual tree to the nearest element treated as a click target (or null).</summary>
        private static FrameworkElement? FindTarget(DependencyObject? src)
        {
            DependencyObject? node = src;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.IsHitTestVisible && fe.IsEnabled &&
                    (GetIsTarget(fe) || fe is ButtonBase || fe.Cursor == Cursors.Hand))
                {
                    return fe;
                }
                node = (node is Visual || node is Visual3D)
                    ? VisualTreeHelper.GetParent(node)
                    : (node as FrameworkContentElement)?.Parent;
            }
            return null;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_adorner == null || _root == null) return;

            long now = Environment.TickCount64;
            double dt = _lastTick == 0 ? 0.016 : Math.Min(0.05, (now - _lastTick) / 1000.0);
            _lastTick = now;

            // Frame-rate-independent exponential smoothing toward the live pointer position.
            double cf = 1.0 - Math.Exp(-dt / CenterTau);
            _center.X += (_mouse.X - _center.X) * cf;
            _center.Y += (_mouse.Y - _center.Y) * cf;

            // Re-resolve the target's rect every frame so the brackets track scrolling / layout shifts.
            Rect? rect = null;
            if (_target != null && _target.IsVisible && _hasMouse)
            {
                try
                {
                    var b = _target.TransformToVisual(_root).TransformBounds(new Rect(_target.RenderSize));
                    if (b.Width > 0 && b.Height > 0) rect = b;
                }
                catch { _target = null; }
            }

            double sf = 1.0 - Math.Exp(-dt / StrengthTau);
            _strength += ((rect.HasValue ? 1.0 : 0.0) - _strength) * sf;
            _scale += (_scaleTarget - _scale) * (1.0 - Math.Exp(-dt / 0.05));
            _angle = (_angle + SpinDegPerSec * dt) % 360.0;

            _adorner.Update(_center, _angle, _strength, _scale, rect, CornerSize, Pad);
        }

        /// <summary>
        /// The adorner that hosts the reticle visuals. It owns a small group (dot + four corner brackets)
        /// positioned at the cursor; the brackets blend between a compact spinning square and the wrapped
        /// target corners as <c>strength</c> goes 0→1.
        /// </summary>
        private sealed class CursorAdorner : Adorner
        {
            private readonly VisualCollection _children;
            private readonly Canvas _outer;     // fills the adorned element
            private readonly Canvas _group;     // origin sits at the cursor center
            private readonly Ellipse _dot;
            private readonly Border[] _corners;
            private readonly ScaleTransform _scale = new(1, 1);
            private readonly RotateTransform _rotate = new(0);
            private Color _accent;
            private SolidColorBrush _brush;

            public CursorAdorner(UIElement adorned) : base(adorned)
            {
                _accent = ThemeManager.AccentColor;
                _brush = Frozen(_accent);

                _dot = new Ellipse { Width = 5, Height = 5, Fill = _brush, IsHitTestVisible = false };
                _corners = new[]
                {
                    MakeCorner(new Thickness(3, 3, 0, 0)), // top-left
                    MakeCorner(new Thickness(0, 3, 3, 0)), // top-right
                    MakeCorner(new Thickness(0, 0, 3, 3)), // bottom-right
                    MakeCorner(new Thickness(3, 0, 0, 3)), // bottom-left
                };

                _group = new Canvas { IsHitTestVisible = false };
                _group.Children.Add(_dot);
                foreach (var c in _corners) _group.Children.Add(c);
                _group.RenderTransform = new TransformGroup { Children = { _scale, _rotate } };
                _group.Effect = new DropShadowEffect { Color = _accent, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.6 };

                _outer = new Canvas { IsHitTestVisible = false };
                _outer.Children.Add(_group);

                _children = new VisualCollection(this) { _outer };
                
                IsHitTestVisible = false;
            }

            private Border MakeCorner(Thickness bt) => new Border
            {
                Width = CornerSize,
                Height = CornerSize,
                BorderThickness = bt,
                BorderBrush = _brush,
                IsHitTestVisible = false
            };

            private static SolidColorBrush Frozen(Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }

            public void SetVisible(bool visible) => _group.Visibility = visible ? Visibility.Visible : Visibility.Hidden;

            public void Update(Point center, double angle, double strength, double scale, Rect? rect, double cornerSize, double pad)
            {
                // Keep the reticle tinted to the live accent (cheap colour compare; rebuild only on change).
                if (ThemeManager.AccentColor != _accent)
                {
                    _accent = ThemeManager.AccentColor;
                    _brush = Frozen(_accent);
                    _dot.Fill = _brush;
                    foreach (var c in _corners) c.BorderBrush = _brush;
                    if (_group.Effect is DropShadowEffect ds) ds.Color = _accent;
                }

                Canvas.SetLeft(_group, center.X);
                Canvas.SetTop(_group, center.Y);
                _scale.ScaleX = _scale.ScaleY = scale;
                // The idle spin fades out as the reticle wraps a target, so wrapped brackets stay axis-aligned.
                _rotate.Angle = angle * (1.0 - strength);

                Canvas.SetLeft(_dot, -_dot.Width / 2);
                Canvas.SetTop(_dot, -_dot.Height / 2);

                // Compact bracket positions (top-left of each box, relative to the dot).
                double a = cornerSize * 1.5, b = cornerSize * 0.5;
                var compact = new[]
                {
                    new Point(-a, -a), new Point(b, -a), new Point(b, b), new Point(-a, b)
                };

                Point[] wrap;
                if (rect.HasValue)
                {
                    var r = rect.Value;
                    wrap = new[]
                    {
                        new Point(r.Left - pad - center.X,                 r.Top - pad - center.Y),
                        new Point(r.Right + pad - cornerSize - center.X,   r.Top - pad - center.Y),
                        new Point(r.Right + pad - cornerSize - center.X,   r.Bottom + pad - cornerSize - center.Y),
                        new Point(r.Left - pad - center.X,                 r.Bottom + pad - cornerSize - center.Y),
                    };
                }
                else
                {
                    wrap = compact;
                }

                for (int i = 0; i < 4; i++)
                {
                    double x = compact[i].X + (wrap[i].X - compact[i].X) * strength;
                    double y = compact[i].Y + (wrap[i].Y - compact[i].Y) * strength;
                    Canvas.SetLeft(_corners[i], x);
                    Canvas.SetTop(_corners[i], y);
                }
            }

            protected override int VisualChildrenCount => _children?.Count ?? 0;
            protected override Visual GetVisualChild(int index) => _children != null ? _children[index] : throw new ArgumentOutOfRangeException(nameof(index));

            protected override Size MeasureOverride(Size constraint)
            {
                _outer.Measure(constraint);
                return AdornedElement.RenderSize;
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                _outer.Arrange(new Rect(finalSize));
                return finalSize;
            }
        }
    }
}
