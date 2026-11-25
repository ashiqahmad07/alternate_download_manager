using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>
/// Explores a site for a <see cref="GrabberProject"/>. Every address is asked two things: is it a file for the list, and
/// is it a page to read for more links. Levels hold back only the second. A stopped crawl leaves addresses it hadn't
/// finished as they were, so running it again carries on, and ends with the same list.
/// </summary>
public sealed partial class GrabberCrawler
{
    /// <summary>An address longer than this is left out; a site's links rarely come close, and traps do.</summary>
    public const int MaxUrlLength = 2048;
    /// <summary>How many addresses a project can hold.</summary>
    public const int MaxEntries = 100_000;
    /// <summary>A page longer than this, also once unpacked, is a file, and isn't read.</summary>
    public const int MaxPageBytes = 32 * 1024 * 1024;

    static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(90);

    readonly GrabberProject _project;
    readonly GrabberSettings _options;
    readonly string _folder;
    readonly Settings _settings;
    readonly RateLimiter _limiter;
    readonly HttpClient _client;
    readonly object _gate;
    readonly Dictionary<string, GrabberEntry> _byAddress = new(StringComparer.Ordinal);
    readonly Queue<GrabberEntry> _waiting = new();
    readonly HashSet<string> _answered = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _copies = new(StringComparer.OrdinalIgnoreCase);
    readonly GrabberMask _include;
    readonly GrabberMask _exclude;
    readonly GrabberPathList _exploreOnly;
    readonly GrabberPathList _exploreNever;
    readonly GrabberPathList _filesOnlyIn;
    readonly GrabberPathList _filesNeverIn;
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    DownloadItem _login = new();
    Uri? _start;
    bool _mayReachPrivate;
    string _startFolder = "/";
    int _inFlight;
    DateTime _lastAnswer;
    DateTime _nextSave;

    public GrabberCrawler(GrabberProject project, string folder, Settings settings, RateLimiter limiter)
    {
        _project = project;
        _options = project.Settings;
        _folder = folder;
        _settings = settings;
        _limiter = limiter;
        _client = Net.ClientFor(settings);
        _gate = project.SyncRoot;
        Retry = RetryPolicy.For(settings);
        _include = GrabberMask.Parse(_options.Include);
        _exclude = GrabberMask.Parse(_options.Exclude);
        _exploreOnly = GrabberPathList.Parse(_options.ExploreOnly);
        _exploreNever = GrabberPathList.Parse(_options.ExploreNever);
        _filesOnlyIn = GrabberPathList.Parse(_options.FilesOnlyIn);
        _filesNeverIn = GrabberPathList.Parse(_options.FilesNeverIn);
    }

    internal RetryPolicy Retry { get; init; }
    internal int EntryLimit { get; set; } = MaxEntries;
    internal int PageLimit { get; set; } = MaxPageBytes;
    internal Func<string, long> FreeSpace { get; init; } = PartFile.FreeSpaceIn;

    /// <summary>Takes the bytes of each page read, toward the download limit; off the UI thread.</summary>
    public Action<long>? Counted { get; init; }
    /// <summary>Waits while the download limit holds, before each request and read; null never waits.</summary>
    public Func<CancellationToken, Task>? Held { get; init; }

    /// <summary>Why the crawl stopped before it was done, such as a disk too full to keep pages; null when it didn't.</summary>
    public string? Error { get; private set; }

