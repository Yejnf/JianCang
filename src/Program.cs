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
[assembly: AssemblyDescription("剪贴板历史器")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("剪藏")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]

namespace JianCang
{
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
                if (HasText && HasImage)
                {
                    return "图文";
                }

                if (HasImage)
                {
                    return "图片";
                }

                return "文字";
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
            if (MaxItems < 0)
            {
                MaxItems = 0;
            }

            if (MaxDays < 0)
            {
                MaxDays = 0;
            }
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
            _imageDirectory = Path.Combine(_rootDirectory, "images");
            _historyPath = Path.Combine(_rootDirectory, "history.json");
            _settingsPath = Path.Combine(_rootDirectory, "settings.json");
            Directory.CreateDirectory(_imageDirectory);
        }

        public string RootDirectory
        {
            get { return _rootDirectory; }
        }

        public List<ClipItem> Load()
        {
            List<ClipItem> result = new List<ClipItem>();

            try
            {
                if (!File.Exists(_historyPath))
                {
                    return result;
                }

                using (FileStream stream = new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length == 0)
                    {
                        return result;
                    }

                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<ClipItem>));
                    object data = serializer.ReadObject(stream);
                    if (data != null)
                    {
                        result = (List<ClipItem>)data;
                    }
                }
            }
            catch
            {
                result = new List<ClipItem>();
            }

            return result;
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return AppSettings.CreateDefault();
                }

                using (FileStream stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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
            if (settings == null)
            {
                settings = AppSettings.CreateDefault();
            }

            settings.Normalize();
            Directory.CreateDirectory(_rootDirectory);

            string tempPath = _settingsPath + ".tmp";
            using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
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

        public void Save(IList<ClipItem> items)
        {
            Directory.CreateDirectory(_rootDirectory);
            Directory.CreateDirectory(_imageDirectory);

            string tempPath = _historyPath + ".tmp";
            using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<ClipItem>));
                serializer.WriteObject(stream, new List<ClipItem>(items));
            }

            if (File.Exists(_historyPath))
            {
                File.Delete(_historyPath);
            }

            File.Move(tempPath, _historyPath);
        }

        public string GetImagePath(ClipItem item)
        {
            if (item == null || String.IsNullOrEmpty(item.ImageFileName))
            {
                return String.Empty;
            }

            return Path.Combine(_imageDirectory, item.ImageFileName);
        }

        public byte[] SaveImage(Image source, ClipItem item)
        {
            if (source == null || item == null)
            {
                return new byte[0];
            }

            Directory.CreateDirectory(_imageDirectory);
            item.ImageFileName = item.Id + ".png";
            item.ImageWidth = source.Width;
            item.ImageHeight = source.Height;

            string path = Path.Combine(_imageDirectory, item.ImageFileName);
            using (Bitmap bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(source, 0, 0, source.Width, source.Height);
                }

                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    byte[] bytes = memory.ToArray();
                    File.WriteAllBytes(path, bytes);
                    return bytes;
                }
            }
        }

        public void DeleteImage(ClipItem item)
        {
            try
            {
                string path = GetImagePath(item);
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    public sealed class MainForm : Form
    {
        private const int HotkeyId = 0x5143;

        private readonly HistoryStore _store;
        private readonly List<ClipItem> _items;
        private readonly AppSettings _settings;
        private readonly string _wakeRequestPath;
        private readonly Color _backgroundColor = Color.FromArgb(246, 248, 251);
        private readonly Color _surfaceColor = Color.White;
        private readonly Color _inkColor = Color.FromArgb(28, 37, 54);
        private readonly Color _mutedColor = Color.FromArgb(99, 111, 130);
        private readonly Color _accentColor = Color.FromArgb(26, 150, 136);
        private readonly Color _dangerColor = Color.FromArgb(198, 62, 75);

        private StartupHintForm _startupHint;
        private TextBox _searchBox;
        private Button _clearSearchButton;
        private Label _searchSummaryLabel;
        private ListView _listView;
        private ContextMenuStrip _listContextMenu;
        private Label _statusLabel;
        private Label _metaLabel;
        private Label _emptyLabel;
        private Button _copyButton;
        private Button _copyPlainButton;
        private Button _pinButton;
        private Button _selectAllButton;
        private Button _clearSelectedButton;
        private Button _clearAllButton;
        private TextBox _textPreview;
        private PictureBox _imagePreview;
        private SplitContainer _mixedPreview;
        private PictureBox _mixedImagePreview;
        private TextBox _mixedTextPreview;
        private Panel _blankPreview;
        private ImageList _typeImages;
        private NotifyIcon _notifyIcon;
        private Icon _appIcon;
        private ContextMenuStrip _quickMenu;
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
            LoadHistoryRows();
            UpdateStatus();
            UpdatePreview();
        }

        public void StartInBackground()
        {
            ShowInTaskbar = false;
            IntPtr ignored = Handle;
            Hide();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(1600, "剪藏已在后台运行", "右键托盘图标可查看历史记录。", ToolTipIcon.Info);
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
                    _notifyIcon.ShowBalloonTip(1200, "剪藏仍在运行", "右键托盘图标可查看历史记录。", ToolTipIcon.Info);
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

            DisposePreviewImages();
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
            MinimumSize = new Size(900, 560);
            Size = new Size(1120, 720);
            BackColor = _backgroundColor;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            root.BackColor = _backgroundColor;
            Controls.Add(root);

            Panel header = BuildHeader();
            root.Controls.Add(header, 0, 0);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            split.BackColor = Color.FromArgb(226, 232, 240);
            root.Controls.Add(split, 0, 1);
            split.SplitterDistance = 650;

            Panel listPanel = BuildListPanel();
            split.Panel1.Controls.Add(listPanel);

            Panel previewPanel = BuildPreviewPanel();
            split.Panel2.Controls.Add(previewPanel);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Color.FromArgb(238, 242, 247);
            footer.Padding = new Padding(16, 5, 16, 0);
            _statusLabel = new Label();
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.ForeColor = _mutedColor;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(_statusLabel);
            root.Controls.Add(footer, 0, 2);

            ConfigureTrayIcon();

            ResumeLayout(false);
        }

        private Panel BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Color.FromArgb(24, 34, 51);
            header.Padding = new Padding(20, 14, 20, 12);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 3;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(layout);

            PictureBox iconBox = new PictureBox();
            iconBox.Width = 48;
            iconBox.Height = 48;
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = _appIcon.ToBitmap();
            iconBox.Margin = new Padding(0, 2, 10, 0);
            layout.Controls.Add(iconBox, 0, 0);

            TableLayoutPanel titleLayout = new TableLayoutPanel();
            titleLayout.Dock = DockStyle.Fill;
            titleLayout.RowCount = 2;
            titleLayout.ColumnCount = 1;
            titleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
            titleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            layout.Controls.Add(titleLayout, 1, 0);

            Label title = new Label();
            title.Dock = DockStyle.Fill;
            title.Text = "剪藏";
            title.ForeColor = Color.White;
            title.Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold, GraphicsUnit.Point);
            title.TextAlign = ContentAlignment.BottomLeft;
            titleLayout.Controls.Add(title, 0, 0);

            Label subtitle = new Label();
            subtitle.Dock = DockStyle.Fill;
            subtitle.Text = "剪贴板历史器";
            subtitle.ForeColor = Color.FromArgb(190, 205, 224);
            subtitle.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            subtitle.TextAlign = ContentAlignment.TopLeft;
            titleLayout.Controls.Add(subtitle, 0, 1);

            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.AutoSize = true;
            toolbar.WrapContents = false;
            toolbar.FlowDirection = FlowDirection.LeftToRight;
            toolbar.Padding = new Padding(0, 11, 0, 0);
            toolbar.Margin = new Padding(0);
            layout.Controls.Add(toolbar, 2, 0);

            _copyButton = CreateButton("复制选中", _accentColor, Color.White);
            _copyButton.Click += delegate { CopySelectedBackToClipboard(); };
            toolbar.Controls.Add(_copyButton);

            _copyPlainButton = CreateButton("纯文本", Color.FromArgb(54, 111, 165), Color.White);
            _copyPlainButton.Click += delegate { CopySelectedPlainText(); };
            toolbar.Controls.Add(_copyPlainButton);

            _pinButton = CreateButton("固定", Color.FromArgb(159, 116, 35), Color.White);
            _pinButton.Click += delegate { TogglePinnedForSelection(); };
            toolbar.Controls.Add(_pinButton);

            _selectAllButton = CreateButton("全选", Color.FromArgb(65, 83, 111), Color.White);
            _selectAllButton.Click += delegate { SelectAllRows(); };
            toolbar.Controls.Add(_selectAllButton);

            _clearSelectedButton = CreateButton("清空所选", _dangerColor, Color.White);
            _clearSelectedButton.Click += delegate { ClearSelectedRows(); };
            toolbar.Controls.Add(_clearSelectedButton);

            _clearAllButton = CreateButton("清空全部", Color.FromArgb(105, 65, 82), Color.White);
            _clearAllButton.Click += delegate { ClearAllRows(); };
            toolbar.Controls.Add(_clearAllButton);

            return header;
        }

        private Panel BuildListPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = _surfaceColor;
            panel.Padding = new Padding(14);

            Panel searchPanel = new Panel();
            searchPanel.Dock = DockStyle.Top;
            searchPanel.Height = 54;
            searchPanel.BackColor = _surfaceColor;
            searchPanel.Padding = new Padding(0, 0, 0, 10);

            TableLayoutPanel searchLayout = new TableLayoutPanel();
            searchLayout.Dock = DockStyle.Fill;
            searchLayout.ColumnCount = 4;
            searchLayout.RowCount = 1;
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            searchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            searchPanel.Controls.Add(searchLayout);

            Label searchLabel = new Label();
            searchLabel.Text = "搜索历史";
            searchLabel.Dock = DockStyle.Fill;
            searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            searchLabel.ForeColor = _mutedColor;
            searchLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            searchLayout.Controls.Add(searchLabel, 0, 0);

            _searchBox = new TextBox();
            _searchBox.Dock = DockStyle.Fill;
            _searchBox.BorderStyle = BorderStyle.FixedSingle;
            _searchBox.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            _searchBox.Margin = new Padding(0, 7, 10, 5);
            _searchBox.TextChanged += delegate
            {
                LoadHistoryRows();
                UpdateStatus();
                UpdatePreview();
            };
            searchLayout.Controls.Add(_searchBox, 1, 0);

            _searchSummaryLabel = new Label();
            _searchSummaryLabel.Dock = DockStyle.Fill;
            _searchSummaryLabel.TextAlign = ContentAlignment.MiddleRight;
            _searchSummaryLabel.ForeColor = _mutedColor;
            _searchSummaryLabel.Margin = new Padding(0, 0, 10, 0);
            searchLayout.Controls.Add(_searchSummaryLabel, 2, 0);

            _clearSearchButton = new Button();
            _clearSearchButton.Text = "清除";
            _clearSearchButton.Dock = DockStyle.Fill;
            _clearSearchButton.Margin = new Padding(0, 5, 0, 5);
            _clearSearchButton.FlatStyle = FlatStyle.Flat;
            _clearSearchButton.FlatAppearance.BorderColor = Color.FromArgb(205, 214, 226);
            _clearSearchButton.BackColor = Color.White;
            _clearSearchButton.ForeColor = _mutedColor;
            _clearSearchButton.Enabled = false;
            _clearSearchButton.Click += delegate { _searchBox.Text = ""; };
            searchLayout.Controls.Add(_clearSearchButton, 3, 0);

            Panel contentPanel = new Panel();
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.BackColor = _surfaceColor;

            _typeImages = new ImageList();
            _typeImages.ColorDepth = ColorDepth.Depth32Bit;
            _typeImages.ImageSize = new Size(24, 24);
            _typeImages.Images.Add("文字", DrawTypeIcon("T", Color.FromArgb(42, 116, 190)));
            _typeImages.Images.Add("图片", DrawTypeIcon("P", Color.FromArgb(25, 153, 123)));
            _typeImages.Images.Add("图文", DrawTypeIcon("M", Color.FromArgb(142, 91, 185)));

            _listView = new ListView();
            _listView.Dock = DockStyle.Fill;
            _listView.View = View.Details;
            _listView.CheckBoxes = true;
            _listView.FullRowSelect = true;
            _listView.MultiSelect = true;
            _listView.HideSelection = false;
            _listView.GridLines = true;
            _listView.ShowItemToolTips = true;
            _listView.BorderStyle = BorderStyle.FixedSingle;
            _listView.BackColor = _surfaceColor;
            _listView.ForeColor = _inkColor;
            _listView.SmallImageList = _typeImages;
            _listView.Columns.Add("时间", 160);
            _listView.Columns.Add("固定", 54);
            _listView.Columns.Add("类型", 72);
            _listView.Columns.Add("来源", 140);
            _listView.Columns.Add("内容预览", 340);
            _listView.Columns.Add("尺寸", 90);
            _listView.SelectedIndexChanged += delegate { UpdatePreview(); };
            _listView.ItemChecked += delegate { UpdatePreview(); };
            _listView.DoubleClick += delegate { CopySelectedBackToClipboard(); };
            _listView.Resize += delegate { ResizeListColumns(); };
            _listView.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    ListViewItem row = _listView.GetItemAt(e.X, e.Y);
                    if (row != null && !row.Selected)
                    {
                        _listView.SelectedItems.Clear();
                        row.Selected = true;
                        row.Focused = true;
                    }
                }
            };
            _listContextMenu = new ContextMenuStrip();
            _listContextMenu.Opening += delegate { RebuildListContextMenu(); };
            _listView.ContextMenuStrip = _listContextMenu;
            contentPanel.Controls.Add(_listView);

            _emptyLabel = new Label();
            _emptyLabel.Dock = DockStyle.Fill;
            _emptyLabel.Text = "暂无剪贴板记录";
            _emptyLabel.ForeColor = Color.FromArgb(140, 151, 167);
            _emptyLabel.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Point);
            _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            _emptyLabel.Visible = false;
            contentPanel.Controls.Add(_emptyLabel);
            _emptyLabel.BringToFront();

            panel.Controls.Add(contentPanel);
            panel.Controls.Add(searchPanel);

            return panel;
        }

        private Panel BuildPreviewPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Color.FromArgb(250, 251, 253);
            panel.Padding = new Padding(14);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.Controls.Add(layout);

            Panel metaPanel = new Panel();
            metaPanel.Dock = DockStyle.Fill;
            metaPanel.BackColor = Color.FromArgb(250, 251, 253);
            metaPanel.Padding = new Padding(0, 2, 0, 10);
            _metaLabel = new Label();
            _metaLabel.Dock = DockStyle.Fill;
            _metaLabel.TextAlign = ContentAlignment.MiddleLeft;
            _metaLabel.ForeColor = _inkColor;
            _metaLabel.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
            metaPanel.Controls.Add(_metaLabel);
            layout.Controls.Add(metaPanel, 0, 0);

            Panel contentPanel = new Panel();
            contentPanel.Dock = DockStyle.Fill;
            contentPanel.BackColor = Color.White;
            contentPanel.Padding = new Padding(14);
            layout.Controls.Add(contentPanel, 0, 1);

            _textPreview = new TextBox();
            _textPreview.Dock = DockStyle.Fill;
            _textPreview.Multiline = true;
            _textPreview.ReadOnly = true;
            _textPreview.ScrollBars = ScrollBars.Both;
            _textPreview.BorderStyle = BorderStyle.None;
            _textPreview.BackColor = Color.White;
            _textPreview.ForeColor = _inkColor;
            _textPreview.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            contentPanel.Controls.Add(_textPreview);

            _imagePreview = new PictureBox();
            _imagePreview.Dock = DockStyle.Fill;
            _imagePreview.BackColor = Color.White;
            _imagePreview.SizeMode = PictureBoxSizeMode.Zoom;
            contentPanel.Controls.Add(_imagePreview);

            _mixedPreview = new SplitContainer();
            _mixedPreview.Dock = DockStyle.Fill;
            _mixedPreview.Orientation = Orientation.Horizontal;
            _mixedPreview.SplitterWidth = 5;
            _mixedPreview.Panel1.BackColor = Color.White;
            _mixedPreview.Panel2.BackColor = Color.White;
            _mixedPreview.SplitterDistance = 260;
            _mixedImagePreview = new PictureBox();
            _mixedImagePreview.Dock = DockStyle.Fill;
            _mixedImagePreview.BackColor = Color.White;
            _mixedImagePreview.SizeMode = PictureBoxSizeMode.Zoom;
            _mixedPreview.Panel1.Controls.Add(_mixedImagePreview);
            _mixedTextPreview = new TextBox();
            _mixedTextPreview.Dock = DockStyle.Fill;
            _mixedTextPreview.Multiline = true;
            _mixedTextPreview.ReadOnly = true;
            _mixedTextPreview.ScrollBars = ScrollBars.Both;
            _mixedTextPreview.BorderStyle = BorderStyle.None;
            _mixedTextPreview.BackColor = Color.White;
            _mixedTextPreview.ForeColor = _inkColor;
            _mixedTextPreview.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _mixedPreview.Panel2.Controls.Add(_mixedTextPreview);
            contentPanel.Controls.Add(_mixedPreview);

            _blankPreview = new Panel();
            _blankPreview.Dock = DockStyle.Fill;
            _blankPreview.BackColor = Color.White;
            Label blank = new Label();
            blank.Dock = DockStyle.Fill;
            blank.Text = "选择一条记录查看内容";
            blank.TextAlign = ContentAlignment.MiddleCenter;
            blank.ForeColor = Color.FromArgb(145, 156, 172);
            blank.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
            _blankPreview.Controls.Add(blank);
            contentPanel.Controls.Add(_blankPreview);

            return panel;
        }

        private void ConfigureTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
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
            ClearTrayMenuItems(menu);

            ToolStripMenuItem showItem = new ToolStripMenuItem("查看历史记录");
            showItem.Font = new Font(showItem.Font, FontStyle.Bold);
            showItem.Click += delegate { ShowFromTray(); };
            menu.Items.Add(showItem);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem recentTitle = new ToolStripMenuItem("最近20条复制记录");
            recentTitle.Enabled = false;
            menu.Items.Add(recentTitle);

            if (_items.Count == 0)
            {
                ToolStripMenuItem emptyItem = new ToolStripMenuItem("暂无复制记录");
                emptyItem.Enabled = false;
                menu.Items.Add(emptyItem);
            }
            else
            {
                int count = Math.Min(20, _items.Count);
                for (int index = 0; index < count; index++)
                {
                    ClipItem item = _items[index];
                    ToolStripMenuItem recordItem = new ToolStripMenuItem(BuildTrayRecordText(item, index + 1));
                    recordItem.Tag = item;
                    recordItem.ToolTipText = BuildTrayRecordToolTip(item);
                    recordItem.Click += delegate(object sender, EventArgs e)
                    {
                        ToolStripItem clicked = sender as ToolStripItem;
                        ClipItem clickedItem = clicked == null ? null : clicked.Tag as ClipItem;
                        CopyItemBackToClipboard(clickedItem, true);
                    };
                    menu.Items.Add(recordItem);
                }
            }

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem clearItem = new ToolStripMenuItem("清空全部记录");
            clearItem.Enabled = _items.Count > 0;
            clearItem.Click += delegate { ClearAllRows(); };
            menu.Items.Add(clearItem);

            ToolStripMenuItem settingsItem = BuildSettingsMenu();
            menu.Items.Add(settingsItem);

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate
            {
                _reallyExit = true;
                Close();
                Application.ExitThread();
            };
            menu.Items.Add(exitItem);
        }

        private void ClearTrayMenuItems(ContextMenuStrip menu)
        {
            while (menu.Items.Count > 0)
            {
                ToolStripItem item = menu.Items[0];
                menu.Items.RemoveAt(0);
                item.Dispose();
            }
        }

        private void RebuildListContextMenu()
        {
            if (_listContextMenu == null)
            {
                return;
            }

            ClearTrayMenuItems(_listContextMenu);
            List<ClipItem> targets = GetActionItems();
            ClipItem primary = GetPrimarySelection();

            ToolStripMenuItem copyItem = new ToolStripMenuItem("复制选中");
            copyItem.Enabled = primary != null;
            copyItem.Click += delegate { CopySelectedBackToClipboard(); };
            _listContextMenu.Items.Add(copyItem);

            ToolStripMenuItem copyPlainItem = new ToolStripMenuItem("复制为纯文本");
            copyPlainItem.Enabled = primary != null && primary.HasText;
            copyPlainItem.Click += delegate { CopySelectedPlainText(); };
            _listContextMenu.Items.Add(copyPlainItem);

            ToolStripMenuItem pinItem = new ToolStripMenuItem(ShouldPinActionItems(targets) ? "固定记录" : "取消固定");
            pinItem.Enabled = targets.Count > 0;
            pinItem.Click += delegate { TogglePinnedForSelection(); };
            _listContextMenu.Items.Add(pinItem);

            _listContextMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem deleteItem = new ToolStripMenuItem("清空所选");
            deleteItem.Enabled = targets.Count > 0;
            deleteItem.Click += delegate { ClearSelectedRows(); };
            _listContextMenu.Items.Add(deleteItem);

            ToolStripMenuItem clearSearchItem = new ToolStripMenuItem("清除搜索");
            clearSearchItem.Enabled = _searchBox != null && !String.IsNullOrWhiteSpace(_searchBox.Text);
            clearSearchItem.Click += delegate { _searchBox.Text = ""; };
            _listContextMenu.Items.Add(clearSearchItem);
        }

        private string BuildTrayRecordText(ClipItem item, int displayIndex)
        {
            if (item == null)
            {
                return EscapeMenuText(displayIndex.ToString("00") + ". [记录] 空内容");
            }

            string text = "";
            if (item.HasText)
            {
                text = NormalizeForPreview(item.Text);
            }
            else if (item.HasImage && item.ImageWidth > 0 && item.ImageHeight > 0)
            {
                text = item.ImageWidth.ToString() + " x " + item.ImageHeight.ToString();
            }
            else if (item.HasImage)
            {
                text = "图片记录";
            }

            if (String.IsNullOrEmpty(text))
            {
                text = "空内容";
            }

            text = LimitText(text, 42);
            string pinned = item.IsPinned ? "★ " : "";
            string source = GetSourceDisplay(item);
            if (String.Equals(source, "未知", StringComparison.Ordinal))
            {
                source = "";
            }
            else
            {
                source = source + "  ";
            }

            return EscapeMenuText(displayIndex.ToString("00") + ". " + pinned + "[" + item.DisplayKind + "] " + item.CreatedAt.ToString("HH:mm:ss") + "  " + source + text);
        }

        private string BuildTrayRecordToolTip(ClipItem item)
        {
            if (item == null)
            {
                return "";
            }

            if (item.HasText)
            {
                return LimitText(NormalizeForPreview(item.Text), 180);
            }

            if (item.HasImage && item.ImageWidth > 0 && item.ImageHeight > 0)
            {
                return "图片 " + item.ImageWidth.ToString() + " x " + item.ImageHeight.ToString();
            }

            return item.DisplayKind;
        }

        private string LimitText(string text, int maxLength)
        {
            if (String.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text == null ? "" : text;
            }

            return text.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private string EscapeMenuText(string text)
        {
            return (text == null ? "" : text).Replace("&", "&&");
        }

        private ToolStripMenuItem BuildSettingsMenu()
        {
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置");

            ToolStripMenuItem dedupeItem = new ToolStripMenuItem("复制内容去重");
            dedupeItem.Checked = _settings.Deduplicate;
            dedupeItem.CheckOnClick = true;
            dedupeItem.Click += delegate
            {
                _settings.Deduplicate = dedupeItem.Checked;
                SaveSettings();
            };
            settingsItem.DropDownItems.Add(dedupeItem);

            ToolStripMenuItem startupItem = new ToolStripMenuItem("开机自动启动");
            startupItem.Checked = _settings.StartWithWindows;
            startupItem.CheckOnClick = true;
            startupItem.Click += delegate
            {
                _settings.StartWithWindows = startupItem.Checked;
                ApplyStartupSetting(true);
                SaveSettings();
            };
            settingsItem.DropDownItems.Add(startupItem);

            ToolStripMenuItem hotkeyItem = new ToolStripMenuItem("全局快捷键 Ctrl+Shift+V");
            hotkeyItem.Checked = _settings.GlobalHotkeyEnabled;
            hotkeyItem.CheckOnClick = true;
            hotkeyItem.Click += delegate
            {
                _settings.GlobalHotkeyEnabled = hotkeyItem.Checked;
                if (_settings.GlobalHotkeyEnabled)
                {
                    RegisterGlobalHotkey();
                }
                else
                {
                    UnregisterGlobalHotkey();
                }

                SaveSettings();
            };
            settingsItem.DropDownItems.Add(hotkeyItem);

            settingsItem.DropDownItems.Add(new ToolStripSeparator());
            settingsItem.DropDownItems.Add(BuildMaxItemsMenu());
            settingsItem.DropDownItems.Add(BuildMaxDaysMenu());

            return settingsItem;
        }

        private ToolStripMenuItem BuildMaxItemsMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("自动清理：保留条数");
            AddMaxItemsOption(root, "不限", 0);
            AddMaxItemsOption(root, "最近 100 条", 100);
            AddMaxItemsOption(root, "最近 500 条", 500);
            AddMaxItemsOption(root, "最近 1000 条", 1000);
            return root;
        }

        private void AddMaxItemsOption(ToolStripMenuItem root, string text, int value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = _settings.MaxItems == value;
            item.Tag = value;
            item.Click += delegate(object sender, EventArgs e)
            {
                ToolStripItem clicked = sender as ToolStripItem;
                _settings.MaxItems = clicked == null ? value : (int)clicked.Tag;
                SaveSettings();
                ApplyAutoCleanup(true);
                LoadHistoryRows();
                UpdateStatus();
                UpdatePreview();
            };
            root.DropDownItems.Add(item);
        }

        private ToolStripMenuItem BuildMaxDaysMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("自动清理：保留天数");
            AddMaxDaysOption(root, "不限", 0);
            AddMaxDaysOption(root, "最近 7 天", 7);
            AddMaxDaysOption(root, "最近 30 天", 30);
            AddMaxDaysOption(root, "最近 90 天", 90);
            return root;
        }

        private void AddMaxDaysOption(ToolStripMenuItem root, string text, int value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = _settings.MaxDays == value;
            item.Tag = value;
            item.Click += delegate(object sender, EventArgs e)
            {
                ToolStripItem clicked = sender as ToolStripItem;
                _settings.MaxDays = clicked == null ? value : (int)clicked.Tag;
                SaveSettings();
                ApplyAutoCleanup(true);
                LoadHistoryRows();
                UpdateStatus();
                UpdatePreview();
            };
            root.DropDownItems.Add(item);
        }

        private void SaveSettings()
        {
            _store.SaveSettings(_settings);
        }

        private Button CreateButton(string text, Color backColor, Color foreColor)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 92;
            button.Height = 34;
            button.Margin = new Padding(6, 0, 0, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private Bitmap DrawTypeIcon(string text, Color color)
        {
            Bitmap bitmap = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(235, color)))
                {
                    graphics.FillEllipse(brush, 1, 1, 22, 22);
                }

                using (Font font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    SizeF size = graphics.MeasureString(text, font);
                    graphics.DrawString(text, font, brush, (24F - size.Width) / 2F, (24F - size.Height) / 2F - 1F);
                }
            }

            return bitmap;
        }

        private Icon LoadApplicationIcon()
        {
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null)
                {
                    return extracted;
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }

        private void LoadHistoryRows()
        {
            _listView.BeginUpdate();
            _listView.Items.Clear();

            List<ClipItem> displayItems = GetDisplayItems();
            for (int index = 0; index < displayItems.Count; index++)
            {
                _listView.Items.Add(CreateListViewItem(displayItems[index]));
            }

            _listView.EndUpdate();
            if (_emptyLabel != null)
            {
                _emptyLabel.Text = _items.Count == 0 ? "暂无剪贴板记录" : "没有匹配的记录";
            }
            UpdateSearchSummary(displayItems.Count);
            ResizeListColumns();
        }

        private ListViewItem CreateListViewItem(ClipItem item)
        {
            string preview = CreatePreviewText(item);
            string sizeText = "";
            if (item.HasImage && item.ImageWidth > 0 && item.ImageHeight > 0)
            {
                sizeText = item.ImageWidth.ToString() + " x " + item.ImageHeight.ToString();
            }

            ListViewItem row = new ListViewItem(item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            row.ImageKey = item.DisplayKind;
            row.SubItems.Add(item.IsPinned ? "★" : "");
            row.SubItems.Add(item.DisplayKind);
            row.SubItems.Add(GetSourceDisplay(item));
            row.SubItems.Add(preview);
            row.SubItems.Add(sizeText);
            row.ToolTipText = BuildRecordToolTip(item);
            if (item.IsPinned)
            {
                row.BackColor = Color.FromArgb(255, 250, 232);
            }
            row.Tag = item;
            return row;
        }

        private void UpdateSearchSummary(int visibleCount)
        {
            bool hasQuery = _searchBox != null && !String.IsNullOrWhiteSpace(_searchBox.Text);
            if (_clearSearchButton != null)
            {
                _clearSearchButton.Enabled = hasQuery;
            }

            if (_searchSummaryLabel != null)
            {
                _searchSummaryLabel.Text = hasQuery ? visibleCount.ToString() + " / " + _items.Count.ToString() : _items.Count.ToString() + " 条";
            }
        }

        private string BuildRecordToolTip(ClipItem item)
        {
            if (item == null)
            {
                return "";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append(item.DisplayKind);
            builder.Append(" | ");
            builder.Append(item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.Append(" | 来源：");
            builder.Append(GetSourceDisplay(item));
            if (!String.IsNullOrEmpty(item.SourceWindowTitle))
            {
                builder.Append(" | ");
                builder.Append(item.SourceWindowTitle);
            }
            if (item.IsPinned)
            {
                builder.Append(" | 已固定");
            }

            return LimitText(builder.ToString(), 240);
        }

        private List<ClipItem> GetDisplayItems()
        {
            List<ClipItem> pinned = new List<ClipItem>();
            List<ClipItem> normal = new List<ClipItem>();
            for (int index = 0; index < _items.Count; index++)
            {
                ClipItem item = _items[index];
                if (!MatchesSearch(item))
                {
                    continue;
                }

                if (item.IsPinned)
                {
                    pinned.Add(item);
                }
                else
                {
                    normal.Add(item);
                }
            }

            pinned.AddRange(normal);
            return pinned;
        }

        private bool MatchesSearch(ClipItem item)
        {
            if (item == null || _searchBox == null || String.IsNullOrWhiteSpace(_searchBox.Text))
            {
                return true;
            }

            string query = _searchBox.Text.Trim();
            return ContainsIgnoreCase(item.Text, query)
                || ContainsIgnoreCase(item.DisplayKind, query)
                || ContainsIgnoreCase(GetSourceDisplay(item), query)
                || ContainsIgnoreCase(item.SourceWindowTitle, query)
                || ContainsIgnoreCase(item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), query)
                || ContainsIgnoreCase(CreatePreviewText(item), query);
        }

        private bool ContainsIgnoreCase(string text, string query)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(query))
            {
                return false;
            }

            return text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private string GetSourceDisplay(ClipItem item)
        {
            if (item == null || String.IsNullOrEmpty(item.SourceProcessName))
            {
                return "未知";
            }

            return item.SourceProcessName;
        }

        private string CreatePreviewText(ClipItem item)
        {
            if (item == null)
            {
                return "";
            }

            if (item.HasText)
            {
                string normalized = NormalizeForPreview(item.Text);
                if (normalized.Length > 120)
                {
                    return normalized.Substring(0, 120) + "...";
                }

                return normalized;
            }

            if (item.HasImage)
            {
                return "图片记录";
            }

            return "";
        }

        private string NormalizeForPreview(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }

            StringBuilder builder = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                bool isSpace = Char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                    }

                    lastWasSpace = true;
                }
                else
                {
                    builder.Append(ch);
                    lastWasSpace = false;
                }
            }

            return builder.ToString().Trim();
        }

        private void ResizeListColumns()
        {
            if (_listView == null || _listView.Columns.Count < 6)
            {
                return;
            }

            int available = Math.Max(360, _listView.ClientSize.Width - 28);
            _listView.Columns[0].Width = 160;
            _listView.Columns[1].Width = 54;
            _listView.Columns[2].Width = 72;
            _listView.Columns[3].Width = 140;
            _listView.Columns[5].Width = 92;
            _listView.Columns[4].Width = Math.Max(160, available - 160 - 54 - 72 - 140 - 92);
        }

        private void CaptureClipboardChange()
        {
            if (DateTime.Now < _suppressClipboardUntil)
            {
                return;
            }

            ClipItem item = TryReadClipboardWithRetry();
            if (item == null)
            {
                return;
            }

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

            LoadHistoryRows();
            SelectItemInList(item);

            UpdateStatus();
            UpdatePreview();
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
                if (String.IsNullOrEmpty(text))
                {
                    text = null;
                }
            }

            if (Clipboard.ContainsImage())
            {
                image = Clipboard.GetImage();
            }

            if (String.IsNullOrEmpty(text) && image == null)
            {
                return null;
            }

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
                if (image != null)
                {
                    image.Dispose();
                }
            }

            item.ContentHash = ComputeHash(text, imageBytes);
            return item;
        }

        private string ComputeHash(string text, byte[] imageBytes)
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] textBytes = Encoding.UTF8.GetBytes(text == null ? "" : text);
                int imageLength = imageBytes == null ? 0 : imageBytes.Length;
                byte[] combined = new byte[textBytes.Length + imageLength + 4];
                Buffer.BlockCopy(textBytes, 0, combined, 0, textBytes.Length);
                if (imageLength > 0)
                {
                    Buffer.BlockCopy(imageBytes, 0, combined, textBytes.Length, imageLength);
                }

                byte[] hash = sha.ComputeHash(combined);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private ClipItem FindDuplicate(ClipItem item)
        {
            if (item == null || String.IsNullOrEmpty(item.ContentHash))
            {
                return null;
            }

            for (int index = 0; index < _items.Count; index++)
            {
                ClipItem existing = _items[index];
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
            for (int index = 0; index < _items.Count; index++)
            {
                ClipItem item = _items[index];
                if (item == null || item.IsPinned)
                {
                    continue;
                }

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

            if (remove.Count == 0)
            {
                return;
            }

            for (int index = 0; index < remove.Count; index++)
            {
                _store.DeleteImage(remove[index]);
                _items.Remove(remove[index]);
            }

            if (saveAfterCleanup)
            {
                _store.Save(_items);
            }
        }

        private void SelectItemInList(ClipItem target)
        {
            if (target == null || _listView == null)
            {
                return;
            }

            for (int index = 0; index < _listView.Items.Count; index++)
            {
                if (Object.ReferenceEquals(_listView.Items[index].Tag, target))
                {
                    _listView.Items[index].Selected = true;
                    _listView.Items[index].Focused = true;
                    _listView.Items[index].EnsureVisible();
                    break;
                }
            }
        }

        private void SetSourceInfo(ClipItem item)
        {
            if (item == null)
            {
                return;
            }

            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

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

        private void CopySelectedBackToClipboard()
        {
            CopyItemBackToClipboard(GetPrimarySelection(), false, false);
        }

        private void CopySelectedPlainText()
        {
            CopyItemBackToClipboard(GetPrimarySelection(), false, true);
        }

        private void CopyItemBackToClipboard(ClipItem item, bool showTrayNotice)
        {
            CopyItemBackToClipboard(item, showTrayNotice, false);
        }

        private void CopyItemBackToClipboard(ClipItem item, bool showTrayNotice, bool plainTextOnly)
        {
            if (item == null)
            {
                return;
            }

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
                            using (Image loaded = LoadImageWithoutLock(imagePath))
                            {
                                copiedImage = new Bitmap(loaded);
                                dataObject.SetImage(copiedImage);
                                hasClipboardData = true;
                            }
                        }
                    }

                    if (!hasClipboardData)
                    {
                        throw new InvalidOperationException(plainTextOnly ? "这条记录没有可复制的文字。" : "记录内容不可用。");
                    }

                    _suppressClipboardUntil = DateTime.Now.AddMilliseconds(800);
                    Clipboard.SetDataObject(dataObject, true, 5, 80);
                    if (_statusLabel != null)
                    {
                        _statusLabel.Text = (plainTextOnly ? "已复制纯文本：" : "已复制到剪贴板：") + item.DisplayKind + " | 共 " + _items.Count.ToString() + " 条记录";
                    }

                    if (showTrayNotice && _notifyIcon != null)
                    {
                        _notifyIcon.ShowBalloonTip(900, plainTextOnly ? "已复制纯文本" : "已复制到剪贴板", LimitText(CreatePreviewText(item), 80), ToolTipIcon.Info);
                    }
                }
                finally
                {
                    if (copiedImage != null)
                    {
                        copiedImage.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制回剪贴板失败：\r\n" + ex.Message, "剪藏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SelectAllRows()
        {
            _listView.BeginUpdate();
            for (int index = 0; index < _listView.Items.Count; index++)
            {
                _listView.Items[index].Checked = true;
                _listView.Items[index].Selected = true;
            }
            _listView.EndUpdate();
            UpdatePreview();
        }

        private void TogglePinnedForSelection()
        {
            List<ClipItem> targets = GetActionItems();
            if (targets.Count == 0)
            {
                return;
            }

            bool shouldPin = false;
            for (int index = 0; index < targets.Count; index++)
            {
                if (!targets[index].IsPinned)
                {
                    shouldPin = true;
                    break;
                }
            }

            for (int index = 0; index < targets.Count; index++)
            {
                targets[index].IsPinned = shouldPin;
            }

            _store.Save(_items);
            LoadHistoryRows();
            SelectItemInList(targets[0]);
            UpdateStatus();
            UpdatePreview();
        }

        private void ClearSelectedRows()
        {
            List<ClipItem> targets = GetActionItems();
            if (targets.Count == 0)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "确定清空选中的 " + targets.Count.ToString() + " 条记录吗？\r\n此操作不可撤销。",
                "再次确认清空",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes)
            {
                return;
            }

            RemoveItems(targets);
        }

        private void ClearAllRows()
        {
            if (_items.Count == 0)
            {
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "确定清空全部 " + _items.Count.ToString() + " 条剪贴板记录吗？\r\n此操作不可撤销。",
                "再次确认清空全部",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes)
            {
                return;
            }

            RemoveItems(new List<ClipItem>(_items));
        }

        private void RemoveItems(List<ClipItem> targets)
        {
            for (int index = 0; index < targets.Count; index++)
            {
                ClipItem item = targets[index];
                _store.DeleteImage(item);
                _items.Remove(item);
            }

            _store.Save(_items);
            LoadHistoryRows();
            UpdateStatus();
            UpdatePreview();
        }

        private ClipItem GetPrimarySelection()
        {
            if (_listView.SelectedItems.Count > 0)
            {
                return _listView.SelectedItems[0].Tag as ClipItem;
            }

            if (_listView.CheckedItems.Count == 1)
            {
                return _listView.CheckedItems[0].Tag as ClipItem;
            }

            return null;
        }

        private List<ClipItem> GetActionItems()
        {
            List<ClipItem> result = new List<ClipItem>();
            ListView.CheckedListViewItemCollection checkedItems = _listView.CheckedItems;
            if (checkedItems.Count > 0)
            {
                for (int index = 0; index < checkedItems.Count; index++)
                {
                    ClipItem item = checkedItems[index].Tag as ClipItem;
                    if (item != null && !result.Contains(item))
                    {
                        result.Add(item);
                    }
                }

                return result;
            }

            ListView.SelectedListViewItemCollection selectedItems = _listView.SelectedItems;
            for (int index = 0; index < selectedItems.Count; index++)
            {
                ClipItem item = selectedItems[index].Tag as ClipItem;
                if (item != null && !result.Contains(item))
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private void UpdatePreview()
        {
            List<ClipItem> actionItems = GetActionItems();
            ClipItem primary = GetPrimarySelection();

            _copyButton.Enabled = primary != null;
            _copyPlainButton.Enabled = primary != null && primary.HasText;
            _pinButton.Enabled = actionItems.Count > 0;
            _pinButton.Text = ShouldPinActionItems(actionItems) ? "固定" : "取消固定";
            _clearSelectedButton.Enabled = actionItems.Count > 0;
            _clearAllButton.Enabled = _items.Count > 0;
            _selectAllButton.Enabled = _listView.Items.Count > 0;
            _emptyLabel.Visible = _listView.Items.Count == 0;

            if (actionItems.Count > 1)
            {
                ShowBlankPreview("已选中 " + actionItems.Count.ToString() + " 条记录");
                _metaLabel.Text = "多选记录";
                return;
            }

            if (primary == null)
            {
                ShowBlankPreview(_items.Count == 0 ? "暂无剪贴板记录" : "选择一条记录查看内容");
                _metaLabel.Text = "预览";
                return;
            }

            _metaLabel.Text = (primary.IsPinned ? "固定 | " : "") + primary.DisplayKind + " | " + primary.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + " | 来源：" + GetSourceDisplay(primary) + BuildImageMeta(primary);

            if (primary.HasText && primary.HasImage)
            {
                ShowMixedPreview(primary);
            }
            else if (primary.HasImage)
            {
                ShowImagePreview(primary);
            }
            else
            {
                ShowTextPreview(primary.Text);
            }
        }

        private bool ShouldPinActionItems(List<ClipItem> actionItems)
        {
            if (actionItems == null || actionItems.Count == 0)
            {
                return true;
            }

            for (int index = 0; index < actionItems.Count; index++)
            {
                if (!actionItems[index].IsPinned)
                {
                    return true;
                }
            }

            return false;
        }

        private string BuildImageMeta(ClipItem item)
        {
            if (item != null && item.HasImage && item.ImageWidth > 0 && item.ImageHeight > 0)
            {
                return " | " + item.ImageWidth.ToString() + " x " + item.ImageHeight.ToString();
            }

            return "";
        }

        private void ShowTextPreview(string text)
        {
            SetVisiblePreview(_textPreview);
            _textPreview.Text = text == null ? "" : text;
        }

        private void ShowImagePreview(ClipItem item)
        {
            SetVisiblePreview(_imagePreview);
            SetPicture(_imagePreview, _store.GetImagePath(item));
        }

        private void ShowMixedPreview(ClipItem item)
        {
            SetVisiblePreview(_mixedPreview);
            _mixedTextPreview.Text = item.Text == null ? "" : item.Text;
            SetPicture(_mixedImagePreview, _store.GetImagePath(item));
        }

        private void ShowBlankPreview(string text)
        {
            SetVisiblePreview(_blankPreview);
            if (_blankPreview.Controls.Count > 0)
            {
                _blankPreview.Controls[0].Text = text;
            }
        }

        private void SetVisiblePreview(Control visibleControl)
        {
            _textPreview.Visible = Object.ReferenceEquals(visibleControl, _textPreview);
            _imagePreview.Visible = Object.ReferenceEquals(visibleControl, _imagePreview);
            _mixedPreview.Visible = Object.ReferenceEquals(visibleControl, _mixedPreview);
            _blankPreview.Visible = Object.ReferenceEquals(visibleControl, _blankPreview);

            if (!_imagePreview.Visible)
            {
                DisposePicture(_imagePreview);
            }

            if (!_mixedPreview.Visible)
            {
                DisposePicture(_mixedImagePreview);
            }

            visibleControl.BringToFront();
        }

        private void SetPicture(PictureBox pictureBox, string path)
        {
            DisposePicture(pictureBox);

            if (String.IsNullOrEmpty(path) || !File.Exists(path))
            {
                pictureBox.Image = DrawMissingImage();
                return;
            }

            try
            {
                using (Image image = LoadImageWithoutLock(path))
                {
                    pictureBox.Image = new Bitmap(image);
                }
            }
            catch
            {
                pictureBox.Image = DrawMissingImage();
            }
        }

        private Image LoadImageWithoutLock(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }

        private Bitmap DrawMissingImage()
        {
            Bitmap bitmap = new Bitmap(420, 260, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.FromArgb(210, 218, 228), 2F))
                {
                    graphics.DrawRectangle(pen, 30, 30, 360, 200);
                    graphics.DrawLine(pen, 70, 170, 160, 95);
                    graphics.DrawLine(pen, 160, 95, 235, 160);
                    graphics.DrawLine(pen, 235, 160, 300, 112);
                    graphics.DrawLine(pen, 300, 112, 365, 180);
                }

                using (Font font = new Font("Microsoft YaHei UI", 14F, FontStyle.Regular, GraphicsUnit.Point))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(143, 155, 173)))
                {
                    string text = "图片文件不可用";
                    SizeF size = graphics.MeasureString(text, font);
                    graphics.DrawString(text, font, brush, (420F - size.Width) / 2F, 112F);
                }
            }

            return bitmap;
        }

        private void DisposePreviewImages()
        {
            DisposePicture(_imagePreview);
            DisposePicture(_mixedImagePreview);
        }

        private void DisposePicture(PictureBox pictureBox)
        {
            if (pictureBox != null && pictureBox.Image != null)
            {
                Image old = pictureBox.Image;
                pictureBox.Image = null;
                old.Dispose();
            }
        }

        private void UpdateStatus()
        {
            if (_statusLabel == null)
            {
                return;
            }

            int visibleCount = _listView == null ? _items.Count : _listView.Items.Count;
            _statusLabel.Text = "正在监听剪贴板 | 共 " + _items.Count.ToString() + " 条记录 | 当前显示 " + visibleCount.ToString() + " 条 | 数据目录：" + _store.RootDirectory;
        }

        private void ApplyStartupSetting(bool showErrors)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (_settings.StartWithWindows)
                    {
                        key.SetValue("剪藏", "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue("剪藏", false);
                    }
                }
            }
            catch (Exception ex)
            {
                if (showErrors)
                {
                    MessageBox.Show(this, "更新开机自启设置失败：\r\n" + ex.Message, "剪藏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void RegisterGlobalHotkey()
        {
            if (!_settings.GlobalHotkeyEnabled || _hotkeyRegistered || !IsHandleCreated)
            {
                return;
            }

            _hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, (uint)Keys.V);
            if (!_hotkeyRegistered && _notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(1500, "快捷键不可用", "Ctrl+Shift+V 已被其他程序占用。", ToolTipIcon.Warning);
            }
        }

        private void UnregisterGlobalHotkey()
        {
            if (!_hotkeyRegistered || !IsHandleCreated)
            {
                _hotkeyRegistered = false;
                return;
            }

            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        private void ShowQuickHistoryMenu()
        {
            if (_quickMenu == null || _quickMenu.IsDisposed)
            {
                _quickMenu = new ContextMenuStrip();
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
            if (_wakeRequestTimer != null)
            {
                return;
            }

            _wakeRequestTimer = new System.Windows.Forms.Timer();
            _wakeRequestTimer.Interval = 800;
            _wakeRequestTimer.Tick += delegate { CheckWakeRequest(); };
            _wakeRequestTimer.Start();
        }

        private void CheckWakeRequest()
        {
            try
            {
                if (!File.Exists(_wakeRequestPath))
                {
                    return;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(_wakeRequestPath);
                if (writeTime <= _lastWakeRequestTime)
                {
                    return;
                }

                _lastWakeRequestTime = writeTime;
                ShowFromTray();
            }
            catch
            {
            }
        }

        private void ShowStartupHint()
        {
            if (_startupHint != null && !_startupHint.IsDisposed)
            {
                _startupHint.Close();
            }

            _startupHint = new StartupHintForm(_appIcon);
            _startupHint.Show();
        }

        private void ShowFromTray()
        {
            if (_startupHint != null && !_startupHint.IsDisposed)
            {
                _startupHint.Close();
            }

            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    public sealed class StartupHintForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;

        public StartupHintForm(Icon icon)
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Width = 320;
            Height = 88;
            BackColor = Color.FromArgb(24, 34, 51);
            Opacity = 0.96;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);

            PictureBox iconBox = new PictureBox();
            iconBox.Size = new Size(42, 42);
            iconBox.Location = new Point(18, 22);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = icon == null ? SystemIcons.Application.ToBitmap() : icon.ToBitmap();
            Controls.Add(iconBox);

            Label title = new Label();
            title.AutoSize = false;
            title.Location = new Point(74, 18);
            title.Size = new Size(226, 24);
            title.Text = "剪藏已在后台运行";
            title.ForeColor = Color.White;
            title.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
            Controls.Add(title);

            Label body = new Label();
            body.AutoSize = false;
            body.Location = new Point(74, 44);
            body.Size = new Size(226, 24);
            body.Text = "右键托盘图标可查看历史记录";
            body.ForeColor = Color.FromArgb(203, 214, 230);
            Controls.Add(body);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2800;
            _timer.Tick += delegate
            {
                _timer.Stop();
                Close();
            };
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

    internal static class NativeMethods
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NULL = 0x0000;
        public const int HWND_BROADCAST = 0xffff;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public static readonly int WM_SHOW_HISTORY = RegisterWindowMessage("JianCang_ShowHistory");

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    }
}
