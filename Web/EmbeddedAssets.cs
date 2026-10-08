using System.Reflection;

namespace WinsAlt.Web;

/// <summary>
/// The dashboard files compiled into the executable as manifest resources. Read once at startup;
/// requests are then served straight from memory, with nothing on disk beside the exe.
/// (Manifest resources are plain data in the image - reading them is AOT/trim safe.)
/// </summary>
internal static class EmbeddedAssets
{
    public static readonly byte[]? IndexHtml = Load("index.html");
    public static readonly byte[]? VueJs = Load("vue.global.prod.js");

    private static byte[]? Load(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resource is null) return null;

        using var stream = assembly.GetManifestResourceStream(resource)!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
