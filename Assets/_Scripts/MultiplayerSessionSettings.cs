using System;
using System.Linq;
using UnityEngine;

public static class MultiplayerSessionSettings
{
    public const int RelayJoinCodeLength = 6;
    public const int MaxPlayerNameLength = 16;

    private const string PlayerNamePrefsKey = "multiplayer.player_name";
    private const string ConnectionCodePrefsKey = "multiplayer.connection_code";
    private const string DefaultPlayerName = "Player";

    public static string LoadPlayerName()
    {
        string normalized = NormalizePlayerName(PlayerPrefs.GetString(PlayerNamePrefsKey, string.Empty));
        return string.IsNullOrEmpty(normalized) ? DefaultPlayerName : normalized;
    }

    public static void SavePlayerName(string rawName)
    {
        PlayerPrefs.SetString(PlayerNamePrefsKey, NormalizePlayerName(rawName));
        PlayerPrefs.Save();
    }

    public static string LoadConnectionCode()
    {
        return NormalizeConnectionCode(PlayerPrefs.GetString(ConnectionCodePrefsKey, string.Empty));
    }

    public static void SaveConnectionCode(string rawCode)
    {
        PlayerPrefs.SetString(ConnectionCodePrefsKey, NormalizeConnectionCode(rawCode));
        PlayerPrefs.Save();
    }

    public static string ResolvePlayerName(string rawName, ulong ownerClientId)
    {
        string normalized = NormalizePlayerName(rawName);
        return string.IsNullOrEmpty(normalized) ? $"{DefaultPlayerName} {ownerClientId}" : normalized;
    }

    public static bool TryParseRelayJoinCode(string rawInput, out string relayJoinCode)
    {
        relayJoinCode = NormalizeConnectionCode(rawInput);
        if (relayJoinCode.Length != RelayJoinCodeLength)
        {
            relayJoinCode = string.Empty;
            return false;
        }

        bool isAlphaNumeric = relayJoinCode.All(char.IsLetterOrDigit);
        if (isAlphaNumeric == false)
        {
            relayJoinCode = string.Empty;
            return false;
        }

        return true;
    }

    public static string NormalizeConnectionCode(string rawCode)
    {
        return (rawCode ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizePlayerName(string rawName)
    {
        string normalized = (rawName ?? string.Empty).Trim();
        if (normalized.Length > MaxPlayerNameLength)
        {
            normalized = normalized.Substring(0, MaxPlayerNameLength);
        }

        return normalized;
    }
}
