using Newtonsoft.Json;
using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZsxqForwarder");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private AppSettings _settings = new();
    public AppSettings Settings => _settings;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {Path}, using defaults", SettingsPath);
            _settings = new AppSettings();
        }
        return _settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }
}
