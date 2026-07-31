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

    public static string? LastActiveContextUri { get; set; }

    public static async Task<List<SpotifyTrack>?> GetQueueAsync()
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            string? activeCtxUri = LastActiveContextUri;
            string curTrackUri = "";
            string curTrackTitle = "";

            // 1. Check player state for active context (playlist / album) & currently playing track
            var playerRes = await client.GetAsync("https://api.spotify.com/v1/me/player");
            if (playerRes.IsSuccessStatusCode)
            {
                string pJson = await playerRes.Content.ReadAsStringAsync();
                using var pDoc = JsonDocument.Parse(pJson);
                var pRoot = pDoc.RootElement;

                if (pRoot.TryGetProperty("context", out var ctxProp) &&
                    ctxProp.ValueKind == JsonValueKind.Object &&
                    ctxProp.TryGetProperty("uri", out var uriProp))
                {
                    string ctxUri = uriProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(ctxUri))
                    {
                        activeCtxUri = ctxUri;
                        LastActiveContextUri = ctxUri;
                    }
                }

                if (pRoot.TryGetProperty("item", out var itemObj) && itemObj.ValueKind == JsonValueKind.Object)
                {
                    if (itemObj.TryGetProperty("uri", out var curUriProp))
                        curTrackUri = curUriProp.GetString() ?? "";
                    if (itemObj.TryGetProperty("name", out var curNameProp))
                        curTrackTitle = curNameProp.GetString() ?? "";
                }
            }

            // 2. If context is a Spotify playlist, fetch full 50-100+ playlist tracks!
            if (!string.IsNullOrEmpty(activeCtxUri) && activeCtxUri.StartsWith("spotify:playlist:"))
            {
                var plTracks = await GetPlaylistTracksAsync(activeCtxUri, curTrackUri, curTrackTitle);
                if (plTracks != null && plTracks.Count > 0)
                {
                    return plTracks;
                }
            }

            // 3. Fallback to standard player queue
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

    public static async Task<List<SpotifyTrack>?> GetPlaylistTracksAsync(string contextUri, string curTrackUri = "", string curTitle = "")
    {
        if (string.IsNullOrEmpty(contextUri) || !contextUri.StartsWith("spotify:playlist:")) return null;
        string playlistId = contextUri.Replace("spotify:playlist:", "");

        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage playlistRes = await client.GetAsync($"https://api.spotify.com/v1/playlists/{playlistId}/tracks?limit=100");
            if (!playlistRes.IsSuccessStatusCode)
            {
                playlistRes = await client.GetAsync($"https://api.spotify.com/v1/playlists/{playlistId}");
            }

            if (playlistRes.IsSuccessStatusCode)
            {
                string plJson = await playlistRes.Content.ReadAsStringAsync();
                using var plDoc = JsonDocument.Parse(plJson);
                var root = plDoc.RootElement;

                JsonElement itemsArr = default;
                bool foundItems = false;

                if (root.TryGetProperty("items", out var iArr) && iArr.ValueKind == JsonValueKind.Array)
                {
                    itemsArr = iArr;
                    foundItems = true;
                }
                else if (root.TryGetProperty("tracks", out var tObj) && tObj.TryGetProperty("items", out var tItems) && tItems.ValueKind == JsonValueKind.Array)
                {
                    itemsArr = tItems;
                    foundItems = true;
                }

                if (foundItems)
                {
                    var playlistTracks = new List<SpotifyTrack>();
                    int pIdx = 0;
                    foreach (var item in itemsArr.EnumerateArray())
                    {
                        JsonElement trackObj = item;
                        if (item.TryGetProperty("track", out var innerTrack) && innerTrack.ValueKind == JsonValueKind.Object)
                            trackObj = innerTrack;

                        string tUri = trackObj.TryGetProperty("uri", out var uProp) ? uProp.GetString() ?? "" : "";
                        string tName = trackObj.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";

                        bool isCur = (!string.IsNullOrEmpty(curTrackUri) && tUri == curTrackUri) ||
                                     (!string.IsNullOrEmpty(curTitle) && tName.Equals(curTitle, StringComparison.OrdinalIgnoreCase));

                        var parsed = ParseTrackElement(trackObj, pIdx++, isCurrent: isCur);
                        if (parsed != null) playlistTracks.Add(parsed);
                    }

                    if (playlistTracks.Count > 0) return playlistTracks;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get playlist tracks from Spotify");
        }

        return null;
    }

    public static async Task<bool> RemoveTrackFromPlaylistAsync(string contextUri, string trackUri, int index)
    {
        if (string.IsNullOrEmpty(contextUri) || !contextUri.StartsWith("spotify:playlist:")) return false;
        string playlistId = contextUri.Replace("spotify:playlist:", "");

        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var bodyObj = new
            {
                tracks = new[] { new { uri = trackUri, positions = new[] { index } } }
            };
            string bodyJson = JsonSerializer.Serialize(bodyObj);
            var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.spotify.com/v1/playlists/{playlistId}/tracks")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };

            var res = await client.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to remove track from Spotify playlist");
            return false;
        }
    }

    public static async Task<bool> ReorderPlaylistTracksAsync(string contextUri, int rangeStart, int insertBefore)
    {
        if (string.IsNullOrEmpty(contextUri) || !contextUri.StartsWith("spotify:playlist:")) return false;
        string playlistId = contextUri.Replace("spotify:playlist:", "");

        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            int targetInsert = insertBefore > rangeStart ? insertBefore + 1 : insertBefore;

            var bodyObj = new
            {
                range_start = rangeStart,
                insert_before = targetInsert
            };
            string bodyJson = JsonSerializer.Serialize(bodyObj);
            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var res = await client.PutAsync($"https://api.spotify.com/v1/playlists/{playlistId}/tracks", content);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to reorder Spotify playlist tracks");
            return false;
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
                    if (p.TryGetProperty("tracks", out var tracksProp))
                    {
                        if (tracksProp.ValueKind == JsonValueKind.Number)
                            trackCount = tracksProp.GetInt32();
                        else if (tracksProp.ValueKind == JsonValueKind.Object && tracksProp.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
                            trackCount = totalProp.GetInt32();
                    }

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
            LastActiveContextUri = contextUri;
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

    public static async Task<bool> PlayTrackUriAsync(string trackUri, string? contextUri = null, List<SpotifyTrack>? fullQueue = null, int targetIndex = -1)
    {
        string? token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            string activeCtx = !string.IsNullOrEmpty(contextUri) ? contextUri : (LastActiveContextUri ?? "");

            object bodyObj;
            if (!string.IsNullOrEmpty(activeCtx))
            {
                bodyObj = new
                {
                    context_uri = activeCtx,
                    offset = new { uri = trackUri }
                };
            }
            else if (fullQueue != null && targetIndex >= 0 && targetIndex < fullQueue.Count)
            {
                var remainingUris = fullQueue.Skip(targetIndex).Select(t => t.Uri).Where(u => !string.IsNullOrEmpty(u)).ToArray();
                if (remainingUris.Length > 0)
                    bodyObj = new { uris = remainingUris };
                else
                    bodyObj = new { uris = new[] { trackUri } };
            }
            else
            {
                bodyObj = new { uris = new[] { trackUri } };
            }

            string bodyJson = JsonSerializer.Serialize(bodyObj);
            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var res = await client.PutAsync("https://api.spotify.com/v1/me/player/play", content);

            // Fallback if context_uri offset failed (e.g. track not found in context)
            if (!res.IsSuccessStatusCode && !string.IsNullOrEmpty(activeCtx) && fullQueue != null && targetIndex >= 0)
            {
                var remainingUris = fullQueue.Skip(targetIndex).Select(t => t.Uri).Where(u => !string.IsNullOrEmpty(u)).ToArray();
                if (remainingUris.Length > 0)
                {
                    bodyObj = new { uris = remainingUris };
                    bodyJson = JsonSerializer.Serialize(bodyObj);
                    content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                    res = await client.PutAsync("https://api.spotify.com/v1/me/player/play", content);
                }
            }

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
