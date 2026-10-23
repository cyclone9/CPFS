using System;
using System.IO;
using System.Security;
using System.Text;
using Cpfs.Core.Config;

namespace Cpfs.Windows;

internal enum ConfigState
{
    Found,
    Missing,
    Unreadable,
}

// config.json for pages that only need its values (Connect, Monitor). Opened read-only with sharing, so a running
// script or editor is not disturbed and nothing is written.
internal static class ConfigFile
{
    public static ConfigState TryRead(out ConfigDocument? config, out string? error)
    {
        config = null;
        error = null;
        try
        {
            if (!File.Exists(StateReader.ConfigPath))
            {
                return ConfigState.Missing;
            }

            byte[] bytes;
            using (var fs = new FileStream(StateReader.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new MemoryStream())
            {
                fs.CopyTo(copy);
                bytes = copy.ToArray();
            }

            config = ConfigReader.Parse(bytes, Encoding.Default);
            return ConfigState.Found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ConfigFormatException or DecoderFallbackException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            AppLog.Write("config", "Could not read " + StateReader.ConfigPath + ": " + error);
            return ConfigState.Unreadable;
        }
    }
}
