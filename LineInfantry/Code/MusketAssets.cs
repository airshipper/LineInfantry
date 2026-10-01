using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LineInfantry
{
    /// <summary>
    /// 火枪资产注册。
    ///
    /// 数值基准（游戏 build 653 实测）：
    ///   - 文明单位血量 = 100
    ///   - 原版弓：伤害 1~10、射程 6~12、命中 accuracy = 4
    ///   - 命中机制（CombatActionLibrary.attackRangeAction）：
    ///       散布半径 = min(距离 / accuracy × 0.25, 2)   格
    ///       方向系数 = (10 - accuracy) / 10             （accuracy ≥ 10 = 完全无散布）
    ///       最终偏移 = 方向系数 × 散布
    ///     小人本身体宽约 0.5 格，所以偏移超过 0.25 格就开始明显打空。
    ///     注意：装备属性是【相加】到单位属性上的（BaseStats.mergeStats 用的是 +=），
    ///     所以实际命中 = 这里的值 + 单位自身的基础值。
    ///
    /// 因此火枪定位（按用户要求）：
    ///   - 伤害远高于弓，接近一枪致死（65~120）
    ///   - 命中略低于弓但已能实战（accuracy 2.8~5.2，弓是 4）
    ///   - 射程更长（12~18，弓最长 12）
    ///   - 射速更慢（attack_speed 0.30~0.48，弓约 1.0）
    ///   - 弹道平直并带明显云迹拖尾
    ///
    /// 经济：T1/T2 只需普通资源，T3/T4 逐步掺少量银/金。
    ///
    /// 注意：不用 NML 的 ItemAssetCreator.CreateRangeWeapon —— 它内部从 "_range"
    /// 模板克隆，而本游戏模板叫 "$range"，会抛异常导致模组被禁用。
    /// 改用原版霰弹枪的创建方式：AssetManager.items.clone(id, "$range")。
    /// </summary>
    public static class MusketAssets
    {
        public const string Subtype = "musket";
        public const string ProjectileId = "li_musket_ball";

        public static readonly List<string> TierIds = new List<string>
        {
            "li_arquebus", "li_musket", "li_flintlock", "li_rifled"
        };

        class Tier
        {
            public string id;
            public float damage, range, accuracy, attackSpeed, crit;
            public int gold, res1, res2, value;
            public string res1Id, res2Id;
        }

        static readonly List<Tier> tiers = new List<Tier>
        {
            // ⚠ equipment_value 需远高于原版最高档(精金基础 70，叠词条后一般 < 150)。
            // 原因：City.giveItem 会让小人进城时随机拣仓库装备，只要 item.getValue() 更高就替换；
            // 火枪价值若低于 70，就会不断被仓库里的精金弓/剑换走（表现为"给了枪又被换掉"）。
            // 同时该值也决定锻造时是否算作"更好的装备"，高于 70 才能稳定压制原版武器。
            // T1 火绳钩枪 ~1450：入门火器。打得远但很歪，中一发基本致命
            new Tier{ id = "li_arquebus",  damage = 65f,  range = 12f, accuracy = 3.2f, attackSpeed = 0.40f, crit = 0.05f,
                      gold = 0, res1Id = "wood", res1 = 1, res2Id = "none", res2 = 0, value = 200 },
            // T2 重穆什克特 ~1550：口径最大、最接近"一枪一个"，但装填最慢、最不准
            new Tier{ id = "li_musket",    damage = 95f,  range = 13f, accuracy = 2.8f, attackSpeed = 0.30f, crit = 0.05f,
                      gold = 0, res1Id = "wood", res1 = 1, res2Id = "common_metals", res2 = 2, value = 280 },
            // T3 燧发枪 ~1610：击发机构让装填加快，命中略好，线列战术真正成型的一代
            new Tier{ id = "li_flintlock", damage = 80f,  range = 15f, accuracy = 3.8f, attackSpeed = 0.48f, crit = 0.08f,
                      gold = 1, res1Id = "common_metals", res1 = 2, res2Id = "silver", res2 = 1, value = 360 },
            // T4 米涅线膛枪 1849：膛线让射程与精度质变（相对而言），威力最盛
            new Tier{ id = "li_rifled",    damage = 120f, range = 18f, accuracy = 5.2f, attackSpeed = 0.42f, crit = 0.12f,
                      gold = 2, res1Id = "common_metals", res1 = 3, res2Id = "silver", res2 = 1, value = 480 },
        };

        /// <summary>自定义白色弹道特效的 id（用游戏内置的白色尘雾贴图，见 registerTrailEffect）</summary>
        public const string TrailEffectId = "li_white_trail";

        public static void init()
        {
            registerTrailEffect();
            registerProjectile();
            registerItems();
            registerSprites();
        }

        /// <summary>
        /// 注册"白色破空尘雾"弹道特效。
        ///
        /// 为什么这么做：特效贴图只能从游戏内置资源加载（Resources.LoadAll），
        /// 而现有特效里没有白色烟雾贴图。于是改用游戏自带的白色尘雾贴图
        /// drops/drop_dust_white，配 use_basic_prefab（贴图型特效）。
        ///
        /// 关键差别：贴图型特效不会像烟雾粒子那样飘散，
        /// 而且弹道系统会把拖尾特效"旋转到弹丸方向"，
        /// 因此密集生成时会沿弹道叠成一条笔直的白色条带。
        ///
        /// 持续时间：特效存活时长 = 帧数 × time_between_frames，
        /// 把 time_between_frames 调大即可让每团尘雾停留更久。
        /// </summary>
        static void registerTrailEffect()
        {
            if (AssetManager.effects_library.get(TrailEffectId) != null) return;
            try
            {
                AssetManager.effects_library.add(new EffectAsset
                {
                    id = TrailEffectId,
                    use_basic_prefab = true,               // 贴图型（不用粒子 prefab，不会飘散）
                    sorting_layer_id = "EffectsTop",       // 与等离子拖尾同层，压在单位上方
                    sprite_path = "drops/drop_dust_white", // 游戏内置白色尘雾贴图
                    time_between_frames = 0.25f,           // 每帧停留时间（点为按距离投放，需控制同行总量）
                    limit = 3000,                          // 按距离加密后点更密，上限相应抬高
                    show_on_mini_map = true,
                    draw_light_area = false                // 白色烟雾不该发光
                });
                ModClass.LogInfo("LineInfantry 已注册白色弹道特效: " + TrailEffectId);
            }
            catch (System.Exception e)
            {
                ModClass.LogWarning("LineInfantry 注册白色弹道特效失败: " + e.Message);
            }
        }

        static void registerProjectile()
        {
            if (AssetManager.projectiles.get(ProjectileId) != null) return;
            AssetManager.projectiles.add(new ProjectileAsset
            {
                id = ProjectileId,
                // 高弹速 = 弹道平直（对照：箭 speed 20、霰弹 speed 30）
                speed = 50f,
                speed_random = 3f,
                texture = "shotgun_bullet",
                // 影子让弹丸有立体感
                texture_shadow = "shadows/projectiles/shadow_ball",
                look_at_target = true,
                can_be_blocked = true,
                draw_light_area = true,
                // 弹丸本身放大一些，方便看清弹道
                scale_start = 0.05f,
                scale_target = 0.075f,

                // —— 破空云迹 ——
                // fx_fire_smoke 是游戏给弹道拖尾准备的默认特效
                // （ProjectileAsset.trail_effect_id 的默认值就是它），
                // 用的是 PrefabFireSmoke 烟雾粒子：灰白色、会自然消散、无数量上限。
                // 极短的生成间隔让烟点在弹道上相互叠接，形成一条从枪口延伸到目标的
                // 连续白色烟迹，并在轨迹周围泛起云团状烟痕。
                trail_effect_enabled = true,
                trail_effect_id = TrailEffectId,
                trail_effect_scale = 0.15f,
                trail_effect_timer = 0.008f,

                // 命中反馈：爆出一个小命中火花
                end_effect = "fx_hit",
                end_effect_scale = 0.6f,

                sound_launch = "event:/SFX/WEAPONS/WeaponShotgunStart",
                sound_impact = "event:/SFX/WEAPONS/WeaponShotgunLand"
            });
        }

        static void registerItems()
        {
            foreach (var t in tiers)
            {
                if (AssetManager.items.get(t.id) != null) continue;

                AssetManager.items.clone(t.id, "$range");
                ItemAsset item = AssetManager.items.get(t.id);

                var stats = new BaseStats();
                stats["damage"] = t.damage;            // 远高于弓(1~10)
                stats["range"] = t.range;              // 长于弓(6~12)
                stats["accuracy"] = t.accuracy;        // 高于弓(4)；实际命中 = 此值 + 单位基础值
                stats["attack_speed"] = t.attackSpeed; // 低于弓(约 1.0) => 装填更慢
                stats["critical_chance"] = t.crit;
                stats["projectiles"] = 1f;             // 单发弹丸
                stats["targets"] = 1f;
                stats["stamina"] = 3f;
                item.base_stats = stats;

                // ⚠ 关键：必须补齐名字模板与词条。
                // ItemManager.generateItem 在生成武器时会调用
                // ItemAsset.getRandomNameTemplate() -> Randy.LoopRandom(name_templates)，
                // 若 name_templates 为 null 会直接抛 NullReferenceException，
                // 导致该武器在"锻造"和"装备"时都生成失败（这是火枪数量上不去的真正原因）。
                // 这里直接沿用原版武器的配置，保证非空且合法。
                ItemAsset donor = AssetManager.items.get("bow_wood");
                if (donor == null) donor = AssetManager.items.get("sword_wood");
                if (donor != null)
                {
                    if (donor.name_templates != null && donor.name_templates.Count > 0)
                        item.name_templates = new List<string>(donor.name_templates);
                    if (donor.item_modifier_ids != null)
                        item.item_modifier_ids = (string[])donor.item_modifier_ids.Clone();
                    if (donor.name_class != null)
                        item.name_class = donor.name_class;
                }
                if (item.name_templates == null || item.name_templates.Count == 0)
                    item.name_templates = new List<string> { "weapon_name_city" };
                if (item.item_modifier_ids == null)
                    item.item_modifier_ids = new string[0];

                item.equipment_subtype = Subtype;
                item.group_id = "firearm";
                item.translation_key = t.id;
                item.material = "basic";
                item.metallic = true;
                item.rigidity_rating = 5;
                item.is_pool_weapon = false;
                item.pool_rate = 0;
                item.projectile = ProjectileId;
                item.path_icon = "ui/Icons/items/icon_" + t.id;
                item.path_slash_animation = "effects/slashes/slash_punch";
                item.attack_type = WeaponType.Range;
                item.equipment_type = EquipmentType.Weapon;
                item.setCost(t.gold, t.res1Id, t.res1, t.res2Id, t.res2);
            }

            ensureSubtypeRegistered();
        }

        /// <summary>
        /// 确保火枪子类型存在于制造池字典。
        /// 该字典可能在加载世界时被游戏重建，因此补丁每次都会按需调用本方法。
        /// </summary>
        public static void ensureSubtypeRegistered()
        {
            var dict = AssetManager.items.equipment_by_subtypes;
            List<ItemAsset> list;
            if (!dict.TryGetValue(Subtype, out list))
            {
                list = new List<ItemAsset>();
                dict[Subtype] = list;
                ModClass.LogInfo("LineInfantry 重建 musket 制造池");
            }
            foreach (var t in tiers)
            {
                var asset = AssetManager.items.get(t.id);
                if (asset != null && !list.Contains(asset)) list.Add(asset);
            }
        }

        static void registerSprites()
        {
            Sprite fallback = null;
            byte[] iconFallback = null;
            foreach (var t in tiers)
            {
                // 手持贴图：贴图 ID 固定为 "w_" + 物品ID（原版 Item 构造规则）
                byte[] bytes = loadModFile($"Assets/weapons/w_{t.id}.png");
                Sprite sprite = null;
                if (bytes != null)
                {
                    var tex = new Texture2D(0, 0);
                    tex.filterMode = FilterMode.Point;
                    ImageConversion.LoadImage(tex, bytes);
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), 1f);
                    sprite.name = "w_" + t.id;
                }
                if (sprite == null && fallback == null)
                {
                    ModClass.LogWarning($"LineInfantry 缺少手持贴图 Assets/weapons/w_{t.id}.png");
                    continue;
                }
                if (sprite == null) sprite = fallback;
                if (fallback == null) fallback = sprite;
                ActorAnimationLoader._dict_items["w_" + t.id] = new List<Sprite> { sprite };

                // 装备栏图标：某级缺图时复用已有图标，避免按钮空白
                if (bytes == null) bytes = iconFallback;
                if (bytes != null)
                {
                    if (iconFallback == null) iconFallback = bytes;
                    try
                    {
                        SpriteTextureLoader.addSprite("ui/Icons/items/icon_" + t.id, bytes);
                    }
                    catch (System.Exception e)
                    {
                        ModClass.LogWarning($"LineInfantry 图标注册失败 {t.id}: {e.Message}");
                    }
                }
            }
        }

        static byte[] loadModFile(string relativePath)
        {
            try
            {
                string folder = ModClass.I.GetDeclaration().FolderPath;
                string path = Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按城市发达程度挑选该城市能"用得起"的最高火枪等级（用于新兵出场配枪）</summary>
        public static string pickTierFor(City city)
        {
            // 单位刚生成时往往还没归属城市，此时给最基础的一级（保证能配到枪）
            if (city == null) return "li_arquebus";
            int pop = city.getPopulationPeople();
            if (pop >= 150 && city.getResourcesAmount("silver") >= 2) return "li_rifled";
            if (pop >= 80 && city.getResourcesAmount("silver") >= 1) return "li_flintlock";
            if (pop >= 40) return "li_musket";
            if (pop >= 15) return "li_arquebus";
            return null;
        }
    }
}
