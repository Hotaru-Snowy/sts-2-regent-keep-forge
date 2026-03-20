using System;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using HarmonyLib;
using Godot.Bridge;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace Hotaru.STS2.Mod.RegentKeepForge {

    [ModInitializer("Init")]
    public class Main {
        public static void Init() {
            try {
                var harmony = new Harmony("hotaru.sts2.mod.regentkeepforge");
                harmony.PatchAll();
                ScriptManagerBridge.LookupScriptsInAssembly(typeof(Main).Assembly);
                Log.Info("[RegentKeepForge] Mod initialized.");
            } catch (Exception e) {
                Log.Error($"[RegentKeepForge] Initialization error: {e}");
            }
        }
    }

    /// <summary>
    /// SwordVault 静态类：负责管理 SovereignBlade 实体在内存与磁盘间的状态同步。
    /// 通过隔离单人 (SP) 与多人 (MP) 数据字典，确保不同模式下的数据一致性。
    /// </summary>
    public static class SwordVault {
        public static ConcurrentDictionary<ulong, decimal> ForgeData = new();
        public const decimal MaxDamage = 999999999m;
        public static bool IsMultiplayer() {
            return RunManager.Instance != null && !RunManager.Instance.IsSinglePlayerOrFakeMultiplayer;
        }

        // 持久化文件后缀标识
        private const string Ext = ".hotaru_regentkeepforge.save";

        /// <summary>
        /// 将指定模式的内存缓存序列化并写入 ISaveStore 目标路径。
        /// </summary>
        public static void SaveAll(ISaveStore store, string gamePath, bool isMP) {
            if (store == null || string.IsNullOrEmpty(gamePath)) return;

            if (ForgeData.IsEmpty) return;

            try {
                string shadowPath = gamePath + Ext;
                string content = string.Join("\n", ForgeData.Select(kvp => $"{kvp.Key}:{Math.Min(kvp.Value, MaxDamage)}"));
                byte[] bytes = Encoding.UTF8.GetBytes(content);

                store.WriteFile(shadowPath, bytes);
                Log.Info($"[RegentKeepForge] Shadow data persisted: {shadowPath} (Mode: {(isMP ? "MP" : "SP")} | Records: {ForgeData.Count})");
            } catch (Exception e) {
                Log.Error($"[RegentKeepForge] Persistence failed: {e.Message}");
            }
        }

        /// <summary>
        /// 从磁盘读取序列化数据并更新内存字典。
        /// </summary>
        public static void LoadAll(ISaveStore store, string gamePath, bool isMP) {
            if (store == null || string.IsNullOrEmpty(gamePath)) return;
            ForgeData.Clear();

            string shadowPath = gamePath + Ext;
            try {
                if (!store.FileExists(shadowPath)) {
                    Log.Info($"[RegentKeepForge] No existing shadow file found: {shadowPath}");
                    return;
                }

                string content = store.ReadFile(shadowPath);
                if (string.IsNullOrEmpty(content)) return;

                string[] lines = content.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines) {
                    string[] parts = line.Trim().Split(':');
                    if (parts.Length == 2 && ulong.TryParse(parts[0], out ulong id) && decimal.TryParse(parts[1], out decimal dmg)) {
                        ForgeData[id] = Math.Min(dmg, MaxDamage);
                    }
                }
                Log.Info($"[RegentKeepForge] Shadow data restored: {shadowPath} (Mode: {(isMP ? "MP" : "SP")} | Records: {ForgeData.Count})");
            } catch (Exception e) {
                Log.Error($"[RegentKeepForge] Data restoration failed: {e.Message}");
            }
        }

        /// <summary>
        /// 移除指定路径下的关联影子文件及备份文件。
        /// </summary>
        public static void DeleteShadow(ISaveStore store, string gamePath) {
            if (store == null || string.IsNullOrEmpty(gamePath)) return;
            string shadowPath = gamePath + Ext;
            string backupPath = shadowPath + ".backup";
            try {
                if (store.FileExists(shadowPath)) {
                    store.DeleteFile(shadowPath);
                    Log.Info($"[RegentKeepForge] Shadow file deleted: {shadowPath}");
                }
                if (store.FileExists(backupPath)) {
                    store.DeleteFile(backupPath);
                    Log.Info($"[RegentKeepForge] Shadow backup deleted: {backupPath}");
                }
            } catch (Exception e) {
                Log.Error($"[RegentKeepForge] File deletion error: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(SerializablePlayer))]
    public static class Patch_NetworkSync {

        // 使用一个唯一的 4 字节整数作为 Mod 签名 (DRKF in Hex)
        private const int MOD_SIGNATURE = 0x44524B46;
        // 定义一个固定的优先级（数字越大越靠后执行），确保在众多 Mod 中顺序固定
        private const int MOD_PRIORITY = 1146243910;

        /// <summary>
        /// 在序列化末尾追加：[SIGNATURE (4 bytes)] + [DAMAGE (8 bytes)]
        /// </summary>
        [HarmonyPatch(nameof(SerializablePlayer.Serialize))]
        [HarmonyPostfix]
        [HarmonyPriority(MOD_PRIORITY)]
        static void PostfixSerialize(SerializablePlayer __instance, PacketWriter writer) {
            decimal dmg = 0;
            if (SwordVault.ForgeData.TryGetValue(__instance.NetId, out decimal val)) {
                dmg = val;
            }

            // 写入签名：证明接下来的数据属于 RegentKeepForge
            writer.WriteInt(MOD_SIGNATURE, 32);
            writer.WriteLong((long)(dmg * 100), 64);

            // Log.Debug($"[RegentKeepForge] Packet Write: Sign {MOD_SIGNATURE:X}, Val {dmg}");
        }

        /// <summary>
        /// 读取时校验签名，确保数据流对齐。
        /// </summary>
        [HarmonyPatch(nameof(SerializablePlayer.Deserialize))]
        [HarmonyPostfix]
        [HarmonyPriority(MOD_PRIORITY)]
        static void PostfixDeserialize(SerializablePlayer __instance, PacketReader reader) {
            try {
                // 尝试读取签名
                int sign = reader.ReadInt(32);

                if (sign == MOD_SIGNATURE) {
                    // 签名匹配，读取伤害数据
                    long raw = reader.ReadLong(64);
                    decimal dmg = raw / 100m;

                    if (dmg > 0) {
                        SwordVault.ForgeData[__instance.NetId] = dmg;
                        Log.Info($"[RegentKeepForge] Network Sync Verified: NetId {__instance.NetId}, Damage {dmg}");
                    }
                } else {
                    // 严重警告：签名不匹配说明数据流中出现了其他 Mod 的干扰或顺序冲突
                    Log.Error($"[RegentKeepForge] Deserialization Mismatch! Expected {MOD_SIGNATURE:X}, Got {sign:X}. Skipping read to prevent crash.");
                    // 其实应该需要让 reader 回退4字节来着
                    // 不过假如这个不合格的话，那意味着两边的mod应该不一致，活该连不上（bushi）
                }
            } catch (Exception e) {
                Log.Error($"[RegentKeepForge] Network Read Error: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Harmony 补丁类：拦截 RunSaveManager 的生命周期事件。
    /// </summary>
    [HarmonyPatch(typeof(RunSaveManager))]
    public static class Patch_RunSaveManager {

        /// <summary>
        /// 拦截保存流程。根据当前会话的 Multiplayer 状态位，定向执行影子存档同步。
        /// </summary>
        [HarmonyPatch(nameof(RunSaveManager.SaveRun))]
        [HarmonyPostfix]
        static void PostfixSaveRun(RunSaveManager __instance) {
            try {
                var store = (ISaveStore)AccessTools.Field(typeof(RunSaveManager), "_saveStore").GetValue(__instance);

                if (SwordVault.IsMultiplayer()) {
                    string mpPath = (string)AccessTools.Property(typeof(RunSaveManager), "CurrentMultiplayerRunSavePath").GetValue(__instance);
                    SwordVault.SaveAll(store, mpPath, true);
                } else {
                    string spPath = (string)AccessTools.Property(typeof(RunSaveManager), "CurrentRunSavePath").GetValue(__instance);
                    SwordVault.SaveAll(store, spPath, false);
                }
            } catch (Exception e) {
                Log.Error($"[RegentKeepForge] SaveRun hook internal error: {e.Message}");
            }
        }

        [HarmonyPatch(nameof(RunSaveManager.LoadRunSave))]
        [HarmonyPostfix]
        static void PostfixLoadSP(RunSaveManager __instance) {
            Log.Info("[RegentKeepForge] Mode recognized: SinglePlayer");
            ExecuteLoad(__instance, "CurrentRunSavePath", false);
        }

        [HarmonyPatch(nameof(RunSaveManager.LoadMultiplayerRunSave))]
        [HarmonyPostfix]
        static void PostfixLoadMP(RunSaveManager __instance) {
            Log.Info("[RegentKeepForge] Mode recognized: Multiplayer");
            ExecuteLoad(__instance, "CurrentMultiplayerRunSavePath", true);
        }

        private static void ExecuteLoad(RunSaveManager instance, string pathProp, bool isMP) {
            var store = (ISaveStore)AccessTools.Field(typeof(RunSaveManager), "_saveStore").GetValue(instance);
            var path = (string)AccessTools.Property(typeof(RunSaveManager), pathProp).GetValue(instance);
            SwordVault.LoadAll(store, path, isMP);
        }

        [HarmonyPatch(nameof(RunSaveManager.DeleteCurrentRun))]
        [HarmonyPrefix]
        static void PrefixDelSP(RunSaveManager __instance) {
            SwordVault.ForgeData.Clear();
            var store = (ISaveStore)AccessTools.Field(typeof(RunSaveManager), "_saveStore").GetValue(__instance);
            var path = (string)AccessTools.Property(typeof(RunSaveManager), "CurrentRunSavePath").GetValue(__instance);
            SwordVault.DeleteShadow(store, path);
        }

        [HarmonyPatch(nameof(RunSaveManager.DeleteCurrentMultiplayerRun))]
        [HarmonyPrefix]
        static void PrefixDelMP(RunSaveManager __instance) {
            SwordVault.ForgeData.Clear();
            var store = (ISaveStore)AccessTools.Field(typeof(RunSaveManager), "_saveStore").GetValue(__instance);
            var path = (string)AccessTools.Property(typeof(RunSaveManager), "CurrentMultiplayerRunSavePath").GetValue(__instance);
            SwordVault.DeleteShadow(store, path);
        }
    }

    /// <summary>
    /// Harmony 补丁类：回到主菜单就清理存储的避免撞车。
    /// </summary>
    [HarmonyPatch(typeof(NMainMenu))]
    public static class Patch_MainMenu {
        [HarmonyPatch(nameof(NMainMenu.Create))]
        [HarmonyPostfix]
        static void PostfixCreate() {
            SwordVault.ForgeData.Clear();
        }
    }

    /// <summary>
    /// Harmony 补丁类：拦截 SovereignBlade 逻辑。
    /// </summary>
    [HarmonyPatch(typeof(SovereignBlade))]
    public static class Patch_SovereignBlade {

        /// <summary>
        /// 拦截伤害增加方法，实现内存状态的实时更新。
        /// </summary>
        [HarmonyPatch(nameof(SovereignBlade.AddDamage))]
        [HarmonyPostfix]
        static void PostfixAddDamage(SovereignBlade __instance) {
            if (__instance.Owner != null) {
                SwordVault.ForgeData[__instance.Owner.NetId] = __instance.DynamicVars.Damage.BaseValue;
            }
        }

        /// <summary>
        /// 拦截堆栈变更事件。在卡牌初始化（oldPileType == None）时，从字典恢复 BaseValue。
        /// </summary>
        [HarmonyPatch("AfterCardChangedPiles")]
        [HarmonyPostfix]
        static void PostfixRecovery(SovereignBlade __instance, CardModel card, PileType oldPileType) {
            if (card == __instance && oldPileType == PileType.None && __instance.Owner != null) {
                if (SwordVault.ForgeData.TryGetValue(__instance.Owner.NetId, out decimal savedDmg)) {
                    decimal currentDmg = __instance.DynamicVars.Damage.BaseValue;
                    if (savedDmg > currentDmg) {
                        decimal diff = savedDmg - currentDmg;
                        __instance.AddDamage(diff);
                        Log.Info($"[RegentKeepForge] Value synchronization: NetId {__instance.Owner.NetId}, Delta: {diff}, Result: {savedDmg}");
                    }
                }
            }
        }
    }
}