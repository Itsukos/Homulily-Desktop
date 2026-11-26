# How Homulily Desktop works

## Reading what is playing

**Apple Music** is read the way AMWin-RP always did: Microsoft's [UI Automation API](https://learn.microsoft.com/en-us/windows/win32/winauto/windows-automation-api-overview), through the FlaUI library, finds the `AppleMusic.exe` window and reads the song, artist, album and progress out of its controls (`AppleMusicClientScraper.cs`). Covers and song links come from searching Apple Music's website (`AppleMusicWebScraper.cs`).

**Every other player** is read from Windows' own media controls, `GlobalSystemMediaTransportControlsSessionManager` (`MediaSessionReader.cs`), the same information the taskbar media flyout shows: title, artist, album, play state, timeline and the album art the player reports. Sessions are sorted into sources by their app id: `spotify`, `musicbee`, `foobar`, `browser`, `other`. Apple Music's own session is skipped, since the UI Automation reader gets more from it.

**MusicBee and foobar2000 without their add-on** report nothing to Windows, so their window titles are parsed instead (`ParseMusicBeeTitle`, `ParseFoobarTitle`). That gives artist and title (and the album for foobar2000's default title), but no length or pause state, so a song of unknown length must play two minutes before it counts. `PlayerSetup.cs` installs the add-ons in one click: it downloads each from its author's GitHub release, checks it (GitHub's published SHA-256 for foo_mediacontrol, a pinned SHA-256 for mb_MediaControl), then hands the component to foobar2000's own installer, or copies the plugin into MusicBee's `Plugins` folder and restarts MusicBee.

## Scrobbling through Homulily

`HomulilyScrobbler.cs` talks to the Homulily bot's web API with a token, one scrobbler per source:

| Request | What for |
|---|---|
| `GET /api/desktop/status` | who you are signed in as, whether Homulily can scrobble for you, what is waiting |
| `POST /api/desktop/nowplaying` | now playing, on a song or pause change and every 30 seconds |
| `POST /api/desktop/scrobble` | finished plays, in batches, with their start times and source |
| `GET /api/desktop/cover` | a cover from Homulily's own lookups (Last.fm, Deezer, iTunes) |
| `POST /api/desktop/art` | upload the player's own cover so Discord can show it |

The scrobble timing rules are AMWin-RP's: half the song or the maximum wait, whichever comes first. Plays the bot could not be reached for wait in `homulily_queue.json` and are retried every minute.

On the bot, Apple Music scrobbles go into the member's scrobble history at once and are queued for Last.fm. Scrobbles from other sources are held ten minutes, then checked against the member's recent Last.fm scrobbles, so a player that scrobbles by itself is never counted twice. The bot sends to Last.fm with the session key the member gave it, and simply waits while Last.fm is down.

**Signing in** opens `/desktop/login` in the browser while the app listens on a random `127.0.0.1` port. The bot logs in with Discord, asks Last.fm to allow scrobbling if it has no login for that member yet, and redirects back to the app's port with a token, which is kept in Windows Credential Manager.

## Discord status

Apple Music uses AMWin-RP's own Discord application. Other players use the Homulily bot's application, so they read *Listening to Homulily*. Discord only shows presence images from a web address, so covers come from, in order: the player's own album art (uploaded to the bot, which serves it at `/desktop/art/<sha256>.jpg`), Homulily's Last.fm, Deezer and iTunes lookup, then Apple Music's website. Apple Music takes over the status while it plays; Spotify is left alone, since it shows its own.

## Updates

`Updater.cs` reads the latest GitHub release. The new exe is downloaded next to the running one and checked against the SHA-256 GitHub publishes for it. Then the running exe is renamed aside (Windows allows renaming a running program, not replacing it), the new one takes its name and is started, and the old copy exits. The renamed copy is deleted on the next start. Any failure puts the old exe back and opens the releases page instead.
