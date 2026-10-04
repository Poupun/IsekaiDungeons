# Making your own dungeons

Isekai Dungeons is a framework. This document is for **other modders** who want to
add dungeons, tower floors, deep dungeons or room events in their own mod.

Most dungeons need **no C# at all** — one `DungeonKindDef` is a complete dungeon.
Add `Poupun.IsekaiDungeonsAddon` to your `loadAfter` (and `modDependencies`) and start
writing XML.

A complete, compiling template lives in `ModdingExample/` next to this file: an
XML-only dungeon, a custom win condition, a custom room event and a tower floor. Copy
that folder into your Mods directory, rename it, and change the `packageId`. (The game
does not load it from here: RimWorld only reads a mod's root folders.)

**Inherit the shared base.** `ParentName="IsekaiDungeonBase"` gives your dungeon the
gate, all five rank curves and the room sizing the shipped dungeons use, so you only
state what makes yours different (environment, rank band, layout, roster):

```xml
<IsekaiDungeons.DungeonKindDef ParentName="IsekaiDungeonBase">
  <defName>MyMod_BoarDen</defName>
  <label>boar den</label>
  <description>...</description>
  <mapGenerator>IsekaiDungeons_Dungeon</mapGenerator>
  <minRank>F</minRank>
  <maxRank>D</maxRank>
  <layoutMode>Carve</layoutMode>
  <roster><li><kind>WildBoar</kind><weight>3</weight></li></roster>
  <bosses><li>Bear_Grizzly</li></bosses>
  <requiresFullRoster>true</requiresFullRoster>
</IsekaiDungeons.DungeonKindDef>
```

Section 1 below spells every field out instead, for when you want your own curves.

**Where your dungeon shows up.** Every non-`towerOnly` dungeon joins the local gates,
the guild contracts and the gate scrolls automatically, chosen by rank band and
`selectionWeight`. Nothing has to be registered.

---

## 1. The smallest possible dungeon

```xml
<IsekaiDungeons.DungeonKindDef>
  <defName>MyMod_RatKingLair</defName>
  <label>rat king's lair</label>
  <description>Something enormous is nesting in the dark, and it is not alone.</description>

  <!-- Required plumbing -->
  <gateDef>IsekaiDungeons_DungeonGate</gateDef>
  <mapGenerator>IsekaiDungeons_Dungeon</mapGenerator>   <!-- the Cavern environment -->

  <!-- When it can appear -->
  <minRank>F</minRank>
  <maxRank>D</maxRank>
  <selectionWeight>1.0</selectionWeight>

  <!-- Rank-keyed curves. x = rank index: 0=F 1=E 2=D 3=C 4=B 5=A 6=S 7=SS 8=SSS 9=Nation -->
  <mapSizeByRank>         <points><li>(0, 150)</li><li>(2, 170)</li></points> </mapSizeByRank>
  <roomCountByRank>       <points><li>(0, 4)</li><li>(2, 6)</li></points>     </roomCountByRank>
  <encounterPointsByRank> <points><li>(0, 120)</li><li>(2, 260)</li></points> </encounterPointsByRank>
  <breakTimerDaysByRank>  <points><li>(0, 4)</li><li>(2, 5)</li></points>     </breakTimerDaysByRank>
  <lootValueByRank>       <points><li>(0, 270)</li><li>(2, 1080)</li></points></lootValueByRank>

  <!-- Who lives here -->
  <roster>
    <li><kind>Rat</kind><weight>3</weight></li>
    <li><kind>Boomrat</kind><weight>1</weight><minDepth>0.5</minDepth></li>
  </roster>
  <bosses>
    <li>Megaspider</li>
  </bosses>
  <requiresFullRoster>true</requiresFullRoster>
</IsekaiDungeons.DungeonKindDef>
```

That is a working dungeon: it will be offered by the storyteller, generate, populate,
aggro room by room, be clearable, and pay out.

### Rosters are strings on purpose

`<kind>` and `<bosses>` take **PawnKindDef defNames as text**, never def references.
An entry naming a creature the player does not have simply drops out instead of
throwing a load error, so you can reference other mods freely. `minDepth` (0 = first
combat room, 1 = last) gates an entry to the deeper part of the dungeon.

`requiresFullRoster true` means: if *nothing* in the roster resolves, this dungeon is
never offered. Use it whenever your dungeon's identity depends on creatures that might
be missing. Set it false only if the dungeon still makes sense with generic fallback
creatures.

> **The single most common mistake:** using a name that exists but is the wrong *def
> type*. `Fleshbeast` is a `FleshTypeDef`, `Mech_Termite` is a `ThingDef` — neither is
> a `PawnKindDef`, and both silently vanish from a roster. Verify the def TYPE.

---

## 2. Layout modes

`<layoutMode>` picks how the map is built. It must match your generator:

| Mode | Map | What happens | Use for |
|---|---|---|---|
| `Carve` | `isUnderground true` | Rooms + corridors cut out of solid rock, doors at every mouth | caves, crypts, hives |
| `Open` | `isUnderground false` | **Nothing is built.** The biome is the dungeon; encounter zones are invisible group anchors | snowfields, plains, ruined fields |
| `Build` | `isUnderground false` | Rooms floored and ringed with a built wall shell | walled compounds (no shipped dungeon uses it) |

Mismatching mode and generator produces an unplayable map — a `Carve` dungeon on a
surface generator has no rock to cut, an `Open` dungeon underground is solid stone.
`ConfigErrors` catches both at startup, so watch your log.

`Carve` respects `roomSizeRange` (keep max ≤ 12 when roofed — roof support),
`corridorWidth`, `placeDoors`, `doorDef`, `floorTerrain`.
`Open` respects `openZoneRadiusRange` and **ignores** all wall/floor/door fields.

## 3. Your own environment

An environment is a plain `MapGeneratorDef`. Copy one of ours
(`Defs/MapGeneration/DungeonMapGenerator.xml`) and change the biome, temperature and
gen steps. The dungeon steps sort themselves by their own `order`:

```xml
<customMapComponents>
  <li>IsekaiDungeons.DungeonMapComponent</li>   <!-- required: the dungeon brain -->
</customMapComponents>
<genSteps>
  <!-- ...your terrain steps... -->
  <li>IsekaiDungeons_Layout</li>       <!-- required (order 410) -->
  <li>IsekaiDungeons_Entrance</li>     <!-- required (420) -->
  <li>IsekaiDungeons_Decor</li>        <!-- optional (425): rank-scaled dressing -->
  <li>IsekaiDungeons_Population</li>   <!-- required (430) -->
  <!-- ...plants, fog... -->
</genSteps>
```

For a rock (Carve) environment, use `IsekaiDungeons_Underground_RocksFromGrid` instead
of vanilla's `Underground_RocksFromGrid`. Vanilla's step hides an ore scatter (steel,
plasteel, gold, uranium, components), and tower floors regenerate on every visit, so
vanilla ore in a dungeon is a farm. Add `IsekaiDungeons_ManaOre` if you want the
dungeon's own ore.

## 4. Your own gate (and its texture)

`gateDef` takes any `ThingDef` whose `thingClass` is `IsekaiDungeons.Building_DungeonGate`
(or a subclass). Copy `IsekaiDungeons_DungeonGate`, point `graphicData.texPath` at your
own art, and set it on your dungeon. The `<portal>` block's `exitDef` likewise takes any
`PocketMapExit` subclass, so the return portal can be yours too.

Custom gates are first-class: they count toward the concurrency cap and receive the
safety sweep exactly like ours.

---

## 5. Writing code (optional)

Four strategy classes are pluggable per archetype. All are stateless and cached on the
def — put anything that must survive a save on the map component, the gate or the map
parent, never on a worker.

| Field | Base class | Controls |
|---|---|---|
| `workerClass` | `DungeonWorker` | rank rolling, clear/break side effects |
| `objectiveWorkerClass` | `DungeonObjectiveWorker` | **what "cleared" means** |
| `lootWorkerClass` | `DungeonLootWorker` | rewards and clear XP |
| — | `IDungeonSpawnProvider` | generic creature eligibility (registered, not per-def) |

### A different win condition

The default objective is "the boss is defeated". To make a survive-the-timer dungeon:

```csharp
public class MyMod_SurviveObjective : DungeonObjectiveWorker
{
    public override bool IsComplete(DungeonMapComponent dungeon)
    {
        // Polled every 60 ticks — keep it cheap and side-effect free.
        return dungeon.gate != null && dungeon.gate.TicksUntilBreak <= 0;
    }

    public override void OnCompleted(DungeonMapComponent dungeon) { /* one-shot */ }
}
```

```xml
<objectiveWorkerClass>MyMod.MyMod_SurviveObjective</objectiveWorkerClass>
```

### Your own rewards

The built-in loot generator is deliberately Isekai-flavoured (mana cores, star
fragments, runes). To pay in your own currency, override `DungeonLootWorker`:

```csharp
public class MyMod_MechLoot : DungeonLootWorker
{
    public override List<Thing> GenerateClearLoot(MobRankTier rank, DungeonKindDef kind)
    {
        // Ignore the Isekai ladder entirely, or call base and append.
        return MyLootTable.For(rank);
    }
}
```

### Registering a bestiary

A `<roster>` covers "this dungeon uses these creatures". To make your creatures
eligible in *any* dungeon that has no roster of its own, register a provider once:

```csharp
[StaticConstructorOnStartup]
public static class MyMod_Startup
{
    static MyMod_Startup() => DungeonSpawnResolver.RegisterProvider(new MyProvider());
}
```

Registered providers are probed before the built-ins; the base-mod fallback always
stays last so the chain can never come up empty.

### Custom gen steps

Ask where you are with the public resolver — do not re-derive it:

```csharp
DungeonContext.Resolve(map, out DungeonKindDef kind, out MobRankTier rank);
if (DungeonContext.IsDungeonMap(map)) { /* ... */ }
DungeonMapComponent comp = DungeonContext.ComponentOf(map);
```

`comp.rooms` holds the layout (`Entry` first, `Boss` last). Register creatures you
spawn with `comp.RegisterCreature(room, pawn)` so they participate in aggro — and in
`Open` dungeons, in the leash that keeps groups on their patch. A boss must ALSO be
passed to `comp.RegisterBoss(pawn)`.

---

## 6. Known constraints

- **Rank is `IsekaiLeveling.MobRankTier`.** This is an Isekai add-on; rank F→Nation is
  the currency end to end and is not abstracted away.
- **`DungeonRoomRole` is a closed enum** (Entry / Combat / Treasure / Boss). Custom
  room roles are not supported yet.
- **Global knobs are player settings, not XML.** Loot multiplier, gate cap, XP,
  room budgets and the like live in the mod settings window (one registry, 180+
  entries). Your dungeon tunes itself through its own curves and fields.
- **Balance without Isekai Creatures.** When the Creatures add-on is absent, the
  shipped dungeons get budget and boss boosts (`ThreatParity`) so they stay as hard as
  with it. That applies only to dungeons shipped by Isekai Dungeons: **yours keep
  exactly the tuning you wrote**, with or without the add-on.
- **`ConfigErrors` is your friend.** A mismatched layout and generator, a corridor
  width under 2, a roofed room over 12 cells or a min rank above max rank are all
  reported at startup. Read your log after adding a dungeon.

---

## 6b. Music

Set `<music>YourSongDef</music>` on a dungeon to give it its own theme. It plays
inside the dungeon and holds through fights exactly like the shipped tracks. Left
empty, the rank-band tracks play.

## 6c. Tower floors

A dungeon with `<towerOnly>true</towerOnly>` never appears as a gate. Instead the
Tower of Gods can roll it for any floor whose rank falls in its `minRank`..`maxRank`
band. Floor *types* are `IsekaiDungeons.TowerFloorArchetypeDef`s (`floorLabel`,
`weight`, `minFloor`/`maxFloor`, `layoutModes`, `roomCountFactor`, `encounterFactor`,
`treasureRooms`, `peaceful`, `village`, `dungeonKinds` to pin specific dungeons). Add
your own archetype, or let your dungeon fill the existing ones.

`pinnedOnly` keeps a set-piece dungeon off every floor that does not list it in its
archetype's `dungeonKinds`.

## 6d. Deep (multi-floor) dungeons

An `IsekaiDungeons.MultiFloorDungeonDef` chains floors into a descent posted by Guild
surveys: `floorCount`, `kindDef` (or one `floorKinds` entry per floor), `minRank` /
`maxRank` across the descent, `finalBosses` (PawnKindDef names, strings like rosters),
`selectionWeight`, `minColonyRank`, and an optional `spineWorkerClass` deriving from
`MultiFloorSpineWorker` for a rule that runs through the whole descent (see the shipped
split, retreat, rising-dead and regrowth spines). A descent is only offered when every
floor's dungeon resolves its roster and a declared final boss exists.

## 6e. Decor

`IsekaiDungeons.DungeonDecorSetDef` dresses rooms by rank. A set applies to the
map generators in its `generators` list (MapGeneratorDef defNames), or to specific dungeons through `kinds` (DungeonKindDef
defNames). Decor must never be a resource farm: tower floors regenerate, so give decor
no cost list, no stuff, no minified form, and make it unclaimable.

## 7. Room events

A **room event** is something that happens in one room (Carve) or one encounter
zone (Open) instead of — or on top of — the ordinary fight. They are
`DungeonRoomEventDef`s, so adding one is XML plus a worker class; the shipped
ones (`Defs/RoomEventDefs/RoomEvents.xml`) have no special status.

```xml
<IsekaiDungeons.DungeonRoomEventDef>
  <defName>MyMod_CollapsingFloor</defName>
  <label>collapsing floor</label>
  <workerClass>MyMod.RoomEventWorker_CollapsingFloor</workerClass>
  <weight>1.0</weight>
  <minRank>C</minRank>                 <!-- rank band -->
  <layoutModes><li>Carve</li></layoutModes>   <!-- empty = every mode -->
  <minDepth>0.3</minDepth>             <!-- 0 = first combat room, 1 = last -->
  <maxDepth>0.9</maxDepth>
  <maxPerDungeon>1</maxPerDungeon>
  <replacesEncounter>false</replacesEncounter> <!-- false = layered on the fight -->
  <budgetFactor>1.0</budgetFactor>
  <enteredMessageKey>MyMod.Event.Floor.Entered</enteredMessageKey>
</IsekaiDungeons.DungeonRoomEventDef>
```

```csharp
public class RoomEventWorker_CollapsingFloor : DungeonRoomEventWorker
{
    // Optional veto. Returning false leaves the room an ordinary fight.
    public override bool CanPlace(Map map, DungeonRoom room, DungeonKindDef kind, MobRankTier rank)
        => room.rect.Width >= 9;

    // Map GENERATION. Spawn things, place loot, build scenery.
    public override void Populate(DungeonRoomEventContext ctx) { }

    // PLAY TIME, once, when the party first walks in. Traps and ambushes live here.
    public override void OnEntered(DungeonRoomEventContext ctx)
    {
        Message(ctx, "MyMod.Event.Floor.Entered", MessageTypeDefOf.ThreatSmall);
        // Telegraph, then act — see RoomEventWorker_Trap.
        ctx.component.QueueDelayed(120, () => { /* ... */ });
    }
}
```

`DungeonRoomEventContext` carries `map`, `component`, `room`, `kind`, `rank`,
`tier` (after the difficulty ramp), `depth` (0–1) and `budget` (the encounter
this event displaced). Helpers on the base class: `RandomCellIn`, `SpawnGroup`,
`ScatterLoot`, `Message`.

**Rules the selector enforces for you:** at most ~a third of a dungeon's combat
rooms become events, at least one stays a plain fight, and `maxPerDungeon` is
respected. A worker that vetoes or throws degrades the room to an ordinary
encounter — an event can never leave a room empty.

**Persisting state:** put it on `DungeonRoom` (scribed), never on the worker —
workers are stateless singletons cached on the def. `room.entered`,
`room.eventBudget` and `room.depthFrac` are already saved for you.
`QueueDelayed` fuses are deliberately *not* saved: a sub-second fuse that
survived a save/load would resurrect an explosion on load.

---

## 8. The title card, custom fonts and animation

Every arrival on a dungeon map plays a title card: a rank ribbon, the dungeon's
name, and a line of its own description. There is nothing to configure — it
reads what your def already says — but the parts are reusable.

### What it shows

| Slot | Source | Notes |
| --- | --- | --- |
| Ribbon text | `IsekaiDungeons.Title.RankLine` keyed string | Keep it short; the ribbon is a fixed-width chevron. |
| Name | `<label>` of your `DungeonKindDef` | Title-cased through `GenText.CapitalizeAsTitle`. |
| Tagline | first clause of your `<description>` | Everything up to the first `.`, `!`, `?` or `;`. Write a good opening sentence and you get a good card for free. |
| Sparkle tint | the dungeon's rank colour | The ribbon text stays white, as designed. |

A dungeon with **no description at all** falls back to its environment's
`titleSubtitleKey` (see §3), so a card is never blank.

Everything auto-fits: names shrink from 132px down to 64px to stay on one line,
taglines wrap to at most three lines and shrink from 35px to 22px. Across the
53 shipped dungeons nothing reaches either floor, but a very long name or
tagline will simply come out smaller rather than overflowing.

### Fonts

The mod ships its typefaces as **glyph atlases**, baked offline by
`Tools/make_font_atlas.py`, and draws them itself as textured quads. That is not
a stylistic choice — a RimWorld player build cannot be handed a font file at all.
All three routes were tried against the running game:

| Route | Result |
| --- | --- |
| `Font.CreateDynamicFontFromOSFont(family, size)` | Resolves against a font list Unity builds **at startup**, which a mod's file is never on. Worse, it *succeeds*: you get a Font **named** after the family you asked for that renders in a substitute face, so `font.name` proves nothing. |
| `new Font(fullPath)` → `Internal_CreateFontFromPath` | Editor-only. In a player it returns a hollow object: `dynamic=false`, no material, `ascent=0`, uninitialised garbage in `lineHeight`. Confirmed with a known Windows TTF as a control, so it is the API and not the file. |
| AssetBundle | Works, but needs the matching Unity Editor to build one. |

So: rasterise offline, draw the quads yourself. Nothing depends on Unity's font
system, it behaves identically on every platform, and the metrics can be checked
against the design before the game ever runs.

To re-bake after changing a face:

```bash
python Tools/make_font_atlas.py
```

It writes `Fonts/Display.{png,fnt}` and `Fonts/Body.{png,fnt}`. Three things in
there are worth knowing if you copy this:

- **Metrics come from the font binary, not the rasteriser.** Pillow cannot see
  GPOS without libraqm, so every kern pair measures as zero through it — and
  these faces carry 6.5k and 4.4k real pairs, worth up to 20px on a 132px title.
  Advances come from `hmtx` and kerning from GPOS via `fontTools`; Pillow only
  draws the pixels.
- **Everything except the atlas rectangle is stored in em units**, so one bake
  serves every size: multiply by the pixel size at draw time.
- **The atlas is mipmapped with a six-pixel gutter.** The card draws between
  roughly 0.4x and 0.95x of the bake size depending on resolution, and plain
  bilinear minification of a glyph edge aliases badly at the low end. Too little
  padding shows up as faint ghosts of neighbouring glyphs in the lower mips.

The atlas lives in `Fonts/`, not `Textures/`, and is decoded with
`Texture2D.LoadImage` directly rather than through `ContentFinder` — a glyph
atlas must not be DXT-compressed and must not join RimWorld's own texture atlas.

Any face that fails to load, and any string the atlas has no glyphs for (a
translation outside the baked character set), falls back to RimWorld's own font
through the same `ITextFace` interface. The card lays out and animates
identically either way; only the letterforms differ.

### Reusing the animation

Two pieces are worth stealing for your own UI:

**`Tween`** — easing curves plus an **analytically solved** damped spring
(`Spring01(t, omega, zeta)`). Analytic, not integrated, and that is the whole
point: IMGUI calls `OnGUI` several times per rendered frame, so a spring that
stepped `velocity += force * dt` would run at a speed set by how much the mouse
was moving. Every function here is a pure function of elapsed real seconds.

**`AnimatedTextRun`** — lays a string out once and then draws it a glyph or a
word at a time, each with its own delay, rise, fade and spring:

```csharp
// Inside OnGUI — GUIStyle.CalcSize needs the IMGUI text generator.
var run = AnimatedTextRun.Build("Demon Gate", DungeonFonts.Display,
                                maxFontSize: 132, minFontSize: 64,
                                TextGranularity.Character,
                                maxWidth: 656f, maxLines: 1);

var reveal = TextReveal.Make(delay: 0.42f, stagger: 0.042f, rise: 34f,
                             scaleFrom: 0.86f, omega: 15f, zeta: 0.42f, fade: 0.22f);

run.Draw(centerX, top, elapsedSeconds, reveal, Color.white, masterAlpha: 1f,
         shadowColor: new Color(0f, 0f, 0f, 0.55f), shadowOffset: new Vector2(0f, 3f));
```

Use `TextGranularity.Word` for sentences — per-glyph across 140 characters reads
as a stutter, not a reveal.

It measures **cumulative prefixes** (`"D"`, `"De"`, `"Dem"`…) rather than each
glyph alone, so kerning survives being drawn one letter at a time. That matters:
naive per-glyph drawing visibly loosens a condensed display face at 132px.
`run.RevealDuration(reveal)` tells you when the run has landed, so a sequence can
be timed off the tuning instead of off a hard-coded guess.
