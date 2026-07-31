// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services;

public static class SpotifyService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public class SpotifyCurrentSong
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
        public bool IsPlaying { get; set; }
        public int ProgressMs { get; set; }
        public int DurationMs { get; set; }
    }

    public class SpotifyPlaylist
    {
        public string Id { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
        public int TrackCount { get; set; }
    }

    public class SpotifyTrack
    {
        public string Id { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public int TargetIndex { get; set; }
        public bool IsCurrent { get; set; }
    }

    public static async Task<bool> IsApiAvailableAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        return !string.IsNullOrEmpty(token);
    }

    public static async Task EnsureSpotifyRunningAsync()
    {
        var procs = Process.GetProcessesByName("Spotify");
        if (procs.Length > 0) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start spotify:",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to launch Spotify Desktop app");
        }
    }

    public static async Task<SpotifyCurrentSong?> GetCurrentSongAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await client.GetAsync("https://api.spotify.com/v1/me/player/currently-playing");
            if (res.StatusCode == System.Net.HttpStatusCode.NoContent || !res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool isPlaying = root.TryGetProperty("is_playing", out var isPlayingProp) && isPlayingProp.GetBoolean();
            int progressMs = root.TryGetProperty("progress_ms", out var progProp) ? progProp.GetInt32() : 0;

            if (!root.TryGetProperty("item", out var itemProp) || itemProp.ValueKind == JsonValueKind.Null) return null;

            string title = itemProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            int durationMs = itemProp.TryGetProperty("duration_ms", out var durProp) ? durProp.GetInt32() : 0;

            string artist = "";
            if (itemProp.TryGetProperty("artists", out var artistsArr) && artistsArr.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var a in artistsArr.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var aName)) names.Add(aName.GetString() ?? "");
                }
                artist = string.Join(", ", names);
            }

            string coverUrl = "";
            if (itemProp.TryGetProperty("album", out var albumProp) &&
                albumProp.TryGetProperty("images", out var imagesArr) &&
                imagesArr.ValueKind == JsonValueKind.Array &&
                imagesArr.GetArrayLength() > 0)
            {
                coverUrl = imagesArr[0].TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            }

            return new SpotifyCurrentSong
            {
                Title = title,
                Artist = artist,
                CoverUrl = coverUrl,
                IsPlaying = isPlaying,
                ProgressMs = progressMs,
                DurationMs = durationMs
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get current playing song from Spotify Web API");
            return null;
        }
    }

    public static async Task<List<SpotifyTrack>?> GetQueueAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await client.GetAsync("https://api.spotify.com/v1/me/player/queue");
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tracks = new List<SpotifyTrack>();

            int idx = 0;
            if (root.TryGetProperty("currently_playing", out var curItem) && curItem.ValueKind != JsonValueKind.Null)
            {
                var track = ParseTrackElement(curItem, idx++, isCurrent: true);
                if (track != null) tracks.Add(track);
            }

            if (root.TryGetProperty("queue", out var queueArr) && queueArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var qItem in queueArr.EnumerateArray())
                {
                    var track = ParseTrackElement(qItem, idx++, isCurrent: false);
                    if (track != null) tracks.Add(track);
                }
            }

            return tracks;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get queue from Spotify Web API");
            return null;
        }
    }

    public static async Task<List<SpotifyPlaylist>?> GetUserPlaylistsAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await client.GetAsync("https://api.spotify.com/v1/me/playlists?limit=50");
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var playlists = new List<SpotifyPlaylist>();

            if (root.TryGetProperty("items", out var itemsArr) && itemsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in itemsArr.EnumerateArray())
                {
                    string id = p.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    string uri = p.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() ?? "" : "";
                    string name = p.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

                    int trackCount = 0;
                    if (p.TryGetProperty("tracks", out var tracksProp) && tracksProp.TryGetProperty("total", out var totalProp))
                        trackCount = totalProp.GetInt32();

                    string coverUrl = "";
                    if (p.TryGetProperty("images", out var imagesArr) && imagesArr.ValueKind == JsonValueKind.Array && imagesArr.GetArrayLength() > 0)
                        coverUrl = imagesArr[0].TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";

                    playlists.Add(new SpotifyPlaylist
                    {
                        Id = id,
                        Uri = uri,
                        Title = name,
                        CoverUrl = coverUrl,
                        TrackCount = trackCount
                    });
                }
            }

            return playlists;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get user playlists from Spotify Web API");
            return null;
        }
    }

    public static async Task<bool> PlayPlaylistAsync(string contextUri)
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var bodyObj = new { context_uri = contextUri };
            string bodyJson = JsonSerializer.Serialize(bodyObj);
            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var res = await client.PutAsync("https://api.spotify.com/v1/me/player/play", content);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to play Spotify playlist");
            return false;
        }
    }

    public static async Task<bool> PlayTrackUriAsync(string trackUri)
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var bodyObj = new { uris = new[] { trackUri } };
            string bodyJson = JsonSerializer.Serialize(bodyObj);
            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var res = await client.PutAsync("https://api.spotify.com/v1/me/player/play", content);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to play Spotify track");
            return false;
        }
    }

    public static async Task<bool> TogglePlayPauseAsync()
    {
        var current = await GetCurrentSongAsync();
        if (current == null) return false;

        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            string endpoint = current.IsPlaying
                ? "https://api.spotify.com/v1/me/player/pause"
                : "https://api.spotify.com/v1/me/player/play";

            var res = await client.PutAsync(endpoint, new StringContent("", Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to toggle Spotify play/pause");
            return false;
        }
    }

    public static async Task<bool> NextTrackAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await client.PostAsync("https://api.spotify.com/v1/me/player/next", new StringContent("", Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to skip to next track on Spotify");
            return false;
        }
    }

    public static async Task<bool> PrevTrackAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await client.PostAsync("https://api.spotify.com/v1/me/player/previous", new StringContent("", Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to skip to previous track on Spotify");
            return false;
        }
    }

    public static async Task<bool> ToggleShuffleAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var playerRes = await client.GetAsync("https://api.spotify.com/v1/me/player");
            bool curState = false;
            if (playerRes.IsSuccessStatusCode)
            {
                string pJson = await playerRes.Content.ReadAsStringAsync();
                using var pDoc = JsonDocument.Parse(pJson);
                if (pDoc.RootElement.TryGetProperty("shuffle_state", out var sProp)) curState = sProp.GetBoolean();
            }

            bool nextState = !curState;
            var res = await client.PutAsync($"https://api.spotify.com/v1/me/player/shuffle?state={nextState.ToString().ToLower()}", new StringContent("", Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to toggle Spotify shuffle");
            return false;
        }
    }

    public static async Task<int> ToggleRepeatAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return 0;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var playerRes = await client.GetAsync("https://api.spotify.com/v1/me/player");
            string curState = "off";
            if (playerRes.IsSuccessStatusCode)
            {
                string pJson = await playerRes.Content.ReadAsStringAsync();
                using var pDoc = JsonDocument.Parse(pJson);
                if (pDoc.RootElement.TryGetProperty("repeat_state", out var rProp)) curState = rProp.GetString() ?? "off";
            }

            string nextState = curState switch
            {
                "off" => "context",
                "context" => "track",
                _ => "off"
            };

            var res = await client.PutAsync($"https://api.spotify.com/v1/me/player/repeat?state={nextState}", new StringContent("", Encoding.UTF8, "application/json"));
            return nextState switch { "context" => 1, "track" => 2, _ => 0 };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to toggle Spotify repeat");
            return 0;
        }
    }

    private static SpotifyTrack? ParseTrackElement(JsonElement item, int index, bool isCurrent)
    {
        if (item.ValueKind == JsonValueKind.Null) return null;

        string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        string uri = item.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() ?? "" : "";
        string title = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
        int durationMs = item.TryGetProperty("duration_ms", out var durProp) ? durProp.GetInt32() : 0;

        string artist = "";
        if (item.TryGetProperty("artists", out var artistsArr) && artistsArr.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in artistsArr.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var aName)) names.Add(aName.GetString() ?? "");
            }
            artist = string.Join(", ", names);
        }

        string album = "";
        string coverUrl = "";
        if (item.TryGetProperty("album", out var albumProp))
        {
            album = albumProp.TryGetProperty("name", out var albName) ? albName.GetString() ?? "" : "";
            if (albumProp.TryGetProperty("images", out var imagesArr) && imagesArr.ValueKind == JsonValueKind.Array && imagesArr.GetArrayLength() > 0)
                coverUrl = imagesArr[0].TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
        }

        return new SpotifyTrack
        {
            Id = id,
            Uri = uri,
            Title = title,
            Artist = artist,
            Album = album,
            CoverUrl = coverUrl,
            DurationSeconds = durationMs / 1000,
            TargetIndex = index,
            IsCurrent = isCurrent
        };
    }
}
