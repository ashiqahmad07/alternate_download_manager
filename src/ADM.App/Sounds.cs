using System.IO;
using System.Windows.Media;
using ADM.Core;

namespace ADM.App;

/// <summary>Plays the sounds chosen in Options. <see cref="MediaPlayer"/> plays WAV, MP3 and WMA, and belongs to the UI thread.</summary>
public static class Sounds
{
    // A player nothing refers to can be collected in the middle of its sound.
    static readonly HashSet<MediaPlayer> Playing = new();

    /// <summary>The manager raises these on the UI thread.</summary>
    public static void Hook(DownloadManager manager)
    {
        manager.DownloadCompleted += item => { if (item.GrabberProjectId == null) Play(manager, SoundEvent.DownloadComplete); };
        manager.DownloadFailed += item => { if (item.GrabberProjectId == null) Play(manager, SoundEvent.DownloadFailed); };
        manager.QueueStarted += _ => Play(manager, SoundEvent.QueueStarted);
        manager.QueueFinished += _ => Play(manager, SoundEvent.QueueFinished);
    }

    static void Play(DownloadManager manager, SoundEvent sound)
    {
        if (manager.State.Settings.Sounds.FirstOrDefault(s => s.Event == sound) is { On: true } chosen) PlayFile(chosen.File);
    }

    /// <summary>A file that is missing plays nothing.</summary>
    public static void PlayFile(string path)
    {
        if (!File.Exists(path)) return;
        var player = new MediaPlayer();
        void Done()
        {
            player.Close();
            Playing.Remove(player);
        }
        player.MediaEnded += (_, _) => Done();
        player.MediaFailed += (_, _) => Done();
        Playing.Add(player);
        player.Open(new Uri(Path.GetFullPath(path)));
        player.Play();
    }
}
