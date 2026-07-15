using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisLocaleLoader;
using BepisModSettings.DataFeeds;
using BepisResoniteWrapper;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using FrooxEngine.Weaver;
using HarmonyLib;
using ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables;
using Renderite.Shared;
using ResoniteModLoader;

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
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name?.Contains("ResoniteModLoader") == true))
            {
                BepisPluginsPage.CustomPluginsPages += RmlSettingsEnumerate;
                BepisConfigsPage.CustomPluginConfigsPages += RmlSettingsConfigsEnumerate;

                Log.LogInfo($"Plugin {PluginMetadata.GUID} is fully loaded!");
            }
            else
            {
                Log.LogFatal("ResoniteModLoader is not loaded! You cannot use this plugin without it.");
                Task.Run(async () =>
                {
                    while (Userspace.UserspaceWorld?.LocalUserSpace == null) await Task.Delay(10);

                    World w = Userspace.UserspaceWorld;
                    w.RunSynchronously(() =>
                    {
                        Slot slot = w.LocalUserSpace.AddSlot("RML Mod Settings Warning", false);
                        UIBuilder uIBuilder = RadiantUI_Panel.SetupPanel(slot, "RML Mod Settings - <color=Hero.Yellow>Warning</color>", new float2(700f, 350f), pinButton: false);
                        RadiantUI_Constants.SetupEditorStyle(uIBuilder);
                        uIBuilder.VerticalLayout(4f);
                        uIBuilder.Style.MinHeight = 48f;

                        uIBuilder.Text("ResoniteModLoader is <color=Hero.Red>not loaded</color>, or is <color=Hero.Red>not up to date</color>.\nYou cannot use this plugin without it!").Size.Value = 32f;

                        Hyperlink hl = uIBuilder.Button("Go to Latest RML", RadiantUI_Constants.Sub.GREEN).Slot.AttachComponent<Hyperlink>();
                        hl.URL.Value = new Uri("https://github.com/resonite-modding-group/ResoniteModLoader/releases/latest");
                        hl.Reason.Value = "Opening RML Github";

                        slot.PositionInFrontOfUser(float3.Backward, null, 3f);
                        slot.LocalScale *= 0.003f;
                    });
                });
            }
        };

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is partially loaded!");
    }

    [HarmonyPatch]
    private class AssemblyPostProcessorPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(AssemblyPostProcessor), "Process", new[] { typeof(string), typeof(string).MakeByRefType(), typeof(string) });

        // I Honestly hate that this is needed, but net9 has an iron grip on DLL's and apparently referencing RML here causes this exception even though it's not loaded at this point in time
        // It's a non issue exception though, it just causes the engine to explode since it isn't caught in FrooxEngine.Weaver.
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception is IOException ioEx && ioEx.Message.Contains("ResoniteModLoader.dll") && ioEx.Message.Contains("being used by another process"))
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

        DataFeedResettableGroup modsGroup = new DataFeedResettableGroup();
        modsGroup.InitBase("RMLSettingsGroup", path, null, "Settings.RML.Mods".AsLocaleKey());
        modsGroup.InitSlotName();
        modsGroup.InitResetAction(x =>
        {
            BooleanValueDriver<string> bvd = x.Slot.GetComponentInChildren<Text>().Slot.GetComponent<BooleanValueDriver<string>>();
            bvd.FalseValue.Value = "Settings.RML.SaveAll";

            if (bvd.Slot.Parent.Parent.GetComponentInChildren<Image>() is not { } img) return;
            SpriteProvider spr = img.Slot.AttachComponent<SpriteProvider>();
            spr.Texture.Target = img.Slot.AttachTexture(new Uri("resdb:///2f5cc6b6d4249bfdceda48fcd3df6375d47d13614e2100a8ed5a0f511ea9c01e.webp"), wrapMode: TextureWrapMode.Clamp);
            img.Sprite.Target = spr;

            if (x.Slot.GetComponent<Button>() is not { } btn) return;
            btn.LocalPressed += (_, _) =>
            {
                Slot resetBtn = btn.Slot.FindParent(x2 => x2.Name == "Reset Button");
                DataModelValueFieldStore<bool>.Store store = resetBtn?.GetComponentInChildren<DataModelValueFieldStore<bool>.Store>();
                if (store == null) return;

                if (!store.Value.Value || NetChainloader.Instance.Plugins.Count == 0) return;

                Plugin.Log.LogDebug($"Saving All Configs");
                ModLoader.Mods().Do(x3 => x3?.GetConfiguration()?.Save());
            };
        });
        yield return modsGroup;

        DataFeedGrid modsGrid = new DataFeedGrid();
        modsGrid.InitBase("ModsGrid", path, ["RMLSettingsGroup"], "Settings.RML.LoadedMods".AsLocaleKey());
        modsGroup.InitSlotName();
        yield return modsGrid;

        string[] loadedModsGroup = ["RMLSettingsGroup", "ModsGrid"];

        List<(ResoniteModBase Mod, bool IsEmpty)> mods = new List<ResoniteModBase>(ModLoader.Mods()).Select(mod => (Mod: mod, IsEmpty: ConfigHelpers.IsEmpty(mod.GetConfiguration()))).Where(x => !x.IsEmpty || BepisModSettings.Plugin.ShowEmptyPages.Value).OrderBy(x => x.Mod.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (mods.Count == 0)
        {
            DataFeedLabel noMods = new DataFeedLabel();
            noMods.InitBase("NoMods", path, loadedModsGroup, "Settings.RML.Mods.NoMods".AsLocaleKey());
            noMods.InitSlotName();
            yield return noMods;
            yield break;
        }

        foreach ((ResoniteModBase mod, bool isEmpty) in mods)
        {
            ConfigHelpers.SetupConfigKeyFieldInfo(mod);

            string modName = isEmpty ? $"<color=#a8a8a8>{mod.Name}</color>" : mod.Name;
            string modGuid = mod.GetModId();
            string modDesc = $"{modName} ({mod.Version})\nby \"{mod.Author}\"\n\n{modGuid}";

            LocaleLoader.AddLocaleString($"Settings.{modGuid}.Breadcrumb", modName, authors: PluginMetadata.AUTHORS);

            DataFeedCategory loadedPlugin = new DataFeedCategory();
            loadedPlugin.InitBase(modGuid, path, loadedModsGroup, modName, modDesc);
            loadedPlugin.InitVisible(x =>
            {
                if (!x.TryFindClosestSlot(out Slot slot)) return;

                if (isEmpty && slot.GetComponent<Button>() is { ColorDrivers.Count: 2 } btn)
                {
                    var _0 = btn.ColorDrivers[0];
                    _0.NormalColor.Value = RadiantUI_Constants.Sub.ORANGE;
                    _0.HighlightColor.Value = RadiantUI_Constants.Dark.ORANGE;
                }

                slot.EnsureSpace(DynamicVariableHelper.ProcessName(modGuid));
                slot.CreateDynField("Visible", x);

                slot.CreateValVar("Name", modName);
                slot.CreateValVar("Description", modDesc);
                slot.CreateValVar("ID", modGuid);
                slot.CreateValVar("Version", mod.Version);
                slot.CreateValVar("Author", mod.Author);
            });

            if (BepisModSettings.Plugin.SortEmptyPages.Value && isEmpty) loadedPlugin.InitSorting(1);
            yield return loadedPlugin;
        }

        DataFeedLabel noResults = new DataFeedLabel();
        noResults.InitBase("NoSearchResultsRML", path, loadedModsGroup, "Settings.RML.Mods.NoSearchResults".AsLocaleKey());
        noResults.InitVisible(x =>
        {
            x.Value = false;

            if (!x.TryFindClosestSlot(out Slot slot)) return;

            slot.EnsureSpace(DynamicVariableHelper.ProcessName(noResults.ItemKey));
            slot.CreateDynField("Visible", x);

            slot.CreateValVar("Name", noResults.ItemKey);
        });
        noResults.InitSlotName();
        yield return noResults;
    }

    private static async IAsyncEnumerable<DataFeedItem> RmlSettingsConfigsEnumerate(IReadOnlyList<string> path)
    {
        await Task.CompletedTask;

        string modId = path[1];
        ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => mod.GetModId() == modId);
        if (mod == null) yield break;

        ModConfiguration modConfig = mod.GetConfiguration();
        if (ConfigHelpers.IsEmpty(modConfig))
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

        if (!string.IsNullOrWhiteSpace(mod.Link) && Uri.TryCreate(mod.Link, UriKind.Absolute, out Uri uri))
        {
            DataFeedAction modHyperlink = new DataFeedAction();
            modHyperlink.InitBase("Link", path, metadataGroup, "Settings.RML.Mods.ModPage".AsLocaleKey());
            modHyperlink.InitAction(syncDelegate =>
            {
                Slot slot = syncDelegate?.Slot;
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

        const string section = "Configs";

        DataFeedResettableGroup configs = DataFeedHelpers.DataFeedCollapseResettableGroup(section, path, null, section);
        Action<SyncDelegate<Action>> oldAction = configs.ResetAction;
        configs.InitResetAction(a =>
        {
            oldAction?.Invoke(a);

            Button but = a.Slot.GetComponentInChildren<Button>();
            if (but == null) return;

            but.LocalPressed += (b, _) =>
            {
                Slot resetBtn = b.Slot.FindParent(x => x.Name == "Reset Button");
                DataModelValueFieldStore<bool>.Store store = resetBtn?.GetComponentInChildren<DataModelValueFieldStore<bool>.Store>();
                if (store == null) return;

                if (!store.Value.Value) return;
                ResetConfigSection(modId, section);
            };
        });
        yield return configs;

        List<string> added = new List<string>();
        foreach (ModConfigurationKey config in modConfig.ConfigurationItemDefinitions)
        {
            bool isHidden = config.InternalAccessOnly;
            if (!BepisModSettings.Plugin.ShowHidden.Value && isHidden) continue;

            Type valueType = config.ValueType();

            string initKey = section + "." + config.Name;
            string key = added.Contains(initKey) ? initKey + added.Count : initKey;
            added.Add(key);
            
            string defaultValueText;

            if (config.TryComputeDefault(out object value))
            {
                if (value == null)
                {
                    defaultValueText = "<i>Null</i>";
                }
                else
                {
                    string defaultValue = value.ToString();

                    if (string.IsNullOrEmpty(defaultValue))
                    {
                        defaultValueText = "<i>Empty String</i>";
                    }
                    else if (valueType == typeof(string) || valueType == typeof(Uri))
                    {
                        defaultValueText = $"\"{defaultValue}\"";
                    }
                    else
                    {
                        defaultValueText = defaultValue;
                    }
                }
            }
            else
            {
                defaultValueText = "<i>Couldn't Compute Default</i>";
            }


            LocaleString nameKey = isHidden ? $"<color=hero.yellow>{config.Name}</color>" : config.Name;
            LocaleString descKey = $"{config.Description}\n\nDefault: {defaultValueText}";
            LocaleString descKey2 = config.Description;
            LocaleString defaultKey = $"{config.Name} : {valueType}";
            // LocaleString valueKey = $"{config.Name} : {modConfig.GetValue(config)}";

            InternalLocale internalLocale = new InternalLocale(nameKey, descKey);

            string[] groupingKeys = [section];

            if (valueType == typeof(dummy))
            {
                DataFeedIndicator<string> indi = new DataFeedIndicator<string>();
                indi.InitBase(key, path, groupingKeys, nameKey, descKey2);
                indi.InitSetupValue(x => { x.Value = descKey2.content.GetFormattedLocaleString(); });
                indi.InitVisible(x => x.Value = !BepisModSettings.Plugin.DefaultCollapsed.Value);
                yield return indi;
            }
            else if (valueType == typeof(bool))
            {
                DataFeedToggle toggle = ConfigHelpers.GenerateToggle(key, path, groupingKeys, internalLocale, modConfig, config);
                toggle.InitVisible(x => x.Value = !BepisModSettings.Plugin.DefaultCollapsed.Value);
                yield return toggle;
            }
            else if (valueType.IsEnum)
            {
                DataFeedItem enumItem;

                try
                {
                    // TODO: Implement Flags enums for RML Mods, Not sure if any use flags but yeh
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
                    enumItem = (DataFeedItem)ConfigHelpers.GenerateEnumItemsAsync.MakeGenericMethod(valueType).Invoke(null, [key, path, groupingKeys, internalLocale, modConfig, config]);
                    // }
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    enumItem = new DataFeedValueField<dummy>();
                    enumItem.InitBase(key, path, groupingKeys, defaultKey, descKey);
                }

                enumItem.InitVisible(x => x.Value = !BepisModSettings.Plugin.DefaultCollapsed.Value);

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
                        Log.LogError(e);
                        DataFeedValueField<dummy> dummyField = new DataFeedValueField<dummy>();
                        dummyField.InitBase(key, path, groupingKeys, defaultKey, descKey);

                        nullableEnumItems = dummyField.AsAsyncEnumerable();
                    }

                    await foreach (DataFeedItem item in nullableEnumItems)
                    {
                        item.InitVisible(x => x.Value = !BepisModSettings.Plugin.DefaultCollapsed.Value);
                        yield return item;
                    }
                }
            }
            else
            {
                DataFeedItem valueItem;

                try
                {
                    valueItem = (DataFeedItem)ConfigHelpers.GenerateValueField.MakeGenericMethod(valueType).Invoke(null, [key, path, groupingKeys, internalLocale, modConfig, config]);
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    valueItem = new DataFeedValueField<dummy>();
                    valueItem.InitBase(key, path, groupingKeys, defaultKey, descKey);
                }

                valueItem.InitVisible(x => x.Value = !BepisModSettings.Plugin.DefaultCollapsed.Value);
                yield return valueItem;
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
        Log.LogDebug($"Saving Configs for {modId}");

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
            if (!ConfigHelpers.IsEmpty(modConfig))
            {
                foreach (ModConfigurationKey entry in modConfig!.ConfigurationItemDefinitions)
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

            Log.LogInfo($"Configs for {modId} have been reset.");
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
    }

    private static void ResetConfigSection(string modId, string section)
    {
        try
        {
            ResoniteModBase mod = ModLoader.Mods().FirstOrDefault(mod => mod.GetModId() == modId);
            if (mod == null) return;

            ModConfiguration modConfig = mod.GetConfiguration();
            if (!ConfigHelpers.IsEmpty(modConfig))
            {
                foreach (ModConfigurationKey entry in modConfig!.ConfigurationItemDefinitions)
                {
                    if (entry.TryComputeDefault(out object value))
                        modConfig.Set(entry, value);
                }
            }

            Log.LogInfo($"Configs for {modId}-{section} have been reset.");
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
    }
}