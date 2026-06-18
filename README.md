# KCD Save Fixer

A repair tool for **Kingdom Come: Deliverance (2018)** save files (`.whs`) that
have grown so large the game eventually fails to load them or crashes on load.

## What it fixes

Some saves slowly bloat until loading them crashes the game -- a point of no
return where every later save inherits the problem and there's no working save
to go back to.

This happens when an NPC gets stuck and starts spamming internal AI commands
that never get cleared. They pile up by the tens or hundreds of thousands, and
all that junk gets written into your save.

The catch: this barely shows up in the file size on disk, because the repeated
junk compresses down to almost nothing. The real problem is how big the save
becomes **when the game unpacks it into memory to load it**. The pile of stuck
commands pushes that unpacked size past an internal limit, and once it crosses
that limit the game can no longer load the save.

This tool finds the stuck NPC's command pile and defuses it. After the fix and
one in-game resave, the unpacked size drops by roughly half and the save loads
normally again.

## How to use it

Always back up your save first. Saves live in:

```
%USERPROFILE%\Saved Games\kingdomcome\saves\
```

### 1. Check a save

```
kcdsavefixer analyze "save.whs"
```

This is read-only. It prints the on-disk and unpacked sizes, a list of the
NPC command queues it found and how big each one is, and a verdict:

- **HEALTHY** -- nothing oversized was found. If your save still won't load, the
  cause is something this tool doesn't handle (see Scope below).
- **BLOATED** -- a stuck, oversized queue was found, and the tool names it (for
  example `HorseMailbox count = 125,635`) along with the likely culprit NPC (for
  example `ska_matus`). A normal queue holds a handful to a few hundred entries;
  tens of thousands means an NPC is stuck.

```
  MAILBOX NAME               OCCURRENCES   MAX ENTRIES
----------------------------------------------------
HorseMailbox                         2        125635  <-- ABNORMAL
QuestMailbox                        19           506
ToolMailbox                       2146             1
stashClosedMailbox                   1             1

VERDICT: BLOATED. 1 mailbox(es) exceed the threshold of 5 000 entries:
  - HorseMailbox @ 0x399FD46  count = 125 635
      likely culprit NPC: ska_matus  (appears 125 640 times)
```

### 2. Fix it

```
kcdsavefixer fix "save.whs"
```

This writes a new file (e.g. `save.fixed.whs`) and never touches your original.
If the save is healthy it writes nothing and tells you so.

**The fix is two steps, and the second one happens in-game:**

**Step 1 -- run the tool.** It zeroes the stuck queue. The new file will load
in-game, but it is **not** smaller yet -- that comes after you resave in-game.

**Step 2 -- sort out the stuck NPC and resave.** You need to get the NPC
un-stuck so the queue doesn't immediately refill, then let the game write a
fresh save. Here's the practical way to do it using the community
**Cheat** mod (https://www.nexusmods.com/kingdomcomedeliverance/mods/106):

1. **Find out who is stuck.** The `analyze` output names the culprit directly,
   on the line `likely culprit NPC:`. It shows the NPC's internal actor id, like
   `ska_matus` -- that's the NPC **Matus** (named "Matthew" in the English
   version). Find english version of NPC name.
2. Load the fixed save, install the Cheat mod, and open the console.
3. Use the mod's commands to locate that NPC (ex. `cheat_find_npc token:Matthew`) and teleport to them (find their
   position, then `goto` those coordinates).
4. **Un-stick them** -- give them a shove, a light hit, anything that makes them
   react and walk or run off somewhere on their own. The goal is just to break
   them out of the stuck state so they resume normal behavior.
5. **Let time pass** -- skip or wait several in-game hours so the NPC fully
   returns to its normal routine.
6. **Save** with a Saviour Schnapps (or any normal in-game save).
7. Run `kcdsavefixer analyze` on that new save. If the unpacked size is roughly
   half and the verdict is HEALTHY -- bingo, it's fixed.