    /// <summary>Where the project is saved as the crawl goes; null saves nothing.</summary>
    public string? SavePath { get; set; }
    internal TimeSpan SaveEvery { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>More addresses were found than a project can hold, so some were left out.</summary>
    public bool LimitReached { get; private set; }

    /// <summary>Why the crawl is waiting, while it is; off the UI thread.</summary>
    public event Action<string>? Waiting;
    /// <summary>An address joined the list; off the UI thread, with the lock on <see cref="GrabberProject.SyncRoot"/> not held.</summary>
    public event Action<GrabberEntry>? Found;
    /// <summary>An address has been looked at; off the UI thread.</summary>
    public event Action? Changed;
    /// <summary>
    /// What was saved of an address is out of date, as the server's copy has changed; its file state is back to none.
    /// Off the UI thread, with the lock not held.
    /// </summary>
    public event Action<GrabberEntry>? Updated;

    public GrabberCounts Counts
    {
        get
        {
            lock (_gate)
            {
                int pages = 0, files = 0, failed = 0, waiting = 0;
                foreach (var entry in _project.Entries)
                {
                    if (entry.Read) pages++;
                    if (entry.Matched) files++;
                    if (entry.State == GrabberState.Failed) failed++;
                    else if (entry.State == GrabberState.Waiting) waiting++;
                }
                return new GrabberCounts(pages, files, failed, waiting);
            }
        }
    }

    /// <summary>The address a person typed as a crawl can use it, or null if it can't be explored.</summary>
    public static Uri? StartAddress(string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0) return null;
        // "site.test:8080" is a host and a port, and "javascript:go()" isn't.
        if (!SchemePattern().IsMatch(text)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Query));
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+\-]*:(?!\d)")]
    private static partial Regex SchemePattern();

    /// <summary>
    /// The part of a host that sites under it share: the last two labels, or three where the last is a country's two
    /// letters and the one before is short, as in example.co.uk. With no list of public suffixes, it is a guess.
    /// </summary>
    public static string MainDomainOf(string host)
    {
        if (!host.Contains('.') || host.Contains(':') || IPAddress.TryParse(host, out _)) return host;
        var labels = host.Split('.');
        int keep = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
        return labels.Length <= keep ? host : string.Join('.', labels[^keep..]);
    }

    /// <summary>A name or address on this computer or its network, as far as the address itself says.</summary>
    public static bool IsPrivate(Uri address)
    {
        var host = address.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(address.DnsSafeHost, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var first = ip.GetAddressBytes()[0];
            return IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (first & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6Any);
        }
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 10 or 127
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    /// <summary>Explores until nothing is left or <paramref name="ct"/> ends it, which isn't a failure, and saves the project.</summary>
    /// <exception cref="InvalidDataException">The start page's address can't be explored.</exception>
    public async Task RunAsync(CancellationToken ct)
    {
        _start = StartAddress(_options.StartUrl) ?? throw new InvalidDataException("The start page's address can't be explored.");
        _mayReachPrivate = IsPrivate(_start);
        _lastAnswer = DateTime.UtcNow;
        _login = new DownloadItem
        {
            Url = _start.AbsoluteUri,
            UserName = _options.UserName,
            ProtectedPassword = _options.ProtectedPassword,
            Cookies = Secrets.Unprotect(_options.ProtectedCookies),
        };

        lock (_gate)
        {
            // The last crawl went all the way through: this one is a new pass, from the start page.
            if (_project.Explored)
            {
                _project.Pass++;
                _project.Explored = false;
            }
            foreach (var entry in _project.Entries)
            {
                _byAddress[entry.Url] = entry;
                if (entry.Matched && HidingCopies) _copies.Add(CopyKey(entry));
                if (entry.State == GrabberState.Failed)
                {
                    entry.State = GrabberState.Waiting;
                    entry.Error = null;
                }
                if (entry.State == GrabberState.Waiting) _waiting.Enqueue(entry);
            }
            if (!_project.SiteHosts.Contains(_start.Host, StringComparer.OrdinalIgnoreCase)) _project.SiteHosts.Add(_start.Host);
            if (_byAddress.GetValueOrDefault(_start.AbsoluteUri) is { } known)
            {
                SetStartFolder(known);
                if (known.Pass < _project.Pass) AskAgain(known);
            }
            else
            {
                AddEntry(new GrabberEntry { Url = _start.AbsoluteUri, Kind = GrabberKind.Unknown });
            }
            if (_startFolder == "/") _startFolder = FolderOf(_start);
        }

        Directory.CreateDirectory(_folder);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stopAll = stop;
        var workers = Enumerable.Range(0, Math.Clamp(_options.ExploreAtOnce, 1, 10)).Select(_ => Task.Run(() => WorkAsync(stop.Token))).ToArray();
        await Task.WhenAll(workers);
        lock (_gate)
            if (!stop.IsCancellationRequested) _project.Explored = true;
        SaveNow();
    }

    CancellationTokenSource? _stopAll;

    /// <summary>An address this pass hasn't asked about goes in the queue once more. Needs the lock.</summary>
    void AskAgain(GrabberEntry entry)
    {
        if (entry.State == GrabberState.Waiting) return;
        entry.State = GrabberState.Waiting;
        entry.Error = null;
        _waiting.Enqueue(entry);
    }

    void SetStartFolder(GrabberEntry start) => _startFolder = FolderOf(new Uri(start.FinalUrl ?? start.Url));

    static string FolderOf(Uri address)
    {
        var path = address.AbsolutePath;
        return path[..(path.LastIndexOf('/') + 1)];
    }

    void AddEntry(GrabberEntry entry)
    {
        entry.Pass = _project.Pass;
        _project.Entries.Add(entry);
        _byAddress[entry.Url] = entry;
        _waiting.Enqueue(entry);
    }

    async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            GrabberEntry? next;
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
                if (!_waiting.TryDequeue(out next))
                {
                    if (_inFlight == 0) return;
                }
                else
                {
                    _inFlight++;
                }
            }
            if (next == null)
            {
                try { await changed.WaitAsync(ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await ProcessWithRetriesAsync(next, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                lock (_gate) _inFlight--;
                Signal();
            }
            SaveIfDue();
            Changed?.Invoke();
        }
    }

    void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    void SaveIfDue()
    {
        if (SavePath == null || DateTime.UtcNow < _nextSave) return;
        lock (_gate)
        {
            if (DateTime.UtcNow < _nextSave) return;
            var started = DateTime.UtcNow;
            SaveNow();
            // A long list takes a while to write, and a crawl shouldn't spend its time on that.
            var took = DateTime.UtcNow - started;
            _nextSave = DateTime.UtcNow + (SaveEvery > took * 20 ? SaveEvery : took * 20);
        }
    }

    void SaveNow()
    {
        if (SavePath == null) return;
        try
        {
            GrabberStore.Save(SavePath, _project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("A grabber project could not be saved.", e);
        }
    }

    async Task ProcessWithRetriesAsync(GrabberEntry entry, CancellationToken ct)
    {
        var address = new Uri(entry.Url);
        for (int tries = 0; ; tries++)
        {
            try
            {
                await ProcessAsync(entry, address, ct);
                return;
            }
            catch (Exception e) when (Net.IsDiskFull(e))
            {
                // Nothing more can be kept, so the crawl stops and says why. The address waits for the next run.
                lock (_gate) Error ??= e.Message;
                _stopAll?.Cancel();
                return;
            }
            catch (Exception e) when (Net.IsConnectionFailure(e, ct))
            {
                if (Retry.NoSuchName(e) || (IsOutage(address) ? OutageTooLong() : tries >= 3))
                {
                    Fail(entry, e);
                    return;
                }
                await WaitToTryAgainAsync(tries, ct);
            }
            catch (Exception e) when (Net.IsTransient(e, ct) && tries < 3)
            {
                await Retry.BackoffAsync(tries, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Fail(entry, e);
                return;
            }
        }
    }

    // A server that has answered this crawl and stopped is an outage, as is nothing having answered at all.
    // A host that never answered while others do is simply down: the crawl doesn't wait for it.
    bool IsOutage(Uri address)
    {
        if (!Retry.NetworkAvailable()) return true;
        lock (_gate) return _answered.Count == 0 || _answered.Contains(address.Authority);
    }

    bool OutageTooLong()
    {
        lock (_gate) return Retry.ReconnectWindow is { } window && DateTime.UtcNow - _lastAnswer > window;
    }

    async Task WaitToTryAgainAsync(int tries, CancellationToken ct)
    {
        bool network = Retry.NetworkAvailable();
        Waiting?.Invoke(network ? StatusWords.Current.Reconnecting(tries + 1) : StatusWords.Current.WaitingForNetwork);
        if (network)
        {
            await Retry.BackoffAsync(tries, ct);
            return;
        }
        await RetryPolicy.WaitForNetworkAsync(Retry.Backoff(tries), ct);
        // With no network at all, the time doesn't count against the window.
        lock (_gate) _lastAnswer = DateTime.UtcNow;
    }

    void Fail(GrabberEntry entry, Exception e)
    {
        lock (_gate)
        {
            entry.State = GrabberState.Failed;
            entry.Error = e.Message;
            entry.Pass = _project.Pass;
        }
    }

    async Task ProcessAsync(GrabberEntry entry, Uri address, CancellationToken ct)
    {
        if (address.Scheme == Uri.UriSchemeFtp)
        {
            // An FTP address is listed as it is; only downloading it asks that server anything.
            Finish(entry, address, FileNames.FromResponse(null, entry.Url, null));
            return;
        }

        bool read, again;
        long size;
        string? etag, modified;
        lock (_gate)
        {
            read = WillRead(entry, address);
            // Last asked about in an earlier pass: what the server says now is compared with what it said then.
            again = entry.Pass < _project.Pass;
            (size, etag, modified) = (entry.Size, entry.ETag, entry.LastModified);
        }
        // A better route to a page already read in this pass: its links count for more now, and it hasn't changed.
        if (!again && read && entry.Kept && GrabberStore.ReadBody(_folder, entry) is { } kept)
        {
            ReadLinks(entry, kept, new Uri(entry.FinalUrl ?? entry.Url));
            Finish(entry, address, entry.FileName ?? FileNames.FromResponse(null, entry.Url, null));
            return;
        }

        if (Held != null) await Held(ct);
        // A page kept from an earlier pass: the server can say it hasn't changed, and its copy is read again instead.
        var before = again && read && entry.Kept ? GrabberStore.ReadBody(_folder, entry) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PageTimeout);
        using var request = Net.NewRequest(_login, entry.Url, _settings.UserAgent, cookies: Net.SameOrigin(address, _login.Url));
        if (RefererFor(entry.Referrer, address) is { } referer) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (!read) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        if (before != null && etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (before != null && modified != null) request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);

        using var answer = await Net.SendAsync(_client, _settings, _login, request, timeout.Token, RefuseRedirect);
        Answered(address);
        var final = answer.RequestMessage?.RequestUri ?? address;
        if (before != null && answer.StatusCode == HttpStatusCode.NotModified)
        {
            Answered(final);
            ReadLinks(entry, before, final);
            Finish(entry, address, entry.FileName ?? FileNames.FromResponse(null, entry.Url, null));
            return;
        }
        Net.EnsureSuccess(answer);

        Answered(final);
        var name = Describe(entry, answer, address, final);
        bool changed = false;
        if (read && ShouldReadAnswer(entry, address, final))
        {
            var body = await ReadBodyAsync(answer, timeout.Token);
            if (body == null)
            {
                lock (_gate) entry.Kind = GrabberKind.File;
            }
            else
            {
                // A page's own bytes say whether it changed, as a server that sends it again may give nothing to compare.
                if (again) changed = MarkChanged(entry, entry.BodyHash != null ? GrabberStore.HashOf(body) != entry.BodyHash : Differs(entry, size, etag, modified));
                Keep(entry, body);
                ReadLinks(entry, body, final);
            }
        }
        else if (again)
        {
            changed = MarkChanged(entry, Differs(entry, size, etag, modified));
        }
        Finish(entry, address, name);
        if (changed) Updated?.Invoke(entry);
    }

    bool Differs(GrabberEntry entry, long size, string? etag, string? modified)
    {
        lock (_gate) return HttpDownloader.ServerCopyDiffers(entry.Size, entry.ETag, entry.LastModified, size, etag, modified, _settings);
    }

    /// <summary>What was saved of a changed address is to be saved again, in the same place; true if anything was saved.</summary>
    bool MarkChanged(GrabberEntry entry, bool changed)
    {
        lock (_gate)
        {
            if (!changed || entry.FileState != GrabberFileState.Complete) return false;
            entry.FileState = GrabberFileState.None;
            entry.Changed = true;
            return true;
        }
    }

    void Answered(Uri address)
    {
        lock (_gate)
        {
            _answered.Add(address.Authority);
            _lastAnswer = DateTime.UtcNow;
        }
    }

    string? RefuseRedirect(Uri target) =>
        !_mayReachPrivate && IsPrivate(target) ? "The server redirected to a private address, which a public site's pages may not reach." : null;

    // As browsers do by default: the whole address on its own site, its origin on another, and nothing from https to http.
    static string? RefererFor(string? page, Uri target)
    {
        if (page == null || !Uri.TryCreate(page, UriKind.Absolute, out var from)) return null;
        if (from.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps) return null;
        return Net.SameOrigin(target, page) ? page : from.GetLeftPart(UriPartial.Authority) + "/";
    }

    /// <summary>Takes down what the answer says about the address, and returns its file name.</summary>
    string Describe(GrabberEntry entry, HttpResponseMessage answer, Uri address, Uri final)
    {
        var type = answer.Content.Headers.ContentType?.MediaType;
        var range = answer.Content.Headers.ContentRange;
        var size = range?.Length ?? (answer.Content.Headers.ContentEncoding.Count == 0 ? answer.Content.Headers.ContentLength : null) ?? -1;
        var name = NameOf(answer, final, type);
        bool web = IsHtml(type) || (type == null && WebFileTypes.IsPage(Path.GetExtension(final.AbsolutePath)));
        lock (_gate)
        {
            entry.FinalUrl = final == address ? null : final.AbsoluteUri;
            entry.ContentType = type;
            entry.Charset = answer.Content.Headers.ContentType?.CharSet;
            entry.Size = size;
            entry.ETag = answer.Headers.ETag?.Tag;
            entry.LastModified = answer.Content.Headers.LastModified?.ToString("R");
            // A stylesheet or script answered by a page is an error page, or a site that answers everything with its front page.
            if (entry.Kind is GrabberKind.Style or GrabberKind.Script) entry.Kind = web ? GrabberKind.File : entry.Kind;
            else entry.Kind = web ? GrabberKind.Page : GrabberKind.File;

            if (entry.Url == _start!.AbsoluteUri)
            {
                if (!_project.SiteHosts.Contains(final.Host, StringComparer.OrdinalIgnoreCase)) _project.SiteHosts.Add(final.Host);
                _startFolder = FolderOf(final);
            }
            // A link on this site whose redirects end on another one has left it.
            else if (entry.Kind == GrabberKind.Page && IsThisSite(address.Host) && !IsThisSite(final.Host))
            {
                entry.OtherHops++;
            }
        }
        return name;
    }

    static bool IsHtml(string? type) => type is not null
        && (type.Equals("text/html", StringComparison.OrdinalIgnoreCase) || type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    // What a person would call the file: the address alone can't say that of a page.
    static string NameOf(HttpResponseMessage answer, Uri final, string? type)
    {
        var name = FileNames.FromResponse(answer.Content.Headers.ContentDisposition, final.AbsoluteUri, type);
        if (!IsHtml(type)) return name;
        if (final.AbsolutePath.EndsWith('/') && answer.Content.Headers.ContentDisposition == null) return "index.html";
        return Path.HasExtension(name) ? name : name + ".html";
    }

    /// <summary>Whether, having seen the answer, its text is to be read.</summary>
    bool ShouldReadAnswer(GrabberEntry entry, Uri address, Uri final)
    {
        lock (_gate)
        {
            if (entry.Kind is GrabberKind.Style or GrabberKind.Script) return true;
            if (entry.Kind != GrabberKind.Page) return false;
            // Redirected: it's where it ended that counts, unless it's the start page, which moves the site.
            return final == address || entry.Url == _start!.AbsoluteUri || WithinLevels(entry, final);
        }
    }

    async Task<byte[]?> ReadBodyAsync(HttpResponseMessage answer, CancellationToken ct)
    {
        var encodings = answer.Content.Headers.ContentEncoding;
        if (encodings.Count == 0 && answer.Content.Headers.ContentLength > PageLimit) return null;
        await using var raw = await answer.Content.ReadAsStreamAsync(ct);
        await using var body = Unpacked(raw, encodings);
        using var data = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (await body.ReadAsync(buffer, ct) is var read and > 0)
        {
            if (data.Length + read > PageLimit) return null;
            await _limiter.WaitAsync(read, ct);
            Counted?.Invoke(read);
            data.Write(buffer, 0, read);
            if (Held != null) await Held(ct);
        }
        return data.ToArray();
    }

    // Asked for as it is, some servers send a page compressed anyway.
    static Stream Unpacked(Stream raw, ICollection<string> encodings)
    {
        foreach (var encoding in encodings)
        {
            if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase) || encoding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase))
                return new GZipStream(raw, CompressionMode.Decompress);
            if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase)) return new ZLibStream(raw, CompressionMode.Decompress);
            if (encoding.Equals("br", StringComparison.OrdinalIgnoreCase)) return new BrotliStream(raw, CompressionMode.Decompress);
        }
        return raw;
    }

    void Keep(GrabberEntry entry, byte[] body)
    {
        PartFile.EnsureSpace(_folder, body.Length, FreeSpace(_folder), " for the copies of pages the site grabber keeps");
        GrabberStore.WriteBody(_folder, entry, body);
        lock (_gate)
        {
            entry.BodyHash = GrabberStore.HashOf(body);
            entry.Kept = true;
        }
    }

    void ReadLinks(GrabberEntry entry, byte[] bytes, Uri address)
    {
        var body = PageBody.From(bytes, entry.Charset);
        List<PageLinkSpan> links;
        GrabberKind kind;
        lock (_gate) kind = entry.Kind;
        links = kind switch
        {
            GrabberKind.Page => PageParser.Html(body.Text, body.Decode),
            GrabberKind.Style => PageParser.Css(body.Text, body.Decode),
            GrabberKind.Script => PageParser.Script(body.Text, body.Decode),
            _ => new List<PageLinkSpan>(),
        };

        var from = address;
        foreach (var link in links)
        {
            if (link.Kind != PageLinkKind.Base) continue;
            if (Uri.TryCreate(address, link.Value, out var written) && Links.IsDownloadable(written)) from = written;
            break;
        }

        lock (_gate)
        {
            foreach (var link in links)
                if (link.Kind != PageLinkKind.Base) Consider(link, entry, from, address);
            entry.Read = true;
        }
        Signal();
    }

    /// <summary>Takes a link found on a page: adds its address if there's anything to do with it. Needs the lock.</summary>
    void Consider(PageLinkSpan link, GrabberEntry parent, Uri from, Uri page)
    {
        if (!Uri.TryCreate(from, link.Value, out var target) || !Links.IsDownloadable(target) || target.UserInfo.Length > 0) return;
        var address = target.GetLeftPart(UriPartial.Query);
        if (address.Length > MaxUrlLength || (!_mayReachPrivate && IsPrivate(target))) return;
        target = new Uri(address);

        bool on = IsThisSite(target.Host);
        bool partOfPage = link.Kind is PageLinkKind.Style or PageLinkKind.Script or PageLinkKind.Frame or PageLinkKind.Refresh;
        bool parentOn = IsThisSite(page.Host);
        int siteHops = on && !partOfPage ? parent.SiteHops + 1 : parent.SiteHops;
        int otherHops = on ? 0 : partOfPage && !parentOn ? parent.OtherHops : parent.OtherHops + 1;
        var guess = link.Kind switch
        {
            PageLinkKind.Style => GrabberKind.Style,
            PageLinkKind.Script => GrabberKind.Script,
            PageLinkKind.Embedded => GrabberKind.File,
            _ => GrabberKind.Unknown,
        };

        if (_byAddress.TryGetValue(address, out var known))
        {
            bool better = siteHops < known.SiteHops || otherHops < known.OtherHops;
            if (known.LinkText is null or "" && link.Text.Length > 0) known.LinkText = link.Text;
            if (better)
            {
                known.SiteHops = Math.Min(known.SiteHops, siteHops);
                known.OtherHops = Math.Min(known.OtherHops, otherHops);
            }
            // A new pass asks once more about each address it reaches. Within a pass, a better route may let a page
            // count now, or its own links.
            bool ask = known.Pass < _project.Pass
                ? WillRead(known, target) || MayList(target, on)
                : better && known.State == GrabberState.Done && known.Kind is GrabberKind.Page or GrabberKind.Unknown && WillRead(known, target);
            if (ask) AskAgain(known);
            return;
        }

        var entry = new GrabberEntry
        {
            Url = address,
            Referrer = page.AbsoluteUri,
            LinkText = link.Text.Length > 0 ? link.Text : null,
            SiteHops = siteHops,
            OtherHops = otherHops,
            Kind = guess,
        };
        if (!WillRead(entry, target) && !MayList(target, on)) return;
        if (_project.Entries.Count >= EntryLimit)
        {
            LimitReached = true;
            return;
        }
        AddEntry(entry);
    }

    bool IsThisSite(string host)
    {
        foreach (var site in _project.SiteHosts)
        {
            if (string.Equals(site, host, StringComparison.OrdinalIgnoreCase)) return true;
            if (_options.MainDomain && string.Equals(MainDomainOf(site), MainDomainOf(host), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether an address's text is to be read, as far as can be told before asking it. Needs the lock.</summary>
    bool WillRead(GrabberEntry entry, Uri address)
    {
        if (address.Scheme == Uri.UriSchemeFtp) return false;
        if (entry.Url == _start!.AbsoluteUri) return true;
        bool on = IsThisSite(address.Host);
        switch (entry.Kind)
        {
            case GrabberKind.File:
                return false;
            case GrabberKind.Style:
                return !_exploreNever.Matches(address) && (on || !_options.ThisSiteOnly);
            case GrabberKind.Script:
                return !_exploreNever.Matches(address) && on;
        }
        var extension = Path.GetExtension(address.AbsolutePath);
        if (extension.Length > 0 && WebFileTypes.IsFile(extension)) return false;
        return WithinLevels(entry, address);
    }

    /// <summary>Whether a page at this address can be explored, by its levels and the lists. Needs the lock.</summary>
    bool WithinLevels(GrabberEntry entry, Uri address)
    {
        if (_exploreNever.Matches(address) || (!_exploreOnly.IsEmpty && !_exploreOnly.Matches(address))) return false;
        if (!IsThisSite(address.Host)) return !_options.WholeSite && entry.OtherHops <= _options.OtherLevels;
        if (_options.NoParents && !address.AbsolutePath.StartsWith(_startFolder, StringComparison.Ordinal)) return false;
        return _options.WholeSite || entry.SiteHops <= _options.SiteLevels;
    }

    /// <summary>Whether an address can join the list, as far as can be told without asking it. Needs the lock.</summary>
    bool MayList(Uri address, bool on)
    {
        if (!PassesLists(address, on)) return false;
        var extension = Path.GetExtension(address.AbsolutePath);
        if (extension.Length == 0 || !WebFileTypes.NameTells(extension)) return true;
        return PassesMasks(FileNames.FromResponse(null, address.AbsoluteUri, null), address.AbsoluteUri);
    }

    bool PassesLists(Uri address, bool on) =>
        (on || !_options.ThisSiteOnly)
        && !_filesNeverIn.Matches(address)
        && (_filesOnlyIn.IsEmpty || _filesOnlyIn.Matches(address));

    bool PassesMasks(string name, string url) =>
        (_include.IsEmpty || _include.Matches(name, url, _start!.AbsoluteUri))
        && !_exclude.Matches(name, url, _start!.AbsoluteUri);

    bool PassesSizes(long size) =>
        size < 0
        || ((_options.MinSizeKB is not long least || size >= least * 1024)
            && (_options.MaxSizeKB is not long most || size <= most * 1024));

    // With the site's own folders kept, a copy elsewhere has its own place to be.
    bool HidingCopies => _options.HideCopies && !_options.KeepSubfolders;

    static string CopyKey(GrabberEntry entry) => entry.FileName + "|" + entry.Size;

    /// <summary>The address has been looked at: whether it joins the list is settled now.</summary>
    void Finish(GrabberEntry entry, Uri address, string name)
    {
        bool joined;
        lock (_gate)
        {
            entry.FileName = name;
            entry.State = GrabberState.Done;
            entry.Error = null;
            entry.Pass = _project.Pass;
            bool matched = PassesLists(address, IsThisSite(address.Host)) && PassesMasks(name, entry.Url) && PassesSizes(entry.Size);
            if (matched && !entry.Matched && HidingCopies && !_copies.Add(CopyKey(entry))) matched = false;
            joined = matched && !entry.Matched;
            entry.Matched = matched;
        }
        if (joined) Found?.Invoke(entry);
    }
}
