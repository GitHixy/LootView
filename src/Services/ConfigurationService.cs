using System;
using System.IO;
using Dalamud.Plugin;
using Newtonsoft.Json;
using LootView.Models;

namespace LootView.Services;

/// <summary>
/// Service for managing plugin configuration
/// </summary>
public class ConfigurationService : IDisposable
{
    private readonly string configFilePath;
    private Configuration configuration;

    public Configuration Configuration
    {
        get
        {
            if (configuration == null)
                configuration = LoadConfiguration();
            return configuration;
        }
    }

    public ConfigurationService()
    {
        var configDirectory = Plugin.PluginInterface.ConfigDirectory.FullName;
        configFilePath = Path.Combine(configDirectory, "config.json");
        
        // Ensure config directory exists
        Directory.CreateDirectory(configDirectory);
    }

    public void Save()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Configuration, Formatting.Indented);
            File.WriteAllText(configFilePath, json);
            Plugin.Log.Debug("Configuration saved to {ConfigPath}", configFilePath);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to save configuration to {ConfigPath}", configFilePath);
        }
    }

    private Configuration LoadConfiguration()
    {
        try
        {
            if (File.Exists(configFilePath))
            {
                var json = File.ReadAllText(configFilePath);
                var config = JsonConvert.DeserializeObject<Configuration>(json);
                if (config != null)
                {
                    Plugin.Log.Debug("Configuration loaded from {ConfigPath}", configFilePath);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load configuration from {ConfigPath}", configFilePath);
        }

        Plugin.Log.Info("Creating new configuration");
        return new Configuration();
    }

    public void ResetToDefaults()
    {
        configuration = new Configuration();
        Save();
        Plugin.Log.Info("Configuration reset to defaults");
    }

    public void Dispose()
    {
        Save();
    }
}