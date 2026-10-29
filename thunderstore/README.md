# Player Radio

**Every player gets their own radio.** Drop your own music in a folder, press **F5** (or open the pause menu's **RADIO** page), and it plays *from your character* in 3D. Friends hear it get louder as they climb up to you and fade out as they walk away. Their radios play their music the same way, and you hear them all.

## Features
- **Your own music**: `.mp3`, `.wav` and `.ogg` files from your Music folder (subfolders too).
- **Your own radio**: it plays only your songs, and only you can play/pause, skip or go back. Everyone else controls their own radio.
- **Hear everyone in 3D**: every player's radio plays from their character. Several radios can play at once, and everyone hears the same song at the same moment. Players who join late pick up every radio where it is.
- **Songs are shared automatically**: if you don't have the song someone is playing, it's streamed to you over the game's connection, checked, and cached so it only downloads once.
- **Your volumes are yours**: change how loud your radio is for you, how loud everyone else's is, or mute other radios. None of this changes what other players hear.
- **Game music fades out** while a radio is playing near you and comes back afterwards. Ambience and sound effects are not affected.
- **Death**: a dead player's radio keeps playing where they fell until they're revived or leave.
- **Works solo** and offline.
- **RADIO page in the pause menu**: see what's playing, play/pause, skip, set both volumes, mute other radios, open your Music folder and pick any of your songs from a list. Works with mouse and gamepad.
- Small overlay with your song, time, downloads, radios playing nearby and the keys. Looking at a player whose radio is on shows `[Radio]` after their name.

## Controls
Press **Esc** and click **RADIO** in the pause menu for the Player Radio page:
- Now playing, with the song time
- **PREV / PLAY-PAUSE / NEXT**
- **My radio volume** and **Other radios volume** sliders (only for you)
- **Mute other radios** (only for you) and **Open music folder**
- **Your songs**: click a song to play it right away
- **Back** (or Esc / the gamepad back button) returns to the pause menu

The hotkeys still work, in game and while the pause menu is open. Turn them off with `EnableHotkeys = false` if they clash with something.

| Key | Action |
|-----|--------|
| F5  | Play / pause your radio |
| F6  | Previous song (restarts the song if more than 3 s in) |
| F7  | Next song |
| F8  | Mute / unmute other players' radios (only for you) |
| F9 / F10 | Your radio's volume down / up (only for you) |
| F4  | Open your Music folder (works in menus too) |

All keys can be changed in `BepInEx/config/com.inkslinger.playerradio.cfg` using Unity Input System paths, for example `<Keyboard>/f5`, `<Keyboard>/numpad5` or `<Mouse>/forwardButton`. The log warns you if one of your keys clashes with a game binding.

## Adding music
The easiest way: start the game and press **F4** (or Esc → **RADIO** → **Open music folder**). Your Music folder opens in File Explorer. Copy your songs in; they're picked up within about 10 seconds, no restart needed.

The folder is:

```
<profile folder>/BepInEx/config/PlayerRadio/Music
```

- **r2modman / Thunderstore Mod Manager**: Settings → *Browse profile folder*, then open `BepInEx/config/PlayerRadio/Music`.
- **Gale**: open your profile's folder from Gale, then `BepInEx/config/PlayerRadio/Music`.
- **Manual install**: `PEAK/BepInEx/config/PlayerRadio/Music`.

The folder is created on first launch with a `HOW TO ADD MUSIC.txt` inside. Subfolders work. Only `.mp3`, `.wav` and `.ogg` files are used. You can point somewhere else with the `MusicFolder` setting.

Tip: mod managers may include the config folder when you export or share a profile, so a large Music folder can make the export big.

## Multiplayer
- **Everyone who wants to hear radios needs Player Radio**, and everyone should be on the **same 0.2.x version**. Players without the mod hear nothing and aren't affected. 0.1.x players are ignored.
- **Songs stream to other players** who don't have them, over the game's own connection. Downloaded songs are cached in `BepInEx/cache/PlayerRadio` (500 MB limit by default; oldest files are removed first).
- **Bandwidth**: uploads are capped at 64 KB/s in total (`StreamRateKBps`), and downloads at 128 KB/s (`DownloadRateKBps`). A typical 5 MB song takes about a minute. Files over 25 MB (`MaxStreamFileMB`) are never sent. Set `EnableStreaming = false` if you don't want to send or receive files at all.

## What this mod sends and stores
- **Sent**: only song files from players' own Music folders, and only to other players in the same lobby who also have Player Radio. They go over the game's own Photon connection (Photon events), never anywhere else. Files over 25 MB are never sent. Small radio state (which song, playing/paused, position) is shared through Photon player properties.
- **Checked**: every received song is verified with SHA-256 before it's used; anything that doesn't match is thrown away.
- **Stored**: received songs are saved only to `BepInEx/cache/PlayerRadio` (500 MB cap by default, oldest files are deleted first). Nothing else is written outside the mod's config, cache and Music folders.
- **Nothing else**: no internet downloads, no external servers, no analytics. Local audio is loaded only through `file://` paths, and F4 / the Open music folder button open the Music folder the same way.

## Configuration
Settings are in `BepInEx/config/com.inkslinger.playerradio.cfg` (or your mod manager's config editor):

| Setting | What it does |
|---------|--------------|
| `EnableHotkeys` | Turn all radio hotkeys on/off (default on) |
| Hotkeys | All keys above |
| `MyRadioVolume` / `OtherRadiosVolume` / `VolumeStep` | How loud your radio / other players' radios are for you, and the F9/F10 step |
| `MaxDistance` / `MinDistance` | How far radios can be heard (default 60 m) |
| `DuckGameMusic`, `GameMusicDuckVolume`, `DuckOnlyWhenInRange`, `ExcludeFromDucking` | Game-music fading |
| `EnableStreaming`, `StreamRateKBps`, `DownloadRateKBps`, `MaxStreamFileMB` | Song streaming |
| `CacheSizeMB` | Size limit for downloaded songs (default 500 MB) |
| `MusicFolder` | Use a different Music folder |
| `ShowHud`, `ShowKeyHints`, `ShowNearbyRadios`, `MarkRadioCarrierName` | What the overlay shows (`ShowKeyHints` = the key hints line in the top-right overlay, default on) |
| `PauseMenuRadioPage`, `PauseMenuButtonPosition` | The pause menu's RADIO button and page, and where the button goes (empty = automatic) |
| `DumpPauseMenuHierarchy` | Write the pause menu's layout to the log once per launch (for layout bug reports) |
| `VerboseLogging` | More detail in the log |

## Known limitations
- **Memory**: each playing radio keeps its song decoded in memory (a 4-minute song can use roughly 40-85 MB), so many radios at once use more RAM.
- **Multiplayer has only been lightly tested.** Please report problems with your `BepInEx/LogOutput.log` (lines tagged `Player Radio`).
- The pause-menu RADIO button is added by the mod to the game's menu. If a game update moves things around, it may sit in an odd spot (set `PauseMenuButtonPosition`) or not show up at all; the hotkeys keep working either way.
- Big lobbies with lots of new songs can take a while to download everything at the default rates.

## Troubleshooting
Check `BepInEx/LogOutput.log` for lines tagged `Player Radio`. It shows the session mode, which players have a compatible radio, the game's key bindings and every song loaded or downloaded.

## AI disclosure
This mod's code was written with AI assistance: it was generated by the **Grok Bot** assistant, and directed, tested and published by **Inkslinger**. The DLL carries the `AI_Assisted_Creation` and `AI_Model_Vendor` assembly metadata, as Thunderstore asks.

## Source and license
- Source: https://github.com/Inkslinger9/PlayerRadio
- License: MIT (see `LICENSE` in the source repository).

## Credits
Made by **Inkslinger**. See `CHANGELOG.md` for version history.
