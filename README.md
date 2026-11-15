# Moflex Studio

An all-in-one **SUPER MOFLEX** builder for Windows. Give it a video file, a moflex or a SUPER MOFLEX. It produces a 3DS-ready moflex with track 1 audio, an optional track 2, up to 16 subtitle tracks, metadata and a poster.

Special thanks to lenn3 for discovering that raw AVI without audio prevents the moflex encoder hang at 99%, not things are super fast


![Moflex Studio converting a movie](docs/queue.png)

---

## Contents

- [Install](#install)
- [Quick start](#quick-start)
- [What each tab does](#what-each-tab-does)
- [The queue](#the-queue)
- [How long it takes](#how-long-it-takes)
- [Where files go](#where-files-go)
- [Troubleshooting](#troubleshooting)
- [Command line](#command-line)
- [How it works](#how-it-works)
- [Folder layout](#folder-layout)
- [Building the exe](#building-the-exe)

---

## Install

1. Download **`Moflex.Studio.zip`** from the [Releases page](../../releases) (log in to GitHub first; the repo is private).
2. Unzip it anywhere: Desktop, another drive or a USB stick. Nothing gets installed.
3. Run **`Moflex Studio.exe`**.
   - Windows SmartScreen may say "Windows protected your PC" because the app isn't code-signed. Click **More info → Run anyway**. You only have to do this once.
4. **First launch only:** it downloads two tools on its own, with progress shown in the bottom status bar:
   - **Python 3.11** (embeddable, about 11 MB) from python.org, into `app\python`
   - **ffmpeg** (about 110 MB) from gyan.dev, into `app\bin`

   When the status bar says **Ready**, you're set. If a download fails, go to **Settings → Download missing tools** to retry.
5. *(Optional)* Paste your **TMDB token** into **Settings** and click **Save settings**. It's only needed for the *Search TMDB* button.

Requirements: Windows 10 or 11, 64-bit. It uses the .NET Framework 4.8 that Windows already includes. Free disk space: the temporary lossless AVI is about **8–9 GB for a 90-minute movie**. It goes in your `%TEMP%` folder and is deleted automatically.

To remove it, delete the folder.

---

## Quick start

**Fresh video → SUPER MOFLEX**

1. **Open...** a video file, or drag it onto the window. It reads the file and fills in sensible defaults: layout detected, English audio as track 1, another language as track 2, text subtitles, and the title and year from the file name.
2. Check the tabs (described below). Usually you only touch **Info + poster** (search TMDB, pick the result).
3. Click **Add to queue**. The editor clears so you can set up the next file.
4. Click **Start queue**.

**Edit an existing moflex or SUPER MOFLEX** (add a track 2, add or relabel subtitles, change the poster or info, fix quiet audio): open it the same way. The video is never re-encoded, so this takes seconds.

**Batch:** drag **several files or a whole folder** onto the window, or onto the `.exe` icon. Each one is queued with the current Video/Output settings and the defaults above.

---

## What each tab does

### Source line

Shows what was detected:
- **Video files:** resolution, frame rate, length, audio and subtitle counts, and the 3D layout.
- **Moflex files:** 2D/3D, the existing tracks and subtitles, and whether there's a poster.

### Video

![Video tab](docs/tab-video.png)

| Setting | Meaning |
|---|---|
| **Source layout** | **Auto** (detected), **2D**, **Half SBS** (each eye squeezed to half width) or **Full SBS** (each eye full width). Auto uses the shape of the picture and words like "SBS" in the file name. Set it yourself if Auto guesses wrong. |
| **Output** | **3D** (two 400x240 eyes) or **2D** (one 400x240 picture). 2D output from an SBS source uses the left eye. For a moflex input, this only sets the 3D flag the player reads. |
| **Bitrate** | video bitrate in kbps. 1400 is the default (the official 3D setting). |

The picture is always **letterboxed, never stretched**, so the aspect ratio is kept. Sources above 30 fps are halved (60 → 30).

### Audio

| Video file input | Moflex / SUPER MOFLEX input |
|---|---|
| ![Audio tab, video](docs/tab-audio-video.png) | ![Audio tab, moflex](docs/tab-audio-moflex.png) |

- **Track 1** is the in-band audio that plays in **any** moflex player.
  - **Video input:** pick a source stream, **Other file...** (any video or audio file), or **Silent**.
  - **Moflex input:** **Keep as it is**, **Keep, but fix the loudness** (for quiet files), or **Other file...**.
- **Track 2** is stored in the SUPER MOFLEX trailer. Only the CLOWNSEC player uses it; you switch to it there. Options are **None**, **Keep the current track 2**, a source stream, or **Other file...**.
  - **Label** is what the player shows (e.g. `JPN`, `COM` for commentary, `DUB`): 2–3 letters.
- **Offset** shifts an audio file that doesn't line up with the video, in seconds. Negative means the audio starts earlier.
- Every new track is **loudness-normalized** (EBU R128, -16 LUFS), so tracks sound equally loud.

### Subtitles

![Subtitles tab](docs/tab-subtitles.png)

- The list is what goes into the file, **in order. The first track is the default.** Maximum **16** tracks, each under 1 MB.
- **Add from:**
  - an embedded **text** stream in the source
  - a track the SUPER MOFLEX already has
  - a **.srt / .ass / .ssa file**
  - a text stream from **another video file**
- **Label** is what the player shows (`ENG`, `SPA`, `SDH`, `FRC` for forced and so on). **Offset** shifts the timing in seconds.
- Select a row, change the label or offset, and click **Update selected**. Use **Up/Down** to reorder.
- For video input, subtitle files next to the source with the same base name (`Movie.eng.srt`, `Movie.spa.srt`) are added automatically.
- **Image subtitles (PGS/VobSub) can't be used directly.** OCR them to `.srt` first, then add the file.

### Info + poster

![Info tab](docs/tab-info.png)

- **TMDB search:**
  1. Choose **Movie** or **TV episode**, check the search text (pre-filled from the title), and click **Search TMDB**.
  2. Pick a result from the dropdown. Title, year, genres, runtime, date and description are filled in.
- **TV episodes:**
  1. Set **S** (season) and **E** (episode) *before* picking the show, so the episode title and description are fetched too.
  2. S and E are filled in automatically when the file name contains `S01E05` or `1x05`.
- **Category / Format:** where the CLOWNSEC catalog files it. Format is `converted` for 2D→3D conversions and `native` for real 3D.
- **Poster:**
  - **Image...** uses your own picture. Use this for your "in 3D" artwork.
  - **None** removes it.
  - **Keep original** keeps the one the file already has.
  - A TMDB pick fills in the TMDB poster when there isn't one already.
  - It's stored at 132x188, **stretched, not cropped**.
- **Leave the title empty** to keep the file's current info unchanged.

### Output

![Output tab](docs/tab-output.png)

- **Folder:** empty means next to the source file.
- **File name:** empty means automatic. The automatic name is shown underneath and follows the catalog convention:
  - Movies: `Title (YEAR) (3D) (S).moflex`
  - TV: `Show (YEAR) (3D) (S) - S01e05 - Episode Title.moflex`
- **SUPER MOFLEX:** untick it for a plain moflex (track 1 only, no trailer).
- **Replace the original file:** when editing a moflex in place. Otherwise, if the name would be the same, it's saved as `... (edited).moflex`.

If you set a poster, a `.jpg` copy of it is saved next to the moflex.

### Settings

![Settings tab](docs/tab-settings.png)

- **TMDB token:** the *API Read Access Token* from <https://www.themoviedb.org/settings/api>. It's stored only in `app\settings.json` and never committed.
- **Bitrate / Output folder:** defaults for new jobs.
- **Tools:** shows whether Python, ffmpeg, RunAsDate and the license were found. **Download missing tools** fetches Python and ffmpeg again.

---

## The queue

| Button | What it does |
|---|---|
| **Add to queue** | Adds the editor's settings as a job, then clears the editor. |
| **Edit** (or double-click) | Loads a job back into the editor. The button becomes **Update item**; **Cancel edit** discards your changes. The running job can't be edited. |
| **Remove / Up / Down** | Manage pending jobs. The Delete key also removes. |
| **Clear finished** | Removes the jobs that are done. |
| **Start queue** | Runs every Pending job, top to bottom. Jobs marked Error, Stopped or Interrupted are retried. |
| **Pause / Resume** | Freezes the running job, including the encoder, exactly where it is. Resume continues from there with nothing lost. |
| **Stop** | Ends the running job and deletes its temporary files. Its status becomes **Stopped**, and Start runs it again from the beginning. |

- Double-click a **Done** job to open its folder in Explorer. Hover over a job to see its source, output, and any error.
- **The queue is saved all the time** (`app\queue.json`). If the app, Windows or the power dies mid-job, reopen the app: the interrupted job is back to Pending and the finished ones stay done. Click Start to carry on.
- **A crash never leaves anything running.** ffmpeg and the encoder are tied to the app and end with it. Leftover temporary files are deleted the next time the app starts.
- **Status colors:** green is done, red is an error (hover for the reason), orange is stopped, interrupted or paused.

### Progress stages

For a video file:

| Stage | What happens | Progress shown |
|---|---|---|
| **1/3 AVI** | Decodes the source and writes a lossless 400x240 / 800x240 AVI | real %, time left, speed |
| **2/3 Encoding** |  2 passes | estimate (the encoder reports nothing until it's done) |
| **3/3 Building** | Audio normalize and convert, subtitles, poster, info, trailer | per step |

A moflex input only goes through **Building**.

During the encode, the moflex encoder starts hidden in the system tray. Click its tray icon to watch it if you like. Don't close it, because that ends the encode.

---

## How long it takes

Measured on the server (32 threads), for a 76-minute 1080p HEVC full-SBS movie with two audio tracks:

| Stage | Time |
|---|---|
| Lossless AVI | ~5½ min |
| Moflex encode (3D, 1400 kbps, 2 passes) | ~4½ min |
| Building (two 76-min tracks normalized and converted, poster, info) | ~7 min |
| **Total** | **~17 min** |

Editing an existing moflex takes a few seconds. Converting a new track 2 takes about a minute per hour of audio.

---

## Where files go

- **The finished moflex:** the Output folder, or next to the source. It's written under a temporary name and renamed only when complete, so a half-written file never shows up there.
- **Temporary work:** `%TEMP%\moflex_studio_*`, holding the lossless AVI (several GB for a movie). It's deleted when the job finishes or is stopped, or at the next app start after a crash.
- **Settings:** `app\settings.json`. **Queue:** `app\queue.json`. **Errors:** `app\error.log`.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| "Python / ffmpeg missing" | **Settings → Download missing tools**. You need internet access the first time. |
| "the moflex encoder is already open" | Close the Moflex Encoder GUI (or a stuck in Task Manager). Only one encoder can run at a time. |
| "3D output needs a side-by-side source" | The file looks 2D. Set **Video → Source layout** to HSBS or FSBS, or choose 2D output. |
| A subtitle stream shows "(image: needs OCR)" | It's PGS/VobSub. Convert it to `.srt` with an OCR tool and add the file instead. |
| "subtitle N is ... bytes, over 1 MiB" | Split or clean the subtitle file. The player won't load a track over 1 MB. |
| Search TMDB says "no TMDB token set" | Paste your token in **Settings** and click **Save settings**. |
| Something odd happened | Check `app\error.log`. The app logs unexpected errors there instead of closing, and the log box under the queue shows the details of each job. |
| SmartScreen blocks the exe | **More info → Run anyway**. Or right-click the zip → Properties → **Unblock** before unzipping. |

---

## Command line

```
"Moflex Studio.exe" [files or folders...] [--queue] [--start]
```

- One file loads into the editor. Several files or a folder go into the queue.
- `--queue` queues even a single file. `--start` starts the queue right away.

The backend can also be used on its own:

```
app\python\python.exe app\moflex_studio.py probe <file>
app\python\python.exe app\moflex_studio.py run <job.json>      (one JSON event per line)
app\python\python.exe app\moflex_studio.py tmdb-search movie "Title" [year]
```

The job format is documented at the top of `app\moflex_studio.py`.

---

## How it works

1. **Video file:**
   1. ffmpeg makes a **lossless, video-only AVI**: x264 `qp 0`, each eye letterboxed into 400x240 (3D is 800x240 side by side).
   2. The official moflex encoder runs headless from a one-job queue (`-q`), launched through **RunAsDate** with a 2022 clock because the license has expired.
   3. The AVI has no audio on purpose. With audio in it, Moflex encoders final write runs single-threaded and takes about 45 minutes for a movie; without audio it takes minutes. The audio is added in the next step. (thanks for lenn3 for this discovery)
2. **Assemble** (every input):
   1. Track 1 is normalized and converted to 3DS ADPCM, then written into the moflex in-band.
   2. A new **CSXTRA trailer** is written:
      - `NFO0` metadata
      - `ART5` poster
      - `LNG0` track 1 language
      - `SUB1` subtitles (up to 16)
      - `AUD1` track 2
   3. Sections the file already had are copied byte for byte unless you changed them.
   4. The result is checked before it's moved into place.

---

## Folder layout

```
Moflex Studio.exe        GUI (built from src\)
app\moflex_studio.py     backend
app\tools\               shared SUPER MOFLEX helpers (copies; ./sync_tools.sh refreshes them from moflex-encoder)
app\templates\           Moflex encoder one-job queue templates (2D, 3D)
app\encoder\             Moflex encoder (portable copy)
app\RunAsDate.exe        runs the encoder with a 2022 clock
app\User_CTR.lic         the encoder license
app\python\  app\bin\    downloaded on first run (not in git)
app\settings.json        your settings + TMDB token (not in git)
app\queue.json           the saved queue (not in git)
src\                     MoflexStudio.cs, build.bat, icon.ico
docs\                    screenshots for this README
```

## Building the exe

On Windows, run `src\build.bat`. It uses the `csc.exe` that ships with Windows (.NET Framework 4.x, C# 5), so there's nothing to install, and it writes `Moflex Studio.exe` next to `app\`.
