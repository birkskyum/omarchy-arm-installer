using System.Text.Json.Serialization;

namespace OmarchyArmInstaller.Core.Hardware;

/// One ACPI table as the firmware handed it to the OS.
public sealed record AcpiTable(string Signature, byte[] Data);

/// A display's EDID; Internal is the laptop's own panel.
public sealed record DisplayEdid(string Device, byte[] Edid, bool Internal);

/// One present device, as Windows' device manager knows it. BiosName is its
/// ACPI namespace path (\_SB.I2C1.TPL0), which maps it to the DSDT.
public sealed record DeviceInfo
{
    public required string InstanceId { get; init; }
    public string? Name { get; init; }
    public string? Class { get; init; }
    public string? Manufacturer { get; init; }
    public string[] HardwareIds { get; init; } = [];
    public string[] CompatibleIds { get; init; } = [];
    public string? Service { get; init; }
    public string? Parent { get; init; }
    public string? BiosName { get; init; }
    public string[] LocationPaths { get; init; } = [];
    public uint? Problem { get; init; }
    public string? Inf { get; init; }
    public string? InfSection { get; init; }
    public string? DriverProvider { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    /// The driver package's folder in the driver store, when it has one.
    public string? DriverFolder { get; init; }
    /// The extension driver packages Windows applies to the device on top of
    /// its driver: each INF's store folder, newest version of each package.
    /// Qualcomm keeps the DSP firmware in these (qcsubsys_ext_adsp8380).
    public IReadOnlyList<ExtensionPackage> Extensions { get; init; } = [];
    /// The driver's key under Control\Class ({class guid}\0000), where the
    /// driver keeps its settings.
    public string? Driver { get; init; }
    /// Every property the configuration manager has for the device.
    public IReadOnlyList<DeviceProperty> Properties { get; init; } = [];
    /// The device's own key under Enum, with Device Parameters and LogConf.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegistryDump? EnumKey { get; init; }
    /// The driver's settings key under Control\Class.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegistryDump? DriverKey { get; init; }
}

/// One extension INF Windows applies to a device, from its
/// ExtendedConfigurationIds ("oem253.inf:ACPI\VEN_QCOM&DEV_0C1B,
/// SUBSYS_Device_ADSP_ext.NT,04/23/2026,2.0.9100.1"), and its store folder.
public sealed record ExtensionPackage(string Inf, string? HardwareId, string? Section, string? Date, string? Version, string? Folder)
{
    /// Parses one entry; the folder is looked up by the caller.
    public static ExtensionPackage Parse(string entry, Func<string, string?> storeFolder)
    {
        var colon = entry.IndexOf(':');
        var inf = colon < 0 ? entry : entry[..colon];
        var rest = colon < 0 ? [] : entry[(colon + 1)..].Split(',');
        string? At(int i) => i < rest.Length && rest[i].Length > 0 ? rest[i] : null;
        var date = At(2);
        if (date is not null && DateTime.TryParseExact(date, "MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            date = d.ToString("yyyy-MM-dd");
        return new ExtensionPackage(inf.Trim(), At(0), At(1), date, At(3), storeFolder(inf.Trim()));
    }

    /// The package's name without its store hash: qcsubsys_ext_adsp8380.inf.
    [JsonIgnore]
    public string? PackageName => Folder is null ? null
        : System.IO.Path.GetFileName(Folder.TrimEnd('\\', '/')) is var f && f.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase) is var i && i > 0
            ? f[..(i + 4)] : f;

    /// Where two versions of one package are installed, the newer one is
    /// what Windows applies; this keeps that one per package.
    public static IReadOnlyList<ExtensionPackage> Newest(IEnumerable<ExtensionPackage> all) =>
        all.Where(e => e.Folder is not null)
            .GroupBy(e => e.PackageName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => VersionOf(e.Version)).ThenByDescending(e => e.Date).First())
            .ToList();

    static System.Version VersionOf(string? text) => text is not null && System.Version.TryParse(text, out var v) ? v : new System.Version(0, 0);
}

/// Everything read from the machine, before redaction.
public sealed record CollectedHardware
{
    /// The SMBIOS structure table, and the SMBIOS version it follows.
    public byte[]? Smbios { get; init; }
    public (byte Major, byte Minor, byte Revision) SmbiosVersion { get; init; }
    public IReadOnlyList<AcpiTable> Acpi { get; init; } = [];
    public IReadOnlyList<DisplayEdid> Displays { get; init; } = [];
    public IReadOnlyList<DeviceInfo> Devices { get; init; } = [];
    /// The service (driver) keys of the present devices, by service name,
    /// where drivers keep machine-wide parameters.
    public IReadOnlyDictionary<string, RegistryDump> Services { get; init; } = new Dictionary<string, RegistryDump>();
    /// System-wide keys that describe the hardware, by their path under HKLM:
    /// HARDWARE\DESCRIPTION\System, DEVICEMAP, RESOURCEMAP, SystemInformation.
    public IReadOnlyDictionary<string, RegistryDump> Registry { get; init; } = new Dictionary<string, RegistryDump>();
    /// What Windows itself says about storage, battery, power, displays, the
    /// GPU and the driver store: one JSON document, gathered by PowerShell.
    public string? WindowsJson { get; init; }
    /// Names that identify this install rather than the model (the computer
    /// and user names), to take out of everything else.
    public IReadOnlyList<string> Secrets { get; init; } = [];
    public string Os { get; init; } = "";
    /// What could not be read, for the report and the person sending it.
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public interface IHardwareSource
{
    Task<CollectedHardware> CollectAsync(IProgress<string> status, CancellationToken ct);
}
