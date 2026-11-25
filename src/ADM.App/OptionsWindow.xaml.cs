using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

public partial class OptionsWindow : ThemedWindow
{
    public sealed class CategoryRow
    {
        public string Name { get; set; } = "";
        public string Extensions { get; set; } = "";
        public string Folder { get; set; } = "";
    }

    public sealed class ServerLimitRow
    {
        public string Sites { get; set; } = "";
        public int Connections { get; set; } = 1;
    }

    public sealed class SiteLoginRow
    {
        public string Sites { get; set; } = "";
        public string UserName { get; set; } = "";
        /// <summary>Plain text only while the dialog is open; OK encrypts it again.</summary>
        public string Password { get; set; } = "";
        public string Masked => Password.Length > 0 ? "••••••••" : "";
    }

    public sealed class SoundRow : Observable
    {
        bool _on;
        string _file = "";
        public SoundEvent Event { get; init; }
        public string Label { get; init; } = "";
        public bool On { get => _on; set => Set(ref _on, value); }
        public string File { get => _file; set => Set(ref _file, value); }
        public string FileLabel => Lang.T("options.sound_file_for", Label);
        public string BrowseLabel => Lang.T("options.browse_sound_for", Label);
        public string PlayLabel => Lang.T("options.play_sound_for", Label);
    }

    readonly DownloadManager _manager;
    readonly ObservableCollection<CategoryRow> _rows;
    readonly ObservableCollection<ServerLimitRow> _limits;
    readonly ObservableCollection<SiteLoginRow> _logins;
    readonly List<SoundRow> _sounds;
    bool _settingConnections;

