// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services;

public static class DeezerCdpService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient HttpClient = new();
    private const int DebugPort = 9222;

    public static string GetDeezerExePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win32Path = Path.Combine(localAppData, "Programs", "deezer-desktop", "Deezer.exe");
        if (File.Exists(win32Path))
            return win32Path;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfPath = Path.Combine(programFiles, "Deezer", "Deezer.exe");
        if (File.Exists(pfPath))
            return pfPath;

        return win32Path; // Default fallback
    }

    public static async Task<bool> IsCdpAvailableAsync()
    {
        try
        {
            var resp = await HttpClient.GetAsync($"http://localhost:{DebugPort}/json");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCdpAvailableSync
    {
        get
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
                var resp = client.GetAsync($"http://localhost:{DebugPort}/json").Result;
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task EnsureDeezerRunningWithDebugPortAsync()
    {
        if (await IsCdpAvailableAsync())
            return;

        try
        {
            var processes = Process.GetProcessesByName("Deezer");
            // If Deezer is ALREADY running, NEVER kill it!
            if (processes.Length > 0)
            {
                Logger.Info("Deezer is running without debug port. Skipping restart to avoid closing player.");
                return;
            }

            string exePath = GetDeezerExePath();
            if (!File.Exists(exePath))
            {
                Logger.Warn($"Deezer executable not found at {exePath}");
                return;
            }

            // Start Deezer with --remote-debugging-port=9222
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--remote-debugging-port={DebugPort}",
                UseShellExecute = true
            };
            Process.Start(psi);

            // Wait up to 3 seconds for CDP port to open
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(200);
                if (await IsCdpAvailableAsync())
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to launch Deezer with remote debugging port");
        }
    }

    private static ClientWebSocket? _persistentWs;
    private static readonly SemaphoreSlim _wsLock = new SemaphoreSlim(1, 1);
    private static int _nextMessageId = 1;

    private static async Task<ClientWebSocket?> GetConnectedWebSocketAsync()
    {
        if (_persistentWs != null && _persistentWs.State == WebSocketState.Open)
        {
            return _persistentWs;
        }

        await _wsLock.WaitAsync();
        try
        {
            if (_persistentWs != null && _persistentWs.State == WebSocketState.Open)
            {
                return _persistentWs;
            }

            _persistentWs?.Dispose();
            _persistentWs = null;

            string json = await HttpClient.GetStringAsync($"http://localhost:{DebugPort}/json");
            using var doc = JsonDocument.Parse(json);
            
            string? wsUrl = null;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                string url = elem.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string type = elem.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "page" && url.Contains("index.html"))
                {
                    if (elem.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                    {
                        wsUrl = wsProp.GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(wsUrl)) return null;

            var ws = new ClientWebSocket();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            _persistentWs = ws;
            return _persistentWs;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to connect CDP WebSocket: {ex.Message}");
            _persistentWs?.Dispose();
            _persistentWs = null;
            return null;
        }
        finally
        {
            _wsLock.Release();
        }
    }

    public static async Task<bool> ExecuteJsAsync(string jsExpression)
    {
        await EnsureDeezerRunningWithDebugPortAsync();

        try
        {
            var ws = await GetConnectedWebSocketAsync();
            if (ws == null || ws.State != WebSocketState.Open)
                return false;

            int msgId = System.Threading.Interlocked.Increment(ref _nextMessageId);
            var payload = new
            {
                id = msgId,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = jsExpression,
                    awaitPromise = true
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            using var ms = new MemoryStream();
            byte[] buffer = new byte[16384];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Error executing JS via persistent WebSocket: {ex.Message}. Resetting connection.");
            _persistentWs?.Dispose();
            _persistentWs = null;
            return false;
        }
    }

    public static async Task<string?> EvaluateJsAndReturnStringAsync(string jsExpression)
    {
        await EnsureDeezerRunningWithDebugPortAsync();

        try
        {
            var ws = await GetConnectedWebSocketAsync();
            if (ws == null || ws.State != WebSocketState.Open)
                return null;

            int msgId = System.Threading.Interlocked.Increment(ref _nextMessageId);
            var payload = new
            {
                id = msgId,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = jsExpression,
                    awaitPromise = true,
                    returnByValue = true
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            using var ms = new MemoryStream();
            byte[] buffer = new byte[16384];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string responseJson = Encoding.UTF8.GetString(ms.ToArray());

            using var resDoc = JsonDocument.Parse(responseJson);
            if (resDoc.RootElement.TryGetProperty("result", out var resObj) &&
                resObj.TryGetProperty("result", out var innerRes) &&
                innerRes.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.String)
                    return valProp.GetString();
                return valProp.GetRawText();
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Error evaluating JS via persistent WebSocket: {ex.Message}. Resetting connection.");
            _persistentWs?.Dispose();
            _persistentWs = null;
            return null;
        }
    }

    public static async Task<bool> RemoveTrackAsync(int index)
    {
        if (index < 0) return false;
        string script = $"window.dzPlayer && typeof window.dzPlayer.removeTracks === 'function' ? (window.dzPlayer.removeTracks({index}), true) : false";
        return await ExecuteJsAsync(script);
    }

    public static async Task<bool> MoveTrackAsync(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return false;
        string script = $@"(() => {{
            if (!window.dzPlayer || typeof window.dzPlayer.getTrackList !== 'function' || typeof window.dzPlayer.orderTracks !== 'function') return false;
            let tracks = window.dzPlayer.getTrackList();
            if (!tracks || {fromIndex} < 0 || {fromIndex} >= tracks.length || {toIndex} < 0 || {toIndex} >= tracks.length) return false;
            let ids = tracks.map(t => t.SNG_ID || t.id);
            let item = ids.splice({fromIndex}, 1)[0];
            ids.splice({toIndex}, 0, item);
            return window.dzPlayer.orderTracks(ids);
        }})()";
        return await ExecuteJsAsync(script);
    }

    private static int GetIntSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt32();
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out int val)) return val;
        }
        return 0;
    }

    private static string GetStringSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
            return p.ToString() ?? "";
        }
        return "";
    }

    private static bool GetBoolSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out bool b)) return b;
        }
        return false;
    }

    public static async Task<List<DeezerTrack>?> GetQueueFromCdpAsync()
    {
        string js = """
        (function() {
            try {
                if (!window.dzPlayer || typeof window.dzPlayer.getTrackList !== 'function') return null;
                const list = window.dzPlayer.getTrackList();
                const curIdx = typeof window.dzPlayer.getIndexSong === 'function' ? window.dzPlayer.getIndexSong() : 0;
                return JSON.stringify({
                    curIndex: Number(curIdx) || 0,
                    tracks: list.map((t, idx) => ({
                        idx: Number(idx),
                        id: String(t.SNG_ID || ''),
                        title: String(t.SNG_TITLE || ''),
                        artist: String(t.ART_NAME || ''),
                        album: String(t.ALB_TITLE || ''),
                        coverPic: String(t.ALB_PICTURE || ''),
                        duration: parseInt(t.DURATION, 10) || 0,
                        isCurrent: Number(idx) === Number(curIdx)
                    }))
                });
            } catch(e) {
                return null;
            }
        })()
        """;

        string? resultJson = await EvaluateJsAndReturnStringAsync(js);
        if (string.IsNullOrEmpty(resultJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tracks", out var tracksArr)) return null;

            int curIdx = GetIntSafe(root, "curIndex");

            var tracks = new List<DeezerTrack>();
            foreach (var item in tracksArr.EnumerateArray())
            {
                int idx = GetIntSafe(item, "idx");
                string title = GetStringSafe(item, "title");
                string artist = GetStringSafe(item, "artist");
                string album = GetStringSafe(item, "album");
                string coverPic = GetStringSafe(item, "coverPic");
                int duration = GetIntSafe(item, "duration");
                bool isCurrent = GetBoolSafe(item, "isCurrent");

                string coverUrl = string.IsNullOrEmpty(coverPic)
                    ? ""
                    : $"https://e-cdns-images.dzcdn.net/images/cover/{coverPic}/250x250-000000-80-0-0.jpg";

                tracks.Add(new DeezerTrack
                {
                    Id = GetStringSafe(item, "id"),
                    Title = title,
                    Artist = artist,
                    Album = album,
                    CoverUrl = coverUrl,
                    DurationSeconds = duration,
                    TargetIndex = idx,
                    IsCurrent = isCurrent,
                    SkipCount = idx - curIdx
                });
            }

            return tracks;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error parsing CDP tracklist");
            return null;
        }
    }

    public static async Task<bool> PlayTrackAtIndexAsync(int targetIndex)
    {
        string js = $"(function() {{ if (window.dzPlayer && typeof window.dzPlayer.playTrackAtIndex === 'function') {{ window.dzPlayer.playTrackAtIndex({targetIndex}); return 'played_' + {targetIndex}; }} return false; }})()";
        return await ExecuteJsAsync(js);
    }

    /// <summary>
    /// Jump forward by skipCount tracks in Deezer Web Player via CDP JS execution.
    /// </summary>
    public static async Task<bool> SkipTracksAsync(int skipCount)
    {
        string js = $$"""
        (function() {
            try {
                if (window.dzPlayer) {
                    if (typeof window.dzPlayer.playTrackAtIndex === 'function') {
                        const curIdx = typeof window.dzPlayer.getIndexSong === 'function' ? window.dzPlayer.getIndexSong() : 0;
                        const targetIdx = curIdx + {{skipCount}};
                        window.dzPlayer.playTrackAtIndex(targetIdx);
                        return 'played_at_index_' + targetIdx;
                    }
                    if (window.dzPlayer.control && typeof window.dzPlayer.control.next === 'function') {
                        for (let i = 0; i < {{skipCount}}; i++) {
                            window.dzPlayer.control.next();
                        }
                        return 'called_next';
                    }
                }
                // Fallback: click Next button in Deezer DOM
                for (let i = 0; i < {{skipCount}}; i++) {
                    const btn = document.querySelector('button[aria-label="Morceau suivant"], button[aria-label="Next track"], button[aria-label="Next"]');
                    if (btn) btn.click();
                }
                return 'dom_clicked';
            } catch(e) {
                return 'error: ' + e.message;
            }
        })()
        """;

        return await ExecuteJsAsync(js);
    }

    // CDP Controls for Shuffle and Repeat
    public static async Task<bool> ToggleShuffleAsync()
    {
        string js = @"(function() {
            if (window.dzPlayer && window.dzPlayer.control) {
                const cur = typeof window.dzPlayer.isShuffle === 'function' ? window.dzPlayer.isShuffle() : false;
                const next = !cur;
                if (typeof window.dzPlayer.control.setShuffle === 'function') {
                    window.dzPlayer.control.setShuffle(next);
                    return next ? 'true' : 'false';
                }
            }
            return 'false';
        })()";
        string? res = await EvaluateJsAndReturnStringAsync(js);
        return res == "true" || res == "\"true\"";
    }

    public static async Task<int> ToggleRepeatAsync()
    {
        string js = @"(function() {
            if (window.dzPlayer && window.dzPlayer.control) {
                const cur = typeof window.dzPlayer.getRepeat === 'function' ? window.dzPlayer.getRepeat() : 0;
                const next = (cur + 1) % 3;
                if (typeof window.dzPlayer.control.setRepeat === 'function') {
                    window.dzPlayer.control.setRepeat(next);
                    return String(next);
                }
            }
            return '0';
        })()";
        string? res = await EvaluateJsAndReturnStringAsync(js);
        if (int.TryParse(res?.Trim('"', ' '), out int mode)) return mode;
        return 0;
    }

    public static async Task<bool> GetShuffleStateAsync()
    {
        string js = @"(function() {
            return (window.dzPlayer && typeof window.dzPlayer.isShuffle === 'function' && window.dzPlayer.isShuffle()) ? 'true' : 'false';
        })()";
        string? res = await EvaluateJsAndReturnStringAsync(js);
        return res == "true" || res == "\"true\"";
    }

    public static async Task<int> GetRepeatStateAsync()
    {
        string js = @"(function() {
            return (window.dzPlayer && typeof window.dzPlayer.getRepeat === 'function') ? String(window.dzPlayer.getRepeat()) : '0';
        })()";
        string? res = await EvaluateJsAndReturnStringAsync(js);
        if (int.TryParse(res?.Trim('"', ' '), out int mode)) return mode;
        return 0;
    }

    private static long GetLongSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long val))
                return val;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out long parsedVal))
                return parsedVal;
        }
        return 0;
    }

    private static readonly HttpClient _httpClient = new HttpClient();

    // Playlist Selector CDP API
    public static async Task<List<DeezerPlaylist>> GetUserPlaylistsAsync()
    {
        var list = new List<DeezerPlaylist>();

        string js = @"(async function() {
            try {
                let userId = '';

                if (window.location && window.location.href) {
                    const m = window.location.href.match(/\/profile\/(\d+)/);
                    if (m) userId = m[1];
                }

                if (!userId) {
                    const links = Array.from(document.querySelectorAll('a[href*=""/profile/""]'));
                    for (const a of links) {
                        const m = a.href.match(/\/profile\/(\d+)/);
                        if (m) { userId = m[1]; break; }
                    }
                }

                if (!userId && typeof localStorage !== 'undefined') {
                    for (let i = 0; i < localStorage.length; i++) {
                        const key = localStorage.key(i);
                        const m = key ? key.match(/_(\d{8,})/) : null;
                        if (m) { userId = m[1]; break; }
                    }
                }

                if (userId) {
                    const resp = await fetch('https://api.deezer.com/user/' + userId + '/playlists?limit=50');
                    if (resp.ok) {
                        const resJson = await resp.json();
                        if (resJson && resJson.data && Array.isArray(resJson.data)) {
                            return JSON.stringify(resJson.data.map(p => ({
                                id: p.id,
                                title: p.title,
                                picture: p.picture_medium || p.picture || p.cover || '',
                                tracks: p.nb_tracks || 0
                            })));
                        }
                    }
                }
            } catch(e) {}
            return '[]';
        })()";

        string? json = await EvaluateJsAndReturnStringAsync(js);
        if (!string.IsNullOrEmpty(json) && json != "[]")
        {
            list = ParsePlaylistsJson(json);
        }

        // Failsafe Method: If list is still empty, query profile ID via C# HttpClient
        if (list.Count == 0)
        {
            try
            {
                string getUserIdJs = @"(function() {
                    if (window.location && window.location.href) {
                        const m = window.location.href.match(/\/profile\/(\d+)/);
                        if (m) return m[1];
                    }
                    const links = Array.from(document.querySelectorAll('a[href*=""/profile/""]'));
                    for (const a of links) {
                        const m = a.href.match(/\/profile\/(\d+)/);
                        if (m) return m[1];
                    }
                    if (typeof localStorage !== 'undefined') {
                        for (let i = 0; i < localStorage.length; i++) {
                            const key = localStorage.key(i);
                            const m = key ? key.match(/_(\d{8,})/) : null;
                            if (m) return m[1];
                        }
                    }
                    return '';
                })()";

                string? userId = await EvaluateJsAndReturnStringAsync(getUserIdJs);
                userId = userId?.Trim('"', ' ', '\r', '\n');

                if (!string.IsNullOrEmpty(userId))
                {
                    string apiUrl = $"https://api.deezer.com/user/{userId}/playlists?limit=50";
                    string apiResponse = await _httpClient.GetStringAsync(apiUrl);
                    using var doc = JsonDocument.Parse(apiResponse);
                    if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataArr.EnumerateArray())
                        {
                            long id = GetLongSafe(item, "id");
                            string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                            string pic = item.TryGetProperty("picture_medium", out var picProp) ? picProp.GetString() ?? "" : "";
                            int count = GetIntSafe(item, "nb_tracks");

                            if (id > 0)
                            {
                                list.Add(new DeezerPlaylist
                                {
                                    Id = id,
                                    Title = title,
                                    CoverUrl = string.IsNullOrEmpty(pic) ? "https://e-cdns-images.dzcdn.net/images/cover/250x250-000000-80-0-0.jpg" : pic,
                                    TrackCount = count
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error fetching user playlists via C# HttpClient fallback");
            }
        }

        return list;
    }

    private static List<DeezerPlaylist> ParsePlaylistsJson(string json)
    {
        var list = new List<DeezerPlaylist>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                long id = GetLongSafe(item, "id");
                string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                string pic = item.TryGetProperty("picture", out var picProp) ? picProp.GetString() ?? "" : "";
                int count = GetIntSafe(item, "tracks");

                string coverUrl = string.IsNullOrEmpty(pic)
                    ? "https://e-cdns-images.dzcdn.net/images/cover/250x250-000000-80-0-0.jpg"
                    : (pic.StartsWith("http") ? pic : $"https://e-cdns-images.dzcdn.net/images/cover/{pic}/250x250-000000-80-0-0.jpg");

                if (id > 0)
                {
                    list.Add(new DeezerPlaylist
                    {
                        Id = id,
                        Title = title,
                        CoverUrl = coverUrl,
                        TrackCount = count
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error parsing user playlists JSON from CDP");
        }
        return list;
    }

    public static async Task<bool> PlayPlaylistAsync(long playlistId)
    {
        string js = $@"(async function() {{
            try {{
                const resp = await fetch('https://api.deezer.com/playlist/{playlistId}/tracks?limit=200');
                if (resp.ok) {{
                    const json = await resp.json();
                    if (json.data && Array.isArray(json.data) && json.data.length > 0) {{
                        const sngIds = json.data.map(t => String(t.id));
                        if (window.dzPlayer && typeof window.dzPlayer.play === 'function') {{
                            window.dzPlayer.play({{ type: 'tracks', ids: sngIds }});
                            if (window.dzPlayer.control && typeof window.dzPlayer.control.play === 'function') {{
                                window.dzPlayer.control.play();
                            }}
                            return 'played';
                        }}
                    }}
                }}
            }} catch(e) {{}}

            if (window.dzPlayer && typeof window.dzPlayer.play === 'function') {{
                window.dzPlayer.play({{ type: 'playlist', id: '{playlistId}' }});
                return 'played_fallback';
            }}
            return 'error';
        }})()";

        return await ExecuteJsAsync(js);
    }

    /// <summary>
    /// Gets a full signature of the current Deezer playback queue (track IDs sequence + shuffle + repeat)
    /// to detect any reorders, track deletions, shuffle changes, or track changes made directly inside Deezer Desktop.
    /// </summary>
    public static async Task<string> GetQueueSignatureAsync()
    {
        string js = @"(function() {
            try {
                if (window.dzPlayer) {
                    let list = typeof window.dzPlayer.getTrackList === 'function' ? window.dzPlayer.getTrackList() : [];
                    let ids = Array.isArray(list) ? list.map(t => (t ? (t.SNG_ID || t.id || '') : '')).join(',') : '';
                    let shuffle = typeof window.dzPlayer.isShuffle === 'function' ? (window.dzPlayer.isShuffle() ? '1' : '0') : '0';
                    let repeat = typeof window.dzPlayer.getRepeat === 'function' ? String(window.dzPlayer.getRepeat()) : '0';
                    let curSong = typeof window.dzPlayer.getSongId === 'function' ? String(window.dzPlayer.getSongId()) : '';
                    return ids + '|' + shuffle + '|' + repeat + '|' + curSong;
                }
            } catch(e) {}
            return '';
        })()";

        string? sig = await EvaluateJsAndReturnStringAsync(js);
        return sig?.Trim('"', ' ') ?? "";
    }
}

public class DeezerPlaylist
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public int TrackCount { get; set; }
}
