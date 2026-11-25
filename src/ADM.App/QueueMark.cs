using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// The Q column. Bound to a download's QueueId and QueuePosition; the parameter picks what to produce:
/// "glyph" (a download or sync glyph, plus a clock when the queue is scheduled), "brush" (blue or green
/// for the main queues, grey for added ones; bound to Theme.Revision as well, to follow the palette),
/// "position" (the file's place in its queue), or "name" (the queue's name and the place, for the tooltip and screen readers).
/// </summary>
public sealed class QueueMarkConverter : IMultiValueConverter
{
    // Segoe MDL2 Assets: Download, Sync, Clock.
    const string DownloadGlyph = "";
    const string SyncGlyph = "";
    const string ClockGlyph = "";

    /// <summary>Set once by the main window; the converter looks queues up by id.</summary>
    public static DownloadManager? Manager { get; set; }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var queue = values[0] is string id ? Manager?.Queues.FirstOrDefault(q => q.Id == id) : null;
        var position = values.Length > 1 && values[1] is int p ? p : 0;
        return (parameter as string) switch
        {
            "glyph" => queue == null ? "" : (queue.Kind == QueueKind.Sync ? SyncGlyph : DownloadGlyph) + (queue.Schedule.Enabled ? ClockGlyph : ""),
            "brush" => (Brush)Application.Current.Resources[
                queue == null || !queue.IsMain ? "MutedText" : queue.Kind == QueueKind.Sync ? "SyncText" : "AccentText"],
            "position" => position > 0 ? position.ToString(culture) : "",
            _ => queue == null ? Lang.T("queue.not_in_one") : queue.Schedule.Enabled ? Lang.T("queue.place_scheduled", queue.Name, position) : Lang.T("queue.place", queue.Name, position),
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
