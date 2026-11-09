using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Core;

/// One file the stick's OMARCHY_FW partition carries, found in the driver store.
/// Name keeps any subfolder within the package.
public sealed record FirmwareFile(string Folder, string Name, long Size, string SourcePath);

/// A driver package and the firmware the stick takes from it.
public sealed record FirmwarePackage(string Folder, string Label, IReadOnlyList<FirmwareFile> Found)
{
    public string Details => Found.Count <= 3
        ? string.Join("  ", Found.Select(f => f.Name))
        : $"{Found.Count} files, {Found.Sum(f => f.Size) / 1e6:0.0} MB";

    public bool Complete => Found.Count > 0;
}

/// Finds the firmware Linux needs in the Windows DriverStore\FileRepository:
/// every file that is not a driver or Windows plumbing, from the driver
/// packages of the devices this machine has (other than Microsoft's own) and
/// the extension packages applied to them, where Qualcomm keeps the DSP
/// firmware. Files keep their package folder: the installer's
/// qcom-firmware-extract matches each DSP device tree to the firmware beside it.
public static class FirmwareCollector
{
    const long LargestFile = 256L << 20;

    /// One driver store folder a device draws on: its driver's, or an
    /// extension's applied to it.
    public sealed record Use(string Folder, DeviceInfo Device, ExtensionPackage? Extension)
    {
        public bool IsExtension => Extension is not null;
    }

    /// Every store folder the devices draw on, driver packages and
    /// extensions alike, in the order the devices come.
    public static IEnumerable<Use> Uses(IEnumerable<DeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            if (d.DriverFolder is not null) yield return new Use(d.DriverFolder, d, null);
            foreach (var e in d.Extensions)
                if (e.Folder is not null) yield return new Use(e.Folder, d, e);
        }
    }

    public static IReadOnlyList<FirmwarePackage> Find(string fileRepository, IReadOnlyList<DeviceInfo> devices, long largest = LargestFile)
    {
        var packages = new Dictionary<string, FirmwarePackage>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(fileRepository)) return [];
        var store = Path.GetFullPath(fileRepository).TrimEnd('\\', '/');

        foreach (var group in Uses(devices).Where(u => !IsMicrosoft(u)).GroupBy(u => u.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFullPath(group.Key).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(folder), store, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder))
                continue;
            var found = Files(folder, largest);
            if (found.Count == 0) continue;
            var label = group.Select(u => u.Device.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? Path.GetFileName(folder);
            if (group.All(u => u.IsExtension)) label += " (extension)";
            packages[Path.GetFileName(folder)] = new FirmwarePackage(Path.GetFileName(folder), label, found);
        }

        return packages.Values.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Everything Find takes, plus the firmware in every other package
    /// installed from outside Windows' own image: older versions an update
    /// left behind and drivers for hardware that isn't attached. What goes
    /// to Are we ARM yet, where any version a maker shipped is worth having.
    public static IReadOnlyList<FirmwarePackage> FindAll(string fileRepository, IReadOnlyList<DeviceInfo> devices, IEnumerable<string> packageFolders)
    {
        var packages = Find(fileRepository, devices, FirmwareDrop.LargestFile).ToDictionary(p => p.Folder, StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(fileRepository)) return [];
        var store = Path.GetFullPath(fileRepository).TrimEnd('\\', '/');
        foreach (var path in packageFolders)
        {
            var folder = Path.GetFullPath(path).TrimEnd('\\', '/');
            var name = Path.GetFileName(folder);
            if (packages.ContainsKey(name) || !Directory.Exists(folder)
                || !string.Equals(Path.GetDirectoryName(folder), store, StringComparison.OrdinalIgnoreCase))
                continue;
            var found = Files(folder, FirmwareDrop.LargestFile);
            if (found.Count > 0) packages[name] = new FirmwarePackage(name, $"{name} (not in use)", found);
        }
        return packages.Values.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Microsoft's own packages: a driver by its provider; an extension by
    /// its INF keeping its name, where a third party's is installed as oemNN.inf.
    static bool IsMicrosoft(Use u) => u.Extension is { } e
        ? !System.Text.RegularExpressions.Regex.IsMatch(e.Inf, @"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        : u.Device.DriverProvider is { } provider && provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    static List<FirmwareFile> Files(string folder, long largest)
    {
        var name = Path.GetFileName(folder);
        var list = new List<FirmwareFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (ReportBuilder.NotFirmware.Contains(Path.GetExtension(path))) continue;
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var size = new FileInfo(path).Length;
            if (size > largest) continue;
            list.Add(new FirmwareFile(name, relative, size, path));
        }
        return list;
    }

    public static long Size(IEnumerable<FirmwarePackage> packages) => packages.SelectMany(p => p.Found).Sum(f => f.Size);

    /// Copies everything found to <destination>\DriverStore\FileRepository.
    public static void CopyTo(IEnumerable<FirmwarePackage> packages, string destination)
    {
        var root = Path.Combine(destination, "DriverStore", "FileRepository");
        foreach (var file in packages.SelectMany(p => p.Found))
        {
            var target = Path.Combine(root, file.Folder, file.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.SourcePath, target, overwrite: true);
        }
    }
}