> **Note:** this procedure was proven on a save that still *loaded* (it crashed
> only later, after more playtime). If your save **already won't load at all**,
> this likely won't recover it -- that case wasn't tested. You're better off
> rolling back to an earlier save that still loads and fixing that one before it
> reaches the point of no return.

## Scope

This tool fixes exactly one thing: a save bloated by a stuck NPC command queue.
It does **not** fix saves broken for other reasons (interrupted writes, truncated
or corrupted files, mod conflicts, unrelated quest/script breakage). If `analyze`
says HEALTHY but your save still won't load, this isn't your problem and editing
won't help. It targets KCD1 (2018) `.whs` saves specifically.

**No guarantees.** This tool comes from one investigation into one save. Even if
`analyze` reports BLOATED and you follow every step, there is no promise it fixes
your particular save -- yours may have a different or additional problem that
just happens to look similar. If it doesn't work for you, then unfortunately it
doesn't, and you've lost nothing as long as you kept your backup. Always back up
first, and treat a successful fix as a lucky outcome rather than a sure thing.

**Tested game version.** All of this was verified on game version **1.9.8**. The
save itself originated on earlier versions (it had been played and re-saved
across several patches), but every step of the fix -- editing, loading, the
in-game resave, and confirming the result -- was done on 1.9.8. Behavior on other
versions is unknown.

---

## Technical details

### The `.whs` container

A `.whs` file is a bare sequence of zlib chunks followed by a 64-byte footer --
there is no plaintext header. Each chunk is `uint32 compLen`, `uint32 uncompLen`,
then a zlib stream (block size `0x8000`). The footer starts with the magic
`0XBP` followed by a 16-byte hash; the hash is not validated on load, so the
tool preserves the footer untouched.

### The mailbox structure

Each command queue ("mailbox") in the unpacked payload is an ASCII name ending
in `Mailbox` followed by a `0x00` byte. Immediately after that nul byte is the
entry count as a `uint32`, and a second identical copy of the count 20 bytes
further on. The tool treats a hit as a real mailbox only when the two counts
match and are in a sane range, which filters out coincidental text matches. The
fix writes `0` to both copies.

### Why it only zeroes the counter (and never deletes anything)

The obvious idea -- delete the stuck command objects to shrink the file -- was
tested and **corrupts the save**. Those commands are real entities living in the
level's shared ID space; each shares its ID with other live game objects (items,
horses, quest objects). Removing commands desynchronizes the entity IDs against
the base level the engine cross-checks at load, so the engine silently rejects
the whole save and falls back to a fresh level load -- your progress is gone.

The only safe external edit is to zero the queue's counter **in place**, moving
and deleting nothing. The engine then sees an empty queue and stops walking the
giant list, so the save loads. The bloated objects are still physically present
at this stage, which is why the file hasn't shrunk yet.

### Why the in-game resave is required

Zeroing the counter alone doesn't shrink anything, and fixing the NPC in-game
without zeroing the counter doesn't either -- that was tested directly: a save
where the NPC was sorted out but the counter left intact came back still ~63 MB
unpacked, with the queue fully re-serialized, because as far as the engine was
concerned the commands were still in the mailbox.

Only the combination works, in this order: zero the counter (this tool), then
let the game resave. With the queue empty, the game doesn't write the orphaned
commands back, and the unpacked size drops by roughly half. The game is the only
thing that can safely rewrite its own entity graph, so the cure is to unblock it
and let it do that.

### A note for the developers

This whole class of save corruption looks preventable at the engine level. A
single NPC accumulating tens or hundreds of thousands of queued AI commands is
clearly an anomaly, not legitimate game state -- no NPC has a real reason to hold
125,000 pending commands. It would be reasonable for the game, on load (or on
save), to detect such a runaway command chain -- e.g. any single actor's command
queue past some sane ceiling -- and discard it rather than faithfully serializing
the garbage until the save grows past the load limit and bricks the playthrough.
A small sanity check there would spare players the silent slide toward an
unloadable save and the point of no return entirely.

---

## License

MIT License -- Copyright (c) 2026 REVOLUTiON. See the `LICENSE` file. Free to
use, modify, and redistribute; provided as is, with no warranty.