    /// <param name="tab">The tab to open at, by its header; null opens the first.</param>
    public OptionsWindow(DownloadManager manager, string? tab = null)
    {
        _manager = manager;
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        var s = manager.State.Settings;
        StartupBox.IsChecked = WindowsStartup.IsEnabled;
        ClipboardBox.IsChecked = s.MonitorClipboard;
        UserAgentBox.Text = s.UserAgent;
        PreventKeysBox.Text = s.PreventCaptureKeys;
        ForceKeysBox.Text = s.ForceCaptureKeys;
        ForceOnClickBox.IsChecked = s.ForceOnLinkClick;
        TwoCancelsBox.IsChecked = s.PromptAfterTwoCancels;
        MenuLinkBox.IsChecked = s.MenuDownloadLink;
        MenuAllLinksBox.IsChecked = s.MenuDownloadAllLinks;

        CaptureTypesBox.Text = s.CaptureExtensions;
        ExceptionSitesBox.Text = s.CaptureExceptionSites;
        ExceptionAddressesBox.Text = s.CaptureExceptionAddresses;
        MinSizeBox.Text = s.CaptureMinSizeKB.ToString();

        foreach (var type in Enum.GetValues<ConnectionType>())
            ConnectionTypeBox.Items.Add(new ComboBoxItem { Content = ConnectionTypeLabel(type) });
        ConnectionTypeBox.SelectedIndex = (int)(ConnectionPresets.Connections(s.ConnectionType) == s.DefaultConnections ? s.ConnectionType : ConnectionType.Custom);
        ConnectionsBox.Text = s.DefaultConnections.ToString();
        ConnectionTypeBox.SelectionChanged += ConnectionType_Changed;
        ConnectionsBox.TextChanged += Connections_Changed;
        SpeedBox.Text = s.SpeedLimitKBps.ToString();
        ReconnectBox.Text = s.ReconnectMinutes.ToString();
        QuotaBox.IsChecked = s.QuotaEnabled;
        QuotaMBBox.Text = s.QuotaMB.ToString();
        QuotaHoursBox.Text = s.QuotaHours.ToString();
        _limits = new ObservableCollection<ServerLimitRow>(s.ServerLimits.Select(l => new ServerLimitRow { Sites = l.Sites, Connections = l.Connections }));
        ServerLimitGrid.ItemsSource = _limits;

        var proxy = s.Proxy;
        (proxy.Mode switch
        {
            ProxyMode.None => ProxyNoneBox,
            ProxyMode.Manual => ProxyManualBox,
            ProxyMode.Script => ProxyScriptBox,
            ProxyMode.AutoDetect => ProxyDetectBox,
            _ => ProxySystemBox,
        }).IsChecked = true;
        ProxyScriptUrlBox.Text = proxy.ScriptUrl;
        BrowserProxyBox.IsChecked = s.UseBrowserProxyOnErrors;
        ProxyKindBox.SelectedIndex = (int)proxy.Kind;
        ProxyHostBox.Text = proxy.Host;
        ProxyPortBox.Text = proxy.Port.ToString();
        ProxyUserBox.Text = proxy.UserName ?? "";
        ProxyPasswordBox.Password = Secrets.Unprotect(proxy.ProtectedPassword) ?? "";
        ProxyBypassBox.Text = proxy.Bypass;

        _logins = new ObservableCollection<SiteLoginRow>(s.SiteLogins.Select(l => new SiteLoginRow
        {
            Sites = l.Sites,
            UserName = l.UserName,
            Password = Secrets.Unprotect(l.ProtectedPassword) ?? "",
        }));
        SiteLoginGrid.ItemsSource = _logins;

        RootBox.Text = s.DownloadRoot;
        TempFolderBox.Text = s.TempFolder ?? "";
        _rows = new ObservableCollection<CategoryRow>(manager.State.Categories.Select(c => new CategoryRow
        {
            Name = c.Name,
            Extensions = string.Join(' ', c.Extensions),
            Folder = c.Folder,
        }));
        CategoryGrid.ItemsSource = _rows;

        DuplicateBox.SelectedIndex = (int)s.OnDuplicate;
        ConfirmFinishedBox.IsChecked = s.ConfirmDeleteCompleted;
        ConfirmUnfinishedBox.IsChecked = s.ConfirmDeleteUnfinished;
        ShowCompleteBox.IsChecked = s.ShowCompleteDialog;
        MarkDownloadsBox.IsChecked = s.MarkDownloads;
        IgnoreModifiedBox.IsChecked = s.IgnoreModificationTime;
        VirusScannerBox.Text = s.VirusScanner;
        VirusArgumentsBox.Text = s.VirusScannerArguments;
        VirusScannerBox.TextChanged += VirusScanner_Changed;
        ScannerArgumentsNote.Text = Lang.T("options.file_stands_for", "{file}");
        LimiterAtStartupBox.IsChecked = s.SpeedLimiterOnAtStartup;
        if (tab != null && Tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string?)t.Tag == tab) is { } start) Tabs.SelectedItem = start;

        (s.VideoPanel switch { VideoPanelMode.Off => PanelOffOption, VideoPanelMode.Mini => PanelMiniOption, _ => PanelFullOption }).IsChecked = true;
        PanelMinSizeBox.Text = s.VideoPanelMinSizeKB.ToString();
        PanelExceptionsBox.Text = s.VideoPanelExceptionSites;
        SubtitlesBox.IsChecked = s.DownloadSubtitles;
        ShowVideoTools();

        _sounds = Enum.GetValues<SoundEvent>().Select(sound =>
        {
            var saved = s.Sounds.FirstOrDefault(x => x.Event == sound);
            return new SoundRow { Event = sound, Label = SoundLabel(sound), On = saved?.On == true, File = saved?.File ?? "" };
        }).ToList();
        SoundList.ItemsSource = _sounds;
    }

    static string ConnectionTypeLabel(ConnectionType type) => type switch
    {
        ConnectionType.Low => Lang.T("options.connection_low", ConnectionPresets.Connections(type)),
        ConnectionType.Medium => Lang.T("options.connection_medium", ConnectionPresets.Connections(type)),
        ConnectionType.High => Lang.T("options.connection_high", ConnectionPresets.Connections(type)),
        _ => Lang.T("options.connection_custom"),
    };

    void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Store.LogFolder);
        Process.Start("explorer.exe", $"\"{Store.LogFolder}\"");
    }

    static string SoundLabel(SoundEvent sound) => sound switch
    {
        SoundEvent.DownloadComplete => Lang.T("common.download_complete"),
        SoundEvent.DownloadFailed => Lang.T("options.sound_download_failed"),
        SoundEvent.QueueStarted => Lang.T("options.sound_queue_started"),
        _ => Lang.T("common.queue_finished"),
    };

    void DefaultTypes_Click(object sender, RoutedEventArgs e) => CaptureTypesBox.Text = CaptureRules.DefaultExtensions;

    /// <summary>One line for each tool a site's video needs: where it was found, or that it's missing.</summary>
    void ShowVideoTools()
    {
        var tools = VideoTools.Current;
        static string Line(string name, string? path) => path != null ? Lang.T("options.video_tool_found", name, path) : Lang.T("options.video_tool_missing", name);
        VideoToolsText.Text = string.Join("\n",
            Line(Lang.T("videotools.ytdlp"), tools.YtDlpPath),
            Line(Lang.T("videotools.runtime"), tools.Runtime?.Path),
            Line(Lang.T("videotools.ffmpeg"), tools.FfmpegPath));
    }

    void VideoTools_Click(object sender, RoutedEventArgs e)
    {
        new VideoToolsWindow(_manager.State.Settings) { Owner = this }.ShowDialog();
        ShowVideoTools();
    }

    // A type sets the number of connections, and changing the number by hand makes the type Custom.
    void ConnectionType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionPresets.Connections((ConnectionType)ConnectionTypeBox.SelectedIndex) is not { } count) return;
        _settingConnections = true;
        ConnectionsBox.Text = count.ToString();
        _settingConnections = false;
    }

    void Connections_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingConnections) ConnectionTypeBox.SelectedIndex = (int)ConnectionType.Custom;
    }

    void VirusScanner_Changed(object sender, TextChangedEventArgs e)
    {
        if (VirusScan.KnownArguments(VirusScannerBox.Text) is { } arguments) VirusArgumentsBox.Text = arguments;
    }

    void BrowseScanner_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Lang.T("options.choose_scanner"), Filter = Lang.Filter(Lang.T("options.filter_programs"), "*.exe;*.bat;*.cmd", Lang.T("common.all_files"), "*.*") };
        if (dialog.ShowDialog(this) != true) return;
        // Arguments meant for the scanner chosen before would be wrong for this one.
        if (!string.Equals(dialog.FileName, VirusScannerBox.Text, StringComparison.OrdinalIgnoreCase)) VirusArgumentsBox.Text = "";
        VirusScannerBox.Text = dialog.FileName;
    }

    void SoundBrowse_Click(object sender, RoutedEventArgs e)
    {
        var row = (SoundRow)((FrameworkElement)sender).DataContext;
        var dialog = new OpenFileDialog { Title = Lang.T("options.choose_sound", row.Label), Filter = Lang.Filter(Lang.T("options.filter_sounds"), "*.wav;*.mp3;*.wma", Lang.T("common.all_files"), "*.*") };
        if (dialog.ShowDialog(this) != true) return;
        row.File = dialog.FileName;
        row.On = true;
    }

    void SoundPlay_Click(object sender, RoutedEventArgs e) => Sounds.PlayFile(((SoundRow)((FrameworkElement)sender).DataContext).File.Trim());

    // A PasswordBox cannot be bound, so the password cell copies to and from its row by hand.
    void SiteLoginPassword_Loaded(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.DataContext is SiteLoginRow row) box.Password = row.Password;
        box.Focus();
    }

    void SiteLoginPassword_Changed(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (box.DataContext is SiteLoginRow row) row.Password = box.Password;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : null };
        if (dialog.ShowDialog(this) == true) RootBox.Text = dialog.FolderName;
    }

    void BrowseTemp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(TempFolderBox.Text) ? TempFolderBox.Text : null };
        if (dialog.ShowDialog(this) == true) TempFolderBox.Text = dialog.FolderName;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SpecialKeys.Parse(PreventKeysBox.Text) is not { } preventKeys)
        {
            Warn(Lang.T("options.keys_invalid"), PreventKeysBox);
            return;
        }
        if (SpecialKeys.Parse(ForceKeysBox.Text) is not { } forceKeys)
        {
            Warn(Lang.T("options.keys_invalid"), ForceKeysBox);
            return;
        }
        if (SpecialKeys.Conflict(PreventKeysBox.Text, ForceKeysBox.Text))
        {
            Warn(Lang.T("options.keys_clash"), ForceKeysBox);
            return;
        }
        if (!int.TryParse(ConnectionsBox.Text, out var connections) || connections < 1 || connections > 32)
        {
            Warn(Lang.T("common.connections_range"), ConnectionsBox);
            return;
        }
        if (!long.TryParse(SpeedBox.Text, out var speed) || speed < 0)
        {
            Warn(Lang.T("options.speed_invalid"), SpeedBox);
            return;
        }
        if (!int.TryParse(ReconnectBox.Text, out var reconnectMinutes) || reconnectMinutes < 0)
        {
            Warn(Lang.T("options.reconnect_invalid"), ReconnectBox);
            return;
        }
        if (!long.TryParse(QuotaMBBox.Text, out var quotaMB) || quotaMB < 1)
        {
            Warn(Lang.T("options.quota_invalid"), QuotaMBBox);
            return;
        }
        if (!int.TryParse(QuotaHoursBox.Text, out var quotaHours) || quotaHours < 1 || quotaHours > 23)
        {
            Warn(Lang.T("options.quota_period_invalid"), QuotaHoursBox);
            return;
        }
        if (!long.TryParse(MinSizeBox.Text, out var minSize) || minSize < 0)
        {
            Warn(Lang.T("options.min_size_invalid"), MinSizeBox);
            return;
        }
        if (!long.TryParse(PanelMinSizeBox.Text, out var panelMinSize) || panelMinSize < 0)
        {
            Warn(Lang.T("options.panel_min_size_invalid"), PanelMinSizeBox);
            return;
        }
        if (string.IsNullOrWhiteSpace(CaptureTypesBox.Text))
        {
            Warn(Lang.T("options.capture_types_invalid"), CaptureTypesBox);
            return;
        }
        if (!Path.IsPathFullyQualified(RootBox.Text.Trim()))
        {
            Warn(Lang.T("options.root_invalid"), RootBox);
            return;
        }
        var tempFolder = TempFolderBox.Text.Trim();
        if (tempFolder.Length > 0 && !Path.IsPathFullyQualified(tempFolder))
        {
            Warn(Lang.T("options.temp_invalid"), TempFolderBox);
            return;
        }
        var scanner = VirusScannerBox.Text.Trim().Trim('"');
        // A bare name is left to Windows, which looks along PATH.
        if (Path.IsPathFullyQualified(scanner) && !File.Exists(scanner))
        {
            Warn(Lang.T("options.scanner_invalid"), VirusScannerBox);
            return;
        }

        ServerLimitGrid.CommitEdit();
        var limits = _limits.Where(l => !string.IsNullOrWhiteSpace(l.Sites)).ToList();
        if (limits.Any(l => l.Connections < 1 || l.Connections > 32))
        {
            Warn(Lang.T("options.server_limit_invalid"), ServerLimitGrid);
            return;
        }

        var proxyMode = ProxyManualBox.IsChecked == true ? ProxyMode.Manual
            : ProxyNoneBox.IsChecked == true ? ProxyMode.None
            : ProxyScriptBox.IsChecked == true ? ProxyMode.Script
            : ProxyDetectBox.IsChecked == true ? ProxyMode.AutoDetect
            : ProxyMode.System;
        var scriptUrl = ProxyScriptUrlBox.Text.Trim();
        // The address is saved as plain text, so it can't carry a login.
        if (proxyMode == ProxyMode.Script
            && !(Uri.TryCreate(scriptUrl, UriKind.Absolute, out var script) && script.Scheme is "http" or "https" && script.UserInfo.Length == 0))
        {
            Warn(Lang.T("options.proxy_script_invalid"), ProxyScriptUrlBox);
            return;
        }
        var proxyHost = ProxyHostBox.Text.Trim();
        var portValid = int.TryParse(ProxyPortBox.Text, out var proxyPort) && proxyPort is >= 1 and <= 65535;
        if (proxyMode == ProxyMode.Manual && Uri.CheckHostName(proxyHost) == UriHostNameType.Unknown)
        {
            Warn(Lang.T("options.proxy_host_invalid"), ProxyHostBox);
            return;
        }
        if (proxyMode == ProxyMode.Manual && !portValid)
        {
            Warn(Lang.T("options.proxy_port_invalid"), ProxyPortBox);
            return;
        }

        SiteLoginGrid.CommitEdit();
        var logins = _logins.Where(l => !string.IsNullOrWhiteSpace(l.Sites)).ToList();
        if (logins.Any(l => string.IsNullOrWhiteSpace(l.UserName)))
        {
            Warn(Lang.T("options.site_login_invalid"), SiteLoginGrid);
            return;
        }

        CategoryGrid.CommitEdit();
        var categories = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new Category
            {
                Name = r.Name.Trim(),
                Folder = (r.Folder ?? "").Trim(),
                Extensions = (r.Extensions ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.TrimStart('.').ToLowerInvariant()).ToList(),
            })
            .ToList();
        if (!categories.Any(c => c.Name == Categories.General))
            categories.Add(new Category { Name = Categories.General });

        var s = _manager.State.Settings;
        s.MonitorClipboard = ClipboardBox.IsChecked == true;
        s.UserAgent = string.IsNullOrWhiteSpace(UserAgentBox.Text) ? Settings.DefaultUserAgent : UserAgentBox.Text.Trim();
        s.PreventCaptureKeys = SpecialKeys.Format(preventKeys);
        s.ForceCaptureKeys = SpecialKeys.Format(forceKeys);
        s.ForceOnLinkClick = ForceOnClickBox.IsChecked == true;
        s.PromptAfterTwoCancels = TwoCancelsBox.IsChecked == true;
        s.MenuDownloadLink = MenuLinkBox.IsChecked == true;
        s.MenuDownloadAllLinks = MenuAllLinksBox.IsChecked == true;
        s.CaptureExtensions = NormalizeList(CaptureTypesBox.Text, fileTypes: true);
        s.CaptureExceptionSites = NormalizeList(ExceptionSitesBox.Text);
        s.CaptureExceptionAddresses = NormalizeList(ExceptionAddressesBox.Text);
        s.CaptureMinSizeKB = minSize;
        s.DefaultConnections = connections;
        s.ConnectionType = (ConnectionType)ConnectionTypeBox.SelectedIndex;
        s.SpeedLimitKBps = speed;
        s.ReconnectMinutes = reconnectMinutes;
        s.QuotaEnabled = QuotaBox.IsChecked == true;
        s.QuotaMB = quotaMB;
        s.QuotaHours = quotaHours;
        s.ServerLimits = limits.Select(l => new ServerLimit { Sites = NormalizeList(l.Sites), Connections = l.Connections }).ToList();
        s.Proxy = ReadProxy(s.Proxy, proxyMode, proxyHost, portValid ? proxyPort : s.Proxy.Port);
        s.UseBrowserProxyOnErrors = BrowserProxyBox.IsChecked == true;
        s.SiteLogins = logins.Select(l => new SiteLogin
        {
            Sites = NormalizeList(l.Sites),
            UserName = l.UserName.Trim(),
            ProtectedPassword = Secrets.Protect(l.Password),
        }).ToList();
        s.DownloadRoot = RootBox.Text.Trim();
        s.TempFolder = tempFolder.Length > 0 ? tempFolder : null;
        s.OnDuplicate = (DuplicateAction)Math.Max(0, DuplicateBox.SelectedIndex);
        s.ConfirmDeleteCompleted = ConfirmFinishedBox.IsChecked == true;
        s.ConfirmDeleteUnfinished = ConfirmUnfinishedBox.IsChecked == true;
        s.ShowCompleteDialog = ShowCompleteBox.IsChecked == true;
        s.MarkDownloads = MarkDownloadsBox.IsChecked == true;
        s.IgnoreModificationTime = IgnoreModifiedBox.IsChecked == true;
        s.VirusScanner = scanner;
        s.VirusScannerArguments = VirusArgumentsBox.Text.Trim();
        s.Sounds = _sounds.Where(r => r.On || r.File.Trim().Length > 0)
            .Select(r => new EventSound { Event = r.Event, On = r.On, File = r.File.Trim() })
            .ToList();
        s.SpeedLimiterOnAtStartup = LimiterAtStartupBox.IsChecked == true;
        s.VideoPanel = PanelOffOption.IsChecked == true ? VideoPanelMode.Off
            : PanelMiniOption.IsChecked == true ? VideoPanelMode.Mini
            : VideoPanelMode.Full;
        s.VideoPanelMinSizeKB = panelMinSize;
        s.VideoPanelExceptionSites = NormalizeList(PanelExceptionsBox.Text);
        s.DownloadSubtitles = SubtitlesBox.IsChecked == true;
        _manager.State.Categories = categories;
        _manager.ApplySettings();

        if (StartupBox.IsChecked != WindowsStartup.IsEnabled) WindowsStartup.Set(StartupBox.IsChecked == true);
        DialogResult = true;
    }

    ProxySettings ReadProxy(ProxySettings old, ProxyMode mode, string host, int port)
    {
        var userName = ProxyUserBox.Text.Trim();
        var password = ProxyPasswordBox.Password;
        return new ProxySettings
        {
            Mode = mode,
            Kind = (ProxyKind)Math.Max(0, ProxyKindBox.SelectedIndex),
            Host = host,
            Port = port,
            UserName = userName.Length > 0 ? userName : null,
            // DPAPI output differs on every call, and a changed value means a new connection pool,
            // so an unchanged password keeps its stored form.
            ProtectedPassword = userName.Length == 0 ? null
                : password == (Secrets.Unprotect(old.ProtectedPassword) ?? "") ? old.ProtectedPassword
                : Secrets.Protect(password),
            Bypass = NormalizeList(ProxyBypassBox.Text),
            ScriptUrl = ProxyScriptUrlBox.Text.Trim(),
        };
    }

    // Its choices apply as soon as that dialog's OK is pressed, like the window's own Customize... button.
    void EditProgressWindow_Click(object sender, RoutedEventArgs e)
    {
        if (new ProgressCustomizeWindow(_manager.State.Settings) { Owner = this }.ShowDialog() == true) _manager.MarkDirty();
    }

    /// <summary>One space between entries; file types also lose a leading dot and are lower-cased.</summary>
    static string NormalizeList(string text, bool fileTypes = false) =>
        string.Join(' ', text.Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => fileTypes ? x.TrimStart('.').ToLowerInvariant() : x));

    /// <summary>Shows the problem, then switches to the tab holding the field and focuses it.</summary>
    void Warn(string message, FrameworkElement field)
    {
        MessageBox.Show(this, message, Lang.T("options.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        for (DependencyObject? parent = field; parent != null; parent = LogicalTreeHelper.GetParent(parent))
        {
            if (parent is TabItem tab)
            {
                Tabs.SelectedItem = tab;
                break;
            }
        }
        field.Focus();
    }
}
