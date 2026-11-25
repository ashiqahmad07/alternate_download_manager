using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

public partial class AddDownloadWindow : ThemedWindow
{
    // Segoe MDL2's chevrons: down when "More options" is open, right when it is shut.
    const string OpenGlyph = "";
    const string ShutGlyph = "";

    // Open or shut as the user last left it, for as long as ADM runs.
    static bool s_showMore;

    readonly DownloadManager _manager;
    readonly CancellationTokenSource _probe = new();
    DownloadRequest _request;
    // Set once a command has filled the dialog.
    int? _commandHeadersLeftOut;
    string? _probedUrl;
    // The two halves of "Save as".
    string _folder = "";
    string _name = "";
    bool _nameEditedByUser;
    // A name typed in "Save as" picks its category once the user leaves the box, so the folder doesn't change under the caret.
    bool _nameTyped;
    bool _folderChosenByUser;
    bool _settingPath;
    bool _settingCategory;
    string? _iconExtension;
    // The download already under way for the address the dialog opened with, and that address.
    readonly DownloadItem? _early;
    readonly string _earlyUrl;
    // What the dialog last said of the file, kept so a size the early download learns afterwards can join it.
    (long? Total, string Resume, string Info, string? InfoOnceSized)? _shown;

    /// <param name="early">The download already under way for <paramref name="request"/>: the size it learns is shown.</param>
    public AddDownloadWindow(DownloadManager manager, DownloadRequest request, DownloadItem? early = null)
    {
        _manager = manager;
        _request = request;
        _early = early;
        _earlyUrl = request.Url.Trim();
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        ShowCategories();
        QueueBox.ItemsSource = manager.Queues;
        QueueBox.SelectedItem = manager.MainQueue;
        ConnectionsBox.Text = manager.State.Settings.DefaultConnections.ToString();
        HashBox.Text = request.Sha256 ?? "";
        // A hash that came with the download is shown, not left out of sight.
        ShowMore(s_showMore || HashBox.Text.Length > 0);

        FillFrom(request);

        Loaded += (_, _) =>
        {
            TakeCommand();
            ShowCommandNote();
            // Not one the user just pasted: a command from the clipboard that can't be read shouldn't stay in the box.
            if (CurlCommand.IsCommand(UrlBox.Text)) UrlBox.Clear();
            if (string.IsNullOrEmpty(UrlBox.Text)) UrlBox.Focus();
            else _ = ProbeAsync();
        };
        Closed += (_, _) => _probe.Cancel();
        if (early != null)
        {
            early.PropertyChanged += Early_PropertyChanged;
            Closed += (_, _) => early.PropertyChanged -= Early_PropertyChanged;
        }
    }

    public DownloadRequest? Result { get; private set; }
    public bool StartNow { get; private set; }
    public DownloadQueue? Queue { get; private set; }

    /// <summary>A category was made here, so the main window's list of them is out of date, whether or not the download was added.</summary>
    public bool CategoriesChanged { get; private set; }

    void FillFrom(DownloadRequest request)
    {
        UrlBox.Text = request.Url;
        SetName(request.FileName ?? "");
        _nameEditedByUser = !string.IsNullOrEmpty(request.FileName);
        SelectCategory(Categories.Resolve(request.FileName ?? request.Url, _manager.State.Categories));
        if (!string.IsNullOrEmpty(request.UserName))
        {
            UseLoginBox.IsChecked = true;
            LoginUserBox.Text = request.UserName;
            LoginPasswordBox.Password = request.Password ?? "";
            ShowMore(true);
        }
        ShowHashFor(KindFor(request.Url));
    }

    void ShowCategories() => CategoryBox.ItemsSource = _manager.State.Categories.Select(c => c.Name).ToList();

    /// <summary>The kind the browser said, while the address is still the one it sent; otherwise what the address ends with.</summary>
    DownloadKind KindFor(string url) =>
        _request.Kind != DownloadKind.Http && url == _request.Url ? _request.Kind : Links.StreamKind(url);

    /// <summary>A stream is joined here, so no page's hash could match it.</summary>
    void ShowHashFor(DownloadKind kind) =>
        HashLabel.Visibility = HashBox.Visibility = kind == DownloadKind.Http ? Visibility.Visible : Visibility.Collapsed;

    void ShowMore(bool show)
    {
        MorePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MoreGlyph.Text = show ? OpenGlyph : ShutGlyph;
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        s_showMore = MorePanel.Visibility != Visibility.Visible;
        ShowMore(s_showMore);
    }

