namespace ADM.Core;

/// <summary>
/// Command-line arguments, compatible with IDM's: /d URL [/p folder] [/f name] [/n] [/a] [/q] [/s].
/// </summary>
public sealed record CommandLine
{
    public string? Url { get; init; }
    public string? Directory { get; init; }
    public string? FileName { get; init; }
    public bool Silent { get; init; }
    public bool AddToQueueOnly { get; init; }
    public bool ExitWhenDone { get; init; }
    public bool StartQueue { get; init; }
    public bool Background { get; init; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var c = new CommandLine();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value() => i + 1 < args.Count ? args[++i] : null;

            if (arg.Equals("--background", StringComparison.OrdinalIgnoreCase))
            {
                c = c with { Background = true };
            }
            else if (arg.Length == 2 && arg[0] is '/' or '-')
            {
                // /h (hang up a dial-up connection) is accepted for compatibility and ignored.
                c = char.ToLowerInvariant(arg[1]) switch
                {
                    'd' => c with { Url = Value() },
                    'p' => c with { Directory = Value() },
                    'f' => c with { FileName = Value() },
                    'n' => c with { Silent = true },
                    'a' => c with { AddToQueueOnly = true },
                    'q' => c with { ExitWhenDone = true },
                    's' => c with { StartQueue = true },
                    _ => c,
                };
            }
            else if (c.Url == null && Uri.TryCreate(arg, UriKind.Absolute, out var uri) && Links.IsDownloadable(uri))
            {
                c = c with { Url = arg };
            }
        }
        return c;
    }
}
