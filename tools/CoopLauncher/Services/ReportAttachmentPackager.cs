using System.IO;
using System.IO.Compression;

namespace CoopLauncher.Services;

public readonly record struct ReportAttachmentPackage(bool Success, string? Path, string Message, int FileCount);

/// <summary>Creates a bounded, local ZIP of user-selected images and an optional pre-existing crash bundle.</summary>
public static class ReportAttachmentPackager
{
    private const int MaximumImageCount = 4;
    private const long MaximumImageBytes = 8L * 1024 * 1024;
    private const long MaximumPackageBytes = 120L * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp",
    };

    public static ReportAttachmentPackage Create(IEnumerable<string> imagePaths, string? crashBundlePath)
    {
        try
        {
            var inputs = new List<(string Path, string EntryName)>();
            foreach (string image in imagePaths.Where(File.Exists).Take(MaximumImageCount))
            {
                var info = new FileInfo(image);
                if (!ImageExtensions.Contains(info.Extension) || info.Length > MaximumImageBytes) continue;
                inputs.Add((image, $"images/{Path.GetFileName(image)}"));
            }

            if (!string.IsNullOrWhiteSpace(crashBundlePath) && File.Exists(crashBundlePath))
                inputs.Add((crashBundlePath, "crash/shareable.zip"));

            if (inputs.Count == 0)
                return new(false, null, "No eligible images or crash bundle are attached.", 0);

            long inputSize = inputs.Sum(input => new FileInfo(input.Path).Length);
            if (inputSize > MaximumPackageBytes)
                return new(false, null, "Attachments exceed the 120 MB report limit.", 0);

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CalradiaCoop", "report-uploads");
            Directory.CreateDirectory(root);
            string output = Path.Combine(root, $"report-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");

            using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
            {
                foreach ((string path, string entryName) in inputs)
                    archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
            }

            return new(true, output, $"Prepared {inputs.Count} attachment(s) for upload.", inputs.Count);
        }
        catch (Exception ex)
        {
            return new(false, null, $"Could not package report attachments: {ex.Message}", 0);
        }
    }

    public static bool IsEligibleImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && ImageExtensions.Contains(info.Extension) && info.Length <= MaximumImageBytes;
        }
        catch { return false; }
    }
}