    void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        TakeCommand();
        ShowCommandNote();
        _ = ProbeAsync();
    }

    /// <summary>A command in the address box takes the place of what the dialog held. One that can't be read changes nothing.</summary>
    void TakeCommand()
    {
        var text = UrlBox.Text.Trim();
        if (!CurlCommand.IsCommand(text)) return;
        var command = CurlCommand.Parse(text);
        if (command.Request == null)
        {
            MessageBox.Show(this, command.Error, "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _request = command.Request;
        _commandHeadersLeftOut = command.HeadersLeftOut;
        _probedUrl = null;
        FillFrom(_request);
    }

    /// <summary>Says what a command brought, without showing any of it. It follows the address, since cookies don't go to another site.</summary>
    void ShowCommandNote()
    {
        if (_commandHeadersLeftOut is not int leftOut) return;
        var request = _request.ForAddress(UrlBox.Text.Trim());
        var taken = new List<string> { Lang.T("adddownload.command_address") };
        if (request.Cookies != null) taken.Add(Lang.T("adddownload.command_cookies"));
        if (request.Referrer != null) taken.Add(Lang.T("adddownload.command_referrer"));
        if (request.UserAgent != null) taken.Add(Lang.T("adddownload.command_user_agent"));
        if (request.UserName != null) taken.Add(Lang.T("adddownload.command_login"));
        if (request.FileName != null) taken.Add(Lang.T("adddownload.command_file_name"));
        var note = Lang.T("adddownload.command_took", string.Join(", ", taken));
        CommandNote.Text = leftOut > 0 ? note + "  ·  " + Lang.T("adddownload.command_left_out", leftOut) : note;
        CommandNote.Visibility = Visibility.Visible;
    }

    /// <summary>Checks the server again now that it can be asked with the login.</summary>
    void LoginPassword_LostFocus(object sender, RoutedEventArgs e)
    {
        if (LoginUser == null) return;
        _probedUrl = null;
        _ = ProbeAsync();
    }

    string? LoginUser => UseLoginBox.IsChecked == true && LoginUserBox.Text.Trim().Length > 0 ? LoginUserBox.Text.Trim() : null;

    /// <summary>The download as it stands, for asking the server about it before it is added.</summary>
    DownloadItem ServerItem(string url) => new()
    {
        Url = url,
        Referrer = _request.Referrer,
        Cookies = _request.ForAddress(url).Cookies,
        UserAgent = _request.UserAgent,
        BrowserProxy = _request.BrowserProxy,
        UserName = LoginUser,
        ProtectedPassword = LoginUser == null ? null : Secrets.Protect(LoginPasswordBox.Password),
    };

    /// <summary>Asks the server for name and size up front, like a "file info" dialog.</summary>
    async Task ProbeAsync()
    {
        var url = UrlBox.Text.Trim();
        if (url == _probedUrl || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        _probedUrl = url;

        var kind = KindFor(url);
        ShowHashFor(kind);
        ShowFileKind();
        if (kind != DownloadKind.Http)
        {
            var note = (kind, _request.HlsAudioOnly, _request.HlsBandwidth) switch
            {
                (DownloadKind.Site, _, _) => Lang.T("adddownload.kind_site", SiteChoices.Describe(_request.SiteChoice ?? "original")),
                (DownloadKind.Joined, _, _) => Lang.T("adddownload.kind_joined"),
                (DownloadKind.Dash, true, _) => Lang.T("adddownload.kind_dash_audio"),
                (DownloadKind.Dash, _, long bandwidth) => Lang.T("adddownload.kind_dash_bandwidth", bandwidth / 1000),
                (DownloadKind.Dash, _, _) => Lang.T("adddownload.kind_dash"),
                (_, true, _) => Lang.T("adddownload.kind_hls_audio"),
                (_, _, long bandwidth) => Lang.T("adddownload.kind_hls_bandwidth", bandwidth / 1000),
                _ => Lang.T("adddownload.kind_hls"),
            };
            var untilSized = kind switch
            {
                DownloadKind.Site => Lang.T("adddownload.size_once_read", note),
                DownloadKind.Hls => Lang.T("adddownload.size_once_segments", note),
                _ => Lang.T("adddownload.size_once_parts", note),
            };
            // Not asked here: the download already under way learns the size, from the site's read or the parts' answers.
            ShowSize(-1, info: untilSized, infoOnceSized: note);
            // As a file's address names it: a name from an address typed over isn't kept. ADM changes a DASH name to
            // what its container calls for, such as WebM.
            if (!_nameEditedByUser) SetName(kind == DownloadKind.Hls ? "video.ts" : "video.mp4");
            return;
        }

        // Name from the URL first, so the field is filled even when the server cannot be reached.
        if (!_nameEditedByUser)
        {
            var fromUrl = FileNames.FromResponse(null, url, null);
            SetName(fromUrl);
            SelectCategory(Categories.Resolve(fromUrl, _manager.State.Categories));
        }

        ShowSize(null);
        try
        {
            var result = await HttpDownloader.ProbeAsync(ServerItem(url), _manager.State.Settings, _probe.Token);
            if (url != UrlBox.Text.Trim()) return;
            ShowSize(result.Total, result.RangeSupported ? Lang.T("adddownload.resumable") : Lang.T("adddownload.not_resumable"));
            if (!_nameEditedByUser)
            {
                SetName(result.FileName);
                SelectCategory(Categories.Resolve(result.FileName, _manager.State.Categories));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowSize(-1, info: Lang.T("adddownload.could_not_reach", ex.Message));
        }
    }

    /// <summary>
    /// The size beside the file's icon (null while the server is asked, -1 when it isn't known), whether it can be
    /// paused and resumed, and anything longer underneath, which <paramref name="infoOnceSized"/> replaces once the
    /// size is known. A size the download already under way has learned stands in for one not known here.
    /// </summary>
    void ShowSize(long? total, string resume = "", string info = "", string? infoOnceSized = null)
    {
        _shown = (total, resume, info, infoOnceSized);
        ShowShown();
    }

    void ShowShown()
    {
        if (_shown is not { } shown) return;
        var total = shown.Total is not > 0 && EarlyTotal > 0 ? EarlyTotal : shown.Total;
        SizeText.Text = total switch
        {
            > 0 => Format.Bytes(total.Value),
            null => Lang.T("adddownload.checking"),
            _ => Lang.T("adddownload.unknown_size"),
        };
        ResumeText.Text = shown.Resume;
        var info = total > 0 && shown.InfoOnceSized != null ? shown.InfoOnceSized : shown.Info;
        InfoText.Text = info;
        InfoText.Visibility = info.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the download already under way has learned of the file's size, while the dialog is about the address it started on.</summary>
    long EarlyTotal => _early != null && _probedUrl == _earlyUrl ? _early.TotalBytes : -1;

    // Raised on the download's own thread.
    void Early_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.TotalBytes)) Dispatcher.InvokeAsync(ShowShown);
    }

    // ---- Save as: one path, kept as its folder and its name

    void SetName(string name)
    {
        _name = name;
        ShowPath();
    }

    void SetFolder(string folder)
    {
        _folder = Path.TrimEndingDirectorySeparator(folder);
        ShowPath();
    }

    void ShowPath()
    {
        _settingPath = true;
        SaveAsBox.Text = _folder.Length == 0 ? _name
            : Path.EndsInDirectorySeparator(_folder) ? _folder + _name
            : _folder + Path.DirectorySeparatorChar + _name;
        _settingPath = false;
        ShowFolder();
        ShowFileKind();
    }

    void ShowFolder() => RememberFolderBox.Text = _folder;

    /// <summary>The icon Windows gives the file's type, and a preview for an archive that can be read before it downloads.</summary>
    void ShowFileKind()
    {
        var extension = Path.GetExtension(_name).ToLowerInvariant();
        if (extension != _iconExtension)
        {
            _iconExtension = extension;
            FileIcon.Source = ShellInfo.For("file" + extension).Icon;
        }
        PreviewButton.IsEnabled = extension == ".zip" && KindFor(UrlBox.Text.Trim()) == DownloadKind.Http;
    }

    void SaveAsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingPath) return;
        var path = SaveAsBox.Text.Trim();
        var name = Path.GetFileName(path);
        // A root has no folder above it, and is its own.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? path);
        if (name != _name)
        {
            _name = name;
            _nameEditedByUser = true;
            _nameTyped = true;
        }
        if (folder != _folder)
        {
            _folder = folder;
            _folderChosenByUser = true;
        }
        ShowFolder();
        ShowFileKind();
    }

    void SaveAsBox_LostFocus(object sender, RoutedEventArgs e) => TakeTypedName();

    /// <summary>A name typed in "Save as" picks its category, as one from the server does.</summary>
    void TakeTypedName()
    {
        if (!_nameTyped) return;
        _nameTyped = false;
        SelectCategory(Categories.Resolve(_name, _manager.State.Categories));
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Lang.T("adddownload.choose_save_as"),
            FileName = _name,
            InitialDirectory = Directory.Exists(_folder) ? _folder : null,
            Filter = Lang.Filter(Lang.T("common.all_files"), "*.*"),
            AddExtension = false,
            // A file already there is dealt with as for any download, when it starts.
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        var name = Path.GetFileName(dialog.FileName);
        if (name != _name) _nameEditedByUser = true;
        // Chosen in a Save dialog, the folder is the user's, whatever category the name then picks.
        _folderChosenByUser = true;
        _name = name;
        SetFolder(Path.GetDirectoryName(dialog.FileName) ?? "");
        SelectCategory(Categories.Resolve(_name, _manager.State.Categories));
    }

    // ---- Category

    /// <summary>The category a name gives. The folder follows it unless the user chose one.</summary>
    void SelectCategory(string category)
    {
        _settingCategory = true;
        CategoryBox.SelectedItem = category;
        _settingCategory = false;
        if (!_folderChosenByUser) SetFolder(Categories.FolderFor(category, _manager.State));
        ShowRememberLabel();
    }

    void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowRememberLabel();
        if (_settingCategory || CategoryBox.SelectedItem is not string category) return;
        // Picked by the user: the file goes to that category's folder, as IDM's does.
        _folderChosenByUser = false;
        SetFolder(Categories.FolderFor(category, _manager.State));
    }

    // In a TextBlock, so an underscore in a category's name shows, rather than marking an access key.
    void ShowRememberLabel() =>
        RememberBox.Content = new TextBlock { Text = Lang.T("adddownload.remember_path", CategoryBox.SelectedItem as string ?? Categories.General) };

    void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow(Lang.T("adddownload.new_category"), Lang.T("adddownload.new_category_prompt"), "") { Owner = this };
        if (prompt.ShowDialog() != true || prompt.Value.Length == 0) return;
        var category = Categories.Add(prompt.Value, _manager.State);
        _manager.MarkDirty();
        CategoriesChanged = true;
        ShowCategories();
        // As if picked from the list: the file goes to the new category's folder.
        CategoryBox.SelectedItem = category.Name;
    }

    // ---- Preview

    /// <summary>Lists the archive from the server, the way the File menu does for a download in the list.</summary>
    void Preview_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        var item = ServerItem(url);
        item.FileName = _name;
        item.Directory = _folder;
        new ZipPreviewWindow(_manager, item) { Owner = this }.ShowDialog();
    }

    // ---- Finishing

    void Start_Click(object sender, RoutedEventArgs e) => Finish(true);

    void Later_Click(object sender, RoutedEventArgs e) => Finish(false);

    static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    void Finish(bool startNow)
    {
        var url = UrlBox.Text.Trim();
        // Enter doesn't move the focus, so a command just pasted is read here, and shown before anything starts.
        if (CurlCommand.IsCommand(url))
        {
            TakeCommand();
            ShowCommandNote();
            _ = ProbeAsync();
            return;
        }
        // Nor does it leave "Save as", where a name may have been typed.
        TakeTypedName();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Links.IsDownloadable(uri))
        {
            MessageBox.Show(this, Lang.T("adddownload.enter_address"), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(ConnectionsBox.Text, out var connections) || connections < 1 || connections > 32)
        {
            MessageBox.Show(this, Lang.T("common.connections_range"), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowMore(true);
            ConnectionsBox.Focus();
            return;
        }
        if (UseLoginBox.IsChecked == true && LoginUser == null)
        {
            MessageBox.Show(this, Lang.T("adddownload.enter_user_name"), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowMore(true);
            LoginUserBox.Focus();
            return;
        }

        var category = CategoryBox.SelectedItem as string ?? Categories.General;
        var folder = _folder.Trim();
        var kind = KindFor(url);
        // A stream's field is hidden, and what it held from before doesn't count.
        var hash = kind == DownloadKind.Http ? HashBox.Text : "";
        if (hash.Trim().Length > 0 && FileHash.Normalize(hash) == null)
        {
            MessageBox.Show(this, Lang.T("common.sha256_invalid"), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowMore(true);
            HashBox.Focus();
            return;
        }

        // Ticked, a folder the user chose becomes the category's own, for this download and the ones after it.
        if (RememberBox.IsChecked == true && folder.Length > 0 && !SameFolder(folder, Categories.FolderFor(category, _manager.State)))
        {
            Categories.SetFolder(category, folder, _manager.State);
            _manager.MarkDirty();
        }

        Result = new DownloadRequest
        {
            Url = url,
            Sha256 = FileHash.Normalize(hash),
            Referrer = _request.Referrer,
            PageUrl = _request.PageUrl,
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            Cookies = _request.ForAddress(url).Cookies,
            UserAgent = _request.UserAgent,
            UserName = LoginUser,
            Password = LoginUser == null ? null : LoginPasswordBox.Password,
            Kind = kind,
            FileName = string.IsNullOrWhiteSpace(_name) ? null : _name.Trim(),
            Category = category,
            Directory = folder.Length > 0 && !SameFolder(folder, Categories.FolderFor(category, _manager.State)) ? folder : null,
            Connections = connections,
            // Choices made in the browser's download panel.
            HlsBandwidth = _request.HlsBandwidth,
            HlsAudioOnly = _request.HlsAudioOnly,
            HlsSubtitles = _request.HlsSubtitles,
            SiteChoice = kind == DownloadKind.Site ? _request.SiteChoice : null,
            AudioUrl = kind == DownloadKind.Joined ? _request.AudioUrl : null,
        };
        StartNow = startNow;
        Queue = QueueBox.SelectedItem as DownloadQueue;
        DialogResult = true;
    }
}
