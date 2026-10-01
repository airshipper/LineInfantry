using System.Collections.Generic;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// 一条"预制战线"（一条 = 一个直线段）：世界空间里固定不动的横队基线。
    /// 先由战争态势算出战线在哪、画出来，再让士兵按序号去"填"。
    /// </summary>
    public class BattleLine
    {
        /// <summary>线段中心（世界坐标，已取整格）</summary>
        public Vector2 center;

        /// <summary>正面朝向（指向敌城），齿标朝这一侧</summary>
        public Vector2 dir;

        /// <summary>横队轴（垂直于 dir），士兵沿它排开</summary>
        public Vector2 axis;

        /// <summary>半长（格）</summary>
        public float halfLen = 4f;

        /// <summary>王国颜色</summary>
        public Color color = Color.white;

        /// <summary>这一段拼了几支军团的部队</summary>
        public int memberCount;

        /// <summary>这一段的士兵总数</summary>
        public int totalSlots;

        /// <summary>推进程度：0 = 缩在国境线内深处集结，1 = 压到敌城下</summary>
        public float progress;

        /// <summary>集结位（缩到最深处）离最终战线多远（格）</summary>
        public float staging;
    }

    /// <summary>某座城市在它所属线段上的区段：它的士兵从 start 起依次向右排</summary>
    public class BattleLineSlot
    {
        public BattleLine line;
        public float start;
        public int count;
    }

    /// <summary>
    /// 战线注册表：每帧（由量子精灵 draw_call 驱动）限频重建，
    /// 供【绘制】和【士兵列阵】共用同一份数据 —— 两者天然一致。
    ///
    /// 【v12：兵力决定战线深度】
    ///   · 集结位在【本方国境线内更深处】（borderDistance 再往里 DeepInset 格），
    ///     所以部队是真的在自己家里列阵，不是在边界线上晾着。
    ///   · 战线推到哪一格由【兵力】决定：
    ///       人少 → 缩在深处集结（progress → 0）
    ///       人多 → 一路压到敌城下（progress → 1）
    ///     progress 平滑地朝这个目标移动：往前推受"到位率"限制（不能跑得比人快），
    ///     往后撤不受限制（打残了就该赶紧退）。
    ///   · 一个人都没到位时（到达率为零）→ 目标直接设为"缩到最深处"，先往后退。
    ///   · 一个战略方向最多出 1~2 条线：角度跨度不大就一条，跨度过大才在最大空档处断成两条。
    /// </summary>
    public static class BattleLines
    {
        /// <summary>战线距敌城区域中心的初始距离（格）；落在城内会逐步外推</summary>
        public const float Standoff = 6f;

        /// <summary>相邻士兵间距（格）</summary>
        public const float Spacing = 1.1f;

        /// <summary>重建间隔（秒）</summary>
        const float RefreshInterval = 0.4f;

        /// <summary>单座城市在一段战线上最多占多少格</summary>
        const int MaxSlotsPerCity = 20;

        /// <summary>单段最长长度（格）。只是防爆上限，真实长度由正面宽度决定。</summary>
        const float MaxSegmentLen = 120f;

        /// <summary>
        /// 一个方向最多出 1~2 条线：方位角总跨度不超过这个值（约 100°）就只用一条线。
        /// 超过才在最大空档处断成两条。
        /// </summary>
        const float SingleLineArc = 1.75f;

        /// <summary>方向一致性阈值：同一目标的军团朝向太发散（被包围）时不合并</summary>
        const float MergeAngle = 0.35f;

        /// <summary>往前推进的速度（格/秒）</summary>
        const float AdvanceTilesPerSecond = 4.5f;

        /// <summary>往后撤退的速度（格/秒）。撤退不受到位率限制 —— 打残了就该赶紧退。</summary>
        const float RetreatTilesPerSecond = 8f;

        /// <summary>判断"士兵已到位"的半径（格）</summary>
        const float InPlaceRadius = 10f;

        /// <summary>到位率超过这个值才算"站上了"，低于它就不往前推</summary>
        const float FillGateMin = 0.10f;

        /// <summary>到位率到这里就全速推进</summary>
        const float FillFull = 0.60f;

        /// <summary>集结距离上限（格）</summary>
        const float MaxStaging = 60f;

        /// <summary>集结位再往国境线【内侧】多退的深度（格）—— 让部队在自己家里列阵</summary>
        const float DeepInset = 18f;

        /// <summary>兵力低于这个数就把战线缩到最深处</summary>
        const float MinForce = 6f;

        /// <summary>兵力到这个数就一路压到敌城下</summary>
        const float FullForce = 40f;

        /// <summary>战线半径上限（格）。火枪射程只有 12~18 格，推出去双方就够不着了。</summary>
        const float MaxFrontRadius = 16f;

        /// <summary>战线最多允许落到各军团后面多远（格）</summary>
        const float MaxLag = 10f;

        /// <summary>国境线探测的最远距离（格）</summary>
        const float MaxBorderScan = 160f;

        static readonly Dictionary<City, BattleLineSlot> map = new Dictionary<City, BattleLineSlot>();
        static readonly List<BattleLine> lines = new List<BattleLine>();
        static Dictionary<City, FrontProgress> progressMap = new Dictionary<City, FrontProgress>();
        static float timer = 999f;
        static float lastElapsed = RefreshInterval;

        /// <summary>上一轮因"还在城里集结"被排除在战线外的战士总数（诊断用）</summary>
        static int dMustering;
        public static int musteringWarriors { get { return dMustering; } }

        /// <summary>跨帧保留的推进状态（按进攻目标城市）</summary>
        class FrontProgress
        {
            public Kingdom kingdom;
            public float progress;
            public float staging = -1f;          // 国境线内深处的集结距离，<0 表示还没测过
            public float nextBorderCheck;
        }

        public static BattleLineSlot get(City city)
        {
            if (city == null) return null;
            BattleLineSlot slot;
            return map.TryGetValue(city, out slot) ? slot : null;
        }

        public static List<BattleLine> all { get { return lines; } }
        public static int count { get { return lines.Count; } }

        /// <summary>军团内的战士位次（按 ID，稳定）</summary>
        public static void countRank(Actor pActor, Army army, out int rank, out int total)
        {
            rank = 0;
            total = 0;
            long myId = pActor.getID();
            foreach (Actor unit in army.units)
            {
                if (unit == null || !unit.isAlive() || !unit.is_profession_warrior) continue;
                total++;
                if (unit.getID() < myId) rank++;
            }
        }

        public static int countWarriors(Army army)
        {
            int n = 0;
            foreach (Actor unit in army.units)
                if (unit != null && unit.isAlive() && unit.is_profession_warrior) n++;
            return n;
        }

        // ================= 内部结构 =================

        class Member
        {
            public City city;
            public Vector2 enemy;       // 敌城区域中心
            public Vector2 dir;         // 军团位置 -> 敌城
            public Vector2 pos;         // 军团当前位置（队长位置近似）
            public int warriors;
            public float half;          // 该部队占线的半宽
            public float ang;           // 相对敌城的方位角
        }

        static Vector2 perp(Vector2 v) { return new Vector2(-v.y, v.x); }
        static Vector2 radial(float a) { return new Vector2(Mathf.Cos(a), Mathf.Sin(a)); }

        public static void refresh()
        {
            timer += Time.deltaTime;
            if (timer < RefreshInterval) return;
            lastElapsed = timer;
            timer = 0f;

            map.Clear();
            lines.Clear();
            dMustering = 0;
            var nextProgress = new Dictionary<City, FrontProgress>();
            try
            {
                if (World.world == null) return;

                foreach (Kingdom kingdom in World.world.kingdoms)
                {
                    if (kingdom == null || !kingdom.isAlive()) continue;
                    if (!kingdom.hasEnemies()) continue;                        // 处于战争

                    // —— 1) 收集本王国所有"已下进攻令 + 有军团"的城市 ——
                    var byTarget = new Dictionary<City, List<Member>>();
                    foreach (City city in kingdom.cities)
                    {
                        if (city == null || !city.isAlive()) continue;
                        if (!city.hasAttackZoneOrder()) continue;
                        if (!city.hasArmy() || city.army == null) continue;
                        if (!city.army.hasCaptain()) continue;

                        TileZone zone = KingdomFocus.getTargetAttackZone(city);
                        if (zone == null || zone.centerTile == null) continue;
                        City target = zone.city;
                        if (target == null) continue;

                        // 还在城里集结、未达出兵门槛的军团不划线：
                        // 这种军团整队人留在城里，线画出来就是空挂着的，到位率永远为 0。
                        if (city.army.isGroupInCityAndHaveLeader() && !city.isOkToSendArmy())
                        {
                            dMustering += countWarriors(city.army);
                            continue;
                        }

                        int warriors = countWarriors(city.army);
                        if (warriors < 2) continue;

                        Actor cap = city.army.captain;
                        if (cap == null) continue;

                        Vector2 enemy = new Vector2(zone.centerTile.x, zone.centerTile.y);
                        Vector2 pos = cap.current_position;

                        // 朝向取"军团自己所在位置 -> 敌城"：同一片前线的军团主城在四面八方，
                        // 用主城当参照会炸成一朵放射状短线花。
                        Vector2 d = enemy - pos;
                        if (d.sqrMagnitude < 4f) continue;

                        List<Member> list;
                        if (!byTarget.TryGetValue(target, out list))
                        {
                            list = new List<Member>();
                            byTarget[target] = list;
                        }
                        int slots = Mathf.Min(warriors, MaxSlotsPerCity);
                        list.Add(new Member
                        {
                            city = city,
                            enemy = enemy,
                            dir = d.normalized,
                            pos = pos,
                            warriors = slots,
                            half = (slots - 1) * 0.5f * Spacing
                        });
                    }

                    // —— 2) 按目标城市分组 = 一个战略方向 ——
                    foreach (KeyValuePair<City, List<Member>> kv in byTarget)
                    {
                        try
                        {
                            FrontProgress st;
                            if (!progressMap.TryGetValue(kv.Key, out st) || st.kingdom != kingdom)
                                st = new FrontProgress { kingdom = kingdom, progress = 0f };
                            nextProgress[kv.Key] = st;
                            buildFront(kingdom, kv.Key, kv.Value, st);
                        }
                        catch
                        {
                            // 单个战略方向出错不能拖垮其他方向
                        }
                    }
                }
            }
            catch
            {
                // 世界正在重建/销毁，忽略
            }
            progressMap = nextProgress;
        }

        /// <summary>为一个战略方向（一个进攻目标）构建战线，并按兵力决定它该推多深</summary>
        static void buildFront(Kingdom kingdom, City target, List<Member> group, FrontProgress st)
        {
            Vector2 enemySum = Vector2.zero;
            Vector2 sumDir = Vector2.zero;
            int sumSlots = 0;
            foreach (Member m in group)
            {
                enemySum += m.enemy;
                sumDir += m.dir;
                sumSlots += m.warriors;
            }
            Vector2 enemyC = enemySum / group.Count;

            // 敌城已无可战之兵 → 进入强攻阶段，不再画战线
            if (!target.hasArmy() || target.army == null || target.army.countUnits() <= 0) return;

            bool divergent = group.Count > 1 && sumDir.magnitude < MergeAngle * group.Count;
            Vector2 avgDir = divergent ? Vector2.zero : (group.Count == 1 ? group[0].dir : sumDir.normalized);
            Vector2 probeDir = divergent ? group[0].dir : avgDir;

            // —— 最终战线半径 R：贴到敌城外，且放得下全部正面宽度 ——
            float R = Standoff;
            WorldTile bt = null;
            int guard = 0;
            while (guard++ < 90)
            {
                Vector2 p = enemyC - probeDir * R;
                bt = World.world.GetTile(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                if (bt == null) break;
                if (bt.zone == null || bt.zone.city != target) break;
                R += 1f;
            }
            if (bt == null || bt.is_liquid) return;

            float frontage = sumSlots * Spacing;
            R = Mathf.Max(R, Mathf.Min(frontage / 6.2832f * 1.25f, MaxFrontRadius));

            // —— 集结位：本方国境线【再往内 DeepInset 格】——
            if (st.staging < 0f || Time.time > st.nextBorderCheck)
            {
                st.nextBorderCheck = Time.time + 5f;
                float sum = 0f;
                int n = 0;
                foreach (Member m in group)
                {
                    float rb = borderDistance(kingdom, m);
                    if (rb >= 0f) { sum += rb; n++; }
                }
                st.staging = n > 0
                    ? Mathf.Clamp(sum / n - R + DeepInset, 0f, MaxStaging)
                    : Mathf.Clamp(DeepInset, 0f, MaxStaging);
            }
            float staging = st.staging;

            // —— 兵力决定战线该推多深 ——
            // 人少 → 缩在深处集结(0)；人多 → 一路压到敌城下(1)
            float depthFactor = Mathf.Clamp01((sumSlots - MinForce) / Mathf.Max(1f, FullForce - MinForce));

            // —— 不许落到各军团后面（取到敌城距离的中位数）——
            var dists = new List<float>(group.Count);
            foreach (Member m in group) dists.Add((m.pos - enemyC).magnitude);
            dists.Sort();
            float dMed = dists[dists.Count / 2];
            float maxOffset = Mathf.Max(0f, dMed - R + MaxLag);

            // —— 到达率为零 → 不管兵力多少，先缩回最深处 ——
            float curOffset = Mathf.Min((1f - st.progress) * staging, maxOffset);
            float lineDepth = R + curOffset;
            int predIn = 0;
            foreach (Member m in group)
                if (Mathf.Abs((m.pos - enemyC).magnitude - lineDepth) <= InPlaceRadius) predIn += m.warriors;
            bool nobodyThere = predIn <= 0;

            float targetP = nobodyThere ? 0f : depthFactor;
            float offset = Mathf.Min((1f - st.progress) * staging, maxOffset);

            // —— 各军团相对敌城的方位角 ——
            foreach (Member m in group)
                m.ang = Mathf.Atan2(m.pos.y - enemyC.y, m.pos.x - enemyC.x);
            group.Sort((a, b) => a.ang.CompareTo(b.ang));

            // 从"最大间隙"处断开作为起点（角度是 -π..π，可能跨越边界）
            int startIdx = 0;
            float bestGap = -1f;
            for (int k = 0; k < group.Count; k++)
            {
                float a = group[k].ang;
                float b = (k + 1 < group.Count) ? group[k + 1].ang : group[0].ang + Mathf.PI * 2f;
                float gap = b - a;
                if (gap > bestGap) { bestGap = gap; startIdx = (k + 1) % group.Count; }
            }
            var ordered = new List<Member>(group.Count);
            for (int k = 0; k < group.Count; k++) ordered.Add(group[(startIdx + k) % group.Count]);
            for (int k = 1; k < ordered.Count; k++)
                while (ordered[k].ang < ordered[k - 1].ang) ordered[k].ang += Mathf.PI * 2f;

            Color col = kingdomColor(kingdom);
            Vector2 shift = -probeDir * offset;

            // —— 3) 一个战略方向最多出 1~2 条线 ——
            float span = ordered[ordered.Count - 1].ang - ordered[0].ang;
            if (span <= SingleLineArc || ordered.Count < 4)
            {
                buildArcSegment(ordered, 0, ordered.Count - 1, enemyC, R, col, target, shift, staging, st.progress);
            }
            else
            {
                // 在"除去首尾大空档之后"的内部最大间隙处断成两条
                int cut = -1;
                float cutGap = -1f;
                for (int k = 1; k + 1 < ordered.Count; k++)
                {
                    float g = ordered[k + 1].ang - ordered[k].ang;
                    if (g > cutGap) { cutGap = g; cut = k; }
                }
                if (cut < 0 || cut + 1 >= ordered.Count)
                {
                    buildArcSegment(ordered, 0, ordered.Count - 1, enemyC, R, col, target, shift, staging, st.progress);
                }
                else
                {
                    buildArcSegment(ordered, 0, cut, enemyC, R, col, target, shift, staging, st.progress);
                    buildArcSegment(ordered, cut + 1, ordered.Count - 1, enemyC, R, col, target, shift, staging, st.progress);
                }
            }

            // —— 4) 推进 / 后撤 ——
            {
                int inPlace = 0, totalW = 0;
                foreach (Member m in group)
                {
                    BattleLineSlot s = get(m.city);
                    if (s == null) continue;
                    totalW += m.warriors;

                    // 判据取"这支部队到线段的垂直距离"，比精确落点稳得多
                    Vector2 actual = armyCentroid(m.city);
                    Vector2 rel = actual - s.line.center;
                    float along = Mathf.Clamp(Vector2.Dot(rel, s.line.axis), -s.line.halfLen, s.line.halfLen);
                    Vector2 nearest = s.line.center + s.line.axis * along;
                    if ((actual - nearest).sqrMagnitude <= InPlaceRadius * InPlaceRadius)
                        inPlace += m.warriors;
                }
                float fill = totalW > 0 ? (float)inPlace / totalW : 0f;
                float gate = Mathf.Clamp01((fill - FillGateMin) / Mathf.Max(0.01f, FillFull - FillGateMin));

                float spanUnits = Mathf.Max(4f, staging);
                if (st.progress < targetP)
                {
                    // 往前推：受到位率限制，不能跑得比人快
                    st.progress = Mathf.Min(targetP,
                        st.progress + (AdvanceTilesPerSecond / spanUnits) * lastElapsed * gate);
                }
                else if (st.progress > targetP)
                {
                    // 往后撤：不受限制，打残了就该赶紧退
                    st.progress = Mathf.Max(targetP,
                        st.progress - (RetreatTilesPerSecond / spanUnits) * lastElapsed);
                }
            }
        }

        /// <summary>该军团战士的实际质心（判断"到位"用）</summary>
        static Vector2 armyCentroid(City city)
        {
            Vector2 sum = Vector2.zero;
            int n = 0;
            if (city.army != null)
            {
                foreach (Actor u in city.army.units)
                {
                    if (u == null || !u.isAlive() || !u.is_profession_warrior) continue;
                    sum += u.current_position;
                    n++;
                }
            }
            return n > 0 ? sum / n : Vector2.zero;
        }

        /// <summary>本方国境线到敌城区域中心的距离；测不到返回 -1</summary>
        static float borderDistance(Kingdom kingdom, Member m)
        {
            WorldTile home = m.city.getTile();
            if (home == null) return -1f;

            Vector2 homePos = new Vector2(home.x, home.y);
            Vector2 d = m.enemy - homePos;
            float dist = d.magnitude;
            if (dist < 4f) return -1f;
            Vector2 dir = d / dist;

            WorldTile last = home;
            float scan = Mathf.Min(dist, MaxBorderScan);
            for (float s = 1f; s < scan; s += 1f)
            {
                Vector2 p = homePos + dir * s;
                WorldTile t = World.world.GetTile(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                if (t == null || t.is_liquid) break;
                // 判定"还是本方领土"用 kingdom.cities 包含关系（public），
                // 不要碰 City.kingdom —— 它是 internal，直接读会抛 FieldAccessException。
                City zc = t.zone != null ? t.zone.city : null;
                if (zc == null || !kingdom.cities.Contains(zc)) break;
                last = t;
            }
            return Mathf.Max(0f, (m.enemy - new Vector2(last.x, last.y)).magnitude);
        }

        /// <summary>
        /// 把 ordered[i..j] 这几支军团做成一段：取该方位角区间在半径 R 圆上的弓弦，
        /// 朝向取该段弧中点的半径方向。相邻两段共用弧上端点 → 首尾相接、互不重叠。
        /// </summary>
        static void buildArcSegment(List<Member> ordered, int i, int j,
                                    Vector2 enemyC, float R, Color color, City target,
                                    Vector2 shift, float staging, float progress)
        {
            float a0 = ordered[i].ang;
            float a1 = ordered[j].ang;
            float aMid = (a0 + a1) * 0.5f;

            Vector2 segDir = -radial(aMid);                 // 面向敌城
            Vector2 axis = perp(segDir);
            Vector2 arcMid = enemyC + radial(aMid) * R + shift;

            WorldTile t = World.world.GetTile(Mathf.RoundToInt(arcMid.x), Mathf.RoundToInt(arcMid.y));
            if (t == null || t.is_liquid) return;

            var run = new List<Member>();
            for (int k = i; k <= j; k++) run.Add(ordered[k]);

            var offs = new float[run.Count];
            for (int k = 0; k < run.Count; k++)
            {
                Vector2 p = enemyC + radial(run[k].ang) * R;
                offs[k] = Vector2.Dot(p - arcMid, axis);
            }

            // 按落点排序并顺次码放，保证同一段内两支部队不重叠
            var idx = new int[run.Count];
            for (int k = 0; k < run.Count; k++) idx[k] = k;
            System.Array.Sort(idx, (a, b) => offs[a].CompareTo(offs[b]));

            float half = 4f;
            float prevEnd = float.NegativeInfinity;
            var starts = new float[run.Count];
            foreach (int k in idx)
            {
                float stt = Mathf.Max(offs[k] - run[k].half, prevEnd + Spacing);
                starts[k] = stt;
                prevEnd = stt + (run[k].warriors - 1) * Spacing;
                // 占用区间就是 [stt, prevEnd]，线的半长按它算，不多算以免画出空线
                half = Mathf.Max(half, Mathf.Abs(stt), Mathf.Abs(prevEnd));
            }
            half = Mathf.Min(half, MaxSegmentLen * 0.5f);

            var line = new BattleLine
            {
                center = new Vector2(t.x, t.y),
                dir = segDir,
                axis = axis,
                color = color,
                memberCount = run.Count,
                halfLen = half,
                progress = progress,
                staging = staging
            };

            int total = 0;
            for (int k = 0; k < run.Count; k++)
            {
                total += run[k].warriors;
                map[run[k].city] = new BattleLineSlot
                {
                    line = line,
                    start = Mathf.Clamp(starts[k], -half, half),
                    count = run[k].warriors
                };
            }
            line.totalSlots = total;
            lines.Add(line);
        }

        static Color kingdomColor(Kingdom kingdom)
        {
            try { return kingdom.getColor().getColor32Main(); }
            catch { return Color.white; }
        }
    }
}
