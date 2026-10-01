using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// 弹道烟迹加密（按"距离"而非"帧"投放特效点）。
    ///
    /// 原版 Projectile.updateTrailEffect 每帧最多生成 1 个拖尾特效，
    /// 因此点间距被硬性限制为「弹速 × 帧间隔」（50 格/秒 × 1/60 秒 ≈ 0.83 格），
    /// 弹速越快点越稀，无法再密。
    ///
    /// 本补丁改为：记录弹丸上一帧的位置，在本帧走过的线段上
    /// 按固定间距（TrailSpacing）均匀补插多个特效点。
    /// 于是点间距与弹速、帧率都无关，恒定为一格内 3 个以上的点，
    /// 白点之间首尾相接，形成笔直连续的烟迹。
    ///
    /// 注意：Projectile._current_position_3d 是 private 字段，用缓存反射读取。
    /// </summary>
    public static class TrailDensity
    {
        /// <summary>特效点之间的间距（格）。越小线条越连续，但同行特效数量成倍增加</summary>
        const float TrailSpacing = 0.3f;

        /// <summary>单帧最多补插的点数，避免超高速弹丸瞬间产生海量特效</summary>
        const int MaxPointsPerFrame = 6;

        static readonly FieldInfo PosField = typeof(Projectile).GetField(
            "_current_position_3d",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static readonly Dictionary<Projectile, Vector3> lastPositions = new Dictionary<Projectile, Vector3>();

        public static bool updateTrailEffect_Prefix(Projectile __instance)
        {
            try
            {
                if (PosField == null) return true;

                ProjectileAsset asset = __instance.asset;
                if (asset == null || !asset.trail_effect_enabled) return true;

                Vector3 cur = (Vector3)PosField.GetValue(__instance);
                Vector3 prev;
                if (!lastPositions.TryGetValue(__instance, out prev))
                {
                    lastPositions[__instance] = cur;   // 首帧只记录起点
                    return false;
                }
                lastPositions[__instance] = cur;

                float dist = Vector3.Distance(prev, cur);
                if (dist < 0.01f) return false;

                int points = Mathf.Clamp(Mathf.CeilToInt(dist / TrailSpacing), 1, MaxPointsPerFrame);
                for (int i = 1; i <= points; i++)
                {
                    Vector3 p = Vector3.Lerp(prev, cur, (float)i / points);
                    BaseEffect fx = EffectsLibrary.spawnAt(
                        asset.trail_effect_id,
                        new Vector3(p.x, p.y + p.z, 0f),
                        asset.trail_effect_scale);
                    if (fx != null && asset.look_at_target)
                        fx.transform.rotation = __instance.rotation;
                }

                // 弹丸对象是池化复用的，字典不会无限增长；留一道保险
                if (lastPositions.Count > 4000) lastPositions.Clear();
                return false;   // 跳过原版单点生成
            }
            catch
            {
                return true;    // 出任何问题都退回原版，绝不打断弹丸更新
            }
        }
    }
}
