using System.Reflection;
using System.Text.Json;
using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Core;

/// A model systemd-stub can pick a device tree for (systemd's hwids or Omarchy's kernel's).
public sealed record KnownModel(string File, string Name, string Compatible, string[] Hwids);

/// The models Linux knows: systemd's hwids list and the IDs Omarchy's kernel adds, embedded as models.json.
public static class Models
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static readonly Lazy<IReadOnlyList<KnownModel>> all = new(Load);

    public static IReadOnlyList<KnownModel> All => all.Value;

    /// The CHID types systemd-stub compares, from most to least specific
    /// (chid_match() in systemd's src/boot/chid.c). It skips 0 to 2, which
    /// carry the BIOS version, and 12 to 14, which are too broad: type 14 is
    /// the manufacturer alone, so every HP shares one.
    static readonly string[] StubOrder = ["ext2", "ext1", "ext0", "3", "6", "8", "10", "4", "5", "7", "9", "11"];

    /// The model systemd-stub would pick a device tree for on this machine.
    public static KnownModel? For(IReadOnlyList<ChidValue> chids)
    {
        foreach (var type in StubOrder)
        {
            if (chids.FirstOrDefault(c => c.Type == type) is not { } chid) continue;
            var id = chid.Value.ToString();
            if (All.FirstOrDefault(m => m.Hwids.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } model)
                return model;
        }
        return null;
    }

    static IReadOnlyList<KnownModel> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("models.json");
        if (stream is null) return [];
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("models").Deserialize<List<KnownModel>>(Options) ?? [];
    }
}
