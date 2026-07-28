// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace FluentFlyoutWPF.Classes.Services;

public static class DeezerAuthService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public const string OfficialDeezerAppId = "179891";
    private const string RedirectUri = "http://localhost:8888/callback/";

    public static bool IsAuthenticated => !string.IsNullOrEmpty(SettingsManager.Current.DeezerAccessToken);

    public static async Task<bool> AuthenticateAsync(string? customAppId = null)
    {
        string appId = !string.IsNullOrWhiteSpace(customAppId) 
            ? customAppId.Trim()
            : (!string.IsNullOrWhiteSpace(SettingsManager.Current.DeezerAppId) 
                ? SettingsManager.Current.DeezerAppId.Trim() 
                : OfficialDeezerAppId);

        try
        {
            string authUrl = $"https://connect.deezer.com/oauth/auth.php?app_id={appId.Trim()}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&perms=basic_access,email,offline_access,manage_library&response_type=token";

            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri);
            listener.Start();

            Process.Start(new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });

            var context = await listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            // Handle token returned in query string or URL fragment
            string? token = request.QueryString["access_token"];

            string responseString = "<html><body><h2>Connexion a Deezer reussie ! Vous pouvez fermer cette fenetre.</h2></body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();

            listener.Stop();

            if (!string.IsNullOrEmpty(token))
            {
                SettingsManager.Current.DeezerAccessToken = token;
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to authenticate with Deezer OAuth");
        }

        return false;
    }

    public static void Logout()
    {
        SettingsManager.Current.DeezerAccessToken = string.Empty;
    }
}
