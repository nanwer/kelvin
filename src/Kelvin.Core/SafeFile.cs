namespace Kelvin.Core;

/// <summary>
/// Replaces a file in one step, so a crash can't leave it half written. The new content goes into
/// a temporary file that this call creates itself, under a name nobody can predict, and is then
/// moved over the target. A file that was already waiting under a fixed temporary name could
/// belong to someone else and would keep their permissions after the move.
/// </summary>
public static class SafeFile
{
    public static void Replace(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
                writer.Write(content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* nothing more to do */ }
            throw;
        }
    }
}
