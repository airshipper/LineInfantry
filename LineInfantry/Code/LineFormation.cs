using System.Reflection;
using ai.behaviours;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// 线列阵型 + 纪律 v12。
    ///
    /// 【战线怎么来的】由战争态势预先算出、世界坐标里固定不动的一排格子（见 BattleLine /
    ///   BattleLines）。士兵只是按 ID 序号去站自己那一格。站位不随队长漂移，
    ///   走到就停住，队列天然整齐。
    ///
    /// 【v12：纪律】之前士兵会莫名其妙乱跑，永远集结不起来。日志和反编译定位到两个来源：
    ///   ① warrior_random_move / warrior_army_leader_move_random
    ///        → BehFindRandomTile：在附近区域随机挑一格走过去 ← 纯粹的乱走
    ///   ② 自动战斗任务 fighting
    ///        → BehFightCheckEnemyIsOk → BehGoToActorTarget：
    ///          只要挂上了攻击目标，就会走到"敌人所在的那一格" ← 明明拿着火枪
    ///          （射程 12~18）也要贴到脸上，于是整条线被敌人拖着走
    ///
    ///   两个钩子统一改成：有战线的在编士兵，落脚点一律是自己的战线格。
    ///   拿枪的就该站在线上开枪，而不是追着敌人跑。
    ///
    /// 【没有战线时一律交还原版】敌城守军被清空后战线消失，
    ///   此时全军恢复原版行为去占领城市，战争不会卡死。
    ///
    /// 注意：Actor.beh_tile_target 在真实程序集里是 internal，用缓存反射写入。
    /// </summary>
    public static class LineFormation
    {
        static readonly FieldInfo TileTargetField = typeof(Actor).GetField(
            "beh_tile_target",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static int logCount;

        /// <summary>目的地落在战线这个距离之后（沿 -dir）就判定为"往后方走"（格）</summary>
        const float BackwardTolerance = 8f;

        // —— 临时诊断计数（确认纪律生效后即可移除）——
        static int stCall, stOk, stNoLine, stBadTile, stIsland, stNoArmy, stHold, stWander, stBackward;

        public static string statLine()
        {
            return $"调用{stCall} 到位{stOk} 无战线{stNoLine} 无军团{stNoArmy} 无效格{stBadTile} 跨岛{stIsland} " +
                   $"追敌改列{stHold} 乱走改列{stWander} 后退改列{stBackward}";
        }

        public static void statReset()
        {
            stCall = stOk = stNoLine = stBadTile = stIsland = stNoArmy = stHold = stWander = stBackward = 0;
        }

        // ===================== 公共：把士兵按回战线 =====================

        /// <summary>
        /// 如果这个兵属于某条战线，就把"自己那一格"算出来。
        /// 返回 false 表示不归战线管（没有战线/不在编/还没到出兵门槛/落点无效），调用方应走原版。
        /// </summary>
        static bool tryHoldLine(Actor pActor, out WorldTile tile)
        {
            tile = null;
            if (TileTargetField == null) return false;
            if (!LinePatches.configOn("lineFormation")) return false;

            if (!pActor.hasArmy()) { stNoArmy++; return false; }
            Army army = pActor.army;
            if (army == null || !army.hasCaptain()) { stNoArmy++; return false; }

            City city = pActor.city;
            if (city == null) { stNoArmy++; return false; }

            // 军团还在城里集结、未达出兵门槛 → 交还原版，避免提前零散溜出城去填线
            if (army.isGroupInCityAndHaveLeader() && !city.isOkToSendArmy()) { stNoLine++; return false; }

            BattleLineSlot slot = BattleLines.get(city);
            if (slot == null) { stNoLine++; return false; }

            int rank, total;
            BattleLines.countRank(pActor, army, out rank, out total);
            if (total <= 1) return false;

            // 把 [0, total) 的位次按比例铺满本城占的那一段。
            // 【为什么不用 rank 直接乘间距】编制大于这段的格数时，rank 会溢出到线段外，
            // 多出来的兵全被夹到同一格堆成一坨。按比例铺就永远落在自己那一段里。
            float k = total > 1 ? (float)rank / (total - 1) : 0f;
            float off = slot.start + k * Mathf.Max(0, slot.count - 1) * BattleLines.Spacing;
            off = Mathf.Clamp(off, -slot.line.halfLen, slot.line.halfLen);

            Vector2 pos = slot.line.center + slot.line.axis * off;
            tile = World.world.GetTile(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y));
            if (tile == null || tile.is_liquid) { stBadTile++; tile = null; return false; }
            if (!tile.isSameIsland(pActor.current_tile)) { stIsland++; tile = null; return false; }

            stOk++;
            return true;
        }

        // ===================== ① 跟随队长 → 站到自己那一格 =====================

        public static bool findTile_Prefix(Actor pActor, ref BehResult __result)
        {
            try
            {
                stCall++;
                WorldTile tile;
                if (!tryHoldLine(pActor, out tile)) return true;

                TileTargetField.SetValue(pActor, tile);
                __result = BehResult.Continue;

                if (logCount++ < 3)
                    ModClass.LogInfo("LineInfantry 线列阵型生效: 士兵就位战线");
                return false;
            }
            catch (System.Exception e)
            {
                if (logCount++ < 5)
                    ModClass.LogWarning("LineInfantry 线列阵型异常: " + e.Message);
                return true;
            }
        }

        // ===================== ② 乱走（随机找格）→ 站到自己那一格 =====================

        /// <summary>
        /// warrior_random_move / warrior_army_leader_move_random 都从 BehFindRandomTile 开始 ——
        /// 在附近区域随机挑一格走过去。在编士兵一律改成走回自己的战线格。
        /// </summary>
        public static bool findRandomTile_Prefix(Actor pActor, ref BehResult __result)
        {
            try
            {
                WorldTile tile;
                if (!tryHoldLine(pActor, out tile)) return true;   // 不归战线管 → 原版随机走

                TileTargetField.SetValue(pActor, tile);
                __result = BehResult.Continue;
                stWander++;
                return false;
            }
            catch
            {
                return true;
            }
        }

        // ===================== ③ 追敌（自动战斗）→ 守在线上打 =====================

        /// <summary>
        /// 自动战斗任务 fighting = BehFightCheckEnemyIsOk + BehGoToActorTarget：
        /// 只要挂上攻击目标就会走到"敌人那一格"。拿着火枪（射程 12~18）的兵不该贴脸，
        /// 否则整条线会被敌人一路拖着走 —— 这就是"莫名其妙乱跑、永远集结不起来"的主因。
        /// 这里改成：在编士兵一律走回自己的战线格（射击本身是自动的，不受影响）。
        /// </summary>
        public static bool goToActorTarget_Prefix(Actor pActor, ref BehResult __result)
        {
            try
            {
                WorldTile tile;
                if (!tryHoldLine(pActor, out tile)) return true;   // 不归战线管 → 原版追敌

                TileTargetField.SetValue(pActor, tile);
                pActor.goTo(tile);
                __result = BehResult.Continue;
                stHold++;
                return false;
            }
            catch
            {
                return true;
            }
        }

        // ===================== ④ 兜底：不许往战线后方走 =====================

        /// <summary>
        /// 兜底纪律。BehGoToTileTarget 是所有"走到某个格子"任务的共同执行环节，
        /// 前面三个钩子覆盖不到的路径（运输、训练木桩、危险区巡查等）都会从这里过。
        /// 在编士兵只要目的地明显落在战线【后方】，就把目标格换成自己那一格。
        ///
        /// 注意这里只改 beh_tile_target 后返回 true，让原版 BehGoToTileTarget
        /// 照常用（改过的）目标格继续走 —— 保留它自身的空值检查等逻辑。
        /// </summary>
        public static bool goToTileTarget_Prefix(Actor pActor)
        {
            try
            {
                if (TileTargetField == null) return true;
                if (!LinePatches.configOn("lineFormation")) return true;

                WorldTile dest = TileTargetField.GetValue(pActor) as WorldTile;
                if (dest == null) return true;

                WorldTile slot;
                if (!tryHoldLine(pActor, out slot)) return true;

                BattleLineSlot s = BattleLines.get(pActor.city);
                if (s == null) return true;

                // dir 指向敌城：目的地在战线后方超过 BackwardTolerance 格就去拦
                Vector2 d = new Vector2(dest.x, dest.y) - s.line.center;
                if (Vector2.Dot(d, s.line.dir) >= -BackwardTolerance) return true;

                TileTargetField.SetValue(pActor, slot);
                stBackward++;
                return true;
            }
            catch
            {
                return true;
            }
        }

        // ===================== ⑤ 队长：站到战线中心 =====================

        /// <summary>
        /// 原版 BehCityActorCheckAttack 把队长随机扔进敌城区域的某一格（散兵游勇）。
        /// 这里改为：只要敌城还有守军，队长就站到自己那段战线上的格子里；
        /// 守军清空后返回 true 交还原版，正常进城占领。
        /// </summary>
        public static bool checkAttack_Prefix(Actor pActor)
        {
            try
            {
                if (TileTargetField == null) return true;
                if (!LinePatches.configOn("lineFormation")) return true;

                if (!pActor.hasArmy()) return true;
                Army army = pActor.army;
                if (army == null || !army.hasCaptain()) return true;
                if (army.captain != pActor) return true;             // 只接管队长

                City city = pActor.city;
                if (city == null) return true;

                // 与原版一致：还在城里集结且未达出兵门槛 → 交还原版（原版会 Stop）
                if (army.isGroupInCityAndHaveLeader() && !city.isOkToSendArmy()) return true;

                BattleLineSlot slot = BattleLines.get(city);
                if (slot == null) return true;                       // 没有战线 → 原版

                // 敌城还有成建制守军 → 列阵对射；守军清空 → 交还原版进城占领
                City target = null;
                try { target = KingdomFocus.getTargetAttackZone(city)?.city; }
                catch { }
                if (target == null) return true;
                if (!target.hasArmy() || target.army == null) return true;
                if (target.army.countUnits() <= 0) return true;

                int rank, total;
                BattleLines.countRank(pActor, army, out rank, out total);
                if (total <= 0) return true;

                float k = total > 1 ? (float)rank / (total - 1) : 0f;
                float off = slot.start + k * Mathf.Max(0, slot.count - 1) * BattleLines.Spacing;
                off = Mathf.Clamp(off, -slot.line.halfLen, slot.line.halfLen);
                Vector2 pos = slot.line.center + slot.line.axis * off;

                WorldTile tile = World.world.GetTile(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y));
                if (tile == null || tile.is_liquid) return true;
                if (!tile.isSameIsland(pActor.current_tile)) return true;

                TileTargetField.SetValue(pActor, tile);

                if (logCount++ < 3)
                    ModClass.LogInfo("LineInfantry 战争线列生效: 队长站上战线");
                return false;   // 跳过原版随机落点；__result 保持默认 Continue
            }
            catch (System.Exception e)
            {
                if (logCount++ < 5)
                    ModClass.LogWarning("LineInfantry 战争线列异常: " + e.Message);
                return true;
            }
        }
    }
}
