using System.Collections.Generic;
using ai.behaviours;
using HarmonyLib;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// LineInfantry 全部 Harmony 补丁（显式注册；PatchAll 在本环境实测静默失效）。
    ///
    /// 【核心：武器锻造接管】craftItem Prefix 完全接管武器锻造：
    ///   只造火枪（从付得起的最高级选），绝不造剑斧弓矛、绝不降级。
    ///   堵死原版三条问题路径：
    ///     ① 无文化城市走 default_weapon_pool 随机造剑斧弓（换装元凶）
    ///     ② 文化偏好武器分支（50% 锻造机会被原版武器占走）
    ///     ③ "新武器必须比手上值钱"门槛在压价后语义正确，但接管后天然不降级
    ///
    /// 【其余补丁】
    ///   DecisionsLibrary.init               打造/换装频率 + 打造门槛（木头即可）
    ///   City.giveItem(Prefix)               火枪最高优先分配 + 持枪不被降级
    ///   Actor.generateDefaultSpawnWeapons   出场配枪（默认关闭）
    ///   City.isOkToSendArmy                 出兵门槛
    ///   Projectile.updateTrailEffect        弹迹按距离投放（连续白线）
    /// 初始化时一次性调整：特效上限 / 压低原版武器价值
    /// </summary>
    public static class LinePatches
    {
        static Harmony harmony;

        const float SpawnMusketChance = 0.001f;   // 出场配枪：千分之一（配置开关也默认关）

        static int spawnLogCount, giveLogCount, giveBlockLogCount, takeoverLogCount;

        public static void init()
        {
            harmony = new Harmony("li.lineinfantry.worldbox");
            int ok = 0;

            // —— 一次性调整（决策库/特效库此时已初始化）——
            boostCraftingFrequency();
            tuneEffectLimits();
            depressVanillaWeaponValues();

            // —— Harmony 补丁 ——
            ok += reg(typeof(City), "giveItem",
                      nameof(giveItem_Prefix), typeof(LinePatches), postfix: false);
            ok += reg(typeof(Actor), "generateDefaultSpawnWeapons",
                      nameof(spawnWithMusket), typeof(LinePatches));
            // 战争行军线列（核心）：敌城前排成一线，替换"随机扔进敌城区域"

            ok += reg(typeof(City), "isOkToSendArmy",
                      nameof(KingdomFocus.onCheckSendArmy), typeof(KingdomFocus));
            // 线列阵型：跟随队长的找格逻辑改为"全军团单排横队"（行军与战斗都维持）
            ok += reg(typeof(BehFindTileNearbyGroupLeader), "execute",
                      nameof(LineFormation.findTile_Prefix), typeof(LineFormation), postfix: false);
            // 战争行军线列（核心）：开战后士兵不再被随机扔进敌城区域，而是在敌城前排成一线
            ok += reg(typeof(BehCityActorCheckAttack), "execute",
                      nameof(LineFormation.checkAttack_Prefix), typeof(LineFormation), postfix: false);
            // 纪律① 乱走：warrior_random_move / leader_move_random 的"随机找格"改为走回自己的战线格
            ok += reg(typeof(BehFindRandomTile), "execute",
                      nameof(LineFormation.findRandomTile_Prefix), typeof(LineFormation), postfix: false);
            // 纪律② 追敌：自动战斗 fighting 的"走到敌人那一格"改为守在自己战线格上开枪
            ok += reg(typeof(BehGoToActorTarget), "execute",
                      nameof(LineFormation.goToActorTarget_Prefix), typeof(LineFormation), postfix: false);
            // 纪律③ 兜底：所有"走到某个格子"的最后一道闸 —— 目的地落在战线后方就摁回战线格
            ok += reg(typeof(BehGoToTileTarget), "execute",
                      nameof(LineFormation.goToTileTarget_Prefix), typeof(LineFormation), postfix: false);

            // 战列线可视化（黑色实线，地图视角显示；含 group_system 补建修复）
            FrontlineVisual.register();
            ok += reg(typeof(Projectile), "updateTrailEffect",
                      nameof(TrailDensity.updateTrailEffect_Prefix), typeof(TrailDensity), postfix: false);
            // 武器锻造接管（核心）
            ok += reg(typeof(ItemCrafting), "craftItem",
                      nameof(craftItem_Takeover), typeof(LinePatches), postfix: false);

            // 战列线可视化（火枪手高亮 + 连线；非 Harmony 补丁，是地图绘制图层）

            ModClass.LogInfo($"LineInfantry 初始化完成: {ok}/10 补丁 + 战线可视化 + 3 项一次性调整");

            // 决策库如果在模组加载后才初始化，这里的补丁已覆盖；若之前已初始化，
            // 上面的直接调用已生效。
        }

        static int reg(System.Type targetType, string targetMethod, string patchMethod,
                       System.Type patchType, bool postfix = true)
        {
            try
            {
                var target = AccessTools.Method(targetType, targetMethod);
                if (target == null)
                {
                    ModClass.LogError($"LineInfantry 未找到目标方法 {targetType.Name}.{targetMethod}");
                    return 0;
                }
                var patch = AccessTools.Method(patchType, patchMethod);
                if (patch == null)
                {
                    ModClass.LogError($"LineInfantry 未找到补丁方法 {patchType.Name}.{patchMethod}");
                    return 0;
                }
                var hm = new HarmonyMethod(patch);
                if (postfix) harmony.Patch(target, postfix: hm);
                else harmony.Patch(target, prefix: hm);
                return 1;
            }
            catch (System.Exception e)
            {
                ModClass.LogError($"LineInfantry 注册补丁失败 {targetType.Name}.{targetMethod}: {e}");
                return 0;
            }
        }

        public static bool configOn(string id)
        {
            try
            {
                return (bool)ModClass.Config["LineInfantry"][id].GetValue();
            }
            catch
            {
                return true;
            }
        }

        // ================= 武器锻造接管（核心） =================

        /// <summary>
        /// 完全接管武器锻造。
        /// 1. 只造火枪：按资产价值从高到低找第一把"付得起"的（天然是可负担的最高级）
        /// 2. 一个都付不起 → 不造（绝不造原版武器、绝不降级）
        /// 3. 成品装给打造者，换下的旧武器回仓库
        /// 4. 额外存一把同款进仓库，进入全军分发池
        /// 注意：资源消耗在此处真实结算（钱 + 城市资源），非凭空造枪。
        /// </summary>
        static bool craftItem_Takeover(Actor pActor, string pCreatorName, EquipmentType pType,
                                       int pTries, City pCity, ref bool __result)
        {
            if (pType != EquipmentType.Weapon) return true;    // 防具等走原版
            try
            {
                if (pActor == null || pCity == null) return true;

                // 从最高级往低找第一把付得起的火枪
                ItemAsset chosen = null;
                for (int i = MusketAssets.TierIds.Count - 1; i >= 0; i--)
                {
                    ItemAsset a = AssetManager.items.get(MusketAssets.TierIds[i]);
                    if (a == null) continue;
                    if (ItemCrafting.hasEnoughResourcesToCraft(pActor, a, pCity))
                    {
                        chosen = a;
                        break;
                    }
                }
                if (chosen == null)
                {
                    __result = false;       // 资源不足：这轮不造（也不造原版武器）
                    return false;
                }

                // ⚠ 防降级：打造者已持有同级或更高级的火枪时不造。
                // 否则穷城市的铁匠会用 T1 换掉手里的 T4（"给了高级枪被换成低级"的元凶）。
                // 手上是非火枪（价值已被压到极低）则正常打造，完成换装。
                Item curItem = pActor.equipment?.weapon?.getItem();
                if (curItem != null)
                {
                    ItemAsset curAsset = curItem.getAsset();
                    if (curAsset != null && curAsset.equipment_subtype == MusketAssets.Subtype
                        && curAsset.equipment_value >= chosen.equipment_value)
                    {
                        __result = false;   // 已有同级/更高级火枪，无需打造
                        return false;
                    }
                }

                // 真实结算成本
                pActor.spendMoney(chosen.get_total_cost);
                if (chosen.cost_resource_id_1 != "none")
                    pCity.takeResource(chosen.cost_resource_id_1, chosen.cost_resource_1);
                if (chosen.cost_resource_id_2 != "none")
                    pCity.takeResource(chosen.cost_resource_id_2, chosen.cost_resource_2);

                // 生成成品并装备
                Item newItem = World.world.items.generateItem(chosen, pActor.kingdom, pCreatorName, pTries, pActor);
                if (newItem == null)
                {
                    __result = false;
                    return false;
                }

                ActorEquipmentSlot slot = pActor.equipment.getSlot(EquipmentType.Weapon);
                if (slot.isEmpty())
                {
                    slot.setItem(newItem, pActor);
                }
                else
                {
                    Item old = slot.getItem();
                    slot.takeAwayItemFromSlot();
                    pCity.tryToPutItem(old);
                    slot.setItem(newItem, pActor);
                }

                // 额外一把入库，进入全军分发池（加速扩散）
                Item extra = World.world.items.generateItem(chosen, pCity.kingdom, pCreatorName, 1, pActor);
                if (extra != null) pCity.tryToPutItem(extra);

                if (takeoverLogCount++ < 5)
                    ModClass.LogInfo($"LineInfantry 锻造接管生效: 产出 {chosen.id}（另入库一把）");

                __result = true;
                return false;
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 锻造接管异常: " + e.Message);
                return true;    // 出错退回原版，保游戏稳定
            }
        }

        // ================= 打造/换装频率 =================

        static void boostCraftingFrequency()
        {
            try
            {
                DecisionAsset d = AssetManager.decisions_library.get("make_items");
                if (d == null)
                {
                    ModClass.LogWarning("LineInfantry 未找到 make_items 决策");
                    return;
                }
                d.cooldown = 5;     // 原 90 秒
                d.weight = 6.0f;    // 原 0.4
                // 原版门槛要求"城市某战略资源>10"，卡死没矿的小城；
                // 火绳钩枪只要木头，这里换成"城市有木头即可打造"
                d.action_check_launch = (DecisionAction)(pActor =>
                {
                    if (pActor == null || !pActor.hasHouse()) return false;
                    if (!pActor.inOwnCityBorders()) return false;
                    return pActor.city != null && pActor.city.amount_wood >= 1;
                });

                DecisionAsset take = AssetManager.decisions_library.get("try_to_take_city_item");
                if (take != null) take.cooldown = 10;   // 原 180 秒

                ModClass.LogInfo("LineInfantry 已提高打造/换装频率: 打造 90->5秒(权重6.0), 领装备 180->10秒");
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 调整打造频率失败: " + e.Message);
            }
        }

        // ================= 城市分发：火枪最高优先 =================

        static bool giveItem_Prefix(Actor pActor, List<long> pItems, City pCity, ref bool __result)
        {
            if (pItems == null || pItems.Count == 0) return true;
            try
            {
                ActorEquipmentSlot slot = pActor.equipment.getSlot(EquipmentType.Weapon);
                bool holdingMusket = false;
                int curValue = 0;
                if (!slot.isEmpty())
                {
                    Item cur = slot.getItem();
                    curValue = cur.getValue();
                    ItemAsset curAsset = cur.getAsset();
                    holdingMusket = curAsset != null && curAsset.equipment_subtype == MusketAssets.Subtype;
                }

                long bestMusketId = -1;
                int bestValue = -1;
                foreach (long id in pItems)
                {
                    Item it = World.world.items.get(id);
                    if (it == null) continue;
                    ItemAsset asset = it.getAsset();
                    if (asset == null || asset.equipment_subtype != MusketAssets.Subtype) continue;
                    int v = it.getValue();
                    if (v > bestValue) { bestValue = v; bestMusketId = id; }
                }

                if (bestMusketId >= 0 && bestValue > curValue)
                {
                    Item musket = World.world.items.get(bestMusketId);
                    Item old = null;
                    if (!slot.isEmpty())
                    {
                        old = slot.getItem();
                        slot.takeAwayItemFromSlot();
                    }
                    slot.setItem(musket, pActor);
                    pItems.Remove(bestMusketId);
                    pActor.setStatsDirty();
                    if (old != null) pCity.data.equipment.addItem(pCity, old);

                    if (giveLogCount++ < 3)
                        ModClass.LogInfo("LineInfantry 火枪优先分配生效: 士兵从城市领取火枪");
                    __result = true;
                    return false;
                }

                // 判断本次清单是否武器类（giveItem 按装备类别分别调用）
                bool incomingIsWeapon = false;
                foreach (long id0 in pItems)
                {
                    Item it0 = World.world.items.get(id0);
                    if (it0 == null) continue;
                    ItemAsset a0 = it0.getAsset();
                    if (a0 != null) incomingIsWeapon = a0.equipment_type == EquipmentType.Weapon;
                    break;
                }

                // 持枪者：武器类一律不再领取（不降级、不被原版武器替换）
                if (holdingMusket && incomingIsWeapon)
                {
                    if (giveBlockLogCount++ < 3)
                        ModClass.LogInfo("LineInfantry 已阻止火枪被替换");
                    __result = false;
                    return false;
                }

                return true;
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 火枪优先分配异常: " + e.Message);
                return true;
            }
        }

        // ================= 出场配枪（默认关闭） =================

        static void spawnWithMusket(Actor __instance)
        {
            try
            {
                // 出场配枪默认关闭，由配置项 spawnWithMusket 控制（default_config.json 中为 false）
                if (!configOn("spawnWithMusket")) return;
                if (!__instance.canUseItems()) return;
                if (!Randy.randomChance(SpawnMusketChance)) return;

                string id = MusketAssets.pickTierFor(__instance.city);
                if (id == null) return;
                __instance.createNewWeapon(id);

                if (spawnLogCount++ < 3)
                    ModClass.LogInfo($"LineInfantry 新兵配枪生效: {id}");
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 新兵配枪异常: " + e.Message);
            }
        }

        // ================= 一次性调整 =================

        static void tuneEffectLimits()
        {
            try
            {
                // 弹道拖尾特效不设上限（0 = 无限）
                EffectAsset smoke = AssetManager.effects_library.get("fx_fire_smoke");
                if (smoke != null) smoke.limit = 0;
                EffectAsset whiteTrail = AssetManager.effects_library.get(MusketAssets.TrailEffectId);
                if (whiteTrail != null) whiteTrail.limit = 3000;

                EffectAsset hit = AssetManager.effects_library.get("fx_hit");
                if (hit != null) hit.limit = 400;          // 原 20

                ModClass.LogInfo("LineInfantry 已提高弹道/命中特效上限");
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 调整特效上限失败: " + e.Message);
            }
        }

        static void depressVanillaWeaponValues()
        {
            int lowered = 0;
            try
            {
                foreach (ItemAsset item in AssetManager.items.list)
                {
                    if (item == null) continue;
                    if (item.id != null && item.id.StartsWith("li_")) continue;
                    if (item.equipment_type != EquipmentType.Weapon) continue;
                    if (item.equipment_subtype == "alien_blaster") continue; // 彩蛋神器保留
                    if (item.equipment_value <= 1) continue;
                    item.equipment_value = System.Math.Max(1, item.equipment_value / 10);
                    lowered++;
                }
                ModClass.LogInfo($"LineInfantry 已压低原版武器价值 x0.1（{lowered} 件）—— 火枪成为价值断层霸主");
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 压低原版武器价值失败: " + e.Message);
            }
        }
    }
}
