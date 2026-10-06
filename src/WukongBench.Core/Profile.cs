using System.Globalization;

namespace WukongBench.Core;

public sealed record Profile(string Name, int Width, int Height, int RenderPercent, bool RayTracing)
{
    public const string Section = "/Script/GSGameSettings.GSGameUserSettings";
    public bool IsCpu => Name == "CPU";
    public Dictionary<string, string> Scalability => new()
    {
        ["sg.ResolutionQuality"] = RenderPercent.ToString(CultureInfo.InvariantCulture),
        ["sg.ViewDistanceQuality"] = "4", ["sg.FoliageQuality"] = "4",
        ["sg.AntiAliasingQuality"] = IsCpu ? "0" : "4",
        ["sg.ShadowQuality"] = IsCpu ? "0" : "4", ["sg.GlobalIlluminationQuality"] = IsCpu ? "0" : "4",
        ["sg.RayTracingQuality"] = RayTracing ? "3" : "0", ["sg.ReflectionQuality"] = IsCpu ? "0" : "4",
        ["sg.PostProcessQuality"] = IsCpu ? "0" : "4", ["sg.TextureQuality"] = IsCpu ? "0" : "4",
        ["sg.EffectsQuality"] = IsCpu ? "0" : "4", ["sg.ShadingQuality"] = IsCpu ? "0" : "4"
    };
    public Dictionary<string, string> UiValues => new()
    {
        ["ScreenMode"] = IsCpu ? "2" : "1", ["LockFrameRate"] = "0", ["Vsync"] = "0",
        ["InsertFrame"] = "0", ["Rtx"] = RayTracing ? "1" : "0", ["RtxLevel"] = "3",
        ["QualityLevel"] = "6", ["ViewDistance"] = "5", ["VegetationQuality"] = "5",
        ["AntiAliasing"] = IsCpu ? "1" : "5", ["PostProcessing"] = IsCpu ? "1" : "5",
        ["ShadowQuality"] = IsCpu ? "1" : "5", ["TextureQuality"] = IsCpu ? "1" : "5",
        ["FxQuality"] = IsCpu ? "1" : "5", ["MaterialQuality"] = IsCpu ? "1" : "5",
        ["GlobalIllumination"] = IsCpu ? "1" : "5", ["ReflectionQuality"] = IsCpu ? "1" : "5"
    };
    public string Apply(string original)
    {
        var ini = new IniFile(original);
        if (ini.Get(Section, "UISettingData") is null)
            throw new InvalidDataException("Unsupported GameUserSettings.ini: GSGameUserSettings/UISettingData missing. Initialize Benchmark Tool once or specify --config.");
        foreach (string k in new[] { "ResolutionSizeX", "LastUserConfirmedResolutionSizeX", "DesiredScreenWidth", "LastUserConfirmedDesiredScreenWidth" })
            ini.Set(Section, k, Width.ToString(CultureInfo.InvariantCulture));
        foreach (string k in new[] { "ResolutionSizeY", "LastUserConfirmedResolutionSizeY", "DesiredScreenHeight", "LastUserConfirmedDesiredScreenHeight" })
            ini.Set(Section, k, Height.ToString(CultureInfo.InvariantCulture));
        foreach (string k in new[] { "FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode" })
            ini.Set(Section, k, IsCpu ? "2" : "1");
        ini.Set(Section, "bUseVSync", "False"); ini.Set(Section, "bUseDynamicResolution", "False");
        ini.Set(Section, "FrameRateLimit", "0.000000");
        ini.UpdateMap(Section, "UISettingData", UiValues);
        foreach (var (k, v) in Scalability) ini.Set("ScalabilityGroups", k, v);
        ini.Set("RayTracing", "r.RayTracing.EnableInGame", RayTracing ? "True" : "False");
        return ini.ToString();
    }
    public void Verify(string saved)
    {
        var ini = new IniFile(saved);
        var map = ini.ReadMap(Section, "UISettingData");
        var differences = new List<string>();
        // Preset identity and RT-level enums may be rewritten without changing the effective quality.
        // Every quality is checked independently; Full RT level is checked by its visible name in the UI.
        foreach (var (k, v) in UiValues.Where(p => p.Key is not ("ScreenMode" or "QualityLevel" or "RtxLevel")))
            if (!map.TryGetValue(k, out var actual) || actual != v) differences.Add($"UI {k}: expected {v}, got {actual}");
        foreach (var (k, v) in Scalability.Where(p => p.Key != "sg.RayTracingQuality"))
            if (!double.TryParse(ini.Get("ScalabilityGroups", k), CultureInfo.InvariantCulture, out double actual) || Math.Abs(actual - double.Parse(v, CultureInfo.InvariantCulture)) > .1)
                differences.Add($"{k}: expected {v}, got {actual}");
        foreach (var (k, v) in new[] { ("ResolutionSizeX", Width), ("ResolutionSizeY", Height) })
            if (ini.Get(Section, k) != v.ToString(CultureInfo.InvariantCulture)) differences.Add($"{k} differs");
        if (differences.Count > 0) throw new InvalidDataException("Benchmark changed requested settings: " + string.Join("; ", differences));
    }
}
