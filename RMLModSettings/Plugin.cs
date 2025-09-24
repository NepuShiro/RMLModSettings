using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisLocaleLoader;
using BepisModSettings.DataFeeds;
using Elements.Core;
using FrooxEngine;
using ResoniteModLoader;
using BepisResoniteWrapper;
using FrooxEngine.UIX;
using FrooxEngine.Weaver;
using HarmonyLib;
using ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables;

namespace RMLModSettings;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(BepisModSettings.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal new static ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;

        HarmonyInstance.PatchAll();

        ResoniteHooks.OnEngineReady += () =>
        {
            bool isRmlLoaded = false;

            try
            {
                isRmlLoaded = !string.IsNullOrEmpty(ModLoader.VERSION);
            }
            catch { }

            if (isRmlLoaded)
            {
                BepisSettingsPage.CustomPluginsPages += RmlSettingsEnumerate;
                BepisPluginPage.CustomPluginConfigsPages += RmlSettingsConfigsEnumerate;

                Log.LogInfo($"Plugin {PluginMetadata.GUID} is fully loaded!");
            }
            else
            {
                Log.LogFatal("ResoniteModLoader is not loaded! You cannot use this mod without it.");
            }
        };

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is partially loaded!");
    }

    [HarmonyPatch(typeof(AssemblyPostProcessor))]
    private class AssemblyPostProcessorPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(AssemblyPostProcessor), "Process", new[] { typeof(string), typeof(string).MakeByRefType(), typeof(string) });

        // I Honestly hate that this is needed, but net9 has an iron grip on DLL's and apparently referencing RML here causes this exception even though it's not loaded at this point in time
        // It's a non issue exception though, it just causes the engine to explode since it isn't caught in FrooxEngine.Weaver.
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception is IOException ioEx &&
                ioEx.Message.Contains("ResoniteModLoader.dll") &&
                ioEx.Message.Contains("being used by another process"))
            {
                Log.LogError("Suppressed IOException: " + ioEx.Message);
                return null;
            }

            return __exception;
        }
    }

    private static async IAsyncEnumerable<DataFeedItem> RmlSettingsEnumerate(IReadOnlyList<string> path)
    {
        await Task.CompletedTask;

        DataFeedGroup plguinsGroup = new DataFeedGroup();
        plguinsGroup.InitBase("RMLSettingsGroup", path, null, "Settings.RML.Mods".AsLocaleKey());
        yield return plguinsGroup;

        DataFeedGrid modsGrid = new DataFeedGrid();
        modsGrid.InitBase("ModsGrid", path, ["RMLSettingsGroup"], "Settings.RML.LoadedMods".AsLocaleKey());
        yield return modsGrid;

        string[] loadedModsGroup = ["RMLSettingsGroup", "ModsGrid"];

        List<ResoniteModBase> mods = new List<ResoniteModBase>(ModLoader.Mods());
        if (mods.Count > 0)
        {
            List<ResoniteModBase> sortedMods = new List<ResoniteModBase>(mods);
            sortedMods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            List<ResoniteModBase> filteredMods = FilterMods(sortedMods, BepisSettingsPage.SearchString).ToList();

            if (filteredMods.Count > 0)
            {
                foreach (ResoniteModBase mod in filteredMods)
                {
                    string modname = mod.Name;
                    string modGuid = mod.GetModId();

                    LocaleLoader.AddLocaleString($"Settings.{modGuid}.Breadcrumb", modname, authors: PluginMetadata.AUTHORS);

                    DataFeedCategory loadedPlugin = new DataFeedCategory();
                    loadedPlugin.InitBase(modGuid, path, loadedModsGroup, modname, $"{modname}\n{modGuid}\n({mod.Version})");
                    yield return loadedPlugin;
                }
            }
            else if (!string.IsNullOrEmpty(BepisSettingsPage.SearchString))
            {
                DataFeedLabel noResults = new DataFeedLabel();
                noResults.InitBase("NoSearchResults", path, loadedModsGroup, "Settings.RML.Mods.NoSearchResults".AsLocaleKey());
                yield return noResults;
            }
            else
            {
                DataFeedLabel noMods = new DataFeedLabel();
                noMods.InitBase("NoMods", path, loadedModsGroup, "Settings.RML.Mods.NoMods".AsLocaleKey());
                yield return noMods;
            }
        }
        else
        {
            DataFeedLabel noMods = new DataFeedLabel();
            noMods.InitBase("NoMods", path, loadedModsGroup, "Settings.RML.Mods.NoMods".AsLocaleKey());
            yield return noMods;
        }
    }

    private static IEnumerable<ResoniteModBase> FilterMods(List<ResoniteModBase> mods, string searchString)
    {
        if (string.IsNullOrWhiteSpace(searchString))
        {
            return mods;
        }

        string searchLower = searchString.ToLowerInvariant();

        return mods.Where(plugin =>
        {
            if (plugin.Name.Contains(searchLower, StringComparison.InvariantCultureIgnoreCase))
                return true;

            if (plugin.Version.ToString().Contains(searchLower, StringComparison.InvariantCultureIgnoreCase))
                return true;

            if (plugin.Author.Contains(searchLower, StringComparison.InvariantCultureIgnoreCase))
                return true;

            return false;
        });
    }

    private static async IAsyncEnumerable<DataFeedItem> RmlSettingsConfigsEnumerate(IReadOnlyList<string> path)
    {
        await Task.CompletedTask;

        string modId = path[1];
        ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => mod.GetModId() == modId);
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
        idIndicator.InitSetupValue(field => field.Value = modId);
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

        string modId = path[1];

        if (modConfig.ConfigurationItemDefinitions.Count > 0)
        {
            List<string> added = new List<string>();
            foreach (ModConfigurationKey config in modConfig.ConfigurationItemDefinitions)
            {
                if (config.InternalAccessOnly) continue;

                Type valueType = config.ValueType();

                const string section = "Configs";

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
                        ResetConfigSection(modId, section);
                    };
                });
                yield return configs;

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
                    yield return ConfigHelpers.GenerateToggle(key, path, groupingKeys, modConfig, config);
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
                        enumItem = (DataFeedItem)ConfigHelpers.GenerateEnumItemsAsync.MakeGenericMethod(valueType).Invoke(null, [key, path, groupingKeys, modConfig, config]);
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
                            nullableEnumItems = (IAsyncEnumerable<DataFeedItem>)ConfigHelpers.GenerateNullableEnumItemsAsync.MakeGenericMethod(nullableType).Invoke(null, [key, path, groupingKeys, config]);
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError(e);
                            DataFeedValueField<dummy> dummyField = new DataFeedValueField<dummy>();
                            dummyField.InitBase(key, path, groupingKeys, defaultKey, descKey);

                            nullableEnumItems = dummyField.AsAsyncEnumerable();
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
                        valueItem = (DataFeedItem)ConfigHelpers.GenerateValueField.MakeGenericMethod(valueType).Invoke(null, [key, path, groupingKeys, modConfig, config]);
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

            btn.LocalPressed += (_, _) => SaveConfigs(modId);
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

            btn.LocalPressed += (b, _) => ResetConfigs(b, modId, valueDriver);

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

    private static void SaveConfigs(string modId)
    {
        Plugin.Log.LogDebug($"Saving Configs for {modId}");

        ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => mod.GetModId() == modId);
        mod?.GetConfiguration()?.Save();
    }

    private static bool _resetPressed;
    private static CancellationTokenSource _cts;

    private static void ResetConfigs(IButton btn, string modId, ValueMultiDriver<bool> vmd = null)
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

            ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => mod.GetModId() == modId);
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

            Plugin.Log.LogInfo($"Configs for {modId} have been reset.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private static void ResetConfigSection(string modId, string section)
    {
        try
        {
            ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => mod.GetModId() == modId);
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

            Plugin.Log.LogInfo($"Configs for {modId}-{section} have been reset.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }
}