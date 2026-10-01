using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// 出兵门槛调整：兵力达到满编的 50% 就出动（原版 0.7，更早出动）。
    ///
    /// （原"全王国统一进攻目标"的强制覆盖已移除——它会把各城目标
    ///   全部改成敌国首都，导致进攻箭头/行军路线表现错误。）
    ///
    /// 保留反射读取 target_attack_zone 的工具方法，供战争线列补丁使用。
    /// </summary>
    public static class KingdomFocus
    {
        /// <summary>
        /// 出兵门槛（已放到最低）：军团队伍只要有 1 个人就允许出动。
        ///
        /// ⚠ 这里之前写错过，值得记一笔：
        /// 本补丁挂在 City.isOkToSendArmy 的【后面】（postfix），而原版那个方法算的是
        /// "兵力 / 编制 >= 0.7"。postfix 只能在原版结果上做加法，**没法放宽** ——
        /// 原版已经返回 false 时，写成"比例 >= 0.5 就放行"根本没有意义（那时早就 return 了）。
        /// 于是原版 0.7 一直是真正卡住出兵的闸门，小城永远填不满编制，整队人在城里挂机。
        ///
        /// 现在改成：人数够就【强制】__result = true，绕过原版的 0.7 比例。
        /// 小队不会零散送死 —— 前端会把攻打同一目标的各城军团合并成同一条战线。
        /// </summary>
        const int MinWarriorsToDeploy = 1;

        static int ratioLogCount;

        public static void onCheckSendArmy(City __instance, ref bool __result)
        {
            try
            {
                if (__instance == null || !__instance.hasArmy() || __instance.army == null) return;
                if (__instance.army.countUnits() < MinWarriorsToDeploy) return;

                if (!__result && ratioLogCount++ < 3)
                    ModClass.LogInfo("LineInfantry 出兵门槛已放开: 强制允许出动（绕过原版 0.7 比例）");
                __result = true;   // 无视原版比例，直接放行
            }
            catch (System.Exception e)
            {
                if (ratioLogCount++ < 5)
                    ModClass.LogWarning("LineInfantry 出兵门槛异常: " + e.Message);
            }
        }

        /// <summary>读取城市的进攻目标区域（City.target_attack_zone 为 internal，反射）</summary>
        static readonly FieldInfo TargetZoneField = typeof(City).GetField(
            "target_attack_zone",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static TileZone getTargetAttackZone(City city)
        {
            try
            {
                return (TileZone)TargetZoneField.GetValue(city);
            }
            catch
            {
                return null;
            }
        }
    }
}
