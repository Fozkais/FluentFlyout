// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace FluentFlyoutWPF.Classes.Services;

public class DeezerTrack : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isCurrent;

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    /// <summary>Number of "Next" presses needed to reach this track from the current one.</summary>
    public int SkipCount { get; set; }
    /// <summary>Absolute index in Deezer's internal tracklist.</summary>
    public int TargetIndex { get; set; } = -1;
    /// <summary>True if this is the currently playing track.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public static class DeezerService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient HttpClient = new();
    private static List<DeezerTrack> _cachedQueue = [];
    private static string _lastSearchKey = string.Empty;

    /// <summary>Human-readable description of where the queue was resolved from.</summary>
    public static string LastQueueSource { get; private set; } = string.Empty;

    private static DateTime _lastCacheTime = DateTime.MinValue;

    public static List<DeezerTrack> CachedQueue => _cachedQueue;

    public static void UpdateCache(List<DeezerTrack> tracks)
    {
        _cachedQueue = new List<DeezerTrack>(tracks);
        _lastCacheTime = DateTime.UtcNow;
    }

    public static void ClearCache()
    {
        _cachedQueue = [];
        _lastSearchKey = string.Empty;
        LastQueueSource = string.Empty;
        _lastCacheTime = DateTime.MinValue;
    }

    public static async Task<List<DeezerTrack>> GetQueueAsync(string currentTitle, string currentArtist)
    {
        // If cache was updated recently (e.g. after reordering/deleting), return cached queue immediately
        if (_cachedQueue.Count > 0 && (DateTime.UtcNow - _lastCacheTime).TotalSeconds < 15)
        {
            return _cachedQueue;
        }

        if (await DeezerCdpService.IsCdpAvailableAsync())
        {
            var cdpQueue = await DeezerCdpService.GetQueueFromCdpAsync();
            if (cdpQueue != null && cdpQueue.Count > 0)
            {
                LastQueueSource = "Lecteur Deezer";
                _cachedQueue = cdpQueue;
                _lastCacheTime = DateTime.UtcNow;
                return cdpQueue;
            }
        }

        return _cachedQueue;
    }

    public static async Task<DeezerTrack?> GetNextTrackAsync(string currentTitle, string currentArtist)
    {
        var queue = await GetQueueAsync(currentTitle, currentArtist);
        if (queue.Count == 0) return null;

        int curIdx = queue.FindIndex(t => t.IsCurrent);
        if (curIdx >= 0 && curIdx < queue.Count - 1)
        {
            return queue[curIdx + 1];
        }
        return queue.Count > 0 ? queue[0] : null;
    }

    public static async Task<BitmapImage?> LoadCoverImageAsync(string coverUrl)
    {
        if (string.IsNullOrEmpty(coverUrl)) return null;

        try
        {
            byte[] bytes = await HttpClient.GetByteArrayAsync(coverUrl);
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static List<DeezerTrack> ParseDeezerTracks(string json)
    {
        List<DeezerTrack> tracks = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var elem in dataArray.EnumerateArray())
                {
                    string id = elem.TryGetProperty("id", out var idProp) ? idProp.ToString() : string.Empty;
                    string title = elem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                    
                    string artist = "";
                    if (elem.TryGetProperty("artist", out var artistObj) && artistObj.TryGetProperty("name", out var artistNameProp))
                    {
                        artist = artistNameProp.GetString() ?? "";
                    }

                    string album = "";
                    string cover = "";
                    if (elem.TryGetProperty("album", out var albumObj))
                    {
                        if (albumObj.TryGetProperty("title", out var albumTitleProp)) album = albumTitleProp.GetString() ?? "";
                        if (albumObj.TryGetProperty("cover_medium", out var coverProp)) cover = coverProp.GetString() ?? "";
                    }

                    int duration = elem.TryGetProperty("duration", out var durProp) ? durProp.GetInt32() : 0;

                    tracks.Add(new DeezerTrack
                    {
                        Id = id,
                        Title = title,
                        Artist = artist,
                        Album = album,
                        CoverUrl = cover,
                        DurationSeconds = duration
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed parsing Deezer tracks JSON");
        }
        return tracks;
    }
}
