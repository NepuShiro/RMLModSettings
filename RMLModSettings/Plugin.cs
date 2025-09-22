using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisLocaleLoader;
using Elements.Core;
using FrooxEngine;
using ResoniteModLoader;
using BepisModSettings.DataFeeds;
using BepisResoniteWrapper;
using FrooxEngine.UIX;
using ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables;

namespace RMLModSettings;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(BepisModSettings.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;

        ResoniteHooks.OnEngineReady += () =>
        {
            bool isRmlLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name?.Contains("ResoniteModLoader") == true);
            if (isRmlLoaded)
            {
                BepisSettingsPage.CustomPluginsPages += RMLSettingsEnumerate;
                BepisPluginPage.CustomPluginConfigsPages += RMLSettingsConfigsEnumerate;
            }
            else
            {
                Log.LogFatal("ResoniteModLoader is not loaded! You cannot use this plugin without it.");
            }
        };

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    public static async IAsyncEnumerable<DataFeedItem> RMLSettingsEnumerate(IReadOnlyList<string> path)
    {
        await Task.CompletedTask;

        DataFeedGroup plguinsGroup = new DataFeedGroup();
        plguinsGroup.InitBase("RMLSettingsGroup", path, null, "Settings.RML.Mods".AsLocaleKey());
        yield return plguinsGroup;

        DataFeedGrid pluginsGrid = new DataFeedGrid();
        pluginsGrid.InitBase("ModsGrid", path, ["RMLSettingsGroup"], "Settings.RML.LoadedMods".AsLocaleKey());
        yield return pluginsGrid;

        string[] loadedModsGroup = ["RMLSettingsGroup", "ModsGrid"];

        List<ResoniteModBase> sortedMods = new List<ResoniteModBase>(ModLoader.Mods());
        if (sortedMods.Count > 0)
        {
            sortedMods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (ResoniteModBase mod in sortedMods)
            {
                string pluginname = mod.Name;
                string pluginGuid = $"rml.{mod.Author}.{mod.Name}_custom_settings";

                LocaleString nameKey = pluginname;
                LocaleString description = $"{pluginname}\n{pluginGuid}\n({mod.Version})";

                LocaleLoader.AddLocaleString($"Settings.{pluginGuid}.Breadcrumb", pluginname, authors: PluginMetadata.AUTHORS);

                DataFeedCategory loadedPlugin = new DataFeedCategory();
                loadedPlugin.InitBase(pluginGuid, path, loadedModsGroup, nameKey, description);
                yield return loadedPlugin;
            }
        }
        else
        {
            DataFeedLabel noMods = new DataFeedLabel();
            noMods.InitBase("NoMods", path, loadedModsGroup, "Settings.RML.Mods.NoMods".AsLocaleKey());
            yield return noMods;
        }
    }

    public static async IAsyncEnumerable<DataFeedItem> RMLSettingsConfigsEnumerate(IReadOnlyList<string> path)
    {
        await Task.CompletedTask;

        string pluginId = path[1].Replace("_custom_settings", "");
        ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => $"rml.{mod.Author}.{mod.Name}" == pluginId);
        if (mod == null) yield break;

        ModConfiguration modConfig = mod.GetConfiguration();
        if (modConfig?.ConfigurationItemDefinitions == null)
        {
            DataFeedLabel noConfigs = new DataFeedLabel();
            noConfigs.InitBase("NoConfigs", path, null, "Settings.RML.Mods.NoConfigs".AsLocaleKey());
            yield return noConfigs;
        }
        else
        {
            IAsyncEnumerable<DataFeedItem> configs = EnumerateConfigs(modConfig, path);
            await foreach (DataFeedItem item in configs)
            {
                yield return item;
            }
        }

        // Metadata
        DataFeedGroup modGroup = new DataFeedGroup();
        modGroup.InitBase("Metadata", path, null, "Settings.RML.Mods.Metadata".AsLocaleKey());
        yield return modGroup;

        string[] metadataGroup = ["Metadata"];

        DataFeedIndicator<string> idIndicator = new DataFeedIndicator<string>();
        idIndicator.InitBase("Id", path, metadataGroup, "Settings.RML.Mods.Guid".AsLocaleKey());
        idIndicator.InitSetupValue(field => field.Value = pluginId);
        yield return idIndicator;

        if (!string.IsNullOrWhiteSpace(mod.Author))
        {
            DataFeedIndicator<string> authorsIndicator = new DataFeedIndicator<string>();
            authorsIndicator.InitBase("Author", path, metadataGroup, "Settings.RML.Mods.Author".AsLocaleKey());
            authorsIndicator.InitSetupValue(field => field.Value = mod.Author);
            yield return authorsIndicator;
        }

        DataFeedIndicator<string> versionIndicator = new DataFeedIndicator<string>();
        versionIndicator.InitBase("Version", path, metadataGroup, "Settings.RML.Mods.Version".AsLocaleKey());
        versionIndicator.InitSetupValue(field => field.Value = mod.Version);
        yield return versionIndicator;

        if (!string.IsNullOrWhiteSpace(mod.Link) && Uri.TryCreate(mod.Link, UriKind.Absolute, out var uri))
        {
            var modHyperlink = new DataFeedAction();
            modHyperlink.InitBase("Link", path, metadataGroup, "Settings.RML.Mods.ModPage".AsLocaleKey());
            modHyperlink.InitAction(syncDelegate =>
            {
                var slot = syncDelegate?.Slot;
                if (slot == null) return;

                slot.AttachComponent<Hyperlink>().URL.Value = uri;
            });
            yield return modHyperlink;
        }
    }

    private static async IAsyncEnumerable<DataFeedItem> EnumerateConfigs(ModConfiguration modConfig, IReadOnlyList<string> path)
    {
        // Used for enum config keys. basically you can define a function which will display a subcategory of this category.
        // CategoryHandlers.Clear();

        string pluginId = path[1].Replace("_custom_settings", "");

        if (modConfig.ConfigurationItemDefinitions.Count > 0)
        {
            HashSet<string> sections = new HashSet<string>();
            List<string> added = new List<string>();
            foreach (ModConfigurationKey config in modConfig.ConfigurationItemDefinitions)
            {
                if (config.InternalAccessOnly) continue;

                Type valueType = config.ValueType();

                string section = "Configs";
                if (sections.Add(section))
                {
                    DataFeedResettableGroup configs = new DataFeedResettableGroup();
                    configs.InitBase(section, path, null, section);
                    configs.InitResetAction(a =>
                    {
                        Button but = a.Slot.GetComponentInChildren<Button>();
                        if (but == null) return;

                        but.LocalPressed += (b, _) =>
                        {
                            Slot resetBtn = b.Slot.FindParent(x => x.Name == "Reset Button");
                            var store = resetBtn?.GetComponentInChildren<DataModelValueFieldStore<bool>.Store>();
                            if (store == null) return;

                            if (!store.Value.Value) return;
                            ResetConfigSection(pluginId, section);
                        };
                    });
                    yield return configs;
                }

                string initKey = section + "." + config.Name;
                string key = added.Contains(initKey) ? initKey + added.Count : initKey;

                LocaleString nameKey = config.Name;
                LocaleString descKey = config.Description;
                LocaleString defaultKey = $"{config.Name} : {valueType}";
                // LocaleString valueKey = $"{config.Name} : {modConfig.GetValue(config)}";

                added.Add(key);

                string[] groupingKeys = [section];

                if (valueType == typeof(dummy))
                {
                    DataFeedItem dummyField = new DataFeedValueField<dummy>();
                    dummyField.InitBase(key, path, groupingKeys, nameKey, descKey);
                    yield return dummyField;
                }
                else if (valueType == typeof(bool))
                {
                    yield return DataFeedHelpers.GenerateToggle(key, path, groupingKeys, modConfig, config);
                }
                else if (valueType.IsEnum)
                {
                    DataFeedItem enumItem;

                    try
                    {
                        // if (valueType.GetCustomAttribute<FlagsAttribute>() != null)
                        // {
                        //     LocaleLoader.AddLocaleString($"Settings.{key}.Breadcrumb", initKey, authors: PluginMetadata.AUTHORS);
                        // 
                        //     CategoryHandlers.Add(key, path2 => (IAsyncEnumerable<DataFeedItem>)DataFeedHelpers.HandleFlagsEnumCategory.MakeGenericMethod(valueType).Invoke(null, [path2, config]));
                        //     enumItem = new DataFeedCategory();
                        //     enumItem.InitBase(key, path, groupingKeys, valueKey, descKey);
                        // }
                        // else
                        // {
                        enumItem = (DataFeedItem)DataFeedHelpers.GenerateEnumItemsAsync.MakeGenericMethod(valueType).Invoke(null, [key, path, groupingKeys, modConfig, config]);
                        // }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError(e);
                        enumItem = new DataFeedValueField<dummy>();
                        enumItem.InitBase(key, path, groupingKeys, defaultKey, descKey);
                    }

                    yield return enumItem;
                }
                else if (valueType.IsNullable())
                {
                    Type nullableType = valueType.GetGenericArguments()[0];
                    if (nullableType.IsEnum)
                    {
                        IAsyncEnumerable<DataFeedItem> nullableEnumItems;

                        try
                        {
                            nullableEnumItems = (IAsyncEnumerable<DataFeedItem>)DataFeedHelpers.GenerateNullableEnumItemsAsync.MakeGenericMethod(nullableType).Invoke(null, [key, path, groupingKeys, config]);
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError(e);
                            DataFeedValueField<dummy> dummyField = new DataFeedValueField<dummy>();
                            dummyField.InitBase(key, path, groupingKeys, defaultKey, descKey);

                            nullableEnumItems = GetDummyAsync(dummyField);
                        }

                        await foreach (DataFeedItem item in nullableEnumItems)
                        {
                            yield return item;
                        }
                    }
                }
                else
                {
                    DataFeedItem valueItem;

                    try
                    {
                        valueItem = (DataFeedItem)DataFeedHelpers.GenerateValueField.MakeGenericMethod(valueType).Invoke(null, [key, path, groupingKeys, modConfig, config]);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError(e);
                        valueItem = new DataFeedValueField<dummy>();
                        valueItem.InitBase(key, path, groupingKeys, defaultKey, descKey);
                    }

                    yield return valueItem;
                }
            }
        }

        const string groupId = "ActionsGroup";
        DataFeedGroup group = new DataFeedGroup();
        group.InitBase(groupId, path, null, "Settings.RML.Mods.Actions".AsLocaleKey());
        yield return group;
        string[] groupKeys = [groupId];

        DataFeedAction saveAct = new DataFeedAction();
        saveAct.InitBase("SaveConfig", path, groupKeys, "Settings.RML.Mods.SaveConfig".AsLocaleKey(), "Settings.RML.Mods.SaveConfig.Description".AsLocaleKey());
        saveAct.InitAction(syncDelegate =>
        {
            Button btn = syncDelegate.Slot.GetComponent<Button>();
            if (btn == null) return;

            btn.LocalPressed += (_, _) => SaveConfigs(pluginId);
        });
        yield return saveAct;

        DataFeedAction resetAct = new DataFeedAction();
        resetAct.InitBase("ResetConfig", path, groupKeys, "Settings.RML.Mods.ResetConfig".AsLocaleKey(), "Settings.RML.Mods.ResetConfig.Description".AsLocaleKey());
        resetAct.InitAction(syncDelegate =>
        {
            Button btn = syncDelegate.Slot?.GetComponent<Button>();
            if (btn == null) return;

            ValueMultiDriver<bool> valueDriver = btn.Slot.GetComponent<ValueMultiDriver<bool>>();
            if (valueDriver != null && valueDriver.Drives.Count > 0)
            {
                SetColor(0, new colorX(0.36f, 0.2f, 0.23f));
                SetColor(1, new colorX(1f, 0.46f, 0.46f));
                SetColor(3, new colorX(0.88f, 0.88f, 0.88f));
            }

            btn.LocalPressed += (b, _) => ResetConfigs(b, pluginId, valueDriver);

            return;

            void SetColor(int index, colorX color)
            {
                if (index >= valueDriver.Drives.Count) return;

                FieldDrive<bool> drive = valueDriver.Drives[index];
                BooleanValueDriver<colorX> colorDriver = btn.Slot.GetComponent<BooleanValueDriver<colorX>>(x => x.State == drive.Target);
                if (colorDriver != null)
                {
                    colorDriver.TrueValue.Value = color;
                }
            }
        });
        yield return resetAct;
    }

    private static async IAsyncEnumerable<DataFeedItem> GetDummyAsync(DataFeedItem item)
    {
        await Task.CompletedTask;

        yield return item;
    }

    private static void SaveConfigs(string pluginId)
    {
        Plugin.Log.LogDebug($"Saving Configs for {pluginId}");

        pluginId = pluginId.Replace("_custom_settings", "");
        ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => $"rml.{mod.Author}.{mod.Name}" == pluginId);
        mod?.GetConfiguration()?.Save();
    }

    private static bool _resetPressed;
    private static CancellationTokenSource _cts;

    private static void ResetConfigs(IButton btn, string pluginId, ValueMultiDriver<bool> vmd = null)
    {
        try
        {
            if (!_resetPressed)
            {
                btn.LabelTextField.SetLocalized("Settings.RML.Mods.ResetConfig.Confirm");

                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                Task.Run(async () =>
                {
                    await Task.Delay(2000, token);

                    if (!_resetPressed) return;
                    btn.RunSynchronously(() => btn.LabelTextField.SetLocalized("Settings.RML.Mods.ResetConfig"));
                    _resetPressed = false;
                    if (vmd != null) vmd.Value.Value = _resetPressed;
                }, token);

                _resetPressed = true;
                if (vmd != null) vmd.Value.Value = _resetPressed;
                return;
            }

            pluginId = pluginId.Replace("_custom_settings", "");
            ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => $"rml.{mod.Author}.{mod.Name}" == pluginId);
            if (mod == null) return;

            ModConfiguration modConfig = mod.GetConfiguration();
            if (modConfig?.ConfigurationItemDefinitions.Count > 0)
            {
                foreach (ModConfigurationKey entry in modConfig.ConfigurationItemDefinitions)
                {
                    if (entry.TryComputeDefault(out object value))
                        modConfig.Set(entry, value);
                }
            }

            btn.LabelTextField.SetLocalized("Settings.RML.Mods.ResetConfig");
            _resetPressed = false;
            if (vmd != null) vmd.Value.Value = _resetPressed;

            _cts?.Cancel();
            _cts = null;

            Plugin.Log.LogInfo($"Configs for {pluginId} have been reset.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private static void ResetConfigSection(string pluginId, string section)
    {
        try
        {
            pluginId = pluginId.Replace("_custom_settings", "");
            ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => $"rml.{mod.Author}.{mod.Name}" == pluginId);
            if (mod == null) return;

            ModConfiguration modConfig = mod.GetConfiguration();
            if (modConfig?.ConfigurationItemDefinitions.Count > 0)
            {
                foreach (ModConfigurationKey entry in modConfig.ConfigurationItemDefinitions)
                {
                    if (entry.TryComputeDefault(out object value))
                        modConfig.Set(entry, value);
                }
            }

            Plugin.Log.LogInfo($"Configs for {pluginId}-{section} have been reset.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }
}