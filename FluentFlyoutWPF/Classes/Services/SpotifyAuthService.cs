// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services;

public static class SpotifyAuthService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // Public Spotify PKCE Client ID (No client secret required)
    public const string DefaultClientId = "65b708073fc0480ea92a077233ca87bd"; 
    private const string RedirectUri = "http://localhost:8888/spotify-callback/";

    public static bool IsAuthenticated => !string.IsNullOrEmpty(SettingsManager.Current.SpotifyRefreshToken);

    public static async Task<bool> AuthenticateAsync(string? customClientId = null)
    {
        string configuredId = SettingsManager.Current.SpotifyClientId?.Trim() ?? "";
        if (configuredId == "5f3a0937a0e24177b960b730ca7b415a") configuredId = "";

        string clientId = !string.IsNullOrWhiteSpace(customClientId)
            ? customClientId.Trim()
            : (!string.IsNullOrWhiteSpace(configuredId)
                ? configuredId
                : DefaultClientId);

        try
        {
            string codeVerifier = GenerateCodeVerifier();
            string codeChallenge = GenerateCodeChallenge(codeVerifier);

            string scope = Uri.EscapeDataString("user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative");
            string authUrl = $"https://accounts.spotify.com/authorize?response_type=code&client_id={clientId}&scope={scope}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&code_challenge_method=S256&code_challenge={codeChallenge}";

            using var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8888/callback/");
            listener.Prefixes.Add("http://localhost:8888/spotify-callback/");
            listener.Start();

            Process.Start(new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });

            var context = await listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            string? code = request.QueryString["code"];

            string responseString = "<html><body style='font-family:sans-serif;text-align:center;padding-top:40px;'><h2>Connexion a Spotify reussie !</h2><p>Vous pouvez fermer cette fenetre et retourner sur FluentFlyout.</p></body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();

            listener.Stop();

            if (!string.IsNullOrEmpty(code))
            {
                return await ExchangeCodeForTokenAsync(clientId, code, codeVerifier);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to authenticate with Spotify OAuth PKCE");
        }

        return false;
    }

    private static async Task<bool> ExchangeCodeForTokenAsync(string clientId, string code, string codeVerifier)
    {
        try
        {
            using var client = new HttpClient();
            var body = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "client_id", clientId },
                { "code_verifier", codeVerifier }
            };

            var res = await client.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(body));
            if (!res.IsSuccessStatusCode) return false;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string accessToken = root.GetProperty("access_token").GetString() ?? "";
            string refreshToken = root.GetProperty("refresh_token").GetString() ?? "";
            int expiresIn = root.GetProperty("expires_in").GetInt32();

            SettingsManager.Current.SpotifyAccessToken = accessToken;
            SettingsManager.Current.SpotifyRefreshToken = refreshToken;
            SettingsManager.Current.SpotifyTokenExpiration = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            SettingsManager.SaveSettings();

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to exchange code for Spotify token");
            return false;
        }
    }

    public static async Task<string?> GetValidAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(SettingsManager.Current.SpotifyRefreshToken))
            return null;

        if (!string.IsNullOrEmpty(SettingsManager.Current.SpotifyAccessToken) &&
            DateTime.UtcNow < SettingsManager.Current.SpotifyTokenExpiration)
        {
            return SettingsManager.Current.SpotifyAccessToken;
        }

        return await RefreshTokenAsync();
    }

    public static async Task<string?> RefreshTokenAsync()
    {
        string refreshToken = SettingsManager.Current.SpotifyRefreshToken;
        if (string.IsNullOrEmpty(refreshToken)) return null;

        string clientId = !string.IsNullOrWhiteSpace(SettingsManager.Current.SpotifyClientId)
            ? SettingsManager.Current.SpotifyClientId.Trim()
            : DefaultClientId;

        try
        {
            using var client = new HttpClient();
            var body = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "client_id", clientId }
            };

            var res = await client.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(body));
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string accessToken = root.GetProperty("access_token").GetString() ?? "";
            int expiresIn = root.GetProperty("expires_in").GetInt32();

            if (root.TryGetProperty("refresh_token", out var newRefresh))
            {
                string? updatedRefresh = newRefresh.GetString();
                if (!string.IsNullOrEmpty(updatedRefresh))
                    SettingsManager.Current.SpotifyRefreshToken = updatedRefresh;
            }

            SettingsManager.Current.SpotifyAccessToken = accessToken;
            SettingsManager.Current.SpotifyTokenExpiration = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            SettingsManager.SaveSettings();

            return accessToken;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh Spotify access token");
            return null;
        }
    }

    public static void Logout()
    {
        SettingsManager.Current.SpotifyAccessToken = string.Empty;
        SettingsManager.Current.SpotifyRefreshToken = string.Empty;
        SettingsManager.Current.SpotifyTokenExpiration = DateTime.MinValue;
        SettingsManager.SaveSettings();
    }

    private static string GenerateCodeVerifier()
    {
        byte[] bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
