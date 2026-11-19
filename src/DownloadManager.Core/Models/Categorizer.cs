namespace DownloadManager.Core.Models;

/// <summary>Maps a file name to a download category key (also used as the localisation key suffix).</summary>
public static class Categorizer
{
    public const string General = "General";

    public static readonly IReadOnlyList<string> All = ["General", "Compressed", "Documents", "Music", "Video", "Programs", "Images"];

    private static readonly Dictionary<string, string> ByExtension = Build();

    public static string ForFileName(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var category) ? category : General;
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string extensions)
        {
            foreach (var e in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[e] = category;
        }
        Add("Compressed", "zip rar 7z gz tgz tar bz2 xz zst cab");
        Add("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt ods epub csv md");
        Add("Music", "mp3 wav flac aac ogg m4a wma opus");
        Add("Video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg");
        Add("Programs", "exe msi msix appx dmg pkg apk deb rpm iso");
        Add("Images", "jpg jpeg png gif bmp webp svg tif tiff heic");
        return map;
    }
}
