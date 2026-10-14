using System.Runtime.Serialization;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public static class SettingsStore
{
    /// <summary>Loads settings; a missing file is created with defaults, a corrupt one is set aside and replaced.</summary>
    public static Settings Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return Json.Deserialize<Settings>(File.ReadAllText(path));
            }
            catch (SerializationException)
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        var defaults = new Settings();
        Save(path, defaults);
        return defaults;
    }

    public static void Save(string path, Settings settings) => AtomicFile.WriteAllText(path, Json.Serialize(settings));
}
