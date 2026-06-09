using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Miura_Labeling
{
    public partial class MainWindow : Window
    {
        [DllImport("tsclib.dll")] static extern void openport(string name);
        [DllImport("tsclib.dll")] static extern void closeport();
        [DllImport("tsclib.dll")] static extern void sendcommand(string cmd);
        [DllImport("tsclib.dll")] static extern void clearbuffer();
        [DllImport("tsclib.dll")] static extern void printlabel(string set, string copy);
        [DllImport("tsclib.dll")] static extern void barcode(string x, string y, string type, string height, string readable, string rot, string narrow, string wide, string code);
        [DllImport("tsclib.dll")] static extern void printerfont(string x, string y, string font, string rot, string xm, string ym, string content);

        const double PxPerMm   = 4.0;   // 预览比例：4px = 1mm
        const double DotsPerMm = 8.0;   // 203 DPI ≈ 8 dots/mm
        const double HandleSz  = 8.0;   // 缩放手柄大小 px

        enum ElemType { Text, Barcode, QRCode }

        class LabelElem
        {
            public string   Name;
            public double   X, Y, W, H;   // mm
            public ElemType Type;
            public Border   Visual;        // 外层 Border
            public TextBox  TbX, TbY, TbW, TbH;
        }

        readonly List<LabelElem> _elems = new List<LabelElem>();

        // 移动
        LabelElem _dragging;
        Point     _dragOffset;

        // 缩放
        LabelElem _resizing;
        Point     _resizeStart;
        double    _resizeStartW, _resizeStartH;

        bool _busy;
        DispatcherTimer _saveTimer;

        static readonly string LayoutFile =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.cfg");

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // ── 初始化 ────────────────────────────────────────────────
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            DpDate.SelectedDate = DateTime.Today;
            TbTime.Text         = DateTime.Now.ToString("HH:mm:ss");
            TbSerial.Text       = "000001";
            TbProduct.Text      = "PN-001";
            TbQRContent.Text    = "{serial}";

            _elems.AddRange(new[]
            {
                new LabelElem { Name="日期时间", X=1, Y=2,  W=28, H=5,  Type=ElemType.Text    },
                new LabelElem { Name="产品编号", X=1, Y=9,  W=28, H=5,  Type=ElemType.Text    },
                new LabelElem { Name="序列号",   X=1, Y=16, W=28, H=5,  Type=ElemType.Text    },
                new LabelElem { Name="条形码",   X=1, Y=23, W=28, H=14, Type=ElemType.Barcode  },
                new LabelElem { Name="二维码",   X=4, Y=42, W=22, H=22, Type=ElemType.QRCode   },
            });

            LoadLayout();
            BuildPosPanel();
            BuildCanvas();
            UpdateContents();

            Closing += (s, ev) => SaveLayout();
        }

        // ── 位置面板 ──────────────────────────────────────────────
        void BuildPosPanel()
        {
            PosPanel.Children.Clear();
            foreach (var el in _elems)
            {
                var tbX = NumTb(el.X); var tbY = NumTb(el.Y);
                var tbW = NumTb(el.W); var tbH = NumTb(el.H);
                el.TbX = tbX; el.TbY = tbY; el.TbW = tbW; el.TbH = tbH;

                var cap = el;
                tbX.TextChanged += (s, ev) => { if (!_busy) MoveElemFromTb(cap); };
                tbY.TextChanged += (s, ev) => { if (!_busy) MoveElemFromTb(cap); };
                tbW.TextChanged += (s, ev) => { if (!_busy) ResizeElemFromTb(cap); };
                tbH.TextChanged += (s, ev) => { if (!_busy) ResizeElemFromTb(cap); };

                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                for (int i = 0; i < 4; i++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                void AddPair(string lbl, TextBox tb, int col)
                {
                    var lb = new TextBlock { Text = lbl, FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(col == 0 ? 0 : 5, 0, 1, 0) };
                    Grid.SetColumn(lb, col * 2); Grid.SetColumn(tb, col * 2 + 1);
                    row.Children.Add(lb); row.Children.Add(tb);
                }
                AddPair("X", tbX, 0); AddPair("Y", tbY, 1);
                AddPair("W", tbW, 2); AddPair("H", tbH, 3);

                var block = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
                block.Children.Add(new TextBlock { Text = el.Name, FontSize = 11,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, 0, 0, 0) });
                block.Children.Add(row);
                PosPanel.Children.Add(block);
                PosPanel.Children.Add(new Separator());
            }
        }

        static TextBox NumTb(double val) => new TextBox
            { Text = val.ToString("F1"), Padding = new Thickness(2), Margin = new Thickness(0) };

        void MoveElemFromTb(LabelElem el)
        {
            if (!double.TryParse(el.TbX.Text, out double x)) return;
            if (!double.TryParse(el.TbY.Text, out double y)) return;
            el.X = Clamp(x, 0, 30 - el.W);
            el.Y = Clamp(y, 0, 100 - el.H);
            Canvas.SetLeft(el.Visual, el.X * PxPerMm);
            Canvas.SetTop(el.Visual,  el.Y * PxPerMm);
            ScheduleSave();
        }

        void ResizeElemFromTb(LabelElem el)
        {
            if (!double.TryParse(el.TbW.Text, out double w)) return;
            if (!double.TryParse(el.TbH.Text, out double h)) return;
            el.W = Clamp(w, 2, 30 - el.X);
            el.H = Clamp(h, 2, 100 - el.Y);
            ApplyVisualSize(el);
            ScheduleSave();
        }

        static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(v, max));

        // ── 画布构建 ──────────────────────────────────────────────
        void BuildCanvas()
        {
            LabelCanvas.Children.Clear();
            foreach (var el in _elems)
            {
                var vis = BuildVisual(el);
                el.Visual = vis;
                Canvas.SetLeft(vis, el.X * PxPerMm);
                Canvas.SetTop(vis,  el.Y * PxPerMm);
                LabelCanvas.Children.Add(vis);
            }
        }

        Border BuildVisual(LabelElem el)
        {
            double pw = el.W * PxPerMm;
            double ph = el.H * PxPerMm;

            // ── 内容 ──
            FrameworkElement content;
            if (el.Type == ElemType.Barcode)
                content = new Viewbox { Child = BuildBarcodeRef(), Stretch = Stretch.Fill };
            else if (el.Type == ElemType.QRCode)
                content = new Viewbox { Child = BuildQRRef(), Stretch = Stretch.Uniform };
            else
                content = new TextBlock { FontSize = 7.5, VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, Padding = new Thickness(1) };

            // ── 缩放手柄（右下角） ──
            var handle = new Rectangle
            {
                Width = HandleSz, Height = HandleSz,
                Fill = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
                Cursor = Cursors.SizeNWSE,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Opacity = 0.9,
                ToolTip = "拖动调整大小"
            };
            var cap = el;
            handle.MouseDown += (s, e) => StartResize(cap, e);

            // ── 叠层 Grid ──
            var overlay = new Grid();
            overlay.Children.Add(content);
            overlay.Children.Add(handle);

            // ── 外层 Border（可拖动） ──
            var border = new Border
            {
                Width = pw, Height = ph,
                BorderBrush = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
                BorderThickness = new Thickness(0.8),
                Child = overlay,
                Cursor = Cursors.SizeAll,
                ToolTip = $"{el.Name}  (拖动移位 / 角落缩放)"
            };
            border.MouseDown += (s, e) => StartDrag(cap, e);
            return border;
        }

        // 条形码参考图（固定尺寸，由 Viewbox 缩放）
        static Canvas BuildBarcodeRef()
        {
            var c = new Canvas { Width = 120, Height = 40, Background = Brushes.White };
            var rng = new Random(42);
            double x = 1, top = 4, barH = 32;
            while (x < 118)
            {
                double bw = rng.NextDouble() < 0.4 ? 3.0 : 1.5;
                if (x + bw > 117) break;
                var r = new Rectangle { Width = bw, Height = barH, Fill = Brushes.Black };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, top);
                c.Children.Add(r);
                x += bw + (rng.NextDouble() < 0.4 ? 3.0 : 1.5);
            }
            return c;
        }

        // QR 占位参考图（固定尺寸，由 Viewbox 缩放）
        static Grid BuildQRRef()
        {
            var g = new Grid { Width = 80, Height = 80, Background = Brushes.White };
            g.Children.Add(new Border { BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(3), Margin = new Thickness(4) });
            g.Children.Add(new TextBlock { Text = "QR", FontSize = 20, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Foreground = Brushes.Gray });
            return g;
        }

        void ApplyVisualSize(LabelElem el)
        {
            if (el.Visual == null) return;
            el.Visual.Width  = el.W * PxPerMm;
            el.Visual.Height = el.H * PxPerMm;
        }

        // ── 内容更新 ──────────────────────────────────────────────
        void UpdateContents()
        {
            string date    = DpDate.SelectedDate?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");
            string time    = TbTime.Text;
            string serial  = TbSerial.Text;
            string product = TbProduct.Text;

            string[] texts = { $"{date}  {time}", $"PN: {product}", $"SN: {serial}", "", "" };

            for (int i = 0; i < _elems.Count; i++)
            {
                if (_elems[i].Type != ElemType.Text) continue;
                if (_elems[i].Visual?.Child is Grid overlay
                    && overlay.Children.Count > 0
                    && overlay.Children[0] is TextBlock tb)
                    tb.Text = texts[i];
            }
        }

        void OnInputChanged(object sender, EventArgs e)
        {
            if (IsLoaded) UpdateContents();
        }

        // ── 移动 ──────────────────────────────────────────────────
        void StartDrag(LabelElem el, MouseButtonEventArgs e)
        {
            if (e.Handled) return;   // 被缩放手柄截获则忽略
            _dragging = el;
            var pos = e.GetPosition(LabelCanvas);
            _dragOffset = new Point(pos.X - el.X * PxPerMm, pos.Y - el.Y * PxPerMm);
            LabelCanvas.CaptureMouse();
            e.Handled = true;
        }

        // ── 缩放 ──────────────────────────────────────────────────
        void StartResize(LabelElem el, MouseButtonEventArgs e)
        {
            _resizing      = el;
            _resizeStart   = e.GetPosition(LabelCanvas);
            _resizeStartW  = el.W;
            _resizeStartH  = el.H;
            LabelCanvas.CaptureMouse();
            e.Handled = true;   // 阻止冒泡到 Border 的拖动逻辑
        }

        // ── 鼠标移动 ──────────────────────────────────────────────
        void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(LabelCanvas);

            if (_resizing != null)
            {
                double dxMm = (pos.X - _resizeStart.X) / PxPerMm;
                double dyMm = (pos.Y - _resizeStart.Y) / PxPerMm;
                _resizing.W = Math.Round(Clamp(_resizeStartW + dxMm, 2, 30 - _resizing.X), 1);
                _resizing.H = Math.Round(Clamp(_resizeStartH + dyMm, 2, 100 - _resizing.Y), 1);
                ApplyVisualSize(_resizing);
                _busy = true;
                _resizing.TbW.Text = _resizing.W.ToString("F1");
                _resizing.TbH.Text = _resizing.H.ToString("F1");
                _busy = false;
            }
            else if (_dragging != null)
            {
                double px = Clamp(pos.X - _dragOffset.X, 0, LabelCanvas.Width  - _dragging.W * PxPerMm);
                double py = Clamp(pos.Y - _dragOffset.Y, 0, LabelCanvas.Height - _dragging.H * PxPerMm);
                _dragging.X = Math.Round(px / PxPerMm, 1);
                _dragging.Y = Math.Round(py / PxPerMm, 1);
                Canvas.SetLeft(_dragging.Visual, px);
                Canvas.SetTop(_dragging.Visual,  py);
                _busy = true;
                _dragging.TbX.Text = _dragging.X.ToString("F1");
                _dragging.TbY.Text = _dragging.Y.ToString("F1");
                _busy = false;
            }
        }

        void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            LabelCanvas.ReleaseMouseCapture();
            bool changed = _dragging != null || _resizing != null;
            _dragging = null;
            _resizing = null;
            if (changed) SaveLayout();
        }

        // ── 布局保存/加载 ─────────────────────────────────────────
        void ScheduleSave()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveLayout(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void SaveLayout()
        {
            try
            {
                var lines = new List<string>();
                foreach (var el in _elems)
                    lines.Add(string.Join("|", el.Name,
                        el.X.ToString("F1", CultureInfo.InvariantCulture),
                        el.Y.ToString("F1", CultureInfo.InvariantCulture),
                        el.W.ToString("F1", CultureInfo.InvariantCulture),
                        el.H.ToString("F1", CultureInfo.InvariantCulture)));
                File.WriteAllLines(LayoutFile, lines);
            }
            catch { }
        }

        void LoadLayout()
        {
            if (!File.Exists(LayoutFile)) return;
            try
            {
                var dict = new Dictionary<string, double[]>();
                foreach (var line in File.ReadAllLines(LayoutFile))
                {
                    var p = line.Split('|');
                    if (p.Length == 5
                        && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                        && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                        && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                        && double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                        dict[p[0]] = new[] { x, y, w, h };
                }
                foreach (var el in _elems)
                    if (dict.TryGetValue(el.Name, out double[] v))
                    { el.X = v[0]; el.Y = v[1]; el.W = v[2]; el.H = v[3]; }
            }
            catch { }
        }

        // ── 打印 ──────────────────────────────────────────────────
        void OnPrint(object sender, RoutedEventArgs e)
        {
            string printerName = GetPrinterName();
            if (!IsPrinterInstalled(printerName, out string list))
            {
                MessageBox.Show(
                    $"找不到打印机: \"{printerName}\"\n\n已安装打印机:\n{list}\n请在 printer.cfg 中填写正确名称。",
                    "打印机错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                string date    = DpDate.SelectedDate?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");
                string time    = TbTime.Text;
                string serial  = TbSerial.Text;
                string product = TbProduct.Text;

                string D(double mm) => ((int)Math.Round(mm * DotsPerMm)).ToString();

                var dt = _elems[0]; var pn = _elems[1]; var sn = _elems[2];
                var bc = _elems[3]; var qr = _elems[4];

                openport(printerName);
                sendcommand("SIZE 30 mm, 100 mm");
                sendcommand("GAP 2 mm, 0");
                sendcommand("SPEED 4");
                sendcommand("DENSITY 12");
                sendcommand("DIRECTION 1");
                sendcommand("SET TEAR ON");
                sendcommand("CODEPAGE UTF-8");
                clearbuffer();

                printerfont(D(dt.X), D(dt.Y), "1", "0", "1", "1", $"{date} {time}");
                printerfont(D(pn.X), D(pn.Y), "2", "0", "1", "1", $"PN: {product}");
                printerfont(D(sn.X), D(sn.Y), "2", "0", "1", "1", $"SN: {serial}");
                barcode(D(bc.X), D(bc.Y), "128", D(bc.H * 0.75), "1", "0", "2", "2", serial);

                string qrData = TbQRContent.Text
                    .Replace("{serial}", serial).Replace("{product}", product)
                    .Replace("{date}", date).Replace("{time}", time);
                sendcommand($"QRCODE {D(qr.X)},{D(qr.Y)},M,6,A,0,\"{qrData}\"");

                printlabel("1", "1");
                closeport();

                MessageBox.Show("打印完成！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打印错误: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 工具 ──────────────────────────────────────────────────
        static string GetPrinterName()
        {
            string cfg = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "printer.cfg");
            return File.Exists(cfg) ? File.ReadAllText(cfg).Trim() : "TSC TTP-244 Plus";
        }

        static bool IsPrinterInstalled(string name, out string list)
        {
            var sb = new StringBuilder();
            bool found = false;
            foreach (string p in PrinterSettings.InstalledPrinters)
            {
                sb.AppendLine("  " + p);
                if (p.Equals(name, StringComparison.OrdinalIgnoreCase)) found = true;
            }
            list = sb.ToString();
            return found;
        }
    }
}
