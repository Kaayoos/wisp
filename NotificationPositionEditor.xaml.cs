using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp
{
    public partial class NotificationPositionEditor : Window
    {
        private readonly int _monitorIndex;
        private readonly double _initialPctX;
        private readonly double _initialPctY;

        private bool _isDragging;
        private Point _dragOffset;

        public double ResultPctX { get; private set; } = 0.95;
        public double ResultPctY { get; private set; } = 0.05;

        public NotificationPositionEditor(int monitorIndex, double initialPctX, double initialPctY)
        {
            InitializeComponent();
            _monitorIndex = monitorIndex;
            _initialPctX = initialPctX;
            _initialPctY = initialPctY;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position the window fullscreen on the selected monitor
            var screens = System.Windows.Forms.Screen.AllScreens;
            int index = _monitorIndex;
            if (index < 0 || index >= screens.Length) index = 0;

            var screen = screens[index];
            // Use the TARGET monitor's DPI (not the window's current monitor) so the editor covers the
            // chosen display correctly under per-monitor DPI - the window's own GetDpi reflects wherever
            // WPF first created it, which may be a differently-scaled monitor.
            double scale = Wisp.Services.DisplayHelper.GetDpiScaleForScreen(screen);

            // Convert physical coordinates to Device Independent Pixels (DIPs)
            this.Left = screen.WorkingArea.Left / scale;
            this.Top = screen.WorkingArea.Top / scale;
            this.Width = screen.WorkingArea.Width / scale;
            this.Height = screen.WorkingArea.Height / scale;

            // Align instructions card in the middle
            CenterInstructionsCard();

            // Position dummy notification at the initial position
            double initLeft = _initialPctX * (this.Width - DummyNotification.Width);
            double initTop = _initialPctY * (this.Height - DummyNotification.Height);

            // Ensure values are sane
            initLeft = Math.Clamp(initLeft, 0, this.Width - DummyNotification.Width);
            initTop = Math.Clamp(initTop, 0, this.Height - DummyNotification.Height);

            Canvas.SetLeft(DummyNotification, initLeft);
            Canvas.SetTop(DummyNotification, initTop);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            CenterInstructionsCard();
        }

        private void CenterInstructionsCard()
        {
            if (InstructionsCard != null)
            {
                Canvas.SetLeft(InstructionsCard, (this.Width - InstructionsCard.Width) / 2);
                Canvas.SetTop(InstructionsCard, (this.Height - InstructionsCard.Height) / 2);
            }
        }

        private void DummyNotification_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _dragOffset = e.GetPosition(DummyNotification);
            DummyNotification.CaptureMouse();
            e.Handled = true;
        }

        private void DragCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point mousePos = e.GetPosition(DragCanvas);
                double newLeft = mousePos.X - _dragOffset.X;
                double newTop = mousePos.Y - _dragOffset.Y;

                // Restrict drag inside the monitor/canvas bounds
                newLeft = Math.Clamp(newLeft, 0, this.Width - DummyNotification.Width);
                newTop = Math.Clamp(newTop, 0, this.Height - DummyNotification.Height);

                Canvas.SetLeft(DummyNotification, newLeft);
                Canvas.SetTop(DummyNotification, newTop);
            }
        }

        private void DragCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                DummyNotification.ReleaseMouseCapture();
                _isDragging = false;
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            double denomX = this.Width - DummyNotification.Width;
            ResultPctX = denomX > 0 ? Canvas.GetLeft(DummyNotification) / denomX : 0.95;

            double denomY = this.Height - DummyNotification.Height;
            ResultPctY = denomY > 0 ? Canvas.GetTop(DummyNotification) / denomY : 0.05;

            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
