using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("剪藏")]
[assembly: AssemblyDescription("剪贴板历史器 - 现代商业级生产力工具")]
[assembly: AssemblyCompany("JianCang")]
[assembly: AssemblyProduct("剪藏")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: ComVisible(false)]

namespace JianCang
{
    #region Entry & Application Context

    internal static class Program
    {
        private static Mutex _singleInstanceMutex;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "Local\\JianCangSingleInstance", out createdNew);
            if (!createdNew)
            {
                WriteWakeRequest();
                NativeMethods.PostMessage(new IntPtr(NativeMethods.HWND_BROADCAST), NativeMethods.WM_SHOW_HISTORY, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new TrayApplicationContext());
            }
            finally
            {
                if (_singleInstanceMutex != null)
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                }
            }
        }

        private static void WriteWakeRequest()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string directory = Path.Combine(appData, "JianCang");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "show.request"), DateTime.Now.ToString("o"), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly MainForm _mainForm;

        public TrayApplicationContext()
        {
            _mainForm = new MainForm();
            _mainForm.StartInBackground();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _mainForm != null && !_mainForm.IsDisposed)
            {
                _mainForm.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    #endregion

    #region Data Models & Persistence

    [DataContract]
    public sealed class ClipItem
    {
        [DataMember]
        public string Id { get; set; }

        [DataMember]
        public string Kind { get; set; }

        [DataMember]
        public string Text { get; set; }

        [DataMember]
        public string ImageFileName { get; set; }

        [DataMember]
        public DateTime CreatedAt { get; set; }

        [DataMember]
        public int ImageWidth { get; set; }

        [DataMember]
        public int ImageHeight { get; set; }

        [DataMember]
        public string ContentHash { get; set; }

        [DataMember]
        public bool IsPinned { get; set; }

        [DataMember]
        public string SourceProcessName { get; set; }

        [DataMember]
        public string SourceWindowTitle { get; set; }

        public bool HasText
        {
            get { return !String.IsNullOrEmpty(Text); }
        }

        public bool HasImage
        {
            get { return !String.IsNullOrEmpty(ImageFileName); }
        }

        public string DisplayKind
        {
            get
            {
                if (HasText && HasImage) return "图文";
                if (HasImage) return "图片";
                return "文字";
            }
        }

        public bool IsLink
        {
            get
            {
                if (!HasText) return false;
                string t = Text.Trim();
                return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                       t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsCode
        {
            get
            {
                if (!HasText || IsLink) return false;
                string t = Text;
                if (t.Length < 12) return false;
                return (t.Contains("{") && t.Contains("}")) ||
                       (t.Contains("public ") || t.Contains("function ") || t.Contains("class ") || t.Contains("import ") || t.Contains("def ") || t.Contains("const ") || t.Contains("SELECT "));
            }
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember]
        public bool Deduplicate { get; set; }

        [DataMember]
        public int MaxItems { get; set; }

        [DataMember]
        public int MaxDays { get; set; }

        [DataMember]
        public bool StartWithWindows { get; set; }

        [DataMember]
        public bool GlobalHotkeyEnabled { get; set; }

        public static AppSettings CreateDefault()
        {
            AppSettings settings = new AppSettings();
            settings.Deduplicate = true;
            settings.MaxItems = 0;
            settings.MaxDays = 0;
            settings.StartWithWindows = false;
            settings.GlobalHotkeyEnabled = true;
            return settings;
        }

        public void Normalize()
        {
            if (MaxItems < 0) MaxItems = 0;
            if (MaxDays < 0) MaxDays = 0;
        }
    }

    public sealed class HistoryStore
    {
        private readonly string _rootDirectory;
        private readonly string _imageDirectory;
        private readonly string _historyPath;
        private readonly string _settingsPath;

        public HistoryStore()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _rootDirectory = Path.Combine(appData, "JianCang");
            _imageDirectory = Path.Combine(_rootDirectory, "Images");
            _historyPath = Path.Combine(_rootDirectory, "history.json");
            _settingsPath = Path.Combine(_rootDirectory, "settings.json");

            Directory.CreateDirectory(_rootDirectory);
            Directory.CreateDirectory(_imageDirectory);
        }

        public string RootDirectory
        {
            get { return _rootDirectory; }
        }

        public string ImageDirectory
        {
            get { return _imageDirectory; }
        }

        public string GetImagePath(ClipItem item)
        {
            if (item == null || String.IsNullOrEmpty(item.ImageFileName))
            {
                return "";
            }

            return Path.Combine(_imageDirectory, item.ImageFileName);
        }

        public long GetImageCacheSizeBytes()
        {
            try
            {
                if (!Directory.Exists(_imageDirectory)) return 0;
                string[] files = Directory.GetFiles(_imageDirectory);
                long total = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    total += new FileInfo(files[i]).Length;
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        public List<ClipItem> Load()
        {
            try
            {
                if (!File.Exists(_historyPath))
                {
                    return new List<ClipItem>();
                }

                using (FileStream stream = File.OpenRead(_historyPath))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<ClipItem>));
                    List<ClipItem> items = serializer.ReadObject(stream) as List<ClipItem>;
                    return items ?? new List<ClipItem>();
                }
            }
            catch
            {
                return new List<ClipItem>();
            }
        }

        public void Save(List<ClipItem> items)
        {
            try
            {
                string tempPath = _historyPath + ".tmp";
                using (FileStream stream = File.Create(tempPath))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<ClipItem>));
                    serializer.WriteObject(stream, items ?? new List<ClipItem>());
                }

                if (File.Exists(_historyPath))
                {
                    File.Delete(_historyPath);
                }

                File.Move(tempPath, _historyPath);
            }
            catch
            {
            }
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return AppSettings.CreateDefault();
                }

                using (FileStream stream = File.OpenRead(_settingsPath))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    AppSettings settings = serializer.ReadObject(stream) as AppSettings;
                    if (settings == null)
                    {
                        settings = AppSettings.CreateDefault();
                    }

                    settings.Normalize();
                    return settings;
                }
            }
            catch
            {
                return AppSettings.CreateDefault();
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            try
            {
                settings = settings ?? AppSettings.CreateDefault();
                settings.Normalize();
                string tempPath = _settingsPath + ".tmp";
                using (FileStream stream = File.Create(tempPath))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    serializer.WriteObject(stream, settings);
                }

                if (File.Exists(_settingsPath))
                {
                    File.Delete(_settingsPath);
                }

                File.Move(tempPath, _settingsPath);
            }
            catch
            {
            }
        }

        public byte[] SaveImage(Image image, ClipItem item)
        {
            if (image == null || item == null)
            {
                return new byte[0];
            }

            string fileName = item.Id + ".png";
            string targetPath = Path.Combine(_imageDirectory, fileName);
            using (MemoryStream memory = new MemoryStream())
            {
                image.Save(memory, ImageFormat.Png);
                byte[] bytes = memory.ToArray();
                File.WriteAllBytes(targetPath, bytes);
                item.ImageFileName = fileName;
                item.ImageWidth = image.Width;
                item.ImageHeight = image.Height;
                return bytes;
            }
        }

        public void DeleteImage(ClipItem item)
        {
            if (item == null || String.IsNullOrEmpty(item.ImageFileName))
            {
                return;
            }

            try
            {
                string path = Path.Combine(_imageDirectory, item.ImageFileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    #endregion

    #region Modern Design System & Helpers

    internal static class ModernTheme
    {
        // Colors
        public static readonly Color CanvasBg = Color.FromArgb(246, 248, 251);
        public static readonly Color HeaderBg = Color.FromArgb(255, 255, 255);
        public static readonly Color CardBg = Color.FromArgb(255, 255, 255);
        public static readonly Color CardHoverBg = Color.FromArgb(248, 250, 253);
        public static readonly Color CardSelectedBg = Color.FromArgb(239, 246, 255);
        public static readonly Color CardPinnedBg = Color.FromArgb(255, 253, 242);

        public static readonly Color Border = Color.FromArgb(230, 235, 243);
        public static readonly Color BorderHover = Color.FromArgb(203, 213, 225);
        public static readonly Color BorderSelected = Color.FromArgb(147, 197, 253);
        public static readonly Color BorderPinned = Color.FromArgb(254, 235, 179);

        public static readonly Color Accent = Color.FromArgb(22, 119, 255);
        public static readonly Color AccentHover = Color.FromArgb(64, 150, 255);
        public static readonly Color AccentActive = Color.FromArgb(9, 88, 217);
        public static readonly Color AccentSoft = Color.FromArgb(230, 244, 255);

        public static readonly Color TextPrimary = Color.FromArgb(26, 32, 44);
        public static readonly Color TextSecondary = Color.FromArgb(100, 116, 139);
        public static readonly Color TextTertiary = Color.FromArgb(156, 163, 175);

        public static readonly Color Amber = Color.FromArgb(217, 119, 6);
        public static readonly Color AmberSoft = Color.FromArgb(254, 243, 199);

        public static readonly Color Danger = Color.FromArgb(239, 68, 68);
        public static readonly Color DangerHover = Color.FromArgb(220, 38, 38);
        public static readonly Color DangerSoft = Color.FromArgb(254, 242, 242);

        public static readonly Color Success = Color.FromArgb(16, 185, 129);
        public static readonly Color SuccessSoft = Color.FromArgb(236, 253, 245);

        public static readonly Color Purple = Color.FromArgb(124, 58, 237);
        public static readonly Color PurpleSoft = Color.FromArgb(245, 243, 255);

        // Typography
        public static readonly Font FontTitle = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FontSubTitle = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FontBody = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font FontBodyBold = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font FontSmallBold = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font FontBadge = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font FontCode = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);

        // Graphics Helpers
        public static GraphicsPath CreateRoundedPath(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = radius * 2F;
            if (diameter > rect.Width) diameter = rect.Width;
            if (diameter > rect.Height) diameter = rect.Height;

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void SetHighQuality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        public static string FormatRelativeTime(DateTime dt)
        {
            TimeSpan span = DateTime.Now - dt;
            if (span.TotalSeconds < 45) return "刚刚";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
            if (span.TotalHours < 24 && dt.Date == DateTime.Today) return dt.ToString("HH:mm");
            if (dt.Date == DateTime.Today.AddDays(-1)) return "昨天 " + dt.ToString("HH:mm");
            if (span.TotalDays < 365) return dt.ToString("MM-dd HH:mm");
            return dt.ToString("yyyy-MM-dd");
        }
    }

    internal static class ThumbnailManager
    {
        private static readonly Dictionary<string, Image> _cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        public static Image GetThumbnail(string imagePath, int targetSize = 56)
        {
            if (String.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            Image cached;
            if (_cache.TryGetValue(imagePath, out cached))
            {
                return cached;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image original = Image.FromStream(ms))
                {
                    Bitmap thumb = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(thumb))
                    {
                        ModernTheme.SetHighQuality(g);
                        g.Clear(Color.Transparent);

                        float scale = Math.Min((float)targetSize / original.Width, (float)targetSize / original.Height);
                        int w = Math.Max(1, (int)(original.Width * scale));
                        int h = Math.Max(1, (int)(original.Height * scale));
                        int x = (targetSize - w) / 2;
                        int y = (targetSize - h) / 2;

                        g.DrawImage(original, new Rectangle(x, y, w, h));
                    }

                    if (_cache.Count > 150)
                    {
                        foreach (Image img in _cache.Values) img.Dispose();
                        _cache.Clear();
                    }

                    _cache[imagePath] = thumb;
                    return thumb;
                }
            }
            catch
            {
                return null;
            }
        }

        public static void Clear()
        {
            foreach (Image img in _cache.Values) img.Dispose();
            _cache.Clear();
        }
    }

    #endregion

    #region Custom Modern Controls

    public sealed class ModernFilterTabs : Control
    {
        public enum FilterType
        {
            All = 0,
            Pinned = 1,
            Text = 2,
            Image = 3
        }

        public sealed class TabItem
        {
            public FilterType Type { get; set; }
            public string Title { get; set; }
            public int Count { get; set; }
            public RectangleF Bounds { get; set; }
        }

        private readonly List<TabItem> _tabs = new List<TabItem>();
        private FilterType _selected = FilterType.All;
        private int _hoverIndex = -1;

        public event EventHandler FilterChanged;

        public FilterType SelectedFilter
        {
            get { return _selected; }
            set
            {
                if (_selected != value)
                {
                    _selected = value;
                    Invalidate();
                    if (FilterChanged != null) FilterChanged(this, EventArgs.Empty);
                }
            }
        }

        public ModernFilterTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = 42;
            BackColor = ModernTheme.CanvasBg;

            _tabs.Add(new TabItem { Type = FilterType.All, Title = "全部" });
            _tabs.Add(new TabItem { Type = FilterType.Pinned, Title = "★ 已固定" });
            _tabs.Add(new TabItem { Type = FilterType.Text, Title = "📝 纯文字" });
            _tabs.Add(new TabItem { Type = FilterType.Image, Title = "🖼️ 图片" });
        }

        public void UpdateCounts(int all, int pinned, int text, int image)
        {
            _tabs[0].Count = all;
            _tabs[1].Count = pinned;
            _tabs[2].Count = text;
            _tabs[3].Count = image;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int prev = _hoverIndex;
            _hoverIndex = -1;
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Bounds.Contains(e.Location))
                {
                    _hoverIndex = i;
                    break;
                }
            }

            if (prev != _hoverIndex)
            {
                Cursor = _hoverIndex >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && _hoverIndex >= 0 && _hoverIndex < _tabs.Count)
            {
                SelectedFilter = _tabs[_hoverIndex].Type;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ModernTheme.SetHighQuality(g);
            g.Clear(BackColor);

            float startX = 14;
            float tabY = 5;
            float tabHeight = Height - 12;

            for (int i = 0; i < _tabs.Count; i++)
            {
                TabItem tab = _tabs[i];
                bool isSelected = tab.Type == _selected;
                bool isHover = i == _hoverIndex && !isSelected;

                string countText = " " + tab.Count;
                string displayText = tab.Title + countText;
                SizeF size = g.MeasureString(displayText, ModernTheme.FontBody);
                float tabWidth = size.Width + 24;

                RectangleF tabRect = new RectangleF(startX, tabY, tabWidth, tabHeight);
                tab.Bounds = tabRect;

                using (GraphicsPath path = ModernTheme.CreateRoundedPath(tabRect, 14))
                {
                    if (isSelected)
                    {
                        using (SolidBrush brush = new SolidBrush(ModernTheme.Accent))
                        {
                            g.FillPath(brush, path);
                        }
                    }
                    else if (isHover)
                    {
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(232, 238, 246)))
                        {
                            g.FillPath(brush, path);
                        }
                    }

                    Color textColor = isSelected ? Color.White : (isHover ? ModernTheme.TextPrimary : ModernTheme.TextSecondary);
                    Color countColor = isSelected ? Color.FromArgb(210, 235, 255) : ModernTheme.TextTertiary;

                    SizeF titleSize = g.MeasureString(tab.Title, ModernTheme.FontBody);
                    float textX = tabRect.X + 12;
                    float textY = tabRect.Y + (tabRect.Height - titleSize.Height) / 2F;

                    using (SolidBrush textBrush = new SolidBrush(textColor))
                    {
                        g.DrawString(tab.Title, isSelected ? ModernTheme.FontBodyBold : ModernTheme.FontBody, textBrush, textX, textY);
                    }

                    using (SolidBrush countBrush = new SolidBrush(countColor))
                    {
                        g.DrawString(countText, ModernTheme.FontSmallBold, countBrush, textX + titleSize.Width - 4, textY + 1.5F);
                    }
                }

                startX += tabWidth + 8;
            }

            using (Pen pen = new Pen(ModernTheme.Border, 1))
            {
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    public sealed class ModernSearchBar : Panel
    {
        private readonly TextBox _textBox;
        private bool _isFocused;
        private bool _hoverClear;
        private RectangleF _clearBounds;

        public event EventHandler SearchChanged;

        public string SearchText
        {
            get { return _textBox.Text; }
            set { _textBox.Text = value; }
        }

        public TextBox InnerTextBox
        {
            get { return _textBox; }
        }

        public ModernSearchBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = 36;
            Width = 320;
            BackColor = ModernTheme.HeaderBg;

            _textBox = new TextBox();
            _textBox.BorderStyle = BorderStyle.None;
            _textBox.Font = ModernTheme.FontBody;
            _textBox.ForeColor = ModernTheme.TextPrimary;
            _textBox.BackColor = Color.FromArgb(243, 246, 250);
            _textBox.Location = new Point(32, 8);
            _textBox.Width = Width - 60;
            _textBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            _textBox.Enter += delegate { _isFocused = true; Invalidate(); };
            _textBox.Leave += delegate { _isFocused = false; Invalidate(); };
            _textBox.TextChanged += delegate
            {
                Invalidate();
                if (SearchChanged != null) SearchChanged(this, EventArgs.Empty);
            };

            Controls.Add(_textBox);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_textBox != null)
            {
                _textBox.Width = Width - 62;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool prev = _hoverClear;
            _hoverClear = !String.IsNullOrEmpty(_textBox.Text) && _clearBounds.Contains(e.Location);
            if (prev != _hoverClear)
            {
                Cursor = _hoverClear ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverClear = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_hoverClear && !String.IsNullOrEmpty(_textBox.Text))
            {
                _textBox.Text = "";
                _textBox.Focus();
            }
            else
            {
                _textBox.Focus();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ModernTheme.SetHighQuality(g);

            RectangleF rect = new RectangleF(1, 1, Width - 3, Height - 3);
            Color fill = _isFocused ? Color.White : Color.FromArgb(243, 246, 250);
            Color border = _isFocused ? ModernTheme.Accent : ModernTheme.Border;

            _textBox.BackColor = fill;

            using (GraphicsPath path = ModernTheme.CreateRoundedPath(rect, Height / 2F - 1F))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }

                using (Pen pen = new Pen(border, _isFocused ? 1.5F : 1F))
                {
                    g.DrawPath(pen, path);
                }
            }

            using (Pen pen = new Pen(ModernTheme.TextTertiary, 1.8F))
            {
                g.DrawEllipse(pen, 11, 11, 10, 10);
                g.DrawLine(pen, 19, 19, 24, 24);
            }

            if (String.IsNullOrEmpty(_textBox.Text) && !_isFocused)
            {
                using (SolidBrush ph = new SolidBrush(ModernTheme.TextTertiary))
                {
                    g.DrawString("搜索剪贴板历史、来源或标题 (Ctrl+F)...", ModernTheme.FontBody, ph, 32, 8);
                }
            }

            if (!String.IsNullOrEmpty(_textBox.Text))
            {
                _clearBounds = new RectangleF(Width - 28, (Height - 16) / 2F, 16, 16);
                using (GraphicsPath btnPath = ModernTheme.CreateRoundedPath(_clearBounds, 8))
                {
                    using (SolidBrush b = new SolidBrush(_hoverClear ? Color.FromArgb(210, 220, 230) : Color.FromArgb(226, 232, 240)))
                    {
                        g.FillPath(b, btnPath);
                    }
                }

                using (Pen xPen = new Pen(ModernTheme.TextSecondary, 1.5F))
                {
                    g.DrawLine(xPen, _clearBounds.X + 5, _clearBounds.Y + 5, _clearBounds.Right - 5, _clearBounds.Bottom - 5);
                    g.DrawLine(xPen, _clearBounds.Right - 5, _clearBounds.Y + 5, _clearBounds.X + 5, _clearBounds.Bottom - 5);
                }
            }
        }
    }

    public sealed class ModernCardListView : Control
    {
        private readonly List<ClipItem> _items = new List<ClipItem>();
        private readonly HistoryStore _store;
        private int _selectedIndex = -1;
        private int _hoverIndex = -1;
        private int _hoverBtnIndex = -1;
        private int _scrollY = 0;
        private bool _isDraggingScroll = false;
        private int _dragStartY = 0;
        private int _dragStartScrollY = 0;

        public const int ItemHeight = 86;
        public const int CardPadding = 4;

        public event EventHandler SelectionChanged;
        public event EventHandler ItemDoubleClicked;
        public event Action<ClipItem> QuickCopyClicked;
        public event Action<ClipItem> QuickPinClicked;
        public event Action<ClipItem> QuickDeleteClicked;

        public ClipItem SelectedItem
        {
            get
            {
                if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                {
                    return _items[_selectedIndex];
                }
                return null;
            }
            set
            {
                int index = _items.IndexOf(value);
                SelectedIndex = index;
            }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (_selectedIndex != value)
                {
                    _selectedIndex = value;
                    EnsureVisible(_selectedIndex);
                    Invalidate();
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                }
            }
        }

        public int TotalCount
        {
            get { return _items.Count; }
        }

        public ModernCardListView(HistoryStore store)
        {
            _store = store;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = ModernTheme.CanvasBg;
            TabStop = true;
        }

        public void SetItems(List<ClipItem> items)
        {
            ClipItem curSelected = SelectedItem;
            _items.Clear();
            if (items != null)
            {
                _items.AddRange(items);
            }

            if (curSelected != null && _items.Contains(curSelected))
            {
                _selectedIndex = _items.IndexOf(curSelected);
            }
            else
            {
                _selectedIndex = _items.Count > 0 ? 0 : -1;
            }

            ClampScroll();
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            int targetY = index * ItemHeight;
            if (targetY < _scrollY)
            {
                _scrollY = targetY;
            }
            else if (targetY + ItemHeight > _scrollY + ClientSize.Height)
            {
                _scrollY = targetY + ItemHeight - ClientSize.Height;
            }
            ClampScroll();
            Invalidate();
        }

        private void ClampScroll()
        {
            int totalHeight = _items.Count * ItemHeight;
            int maxScroll = Math.Max(0, totalHeight - ClientSize.Height + 10);
            if (_scrollY > maxScroll) _scrollY = maxScroll;
            if (_scrollY < 0) _scrollY = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scrollY -= (e.Delta / 120) * (ItemHeight);
            ClampScroll();
            UpdateHover(PointToClient(Cursor.Position));
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_items.Count == 0) return;

            if (e.KeyCode == Keys.Down)
            {
                if (_selectedIndex < _items.Count - 1)
                {
                    SelectedIndex++;
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                if (_selectedIndex > 0)
                {
                    SelectedIndex--;
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Home)
            {
                SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.End)
            {
                SelectedIndex = _items.Count - 1;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (ItemDoubleClicked != null) ItemDoubleClicked(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && SelectedItem != null)
            {
                if (QuickDeleteClicked != null) QuickDeleteClicked(SelectedItem);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.P && SelectedItem != null)
            {
                if (QuickPinClicked != null) QuickPinClicked(SelectedItem);
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDraggingScroll)
            {
                int totalHeight = _items.Count * ItemHeight;
                int maxScroll = Math.Max(0, totalHeight - ClientSize.Height);
                float ratio = (float)maxScroll / (ClientSize.Height - 40);
                _scrollY = _dragStartScrollY + (int)((e.Y - _dragStartY) * ratio);
                ClampScroll();
                Invalidate();
                return;
            }

            UpdateHover(e.Location);
        }

        private void UpdateHover(Point pt)
        {
            int prevHover = _hoverIndex;
            int prevBtn = _hoverBtnIndex;

            _hoverIndex = -1;
            _hoverBtnIndex = -1;

            int scrollBarWidth = GetScrollBarVisible() ? 10 : 0;
            if (pt.X < ClientSize.Width - scrollBarWidth)
            {
                int index = (pt.Y + _scrollY) / ItemHeight;
                if (index >= 0 && index < _items.Count)
                {
                    _hoverIndex = index;
                    RectangleF cardRect = GetCardRect(index);
                    if (cardRect.Contains(pt))
                    {
                        float btnAreaX = cardRect.Right - 88;
                        float btnAreaY = cardRect.Y + 8;
                        if (pt.X >= btnAreaX && pt.X <= cardRect.Right - 8 && pt.Y >= btnAreaY && pt.Y <= btnAreaY + 24)
                        {
                            if (pt.X < btnAreaX + 26) _hoverBtnIndex = 0;
                            else if (pt.X < btnAreaX + 52) _hoverBtnIndex = 1;
                            else _hoverBtnIndex = 2;
                        }
                    }
                }
            }

            if (prevHover != _hoverIndex || prevBtn != _hoverBtnIndex)
            {
                Cursor = (_hoverBtnIndex >= 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            _hoverBtnIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (GetScrollBarVisible() && e.X >= ClientSize.Width - 12)
            {
                _isDraggingScroll = true;
                _dragStartY = e.Y;
                _dragStartScrollY = _scrollY;
                return;
            }

            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                if (_hoverIndex >= 0 && _hoverIndex < _items.Count)
                {
                    ClipItem item = _items[_hoverIndex];
                    if (_hoverBtnIndex >= 0 && e.Button == MouseButtons.Left)
                    {
                        if (_hoverBtnIndex == 0 && QuickCopyClicked != null) QuickCopyClicked(item);
                        else if (_hoverBtnIndex == 1 && QuickPinClicked != null) QuickPinClicked(item);
                        else if (_hoverBtnIndex == 2 && QuickDeleteClicked != null) QuickDeleteClicked(item);
                        return;
                    }

                    SelectedIndex = _hoverIndex;
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDraggingScroll = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && _selectedIndex >= 0 && _hoverBtnIndex < 0)
            {
                if (ItemDoubleClicked != null) ItemDoubleClicked(this, EventArgs.Empty);
            }
        }

        private bool GetScrollBarVisible()
        {
            return _items.Count * ItemHeight > ClientSize.Height;
        }

        private RectangleF GetCardRect(int index)
        {
            float y = index * ItemHeight - _scrollY + CardPadding;
            float w = ClientSize.Width - 20 - (GetScrollBarVisible() ? 10 : 0);
            return new RectangleF(10, y, Math.Max(200, w), ItemHeight - (CardPadding * 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ModernTheme.SetHighQuality(g);
            g.Clear(BackColor);

            if (_items.Count == 0)
            {
                DrawEmptyState(g);
                return;
            }

            int firstIndex = Math.Max(0, _scrollY / ItemHeight);
            int lastIndex = Math.Min(_items.Count - 1, (_scrollY + ClientSize.Height) / ItemHeight + 1);

            for (int i = firstIndex; i <= lastIndex; i++)
            {
                DrawCard(g, i);
            }

            if (GetScrollBarVisible())
            {
                DrawScrollBar(g);
            }
        }

        private void DrawCard(Graphics g, int index)
        {
            ClipItem item = _items[index];
            RectangleF rect = GetCardRect(index);
            bool isSelected = index == _selectedIndex;
            bool isHover = index == _hoverIndex && !isSelected;

            Color bgColor = ModernTheme.CardBg;
            Color borderColor = ModernTheme.Border;

            if (isSelected)
            {
                bgColor = ModernTheme.CardSelectedBg;
                borderColor = ModernTheme.BorderSelected;
            }
            else if (item.IsPinned)
            {
                bgColor = ModernTheme.CardPinnedBg;
                borderColor = ModernTheme.BorderPinned;
            }
            else if (isHover)
            {
                bgColor = ModernTheme.CardHoverBg;
                borderColor = ModernTheme.BorderHover;
            }

            using (GraphicsPath cardPath = ModernTheme.CreateRoundedPath(rect, 8))
            {
                using (SolidBrush bgBrush = new SolidBrush(bgColor))
                {
                    g.FillPath(bgBrush, cardPath);
                }

                using (Pen borderPen = new Pen(borderColor, isSelected ? 1.4F : 1F))
                {
                    g.DrawPath(borderPen, cardPath);
                }

                if (isSelected)
                {
                    using (SolidBrush barBrush = new SolidBrush(ModernTheme.Accent))
                    {
                        RectangleF barRect = new RectangleF(rect.X, rect.Y + 6, 3.5F, rect.Height - 12);
                        using (GraphicsPath barPath = ModernTheme.CreateRoundedPath(barRect, 1.8F))
                        {
                            g.FillPath(barBrush, barPath);
                        }
                    }
                }
            }

            float contentX = rect.X + 14;
            float topY = rect.Y + 9;

            if (item.HasImage)
            {
                int thumbSize = 56;
                RectangleF thumbRect = new RectangleF(contentX, rect.Y + (rect.Height - thumbSize) / 2F, thumbSize, thumbSize);
                Image thumb = ThumbnailManager.GetThumbnail(_store.GetImagePath(item), thumbSize);

                using (GraphicsPath thumbPath = ModernTheme.CreateRoundedPath(thumbRect, 6))
                {
                    using (SolidBrush thumbBg = new SolidBrush(Color.FromArgb(240, 243, 248)))
                    {
                        g.FillPath(thumbBg, thumbPath);
                    }

                    if (thumb != null)
                    {
                        g.SetClip(thumbPath);
                        g.DrawImage(thumb, thumbRect);
                        g.ResetClip();
                    }

                    using (Pen thumbBorder = new Pen(ModernTheme.Border, 1))
                    {
                        g.DrawPath(thumbBorder, thumbPath);
                    }
                }

                contentX += thumbSize + 12;
            }

            float badgeX = contentX;

            // Type Badge
            DrawPillBadge(g, item.DisplayKind, ModernTheme.AccentSoft, ModernTheme.Accent, ref badgeX, topY);

            // Link or Code Feature Badge
            if (item.IsLink)
            {
                DrawPillBadge(g, "🔗 链接", ModernTheme.PurpleSoft, ModernTheme.Purple, ref badgeX, topY);
            }
            else if (item.IsCode)
            {
                DrawPillBadge(g, "{ } 代码", Color.FromArgb(240, 245, 250), Color.FromArgb(71, 85, 105), ref badgeX, topY);
            }

            // Source App Badge
            string source = String.IsNullOrEmpty(item.SourceProcessName) ? "系统剪贴板" : item.SourceProcessName;
            DrawPillBadge(g, source, Color.FromArgb(241, 245, 249), ModernTheme.TextSecondary, ref badgeX, topY);

            // Pinned Badge
            if (item.IsPinned)
            {
                DrawPillBadge(g, "★ 置顶", ModernTheme.AmberSoft, ModernTheme.Amber, ref badgeX, topY);
            }

            // Relative Time (Align Right if not hovering buttons)
            string timeStr = ModernTheme.FormatRelativeTime(item.CreatedAt);
            SizeF timeSize = g.MeasureString(timeStr, ModernTheme.FontSmall);
            float timeX = rect.Right - timeSize.Width - 12;

            bool showActionButtons = (index == _hoverIndex);
            if (!showActionButtons)
            {
                using (SolidBrush timeBrush = new SolidBrush(ModernTheme.TextTertiary))
                {
                    g.DrawString(timeStr, ModernTheme.FontSmall, timeBrush, timeX, topY + 1.5F);
                }
            }
            else
            {
                DrawQuickActionButtons(g, rect, index == _hoverIndex ? _hoverBtnIndex : -1, item.IsPinned);
            }

            // Content Preview Lines (Rows 2 & 3)
            float textY = topY + 24;
            float maxTextWidth = rect.Right - contentX - 14;

            string preview = "";
            if (item.HasText)
            {
                preview = NormalizePreviewText(item.Text);
            }
            else if (item.HasImage)
            {
                preview = "图片尺寸: " + item.ImageWidth + " × " + item.ImageHeight + " 像素";
            }

            if (!String.IsNullOrEmpty(preview))
            {
                RectangleF textBounds = new RectangleF(contentX, textY, Math.Max(50, maxTextWidth), rect.Height - 34);
                using (SolidBrush textBrush = new SolidBrush(ModernTheme.TextPrimary))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.FormatFlags = 0; // Allow 2 lines of wrap
                    g.DrawString(preview, ModernTheme.FontBody, textBrush, textBounds, sf);
                }
            }
        }

        private void DrawPillBadge(Graphics g, string text, Color bg, Color fore, ref float x, float y)
        {
            SizeF size = g.MeasureString(text, ModernTheme.FontBadge);
            float w = size.Width + 12;
            float h = 18;
            RectangleF badgeRect = new RectangleF(x, y, w, h);

            using (GraphicsPath path = ModernTheme.CreateRoundedPath(badgeRect, 4))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, path);
                using (SolidBrush f = new SolidBrush(fore))
                {
                    g.DrawString(text, ModernTheme.FontBadge, f, x + 6, y + 1.5F);
                }
            }

            x += w + 6;
        }

        private void DrawQuickActionButtons(Graphics g, RectangleF cardRect, int activeBtn, bool isPinned)
        {
            float btnX = cardRect.Right - 88;
            float btnY = cardRect.Y + 7;

            DrawActionButton(g, new RectangleF(btnX, btnY, 24, 24), "📋", activeBtn == 0, ModernTheme.AccentSoft, ModernTheme.Accent);
            DrawActionButton(g, new RectangleF(btnX + 28, btnY, 24, 24), isPinned ? "★" : "☆", activeBtn == 1, ModernTheme.AmberSoft, ModernTheme.Amber);
            DrawActionButton(g, new RectangleF(btnX + 56, btnY, 24, 24), "🗑", activeBtn == 2, ModernTheme.DangerSoft, ModernTheme.Danger);
        }

        private void DrawActionButton(Graphics g, RectangleF rect, string icon, bool isHover, Color hoverBg, Color hoverFore)
        {
            using (GraphicsPath path = ModernTheme.CreateRoundedPath(rect, 4))
            {
                if (isHover)
                {
                    using (SolidBrush b = new SolidBrush(hoverBg)) g.FillPath(b, path);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 243, 248))) g.FillPath(b, path);
                }

                using (SolidBrush f = new SolidBrush(isHover ? hoverFore : ModernTheme.TextSecondary))
                {
                    SizeF sz = g.MeasureString(icon, ModernTheme.FontSmallBold);
                    g.DrawString(icon, ModernTheme.FontSmallBold, f, rect.X + (rect.Width - sz.Width) / 2F, rect.Y + (rect.Height - sz.Height) / 2F);
                }
            }
        }

        private void DrawScrollBar(Graphics g)
        {
            int totalHeight = _items.Count * ItemHeight;
            float visibleRatio = (float)ClientSize.Height / totalHeight;
            float thumbHeight = Math.Max(28, ClientSize.Height * visibleRatio);
            float maxScroll = totalHeight - ClientSize.Height;
            float scrollProgress = (float)_scrollY / maxScroll;
            float thumbY = scrollProgress * (ClientSize.Height - thumbHeight);

            RectangleF thumbRect = new RectangleF(ClientSize.Width - 8, thumbY + 2, 5, thumbHeight - 4);
            using (GraphicsPath path = ModernTheme.CreateRoundedPath(thumbRect, 2.5F))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(180, 195, 215)))
                {
                    g.FillPath(b, path);
                }
            }
        }

        private void DrawEmptyState(Graphics g)
        {
            string title = "暂无剪贴板记录";
            string desc = "复制文字、图片或图文，剪藏将自动在此收录历史。";

            SizeF szTitle = g.MeasureString(title, ModernTheme.FontSubTitle);
            SizeF szDesc = g.MeasureString(desc, ModernTheme.FontBody);

            float cx = ClientSize.Width / 2F;
            float cy = ClientSize.Height / 2F - 30;

            RectangleF iconBox = new RectangleF(cx - 24, cy - 40, 48, 48);
            using (Pen pen = new Pen(Color.FromArgb(203, 213, 225), 2.5F))
            {
                g.DrawRectangle(pen, iconBox.X + 8, iconBox.Y + 10, 32, 36);
                g.DrawLine(pen, iconBox.X + 16, iconBox.Y + 10, iconBox.X + 16, iconBox.Y + 6);
                g.DrawLine(pen, iconBox.X + 16, iconBox.Y + 6, iconBox.X + 32, iconBox.Y + 6);
                g.DrawLine(pen, iconBox.X + 32, iconBox.Y + 6, iconBox.X + 32, iconBox.Y + 10);

                g.DrawLine(pen, iconBox.X + 14, iconBox.Y + 22, iconBox.X + 34, iconBox.Y + 22);
                g.DrawLine(pen, iconBox.X + 14, iconBox.Y + 29, iconBox.X + 30, iconBox.Y + 29);
                g.DrawLine(pen, iconBox.X + 14, iconBox.Y + 36, iconBox.X + 26, iconBox.Y + 36);
            }

            using (SolidBrush tBrush = new SolidBrush(ModernTheme.TextPrimary))
            {
                g.DrawString(title, ModernTheme.FontSubTitle, tBrush, cx - szTitle.Width / 2F, cy + 20);
            }

            using (SolidBrush dBrush = new SolidBrush(ModernTheme.TextTertiary))
            {
                g.DrawString(desc, ModernTheme.FontBody, dBrush, cx - szDesc.Width / 2F, cy + 48);
            }
        }

        private string NormalizePreviewText(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            bool lastSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace) sb.Append(' ');
                    lastSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastSpace = false;
                }
                if (sb.Length > 200) break;
            }
            return sb.ToString().Trim();
        }
    }

    public sealed class ModernPreviewPanel : Panel
    {
        private readonly HistoryStore _store;
        private ClipItem _currentItem;

        private readonly Panel _metaHeader;
        private readonly Label _titleLabel;
        private readonly Label _subInfoLabel;
        private readonly Label _timeLabel;

        private readonly FlowLayoutPanel _actionBar;
        private readonly Button _copyPrimaryButton;
        private readonly Button _copyPlainButton;
        private readonly Button _pinButton;
        private readonly Button _saveImageButton;
        private readonly Button _deleteButton;

        private readonly Panel _contentContainer;
        private readonly TextBox _textBox;
        private readonly PictureBox _pictureBox;
        private readonly SplitContainer _mixedContainer;
        private readonly PictureBox _mixedPictureBox;
        private readonly TextBox _mixedTextBox;
        private readonly Panel _emptyPanel;

        public event Action<ClipItem, bool> CopyRequested;
        public event Action<ClipItem> PinToggled;
        public event Action<ClipItem> DeleteRequested;

        public ModernPreviewPanel(HistoryStore store)
        {
            _store = store;
            Dock = DockStyle.Fill;
            BackColor = ModernTheme.CardBg;
            Padding = new Padding(16, 14, 16, 14);

            _metaHeader = new Panel();
            _metaHeader.Dock = DockStyle.Top;
            _metaHeader.Height = 72;
            _metaHeader.BackColor = ModernTheme.CardBg;

            _titleLabel = new Label();
            _titleLabel.Dock = DockStyle.Top;
            _titleLabel.Height = 26;
            _titleLabel.Font = ModernTheme.FontSubTitle;
            _titleLabel.ForeColor = ModernTheme.TextPrimary;
            _titleLabel.Text = "选择记录查看详情";
            _metaHeader.Controls.Add(_titleLabel);

            _subInfoLabel = new Label();
            _subInfoLabel.Dock = DockStyle.Top;
            _subInfoLabel.Height = 22;
            _subInfoLabel.Font = ModernTheme.FontSmall;
            _subInfoLabel.ForeColor = ModernTheme.TextSecondary;
            _metaHeader.Controls.Add(_subInfoLabel);

            _timeLabel = new Label();
            _timeLabel.Dock = DockStyle.Top;
            _timeLabel.Height = 20;
            _timeLabel.Font = ModernTheme.FontSmall;
            _timeLabel.ForeColor = ModernTheme.TextTertiary;
            _metaHeader.Controls.Add(_timeLabel);

            Controls.Add(_metaHeader);

            _actionBar = new FlowLayoutPanel();
            _actionBar.Dock = DockStyle.Top;
            _actionBar.Height = 44;
            _actionBar.BackColor = ModernTheme.CardBg;
            _actionBar.Padding = new Padding(0, 4, 0, 4);

            _copyPrimaryButton = CreateModernButton("📋 复制到剪贴板 (Enter)", ModernTheme.Accent, Color.White, true);
            _copyPrimaryButton.Width = 180;
            _copyPrimaryButton.Click += delegate { if (_currentItem != null && CopyRequested != null) CopyRequested(_currentItem, false); };
            _actionBar.Controls.Add(_copyPrimaryButton);

            _copyPlainButton = CreateModernButton("📄 纯文本", Color.FromArgb(241, 245, 249), ModernTheme.TextPrimary, false);
            _copyPlainButton.Width = 84;
            _copyPlainButton.Click += delegate { if (_currentItem != null && CopyRequested != null) CopyRequested(_currentItem, true); };
            _actionBar.Controls.Add(_copyPlainButton);

            _pinButton = CreateModernButton("⭐ 置顶", Color.FromArgb(241, 245, 249), ModernTheme.Amber, false);
            _pinButton.Width = 76;
            _pinButton.Click += delegate { if (_currentItem != null && PinToggled != null) PinToggled(_currentItem); };
            _actionBar.Controls.Add(_pinButton);

            _saveImageButton = CreateModernButton("💾 另存为", Color.FromArgb(241, 245, 249), ModernTheme.TextPrimary, false);
            _saveImageButton.Width = 84;
            _saveImageButton.Click += delegate { SaveImageToDisk(); };
            _actionBar.Controls.Add(_saveImageButton);

            _deleteButton = CreateModernButton("🗑", Color.FromArgb(254, 242, 242), ModernTheme.Danger, false);
            _deleteButton.Width = 42;
            _deleteButton.Click += delegate { if (_currentItem != null && DeleteRequested != null) DeleteRequested(_currentItem); };
            _actionBar.Controls.Add(_deleteButton);

            Controls.Add(_actionBar);

            Panel divider = new Panel();
            divider.Dock = DockStyle.Top;
            divider.Height = 1;
            divider.BackColor = ModernTheme.Border;
            divider.Margin = new Padding(0, 4, 0, 8);
            Controls.Add(divider);

            _contentContainer = new Panel();
            _contentContainer.Dock = DockStyle.Fill;
            _contentContainer.Padding = new Padding(0, 10, 0, 0);
            Controls.Add(_contentContainer);

            _textBox = new TextBox();
            _textBox.Dock = DockStyle.Fill;
            _textBox.Multiline = true;
            _textBox.ReadOnly = true;
            _textBox.ScrollBars = ScrollBars.Both;
            _textBox.BorderStyle = BorderStyle.None;
            _textBox.BackColor = Color.FromArgb(250, 252, 255);
            _textBox.ForeColor = ModernTheme.TextPrimary;
            _textBox.Font = ModernTheme.FontCode;
            _contentContainer.Controls.Add(_textBox);

            _pictureBox = new PictureBox();
            _pictureBox.Dock = DockStyle.Fill;
            _pictureBox.BackColor = Color.FromArgb(250, 252, 255);
            _pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            _contentContainer.Controls.Add(_pictureBox);

            _mixedContainer = new SplitContainer();
            _mixedContainer.Dock = DockStyle.Fill;
            _mixedContainer.Orientation = Orientation.Horizontal;
            _mixedContainer.SplitterDistance = 220;
            _mixedContainer.SplitterWidth = 6;
            _mixedContainer.BackColor = ModernTheme.Border;

            _mixedPictureBox = new PictureBox();
            _mixedPictureBox.Dock = DockStyle.Fill;
            _mixedPictureBox.BackColor = Color.FromArgb(250, 252, 255);
            _mixedPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            _mixedContainer.Panel1.Controls.Add(_mixedPictureBox);

            _mixedTextBox = new TextBox();
            _mixedTextBox.Dock = DockStyle.Fill;
            _mixedTextBox.Multiline = true;
            _mixedTextBox.ReadOnly = true;
            _mixedTextBox.ScrollBars = ScrollBars.Both;
            _mixedTextBox.BorderStyle = BorderStyle.None;
            _mixedTextBox.BackColor = Color.FromArgb(250, 252, 255);
            _mixedTextBox.ForeColor = ModernTheme.TextPrimary;
            _mixedTextBox.Font = ModernTheme.FontCode;
            _mixedContainer.Panel2.Controls.Add(_mixedTextBox);

            _contentContainer.Controls.Add(_mixedContainer);

            _emptyPanel = new Panel();
            _emptyPanel.Dock = DockStyle.Fill;
            _emptyPanel.BackColor = ModernTheme.CardBg;
            Label emptyText = new Label();
            emptyText.Dock = DockStyle.Fill;
            emptyText.Text = "在左侧选择一条记录以查看完整内容与详情";
            emptyText.TextAlign = ContentAlignment.MiddleCenter;
            emptyText.ForeColor = ModernTheme.TextTertiary;
            emptyText.Font = ModernTheme.FontBody;
            _emptyPanel.Controls.Add(emptyText);
            _contentContainer.Controls.Add(_emptyPanel);

            SetPreviewItem(null);
        }

        private Button CreateModernButton(string text, Color bg, Color fore, bool isBold)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Height = 34;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = bg;
            btn.ForeColor = fore;
            btn.Font = isBold ? ModernTheme.FontBodyBold : ModernTheme.FontBody;
            btn.Cursor = Cursors.Hand;
            btn.Margin = new Padding(0, 0, 8, 0);
            return btn;
        }

        public void SetPreviewItem(ClipItem item)
        {
            _currentItem = item;
            DisposeImages();

            if (item == null)
            {
                _titleLabel.Text = "未选中任何记录";
                _subInfoLabel.Text = "可在左侧卡片列表中单击选中记录";
                _timeLabel.Text = "";

                _copyPrimaryButton.Enabled = false;
                _copyPlainButton.Enabled = false;
                _pinButton.Enabled = false;
                _saveImageButton.Enabled = false;
                _deleteButton.Enabled = false;

                ShowViewport(_emptyPanel);
                return;
            }

            _copyPrimaryButton.Enabled = true;
            _copyPlainButton.Enabled = item.HasText;
            _pinButton.Enabled = true;
            _pinButton.Text = item.IsPinned ? "★ 已固定" : "⭐ 置顶";
            _saveImageButton.Enabled = item.HasImage;
            _deleteButton.Enabled = true;

            string src = String.IsNullOrEmpty(item.SourceProcessName) ? "系统剪贴板" : item.SourceProcessName;
            _titleLabel.Text = src + " · " + item.DisplayKind;

            string winTitle = String.IsNullOrEmpty(item.SourceWindowTitle) ? "" : " (" + item.SourceWindowTitle + ")";
            string charCount = item.HasText ? " | 字数: " + item.Text.Length + " 字符 (" + CountLines(item.Text) + " 行)" : "";
            string resolution = item.HasImage ? " | 分辨率: " + item.ImageWidth + " × " + item.ImageHeight : "";
            _subInfoLabel.Text = src + winTitle + charCount + resolution;

            _timeLabel.Text = "记录时间: " + item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + " (" + ModernTheme.FormatRelativeTime(item.CreatedAt) + ")";

            if (item.HasText && item.HasImage)
            {
                ShowViewport(_mixedContainer);
                _mixedTextBox.Text = item.Text;
                LoadPictureInto(_mixedPictureBox, _store.GetImagePath(item));
            }
            else if (item.HasImage)
            {
                ShowViewport(_pictureBox);
                LoadPictureInto(_pictureBox, _store.GetImagePath(item));
            }
            else
            {
                ShowViewport(_textBox);
                _textBox.Text = item.Text ?? "";
            }
        }

        private void ShowViewport(Control ctrl)
        {
            _textBox.Visible = (ctrl == _textBox);
            _pictureBox.Visible = (ctrl == _pictureBox);
            _mixedContainer.Visible = (ctrl == _mixedContainer);
            _emptyPanel.Visible = (ctrl == _emptyPanel);
            ctrl.BringToFront();
        }

        private int CountLines(string text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        private void LoadPictureInto(PictureBox pb, string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image loaded = Image.FromStream(ms))
                {
                    pb.Image = new Bitmap(loaded);
                }
            }
            catch
            {
            }
        }

        private void SaveImageToDisk()
        {
            if (_currentItem == null || !_currentItem.HasImage) return;
            string srcPath = _store.GetImagePath(_currentItem);
            if (!File.Exists(srcPath)) return;

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|所有文件 (*.*)|*.*";
                dialog.FileName = "剪藏_" + _currentItem.CreatedAt.ToString("yyyyMMdd_HHmmss") + ".png";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.Copy(srcPath, dialog.FileName, true);
                        MessageBox.Show(this, "图片已成功导出至:\r\n" + dialog.FileName, "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "导出图片失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        public void DisposeImages()
        {
            if (_pictureBox.Image != null)
            {
                Image img = _pictureBox.Image;
                _pictureBox.Image = null;
                img.Dispose();
            }
            if (_mixedPictureBox.Image != null)
            {
                Image img = _mixedPictureBox.Image;
                _mixedPictureBox.Image = null;
                img.Dispose();
            }
        }
    }

    public sealed class ModernSettingsDialog : Form
    {
        private readonly AppSettings _settings;
        private readonly HistoryStore _store;

        private CheckBox _chkDedupe;
        private CheckBox _chkStartup;
        private CheckBox _chkHotkey;
        private ComboBox _cmbMaxItems;
        private ComboBox _cmbMaxDays;

        public event Action SettingsChanged;
        public event Action ClearAllRequested;

        public ModernSettingsDialog(AppSettings settings, HistoryStore store)
        {
            _settings = settings;
            _store = store;

            Text = "剪藏 - 设置中心";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Width = 520;
            Height = 540;
            BackColor = ModernTheme.CanvasBg;
            Font = ModernTheme.FontBody;

            InitializeUI();
        }

        private void InitializeUI()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(24, 20, 24, 20);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(layout);

            Panel card1 = CreateCard("常规偏好设置");
            _chkDedupe = CreateCheckBox("复制内容去重 (相同内容自动置顶刷新，不重复记录)", _settings.Deduplicate, 14, 38);
            _chkStartup = CreateCheckBox("开机自动启动 (后台静默常驻系统托盘)", _settings.StartWithWindows, 14, 66);
            _chkHotkey = CreateCheckBox("全局快捷键 Ctrl+Shift+V (快速唤起最近历史浮动菜单)", _settings.GlobalHotkeyEnabled, 14, 94);
            card1.Controls.Add(_chkDedupe);
            card1.Controls.Add(_chkStartup);
            card1.Controls.Add(_chkHotkey);
            layout.Controls.Add(card1, 0, 0);

            Panel card2 = CreateCard("历史自动清理策略");
            Label lblNotice = new Label
            {
                Text = "★ 已置顶固定的常用记录永远不会被自动清理删除。",
                ForeColor = ModernTheme.Amber,
                Font = ModernTheme.FontSmall,
                Location = new Point(14, 34),
                AutoSize = true
            };
            card2.Controls.Add(lblNotice);

            Label lblItems = new Label { Text = "保留最大条数:", Location = new Point(14, 62), AutoSize = true };
            _cmbMaxItems = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 58), Width = 140 };
            _cmbMaxItems.Items.AddRange(new object[] { "不限", "最近 100 条", "最近 500 条", "最近 1000 条" });
            SetComboValue(_cmbMaxItems, _settings.MaxItems);
            card2.Controls.Add(lblItems);
            card2.Controls.Add(_cmbMaxItems);

            Label lblDays = new Label { Text = "保留最大天数:", Location = new Point(14, 94), AutoSize = true };
            _cmbMaxDays = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 90), Width = 140 };
            _cmbMaxDays.Items.AddRange(new object[] { "不限", "最近 7 天", "最近 30 天", "最近 90 天" });
            SetComboDaysValue(_cmbMaxDays, _settings.MaxDays);
            card2.Controls.Add(lblDays);
            card2.Controls.Add(_cmbMaxDays);
            layout.Controls.Add(card2, 0, 1);

            Panel card3 = CreateCard("数据与存储");
            long sizeBytes = _store.GetImageCacheSizeBytes();
            string sizeStr = (sizeBytes / (1024F * 1024F)).ToString("0.1") + " MB";
            Label lblStorage = new Label
            {
                Text = "图片缓存占用: " + sizeStr + "\r\n数据目录: " + _store.RootDirectory,
                Location = new Point(14, 36),
                AutoSize = true,
                ForeColor = ModernTheme.TextSecondary
            };
            card3.Controls.Add(lblStorage);

            Button btnOpenDir = new Button
            {
                Text = "📂 打开目录",
                Location = new Point(14, 86),
                Width = 100,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(241, 245, 249),
                Cursor = Cursors.Hand
            };
            btnOpenDir.FlatAppearance.BorderSize = 0;
            btnOpenDir.Click += delegate
            {
                try { Process.Start("explorer.exe", _store.RootDirectory); } catch { }
            };
            card3.Controls.Add(btnOpenDir);

            Button btnClearAll = new Button
            {
                Text = "🗑 清空全部历史",
                Location = new Point(124, 86),
                Width = 120,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = ModernTheme.DangerSoft,
                ForeColor = ModernTheme.Danger,
                Cursor = Cursors.Hand
            };
            btnClearAll.FlatAppearance.BorderSize = 0;
            btnClearAll.Click += delegate
            {
                if (ClearAllRequested != null) ClearAllRequested();
            };
            card3.Controls.Add(btnClearAll);
            layout.Controls.Add(card3, 0, 2);

            Panel footer = new Panel { Dock = DockStyle.Fill };
            Button btnSave = new Button
            {
                Text = "确定并保存",
                DialogResult = DialogResult.OK,
                Width = 110,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = ModernTheme.Accent,
                ForeColor = Color.White,
                Font = ModernTheme.FontBodyBold,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Location = new Point(footer.ClientSize.Width - 110, 10);
            btnSave.Click += delegate { ApplyAndSave(); };
            footer.Controls.Add(btnSave);
            layout.Controls.Add(footer, 0, 3);
        }

        private Panel CreateCard(string title)
        {
            Panel card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ModernTheme.CardBg,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(14)
            };

            Label lblTitle = new Label
            {
                Text = title,
                Font = ModernTheme.FontBodyBold,
                ForeColor = ModernTheme.TextPrimary,
                Location = new Point(14, 10),
                AutoSize = true
            };
            card.Controls.Add(lblTitle);
            return card;
        }

        private CheckBox CreateCheckBox(string text, bool isChecked, int x, int y)
        {
            return new CheckBox
            {
                Text = text,
                Checked = isChecked,
                Location = new Point(x, y),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
        }

        private void SetComboValue(ComboBox cmb, int val)
        {
            if (val == 100) cmb.SelectedIndex = 1;
            else if (val == 500) cmb.SelectedIndex = 2;
            else if (val == 1000) cmb.SelectedIndex = 3;
            else cmb.SelectedIndex = 0;
        }

        private void SetComboDaysValue(ComboBox cmb, int val)
        {
            if (val == 7) cmb.SelectedIndex = 1;
            else if (val == 30) cmb.SelectedIndex = 2;
            else if (val == 90) cmb.SelectedIndex = 3;
            else cmb.SelectedIndex = 0;
        }

        private void ApplyAndSave()
        {
            _settings.Deduplicate = _chkDedupe.Checked;
            _settings.StartWithWindows = _chkStartup.Checked;
            _settings.GlobalHotkeyEnabled = _chkHotkey.Checked;

            int[] itemVals = { 0, 100, 500, 1000 };
            _settings.MaxItems = itemVals[_cmbMaxItems.SelectedIndex >= 0 ? _cmbMaxItems.SelectedIndex : 0];

            int[] dayVals = { 0, 7, 30, 90 };
            _settings.MaxDays = dayVals[_cmbMaxDays.SelectedIndex >= 0 ? _cmbMaxDays.SelectedIndex : 0];

            _store.SaveSettings(_settings);
            if (SettingsChanged != null) SettingsChanged();
            Close();
        }
    }

    #endregion

    #region Main Form (Modern Redesigned)

    public sealed class MainForm : Form
    {
        private const int HotkeyId = 0x5143;

        private readonly HistoryStore _store;
        private readonly List<ClipItem> _items;
        private readonly AppSettings _settings;
        private readonly string _wakeRequestPath;

        private ModernFilterTabs _filterTabs;
        private ModernSearchBar _searchBar;
        private ModernCardListView _cardList;
        private ModernPreviewPanel _previewPanel;
        private Label _footerStatusLabel;
        private Label _footerShortcutHint;

        private StartupHintForm _startupHint;
        private NotifyIcon _notifyIcon;
        private Icon _appIcon;
        private ContextMenuStrip _quickMenu;
        private ContextMenuStrip _listContextMenu;

        private bool _reallyExit;
        private bool _hotkeyRegistered;
        private System.Windows.Forms.Timer _wakeRequestTimer;
        private DateTime _lastWakeRequestTime = DateTime.MinValue;
        private DateTime _suppressClipboardUntil = DateTime.MinValue;

        public MainForm()
        {
            _store = new HistoryStore();
            _wakeRequestPath = Path.Combine(_store.RootDirectory, "show.request");
            if (File.Exists(_wakeRequestPath))
            {
                _lastWakeRequestTime = File.GetLastWriteTimeUtc(_wakeRequestPath);
            }

            _settings = _store.LoadSettings();
            _items = _store.Load();

            ApplyStartupSetting(false);
            ApplyAutoCleanup(true);

            InitializeComponent();
            RefreshListItems();
        }

        public void StartInBackground()
        {
            ShowInTaskbar = false;
            IntPtr ignored = Handle;
            Hide();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(1600, "剪藏已在后台就绪", "快捷键 Ctrl+Shift+V 随时唤起历史记录。", ToolTipIcon.Info);
            }

            ShowStartupHint();
            StartWakeRequestTimer();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.AddClipboardFormatListener(Handle);
            RegisterGlobalHotkey();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterGlobalHotkey();
            NativeMethods.RemoveClipboardFormatListener(Handle);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                BeginInvoke(new MethodInvoker(CaptureClipboardChange));
            }
            else if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                BeginInvoke(new MethodInvoker(ShowQuickHistoryMenu));
            }
            else if (m.Msg == NativeMethods.WM_SHOW_HISTORY)
            {
                BeginInvoke(new MethodInvoker(ShowFromTray));
            }

            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = true;
                    _notifyIcon.ShowBalloonTip(1200, "剪藏已最小化至托盘", "快捷键 Ctrl+Shift+V 随时呼出。", ToolTipIcon.Info);
                }
                return;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            if (_quickMenu != null)
            {
                _quickMenu.Dispose();
                _quickMenu = null;
            }

            if (_listContextMenu != null)
            {
                _listContextMenu.Dispose();
                _listContextMenu = null;
            }

            if (_wakeRequestTimer != null)
            {
                _wakeRequestTimer.Stop();
                _wakeRequestTimer.Dispose();
                _wakeRequestTimer = null;
            }

            if (_previewPanel != null)
            {
                _previewPanel.DisposeImages();
            }

            ThumbnailManager.Clear();

            if (_appIcon != null)
            {
                _appIcon.Dispose();
            }

            base.OnFormClosing(e);
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            _appIcon = LoadApplicationIcon();
            Icon = _appIcon;
            Text = "剪藏 - 剪贴板历史器";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 600);
            Size = new Size(1160, 740);
            BackColor = ModernTheme.CanvasBg;
            Font = ModernTheme.FontBody;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            root.BackColor = ModernTheme.CanvasBg;
            Controls.Add(root);

            Panel header = BuildModernHeader();
            root.Controls.Add(header, 0, 0);

            _filterTabs = new ModernFilterTabs();
            _filterTabs.Dock = DockStyle.Fill;
            _filterTabs.FilterChanged += delegate { RefreshListItems(); };
            root.Controls.Add(_filterTabs, 0, 1);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            split.BackColor = ModernTheme.Border;
            split.SplitterDistance = 620;
            root.Controls.Add(split, 0, 2);

            _cardList = new ModernCardListView(_store);
            _cardList.Dock = DockStyle.Fill;
            _cardList.SelectionChanged += delegate
            {
                _previewPanel.SetPreviewItem(_cardList.SelectedItem);
                UpdateFooterCounts();
            };
            _cardList.ItemDoubleClicked += delegate { CopySelectedItemAndHide(); };
            _cardList.QuickCopyClicked += delegate(ClipItem item) { CopyItemBackToClipboard(item, true, false); };
            _cardList.QuickPinClicked += delegate(ClipItem item) { TogglePinned(item); };
            _cardList.QuickDeleteClicked += delegate(ClipItem item) { ConfirmAndDeleteItem(item); };

            _listContextMenu = new ContextMenuStrip();
            _listContextMenu.Opening += delegate { RebuildListContextMenu(); };
            _cardList.ContextMenuStrip = _listContextMenu;

            split.Panel1.Controls.Add(_cardList);

            _previewPanel = new ModernPreviewPanel(_store);
            _previewPanel.CopyRequested += delegate(ClipItem item, bool plain) { CopyItemBackToClipboard(item, false, plain); };
            _previewPanel.PinToggled += delegate(ClipItem item) { TogglePinned(item); };
            _previewPanel.DeleteRequested += delegate(ClipItem item) { ConfirmAndDeleteItem(item); };
            split.Panel2.Controls.Add(_previewPanel);

            Panel footer = BuildModernFooter();
            root.Controls.Add(footer, 0, 3);

            ConfigureTrayIcon();

            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F)
                {
                    _searchBar.InnerTextBox.Focus();
                    _searchBar.InnerTextBox.SelectAll();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    if (!String.IsNullOrEmpty(_searchBar.SearchText))
                    {
                        _searchBar.SearchText = "";
                        e.Handled = true;
                    }
                    else
                    {
                        Hide();
                        e.Handled = true;
                    }
                }
            };

            ResumeLayout(false);
        }

        private Panel BuildModernHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = ModernTheme.HeaderBg;
            header.Padding = new Padding(16, 10, 16, 10);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 3;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new RowStyle(SizeType.Absolute, 240F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(layout);

            FlowLayoutPanel brandPanel = new FlowLayoutPanel();
            brandPanel.Dock = DockStyle.Fill;
            brandPanel.WrapContents = false;
            brandPanel.Margin = new Padding(0);

            PictureBox iconBox = new PictureBox();
            iconBox.Size = new Size(32, 32);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = _appIcon.ToBitmap();
            iconBox.Margin = new Padding(0, 2, 8, 0);
            brandPanel.Controls.Add(iconBox);

            Label titleLabel = new Label();
            titleLabel.Text = "剪藏";
            titleLabel.Font = ModernTheme.FontTitle;
            titleLabel.ForeColor = ModernTheme.TextPrimary;
            titleLabel.AutoSize = true;
            titleLabel.Margin = new Padding(0, 4, 8, 0);
            brandPanel.Controls.Add(titleLabel);

            Label badge = new Label();
            badge.Text = "● 监听中";
            badge.Font = ModernTheme.FontSmallBold;
            badge.ForeColor = ModernTheme.Success;
            badge.AutoSize = true;
            badge.Margin = new Padding(0, 8, 0, 0);
            brandPanel.Controls.Add(badge);

            layout.Controls.Add(brandPanel, 0, 0);

            _searchBar = new ModernSearchBar();
            _searchBar.Dock = DockStyle.Fill;
            _searchBar.SearchChanged += delegate { RefreshListItems(); };
            layout.Controls.Add(_searchBar, 1, 0);

            FlowLayoutPanel rightTools = new FlowLayoutPanel();
            rightTools.Dock = DockStyle.Fill;
            rightTools.AutoSize = true;
            rightTools.WrapContents = false;
            rightTools.Margin = new Padding(8, 0, 0, 0);

            Button btnSettings = new Button();
            btnSettings.Text = "⚙ 设置";
            btnSettings.Height = 34;
            btnSettings.Width = 78;
            btnSettings.FlatStyle = FlatStyle.Flat;
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.BackColor = Color.FromArgb(243, 246, 250);
            btnSettings.ForeColor = ModernTheme.TextPrimary;
            btnSettings.Font = ModernTheme.FontBody;
            btnSettings.Cursor = Cursors.Hand;
            btnSettings.Click += delegate { OpenSettingsDialog(); };
            rightTools.Controls.Add(btnSettings);

            Button btnClean = new Button();
            btnClean.Text = "🗑 清理";
            btnClean.Height = 34;
            btnClean.Width = 72;
            btnClean.FlatStyle = FlatStyle.Flat;
            btnClean.FlatAppearance.BorderSize = 0;
            btnClean.BackColor = ModernTheme.DangerSoft;
            btnClean.ForeColor = ModernTheme.Danger;
            btnClean.Font = ModernTheme.FontBody;
            btnClean.Cursor = Cursors.Hand;
            btnClean.Margin = new Padding(8, 0, 0, 0);
            btnClean.Click += delegate { PromptCleanOptions(); };
            rightTools.Controls.Add(btnClean);

            layout.Controls.Add(rightTools, 2, 0);

            Panel div = new Panel();
            div.Dock = DockStyle.Bottom;
            div.Height = 1;
            div.BackColor = ModernTheme.Border;
            header.Controls.Add(div);

            return header;
        }

        private Panel BuildModernFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Color.FromArgb(250, 252, 255);
            footer.Padding = new Padding(14, 0, 14, 0);

            Panel topBorder = new Panel();
            topBorder.Dock = DockStyle.Top;
            topBorder.Height = 1;
            topBorder.BackColor = ModernTheme.Border;
            footer.Controls.Add(topBorder);

            _footerShortcutHint = new Label();
            _footerShortcutHint.Dock = DockStyle.Left;
            _footerShortcutHint.Width = 550;
            _footerShortcutHint.TextAlign = ContentAlignment.MiddleLeft;
            _footerShortcutHint.ForeColor = ModernTheme.TextTertiary;
            _footerShortcutHint.Font = ModernTheme.FontSmall;
            _footerShortcutHint.Text = "⌨ 操作指南: [↑↓] 切换选择   [Enter] 立即复制   [Delete] 删除   [Ctrl+P] 置顶   [Ctrl+F] 搜索";
            footer.Controls.Add(_footerShortcutHint);

            _footerStatusLabel = new Label();
            _footerStatusLabel.Dock = DockStyle.Right;
            _footerStatusLabel.Width = 400;
            _footerStatusLabel.TextAlign = ContentAlignment.MiddleRight;
            _footerStatusLabel.ForeColor = ModernTheme.TextSecondary;
            _footerStatusLabel.Font = ModernTheme.FontSmall;
            footer.Controls.Add(_footerStatusLabel);

            return footer;
        }

        private void RefreshListItems()
        {
            List<ClipItem> filtered = GetFilteredItems();

            int pinCount = 0;
            int textCount = 0;
            int imgCount = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].IsPinned) pinCount++;
                if (_items[i].HasText && !_items[i].HasImage) textCount++;
                if (_items[i].HasImage) imgCount++;
            }

            _filterTabs.UpdateCounts(_items.Count, pinCount, textCount, imgCount);
            _cardList.SetItems(filtered);

            UpdateFooterCounts();
        }

        private List<ClipItem> GetFilteredItems()
        {
            string query = _searchBar != null ? _searchBar.SearchText.Trim() : "";
            ModernFilterTabs.FilterType tabType = _filterTabs != null ? _filterTabs.SelectedFilter : ModernFilterTabs.FilterType.All;

            List<ClipItem> pinned = new List<ClipItem>();
            List<ClipItem> normal = new List<ClipItem>();

            for (int i = 0; i < _items.Count; i++)
            {
                ClipItem item = _items[i];

                if (tabType == ModernFilterTabs.FilterType.Pinned && !item.IsPinned) continue;
                if (tabType == ModernFilterTabs.FilterType.Text && (!item.HasText || item.HasImage)) continue;
                if (tabType == ModernFilterTabs.FilterType.Image && !item.HasImage) continue;

                if (!String.IsNullOrEmpty(query))
                {
                    if (!MatchesSearch(item, query)) continue;
                }

                if (item.IsPinned) pinned.Add(item);
                else normal.Add(item);
            }

            pinned.AddRange(normal);
            return pinned;
        }

        private bool MatchesSearch(ClipItem item, string query)
        {
            return ContainsIgnoreCase(item.Text, query)
                || ContainsIgnoreCase(item.DisplayKind, query)
                || ContainsIgnoreCase(item.SourceProcessName, query)
                || ContainsIgnoreCase(item.SourceWindowTitle, query)
                || ContainsIgnoreCase(item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), query);
        }

        private bool ContainsIgnoreCase(string text, string query)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(query)) return false;
            return text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void UpdateFooterCounts()
        {
            if (_footerStatusLabel == null) return;
            int visible = _cardList != null ? _cardList.TotalCount : 0;
            _footerStatusLabel.Text = "共 " + _items.Count + " 条记录 | 显示 " + visible + " 条 | 全局快捷键: Ctrl+Shift+V";
        }

        private void CopySelectedItemAndHide()
        {
            ClipItem item = _cardList.SelectedItem;
            if (item != null)
            {
                CopyItemBackToClipboard(item, false, false);
                Hide();
            }
        }

        private void CopyItemBackToClipboard(ClipItem item, bool showTrayNotice, bool plainTextOnly)
        {
            if (item == null) return;

            try
            {
                DataObject dataObject = new DataObject();
                if (item.HasText)
                {
                    dataObject.SetText(item.Text, TextDataFormat.UnicodeText);
                }

                Bitmap copiedImage = null;
                bool hasClipboardData = item.HasText;
                try
                {
                    if (item.HasImage && !plainTextOnly)
                    {
                        string imagePath = _store.GetImagePath(item);
                        if (File.Exists(imagePath))
                        {
                            byte[] bytes = File.ReadAllBytes(imagePath);
                            using (MemoryStream ms = new MemoryStream(bytes))
                            using (Image loaded = Image.FromStream(ms))
                            {
                                copiedImage = new Bitmap(loaded);
                                dataObject.SetImage(copiedImage);
                                hasClipboardData = true;
                            }
                        }
                    }

                    if (!hasClipboardData)
                    {
                        throw new InvalidOperationException(plainTextOnly ? "该记录没有可复制的文字内容。" : "记录内容文件已损坏或不可用。");
                    }

                    _suppressClipboardUntil = DateTime.Now.AddMilliseconds(800);
                    Clipboard.SetDataObject(dataObject, true, 5, 80);

                    if (showTrayNotice && _notifyIcon != null)
                    {
                        _notifyIcon.ShowBalloonTip(900, plainTextOnly ? "已复制纯文本" : "已复制到剪贴板",
                            LimitText(item.HasText ? item.Text : "图片记录", 80), ToolTipIcon.Info);
                    }
                }
                finally
                {
                    if (copiedImage != null) copiedImage.Dispose();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制回剪贴板失败: " + ex.Message, "剪藏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void TogglePinned(ClipItem item)
        {
            if (item == null) return;
            item.IsPinned = !item.IsPinned;
            _store.Save(_items);
            RefreshListItems();
            _cardList.SelectedItem = item;
        }

        private void ConfirmAndDeleteItem(ClipItem item)
        {
            if (item == null) return;
            _store.DeleteImage(item);
            _items.Remove(item);
            _store.Save(_items);
            RefreshListItems();
        }

        private void PromptCleanOptions()
        {
            ContextMenuStrip cleanMenu = new ContextMenuStrip();
            cleanMenu.Font = ModernTheme.FontBody;

            ToolStripMenuItem clearUnpinned = new ToolStripMenuItem("清空所有非固定记录");
            clearUnpinned.Click += delegate
            {
                DialogResult res = MessageBox.Show(this, "确定清空全部非固定的剪贴板记录吗？已固定的记录将得到保留。", "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (res == DialogResult.Yes)
                {
                    List<ClipItem> toRemove = new List<ClipItem>();
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (!_items[i].IsPinned) toRemove.Add(_items[i]);
                    }
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        _store.DeleteImage(toRemove[i]);
                        _items.Remove(toRemove[i]);
                    }
                    _store.Save(_items);
                    RefreshListItems();
                }
            };
            cleanMenu.Items.Add(clearUnpinned);

            ToolStripMenuItem clearAll = new ToolStripMenuItem("清空全部历史记录 (包含固定)");
            clearAll.ForeColor = ModernTheme.Danger;
            clearAll.Click += delegate { ClearAllRows(); };
            cleanMenu.Items.Add(clearAll);

            cleanMenu.Show(Cursor.Position);
        }

        private void ClearAllRows()
        {
            if (_items.Count == 0) return;

            DialogResult result = MessageBox.Show(
                this,
                "确定清空全部 " + _items.Count + " 条剪贴板历史记录吗？\r\n此操作不可撤销。",
                "二次确认清空全部",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes) return;

            for (int i = 0; i < _items.Count; i++)
            {
                _store.DeleteImage(_items[i]);
            }

            _items.Clear();
            _store.Save(_items);
            ThumbnailManager.Clear();
            RefreshListItems();
        }

        private void OpenSettingsDialog()
        {
            using (ModernSettingsDialog dlg = new ModernSettingsDialog(_settings, _store))
            {
                dlg.SettingsChanged += delegate
                {
                    ApplyStartupSetting(true);
                    if (_settings.GlobalHotkeyEnabled) RegisterGlobalHotkey();
                    else UnregisterGlobalHotkey();

                    ApplyAutoCleanup(true);
                    RefreshListItems();
                };
                dlg.ClearAllRequested += delegate { ClearAllRows(); };
                dlg.ShowDialog(this);
            }
        }

        private void RebuildListContextMenu()
        {
            _listContextMenu.Items.Clear();
            ClipItem cur = _cardList.SelectedItem;
            if (cur == null) return;

            ToolStripMenuItem copyItem = new ToolStripMenuItem("📋 复制到剪贴板 (Enter)");
            copyItem.Font = ModernTheme.FontBodyBold;
            copyItem.Click += delegate { CopyItemBackToClipboard(cur, false, false); };
            _listContextMenu.Items.Add(copyItem);

            if (cur.HasText)
            {
                ToolStripMenuItem copyPlain = new ToolStripMenuItem("📄 复制为纯文本");
                copyPlain.Click += delegate { CopyItemBackToClipboard(cur, false, true); };
                _listContextMenu.Items.Add(copyPlain);
            }

            ToolStripMenuItem pinItem = new ToolStripMenuItem(cur.IsPinned ? "☆ 取消固定" : "★ 固定置顶 (Ctrl+P)");
            pinItem.Click += delegate { TogglePinned(cur); };
            _listContextMenu.Items.Add(pinItem);

            if (cur.HasImage)
            {
                ToolStripMenuItem saveImgItem = new ToolStripMenuItem("💾 另存图片为...");
                saveImgItem.Click += delegate
                {
                    string p = _store.GetImagePath(cur);
                    if (File.Exists(p))
                    {
                        using (SaveFileDialog sfd = new SaveFileDialog())
                        {
                            sfd.Filter = "PNG 图片 (*.png)|*.png|所有文件 (*.*)|*.*";
                            sfd.FileName = "剪藏_" + cur.CreatedAt.ToString("yyyyMMdd_HHmmss") + ".png";
                            if (sfd.ShowDialog(this) == DialogResult.OK)
                            {
                                File.Copy(p, sfd.FileName, true);
                            }
                        }
                    }
                };
                _listContextMenu.Items.Add(saveImgItem);
            }

            _listContextMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem delItem = new ToolStripMenuItem("🗑 删除此项 (Delete)");
            delItem.ForeColor = ModernTheme.Danger;
            delItem.Click += delegate { ConfirmAndDeleteItem(cur); };
            _listContextMenu.Items.Add(delItem);
        }

        #region Clipboard Capture & Internal Logic

        private void CaptureClipboardChange()
        {
            if (DateTime.Now < _suppressClipboardUntil) return;

            ClipItem item = TryReadClipboardWithRetry();
            if (item == null) return;

            ClipItem duplicate = _settings.Deduplicate ? FindDuplicate(item) : null;
            if (duplicate != null)
            {
                _store.DeleteImage(item);
                duplicate.CreatedAt = item.CreatedAt;
                duplicate.SourceProcessName = item.SourceProcessName;
                duplicate.SourceWindowTitle = item.SourceWindowTitle;
                _items.Remove(duplicate);
                _items.Insert(0, duplicate);
                item = duplicate;
            }
            else
            {
                _items.Insert(0, item);
            }

            ApplyAutoCleanup(false);
            _store.Save(_items);

            RefreshListItems();
            _cardList.SelectedItem = item;
        }

        private ClipItem TryReadClipboardWithRetry()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    return ReadClipboardCore();
                }
                catch (ExternalException)
                {
                    Thread.Sleep(60);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private ClipItem ReadClipboardCore()
        {
            string text = null;
            Image image = null;

            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (String.IsNullOrEmpty(text)) text = null;
            }

            if (Clipboard.ContainsImage())
            {
                image = Clipboard.GetImage();
            }

            if (String.IsNullOrEmpty(text) && image == null) return null;

            ClipItem item = new ClipItem();
            item.Id = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            item.CreatedAt = DateTime.Now;
            item.Text = text;
            item.Kind = image != null ? (String.IsNullOrEmpty(text) ? "Image" : "Mixed") : "Text";
            SetSourceInfo(item);

            byte[] imageBytes = new byte[0];
            try
            {
                if (image != null)
                {
                    imageBytes = _store.SaveImage(image, item);
                }
            }
            finally
            {
                if (image != null) image.Dispose();
            }

            item.ContentHash = ComputeHash(text, imageBytes);
            return item;
        }

        private string ComputeHash(string text, byte[] imageBytes)
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] textBytes = Encoding.UTF8.GetBytes(text ?? "");
                int imageLength = imageBytes == null ? 0 : imageBytes.Length;
                byte[] combined = new byte[textBytes.Length + imageLength + 4];
                Buffer.BlockCopy(textBytes, 0, combined, 0, textBytes.Length);
                if (imageLength > 0)
                {
                    Buffer.BlockCopy(imageBytes, 0, combined, textBytes.Length, imageLength);
                }

                byte[] hash = sha.ComputeHash(combined);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private ClipItem FindDuplicate(ClipItem item)
        {
            if (item == null || String.IsNullOrEmpty(item.ContentHash)) return null;
            for (int i = 0; i < _items.Count; i++)
            {
                ClipItem existing = _items[i];
                if (existing != null && String.Equals(existing.ContentHash, item.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
            return null;
        }

        private void ApplyAutoCleanup(bool saveAfterCleanup)
        {
            List<ClipItem> remove = new List<ClipItem>();
            DateTime cutoff = DateTime.MinValue;
            if (_settings.MaxDays > 0)
            {
                cutoff = DateTime.Now.AddDays(-_settings.MaxDays);
            }

            int unpinnedKept = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                ClipItem item = _items[i];
                if (item == null || item.IsPinned) continue;

                bool tooOld = _settings.MaxDays > 0 && item.CreatedAt < cutoff;
                bool tooMany = _settings.MaxItems > 0 && unpinnedKept >= _settings.MaxItems;
                if (tooOld || tooMany)
                {
                    remove.Add(item);
                }
                else
                {
                    unpinnedKept++;
                }
            }

            if (remove.Count == 0) return;

            for (int i = 0; i < remove.Count; i++)
            {
                _store.DeleteImage(remove[i]);
                _items.Remove(remove[i]);
            }

            if (saveAfterCleanup)
            {
                _store.Save(_items);
            }
        }

        private void SetSourceInfo(ClipItem item)
        {
            if (item == null) return;
            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                int processId;
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
                if (processId > 0)
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        item.SourceProcessName = process.ProcessName;
                    }
                }

                StringBuilder title = new StringBuilder(256);
                if (NativeMethods.GetWindowText(hwnd, title, title.Capacity) > 0)
                {
                    item.SourceWindowTitle = title.ToString();
                }
            }
            catch
            {
            }
        }

        #endregion

        #region Tray & Hotkey

        private void ConfigureTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = ModernTheme.FontBody;
            menu.Opening += delegate { RebuildTrayMenu(menu); };
            RebuildTrayMenu(menu);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "剪藏";
            _notifyIcon.Icon = _appIcon;
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private void RebuildTrayMenu(ContextMenuStrip menu)
        {
            while (menu.Items.Count > 0)
            {
                ToolStripItem itm = menu.Items[0];
                menu.Items.RemoveAt(0);
                itm.Dispose();
            }

            ToolStripMenuItem showItem = new ToolStripMenuItem("📌 打开剪藏主窗口");
            showItem.Font = ModernTheme.FontBodyBold;
            showItem.Click += delegate { ShowFromTray(); };
            menu.Items.Add(showItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem recentTitle = new ToolStripMenuItem("最近复制记录 (快捷呼出)");
            recentTitle.Enabled = false;
            menu.Items.Add(recentTitle);

            if (_items.Count == 0)
            {
                ToolStripMenuItem emptyItem = new ToolStripMenuItem("暂无记录");
                emptyItem.Enabled = false;
                menu.Items.Add(emptyItem);
            }
            else
            {
                int count = Math.Min(18, _items.Count);
                for (int i = 0; i < count; i++)
                {
                    ClipItem item = _items[i];
                    ToolStripMenuItem recordItem = new ToolStripMenuItem(BuildTrayRecordText(item, i + 1));
                    recordItem.Tag = item;
                    recordItem.Click += delegate(object sender, EventArgs e)
                    {
                        ToolStripItem clicked = sender as ToolStripItem;
                        ClipItem clickedItem = clicked == null ? null : clicked.Tag as ClipItem;
                        CopyItemBackToClipboard(clickedItem, true, false);
                    };
                    menu.Items.Add(recordItem);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("⚙ 设置中心");
            settingsItem.Click += delegate { OpenSettingsDialog(); };
            menu.Items.Add(settingsItem);

            ToolStripMenuItem exitItem = new ToolStripMenuItem("❌ 退出程序");
            exitItem.Click += delegate
            {
                _reallyExit = true;
                Close();
                Application.ExitThread();
            };
            menu.Items.Add(exitItem);
        }

        private string BuildTrayRecordText(ClipItem item, int displayIndex)
        {
            if (item == null) return (displayIndex.ToString("00") + ". 空记录");

            string text = item.HasText ? LimitText(item.Text.Replace("\r", " ").Replace("\n", " "), 32) :
                          (item.HasImage ? "图片 " + item.ImageWidth + "x" + item.ImageHeight : "未知记录");

            string pin = item.IsPinned ? "★ " : "";
            string src = String.IsNullOrEmpty(item.SourceProcessName) ? "" : "[" + item.SourceProcessName + "] ";
            return (displayIndex.ToString("00") + ". " + pin + src + text).Replace("&", "&&");
        }

        private string LimitText(string text, int max)
        {
            if (String.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        private void RegisterGlobalHotkey()
        {
            if (_hotkeyRegistered || !_settings.GlobalHotkeyEnabled) return;
            try
            {
                bool ok = NativeMethods.RegisterHotKey(Handle, HotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, (uint)Keys.V);
                _hotkeyRegistered = ok;
            }
            catch
            {
                _hotkeyRegistered = false;
            }
        }

        private void UnregisterGlobalHotkey()
        {
            if (!_hotkeyRegistered) return;
            try
            {
                NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            }
            catch { }
            _hotkeyRegistered = false;
        }

        private void ShowQuickHistoryMenu()
        {
            if (_quickMenu == null || _quickMenu.IsDisposed)
            {
                _quickMenu = new ContextMenuStrip();
                _quickMenu.Font = ModernTheme.FontBody;
            }

            if (_quickMenu.Visible)
            {
                _quickMenu.Close(ToolStripDropDownCloseReason.AppClicked);
                return;
            }

            RebuildTrayMenu(_quickMenu);

            NativeMethods.SetForegroundWindow(Handle);
            _quickMenu.Show(Cursor.Position);
            NativeMethods.PostMessage(Handle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        private void StartWakeRequestTimer()
        {
            if (_wakeRequestTimer != null) return;
            _wakeRequestTimer = new System.Windows.Forms.Timer();
            _wakeRequestTimer.Interval = 800;
            _wakeRequestTimer.Tick += delegate
            {
                try
                {
                    if (!File.Exists(_wakeRequestPath)) return;
                    DateTime wt = File.GetLastWriteTimeUtc(_wakeRequestPath);
                    if (wt <= _lastWakeRequestTime) return;
                    _lastWakeRequestTime = wt;
                    ShowFromTray();
                }
                catch { }
            };
            _wakeRequestTimer.Start();
        }

        private void ShowStartupHint()
        {
            if (_startupHint != null && !_startupHint.IsDisposed) _startupHint.Close();
            _startupHint = new StartupHintForm(_appIcon);
            _startupHint.Show();
        }

        private void ShowFromTray()
        {
            if (_startupHint != null && !_startupHint.IsDisposed) _startupHint.Close();
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ApplyStartupSetting(bool showMessageOnError)
        {
            const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string runValueName = "JianCang";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
                {
                    if (key == null) return;
                    if (_settings.StartWithWindows)
                    {
                        key.SetValue(runValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        if (key.GetValue(runValueName) != null) key.DeleteValue(runValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                if (showMessageOnError)
                {
                    MessageBox.Show(this, "设置开机启动失败: " + ex.Message, "剪藏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private Icon LoadApplicationIcon()
        {
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) return extracted;
            }
            catch { }
            return SystemIcons.Application;
        }

        #endregion
    }

    #endregion

    #region Startup Hint Form (Fluent Style)

    public sealed class StartupHintForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;

        public StartupHintForm(Icon icon)
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Width = 330;
            Height = 84;
            BackColor = Color.FromArgb(20, 26, 38);
            Opacity = 0.95;
            Font = ModernTheme.FontBody;

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);

            PictureBox iconBox = new PictureBox
            {
                Size = new Size(38, 38),
                Location = new Point(16, 23),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = icon == null ? SystemIcons.Application.ToBitmap() : icon.ToBitmap()
            };
            Controls.Add(iconBox);

            Label title = new Label
            {
                AutoSize = false,
                Location = new Point(66, 18),
                Size = new Size(244, 24),
                Text = "剪藏已在后台就绪",
                ForeColor = Color.White,
                Font = ModernTheme.FontSubTitle
            };
            Controls.Add(title);

            Label body = new Label
            {
                AutoSize = false,
                Location = new Point(66, 44),
                Size = new Size(244, 22),
                Text = "快捷键 Ctrl+Shift+V 随时唤起历史",
                ForeColor = Color.FromArgb(180, 195, 215),
                Font = ModernTheme.FontSmall
            };
            Controls.Add(body);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2800;
            _timer.Tick += delegate
            {
                _timer.Stop();
                Close();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            ModernTheme.SetHighQuality(g);

            using (Pen pen = new Pen(Color.FromArgb(60, 120, 220), 1.2F))
            {
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Dispose();
            base.OnFormClosed(e);
        }
    }

    #endregion

    #region Native Methods

    internal static class NativeMethods
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NULL = 0x0000;
        public const int HWND_BROADCAST = 0xffff;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const int WM_SHOW_HISTORY = 0x0400 + 711;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();
    }

    #endregion
}
