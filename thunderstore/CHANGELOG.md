# Changelog

## 0.2.2
- F4 now opens the Music folder through Unity (`Application.OpenURL` with a `file:///` URL) instead of starting `explorer.exe`. The mod no longer uses `System.Diagnostics.Process` anywhere.
- README: added an AI disclosure, a "What this mod sends and stores" section, and links to the source code (MIT license).
- The DLL now carries `AI_Assisted_Creation` and `AI_Model_Vendor` assembly metadata.
- No gameplay or config changes.

## 0.2.1
- Default keys swapped: **F5 is now play/pause** and **F6 is previous song**. Existing configs that still use the old defaults (play/pause F6, previous F5) are switched automatically on first launch. Keys you changed yourself are kept.
- Old settings removed in 0.2.0 (`Volume`, `TakeOver`, `SourceHandoff`) are cleaned out of the config file.
- `HOW TO ADD MUSIC.txt` is refreshed with the new keys.
- Icon fix: the radio waves were cut off.

## 0.2.0
- Per-player radios. Every player now has their own radio on their own character. It plays only their own Music folder, and only they control it. Everyone hears every radio in 3D, so several can play at once.
- New `MyRadioVolume` and `OtherRadiosVolume` settings (local only). F8 now mutes/unmutes other players' radios for you.
- A dead player's radio keeps playing where they died until they are revived or leave.
- Game music ducks for your radio and, depending on distance, for other radios near you.
- The overlay lists radios playing nearby.
- Streaming fetches songs from the radio's owner or anyone else who has them, two at a time, and prefetches each owner's next song. New `DownloadRateKBps` setting.
- `Volume` and `SourceHandoff` were removed.
- Not compatible with 0.1.x: everyone needs 0.2.x.

## 0.1.2
- F4 opens your Music folder in File Explorer.
- A `HOW TO ADD MUSIC.txt` is created in the Music folder.
- Songs in subfolders are now found.
- Game-music ducking only fades actual music. Ambience and sound effects that the game tags as music are left alone. The new `ExcludeFromDucking` setting (default `Ambience`) lets you exclude more by name.

## 0.1.1
- The game's own music fades out while the radio is playing and fades back in when it stops (`DuckGameMusic`, `GameMusicDuckVolume`, `DuckOnlyWhenInRange`).

## 0.1.0
- First release.
