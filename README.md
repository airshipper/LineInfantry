# Line Infantry — a gunpowder-era mod for WorldBox

Turns the endless street brawls into **actual warfare**.

Adds four tiers of muskets to all four races (humans, elves, dwarves, orcs) and makes
musket-armed soldiers fight as **line infantry**: they form up inside their own borders,
advance as a line, and exchange volleys instead of chasing enemies around.

[中文说明 →](README.zh-CN.md)

---

## What it adds

### Four tiers of muskets

| Tier | Name | Damage | Range | Accuracy | Cost |
|---|---|---|---|---|---|
| T1 | Arquebus | 65 | 12 | 3.2 | wood |
| T2 | Heavy Musket | 95 | 13 | 2.8 | wood + metals |
| T3 | Flintlock Musket | 80 | 15 | 3.8 | metals + silver |
| T4 | Rifled Musket | 120 | 18 | 5.2 | metals + silver + gold |

For reference, the vanilla bow sits at damage 1–10 and range 6–12.

- Far higher damage than bows — often a one-shot kill
- Longer range, slower reload
- Flat trajectory with a very visible white vapour trail
- The first two tiers need only common resources; silver/gold only show up at T3/T4
- Cities **prioritise forging muskets**, and soldiers who receive one will never trade it
  back for a vanilla weapon

### Line infantry tactics

- After war is declared, an army first **assembles into ranks just inside its own border**
- How far the line advances is driven by **headcount**: a small force holds back,
  a large one pushes all the way to the enemy city. Take heavy losses and the line
  falls back on its own
- Soldiers **hold the line and fire** — they no longer wander off, and they no longer
  chase enemies into melee with a 15-tile-range weapon
- Once the enemy garrison is wiped out the army reverts to vanilla behaviour and storms
  the city, so wars still resolve

### Frontline on the map

- Shown **only in map view** (the zoom level where kingdom borders appear); it fades out
  as you zoom in to individual units
- Drawn in the **kingdom's own colour**: a main line with teeth pointing at the enemy —
  the standard frontline symbol on a military map
- At most 1–2 lines per strategic direction, so the map doesn't turn into spaghetti

---

## Requirements

- **WorldBox 0.50.3 (build 653)**
- **[NeoModLoader (NML)](https://github.com/WorldBoxOpenMods/ModLoader)** — this mod is an NML mod

## Installation

1. Install NeoModLoader first.
2. Drop the `LineInfantry` folder into the game's `Mods` folder
   (next to `worldbox.exe`).
3. Launch the game. The first start takes a few extra seconds while NML compiles the mod.

> ⚠️ If you already have an older copy of this mod, **delete it first**. Two copies at once
> will apply every Harmony patch twice and behave incorrectly.

---

## Compatibility

- This mod does **not** edit vanilla weapon stats. Instead it lowers vanilla weapon *value*
  so muskets are clearly the best item a soldier can be handed.
- It **does** patch warrior decision flow and city army deployment. Other mods that touch
  the same systems (army formation, city attack logic) may interact with it.
- Built against the **publicized** assembly; internal members are reached through cached
  reflection at runtime.

---

## Tuning

Every number below is a constant you can edit and then just restart the game —
NML recompiles the sources on launch, there is no build step.

**Musket stats** — `LineInfantry/Code/MusketAssets.cs`, the `tiers` table
`damage` / `range` / `accuracy` (≥ 10 disables bullet spread entirely) / `attackSpeed`

**Frontline behaviour** — `LineInfantry/Code/BattleLine.cs`

| Constant | Default | Meaning |
|---|---|---|
| `DeepInset` | 18 | how far *inside* the border the army assembles (tiles) |
| `MinForce` / `FullForce` | 6 / 40 | troop count at which the line starts advancing / is fully committed |
| `MaxFrontRadius` | 16 | how far from the enemy city the line may sit — keep it inside musket range (12–18) |
| `AdvanceTilesPerSecond` | 4.5 | how fast the line pushes forward |
| `RetreatTilesPerSecond` | 8 | how fast it falls back |
| `FillGateMin` / `FillFull` | 0.10 / 0.60 | how the arrival rate gates the advance |
| `SingleLineArc` | 1.75 | angular span above which one direction is split into two lines |

**Discipline** — `LineInfantry/Code/LineFormation.cs`
`BackwardTolerance` (8): how many tiles behind the line a destination must be before a
soldier is pulled back to his slot.

**Deployment gate** — `LineInfantry/Code/KingdomFocus.cs`
`MinWarriorsToDeploy` (1): minimum unit count before an army is allowed to march out.

**Debug logging** — `LineInfantry/Code/FrontlineVisual.cs`
`DebugDiagnostics` (default `false`): set to `true` to print a frontline status line to
the log every 6 seconds. Very handy when diagnosing, but noisy for normal play.

---

## Building / contributing

No build step is needed to *run* the mod — NML compiles `LineInfantry/Code/*.cs` at
launch with its bundled Roslyn.

For IDE autocomplete, create a `net48` class library pointing at the game's
`Assembly-CSharp-Publicized.dll` (shipped with NML) plus `UnityEngine.dll`,
`UnityEngine.CoreModule.dll` and `0Harmony.dll`, and include `LineInfantry/Code/**/*.cs`.

Note: source comments are currently in Chinese. Issues and PRs in English are welcome.

---

## License

MIT — see [LICENSE](LICENSE).
