using System.Runtime.Serialization.Json;
using System.Text;

namespace Nightcrew.Core;

public static class Json
{
    private static readonly DataContractJsonSerializerSettings Options = new() { UseSimpleDictionaryFormat = true };

    public static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T), Options).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Deserialize<T>(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return (T)new DataContractJsonSerializer(typeof(T), Options).ReadObject(stream)!;
    }
}
