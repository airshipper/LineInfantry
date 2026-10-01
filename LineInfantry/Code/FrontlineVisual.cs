using System.Reflection;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// 战列线可视化 v8 —— 直接把"预制战线"画出来。
    ///
    /// 【与 v3~v7 的根本区别：不再做几何检测】
    ///   v7 的实测结论：绘线=1 上帧精灵=2 —— 绘制管线完全正常（精灵确实被创建并启用），
    ///   但候选段全是垃圾（n=31 跨31.4 偏59.6 / n=42 跨48.8 偏85.3 / n=45 在城内）。
    ///   也就是说："在士兵实际位置里找直线"这条路本质是在噪声里捞针。
    ///
    ///   v8 反过来：战线由战争态势预先算好（BattleLines），绘制只是把它画出来，
    ///   **没有任何判定阈值，必然可见**。士兵则被派去填这条线（LineFormation），
    ///   两边共用同一份数据，所以"画出来的线"和"士兵站的位置"天然一致。
    ///
    /// 【画法】王国颜色，主线 + 朝敌方一侧的齿标（经典前线符号）。
    /// 【显示时机】只在地图视角（QualityChanger.isLowRes()）淡入显示。
    /// </summary>
    public static class FrontlineVisual
    {
        public const string LineAssetId = "li_frontline_line";

        const float MaxAlpha = 0.9f;         // 要明显

        // —— 军事地图齿标 ——
        // 主线与齿标共用图层参数，齿的粗细 = 主线粗细 ≈ line_height × 缩放 ≈ 4.8 格，
        // 所以齿长必须明显大于 4.8 才像"倒刺"而不是方块。
        const float ToothEvery = 8f;
        const float ToothLength = 8f;

        static MethodInfo _drawLine;
        static bool _drawResolved;
        static Sprite _barSprite;
        static float alpha;

        // —— 诊断输出开关 ——
        // 开发时改成 true：每 6 秒往日志里打一行战线状态（战线数/绘线数/城中挂机人数/各段规模）。
        // 发布版保持 false，否则会持续污染玩家日志。
        // 用 static readonly 而不是 const：const false 会让编译器把下面的代码判成"不可达"并报警告。
        public static readonly bool DebugDiagnostics = false;

        // —— 诊断计时 ——
        static float diagTimer;
        static int diagCount;

        public static void register()
        {
            if (AssetManager.quantum_sprites.get(LineAssetId) != null) return;
            try
            {
                byte[] bytes = loadModFile("Assets/frontline_bar.png");
                if (bytes != null)
                {
                    var tex = new Texture2D(0, 0);
                    tex.filterMode = FilterMode.Point;
                    tex.LoadImage(bytes);
                    _barSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), 1f, 0u, SpriteMeshType.FullRect, new Vector4(6, 6, 6, 6));
                    _barSprite.name = "li_frontline_bar";
                }

                // 参数对齐原版 arrows_army_targets（base_scale 0.3）。
                // 地图视角下 num2 = base_scale × clamp(ortho/30, 1, 8) ≈ 2.4，
                // 因此 line_height=2 → 约 4.8 格的清晰细线（不是色块）。
                // line_width 必须为 0：齿标只有 8 格长，被剔除阈值挡住就什么都看不见。
                var asset = new QuantumSpriteAsset
                {
                    id = LineAssetId,
                    id_prefab = "p_mapArrow_stroke",
                    base_scale = 0.3f,
                    line_height = 2,
                    line_width = 0,
                    render_arrow_end = false,
                    render_arrow_start = false,
                    arrow_animation = false,
                    render_on_map = true,
                    render_in_game = true,
                    draw_call = drawFrontline,
                    create_object = delegate(QuantumSpriteAsset _, QuantumSprite q)
                    {
                        if (_barSprite != null && q.spriteArrowMiddle != null)
                            q.spriteArrowMiddle.sprite = _barSprite;
                    }
                };
                AssetManager.quantum_sprites.add(asset);
                ensureGroupSystem(asset);
                ModClass.LogInfo("LineInfantry 已注册战列线图层（预制战线，王国颜色）");
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 注册战列线图层失败: " + e.Message);
            }
        }

        static void ensureGroupSystem(QuantumSpriteAsset asset)
        {
            try
            {
                var initiatedField = typeof(QuantumSpriteManager).GetField(
                    "_initiated", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                bool initiated = initiatedField != null && (bool)initiatedField.GetValue(null);
                if (!initiated) return;
                if (asset.group_system != null) return;

                var go = new GameObject();
                var gs = go.AddComponent<QuantumSpriteGroupSystem>();
                gs.create(asset);
                asset.group_system = gs;
                ModClass.LogInfo("LineInfantry 已为战列线图层补建绘制组");
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 补建绘制组失败: " + e.Message);
            }
        }

        static byte[] loadModFile(string relativePath)
        {
            try
            {
                string folder = ModClass.I.GetDeclaration().FolderPath;
                string path = System.IO.Path.Combine(folder, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }

        static void resolveDraw()
        {
            if (_drawResolved) return;
            _drawResolved = true;
            _drawLine = typeof(QuantumSpriteLibrary).GetMethod(
                "drawArrowQuantumSprite",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(QuantumSpriteAsset), typeof(Vector3), typeof(Vector3),
                        typeof(Color).MakeByRefType(), typeof(City) }, null);
            if (_drawLine == null)
                ModClass.LogWarning("LineInfantry 未找到 drawArrowQuantumSprite");
        }

        static void drawSeg(QuantumSpriteAsset pAsset, Vector2 a, Vector2 b, Color color)
        {
            if ((a - b).sqrMagnitude < 0.0004f) return;
            object[] args = { pAsset,
                new Vector3(a.x, a.y, 0f),
                new Vector3(b.x, b.y, 0f),
                color, null };
            _drawLine.Invoke(null, args);
        }

        // ================= 绘制 =================

        static void drawFrontline(QuantumSpriteAsset pAsset)
        {
            // 战线数据由阵型补丁共用，所以无论当前是不是地图视角都要保持最新
            BattleLines.refresh();

            if (!LinePatches.configOn("frontlineVisual")) { alpha = 0f; return; }
            resolveDraw();
            if (_drawLine == null) return;

            // —— 只在地图视角显示 ——
            bool mapMode;
            try { mapMode = World.world.quality_changer.isLowRes(); }
            catch { return; }

            float target = mapMode ? MaxAlpha : 0f;
            alpha = Mathf.MoveTowards(alpha, target, Time.deltaTime * 3f);
            if (alpha <= 0.01f) return;

            try
            {
                int drawn = 0, teeth = 0;

                foreach (BattleLine line in BattleLines.all)
                {
                    if (line == null) continue;

                    Color c = new Color(line.color.r, line.color.g, line.color.b, alpha);
                    float half = line.halfLen;

                    // 主线：横队轴两端
                    Vector2 p0 = line.center - line.axis * half;
                    Vector2 p1 = line.center + line.axis * half;
                    drawSeg(pAsset, p0, p1, c);
                    drawn++;

                    // 齿标：垂直于主线、指向敌方
                    for (float t = -half + ToothEvery * 0.5f; t < half; t += ToothEvery)
                    {
                        Vector2 q0 = line.center + line.axis * t;
                        drawSeg(pAsset, q0, q0 + line.dir * ToothLength, c);
                        teeth++;
                    }
                }

                // —— 诊断输出（仅在 DebugDiagnostics = true 时）——
                if (!DebugDiagnostics) return;

                diagTimer += Time.deltaTime;
                if (diagTimer > 6f && diagCount < 200)
                {
                    diagTimer = 0f;
                    diagCount++;
                    string col = LineFormation.statLine();
                    LineFormation.statReset();
                    // 上帧精灵 = 上一帧真正被启用的量子精灵数（group_system.update 统计）。
                    // 绘线>0 且 上帧精灵>0 却仍看不见 → 精灵确实存在，问题在缩放/层级/颜色。
                    int live = -1;
                    try { live = pAsset.group_system.count_active; } catch { }

                    // 每条战线的规模：合并了同方向几支军团、多少人、多长
                    var sb = new System.Text.StringBuilder();
                    foreach (BattleLine l in BattleLines.all)
                    {
                        if (sb.Length > 180) break;
                        sb.Append($"[并{l.memberCount}支/{l.totalSlots}人/长{l.halfLen * 2f:F0}/集{l.staging:F0}/进{l.progress * 100f:F0}%]");
                    }

                    ModClass.LogInfo(
                        $"LineInfantry 战线诊断#{diagCount}: 战线数={BattleLines.count} 绘线={drawn} 齿标={teeth} " +
                        $"上帧精灵={live} 城中挂机={BattleLines.musteringWarriors}人 列阵[{col}] 战线={sb}");
                }
            }
            catch
            {
                // 每帧绘制，异常静默
            }
        }
    }
}
