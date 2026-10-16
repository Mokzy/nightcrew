using System.Runtime.Serialization;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public static class SettingsStore
{
    /// <summary>Loads settings; a missing file is created with defaults, an unreadable one is set aside and replaced.</summary>
    public static Settings Load(string path) => Load(path, out _);

    /// <summary>
    /// As <see cref="Load(string)"/>; <paramref name="created"/> is true when there was no settings file before this call,
    /// which is Nightcrew's first start. Never throws: a file that can't be read or parsed, of any kind, gives the defaults.
    /// </summary>
    public static Settings Load(string path, out bool created)
    {
        created = !File.Exists(path);
        if (!created)
        {
            try
            {
                return Json.Deserialize<Settings?>(File.ReadAllText(path)) ?? throw new SerializationException("Settings file is empty.");
            }
            catch (Exception)
            {
                try { File.Copy(path, path + ".corrupt", overwrite: true); }
                catch (Exception) { } // keeping a copy is a courtesy; the defaults matter more
            }
        }
        var defaults = new Settings();
        try { Save(path, defaults); }
        catch (Exception) { } // a settings file that can't be written still leaves Nightcrew running on the defaults
        return defaults;
    }

    public static void Save(string path, Settings settings) => AtomicFile.WriteAllText(path, Json.Serialize(settings));
}
