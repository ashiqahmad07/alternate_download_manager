using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

/// <summary>
/// A download's details. The description can always be edited; the folder, address, referer and login only
/// while the download is stopped. A new folder moves the file, finished or not.
/// </summary>
public partial class PropertiesWindow : ThemedWindow
{
    readonly DownloadManager _manager;
    readonly DownloadItem _item;
    readonly string _shownUser;

    public PropertiesWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _item = item;

        var (icon, typeName) = ShellInfo.For(item.FileName);
        FileIcon.Source = icon;
        NameText.Text = item.ToString();
        TypeText.Text = typeName;
        Title = Lang.T("properties.title_of", item);
        StatusValue.Text = item.StatusText;
        SizeValue.Text = item.TotalBytes >= 0 ? Lang.T("common.size_and_bytes", Format.Bytes(item.TotalBytes), item.TotalBytes) : Lang.T("common.unknown");
        LastTryValue.Text = item.LastTryAt is DateTime at ? $"{at:g}  ·  {item.LastResultText}" : Lang.T("properties.never");
        FolderBox.Text = item.Directory;
        UrlBox.Text = item.Url;
        DescriptionBox.Text = item.Description ?? "";
        HashBox.Text = item.ExpectedSha256 ?? "";
        HashBox.IsReadOnly = item.Status == DownloadStatus.Completed || _manager.IsRunning(item);
        HashResultText.Text = item.HashResult ?? "";
        HashResultText.Visibility = item.HashResult == null ? Visibility.Collapsed : Visibility.Visible;
        // A stream is joined here, so no page's hash could match it.
        HashLabel.Visibility = HashPanel.Visibility = item.IsStream ? Visibility.Collapsed : Visibility.Visible;
        RefererBox.Text = item.Referrer ?? "";
        PageBox.Text = string.IsNullOrEmpty(item.PageUrl) ? Lang.T("common.unknown") : item.PageUrl;
        LoginUserBox.Text = _shownUser = item.UserName ?? "";
        OpenButton.Visibility = item.Status == DownloadStatus.Completed ? Visibility.Visible : Visibility.Collapsed;

        if (_manager.IsRunning(item) || _manager.IsMoving(item))
        {
            FolderBox.IsReadOnly = UrlBox.IsReadOnly = RefererBox.IsReadOnly = LoginUserBox.IsReadOnly = true;
            BrowseButton.IsEnabled = LoginPasswordBox.IsEnabled = false;
            RunningNote.Visibility = Visibility.Visible;
        }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_item.FullPath)) return;
        try { Process.Start(new ProcessStartInfo(_item.FullPath) { UseShellExecute = true }); } catch (Win32Exception) { }
    }

    async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var item = _item;
        var url = UrlBox.Text.Trim();
        var folder = FolderBox.Text.Trim();
        // Started by a queue while the dialog was open: only the description still applies.
        var stopped = !_manager.IsRunning(item) && !_manager.IsMoving(item);
        var newFolder = stopped && !string.Equals(folder, item.Directory, StringComparison.OrdinalIgnoreCase);
        if (stopped && url != item.Url && !IsWebAddress(url))
        {
            Warn(Lang.T("properties.enter_address"));
            UrlBox.Focus();
            return;
        }
        if (newFolder && !Path.IsPathFullyQualified(folder))
        {
            Warn(Lang.T("properties.enter_folder"));
            FolderBox.Focus();
            return;
        }

        if (!HashBox.IsReadOnly && HashBox.Text.Trim().Length > 0 && FileHash.Normalize(HashBox.Text) == null)
        {
            Warn(Lang.T("common.sha256_invalid"));
            HashBox.Focus();
            return;
        }

        item.Description = NullIfBlank(DescriptionBox.Text);
        if (stopped && !HashBox.IsReadOnly) item.ExpectedSha256 = FileHash.Normalize(HashBox.Text);
        if (stopped)
        {
            var hadLogin = item.UserName != null;
            if (url != item.Url) _manager.SetAddress(item, url);
            item.Referrer = NullIfBlank(RefererBox.Text);
            // The address comes first: on another site the old login is dropped, and only a login typed here comes back.
            var user = LoginUserBox.Text.Trim();
            var password = LoginPasswordBox.Password;
            if (user != _shownUser || password.Length > 0) _manager.SetLogin(item, user, password.Length > 0 ? password : null);
            if (hadLogin && item.UserName == null && user.Length > 0 && password.Length == 0)
                MessageBox.Show(this, Lang.T("properties.login_removed"),
                    Lang.T("properties.title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        _manager.MarkDirty();

        if (newFolder && !await MoveToAsync(folder)) return;
        DialogResult = true;
    }

    /// <summary>Moves the download to <paramref name="folder"/>; on failure says why and returns false, so the dialog stays open.</summary>
    async Task<bool> MoveToAsync(string folder)
    {
        var item = _item;
        if (item.FileName.Length == 0)
        {
            // Nothing on disk yet: the file will be saved there once its name is known.
            item.Directory = folder;
            item.DirectoryChosenByUser = true;
            _manager.MarkDirty();
            return true;
        }
        var target = Path.Combine(folder, item.FileName);
        var replace = File.Exists(target);
        if (replace && MessageBox.Show(this, Lang.T("properties.replace_prompt", item.FileName, folder),
                Lang.T("properties.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _manager.MoveAsync(item, target, replace);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Warn(Lang.T("common.could_not_move", ex.Message));
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }

    void Warn(string message) => MessageBox.Show(this, message, Lang.T("properties.title"), MessageBoxButton.OK, MessageBoxImage.Warning);

    static bool IsWebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && Links.IsDownloadable(uri);

    static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
