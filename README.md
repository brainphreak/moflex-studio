# Moflex Studio

All-in-one SUPER MOFLEX builder for Windows. Private: contains the Mobiclip encoder and license.

## Use

1. Code → Download ZIP (or `git clone`), unzip anywhere.
2. Run **Moflex Studio.exe**. If SmartScreen warns about an unsigned app, click *More info → Run anyway*.
3. On first launch it downloads Python 3.11 (python.org) into `app\python` and ffmpeg (gyan.dev) into `app\bin`.
4. Optional: paste your TMDB token in **Settings** for the Search TMDB button.

Nothing gets installed. Delete the folder to remove it. Needs Windows 10/11 (it uses the built-in .NET Framework 4.8).

## What it does

Input: a video file (2D, half SBS or full SBS), a plain moflex or a SUPER MOFLEX.

- **Video file:** builds a lossless 400x240 / 800x240 AVI (letterboxed, never stretched), encodes it with the official Mobiclip encoder (headless, 2-pass), and then assembles the result.
- **Moflex or SUPER MOFLEX:** goes straight to assembly. The video isn't touched, and existing trailer sections are kept byte for byte unless you change them.

Assembly covers:

- Track 1, the in-band audio: keep it, normalize it, or replace it.
- Track 2, a trailer ADPCM track.
- Up to 16 subtitle tracks (SRT or ASS).
- Metadata, scraped from TMDB or typed in.
- A poster.
- The 3D flag.

Queue: set the fields, click *Add to queue*, set up the next file, then *Start queue*. Items can be edited, reordered or removed.
Drop several files or a folder (on the window or the .exe) to queue them all with the current settings.
Command line: `"Moflex Studio.exe" [files...] [--queue] [--start]`.

## Layout

```
Moflex Studio.exe        GUI (built from src\)
app\moflex_studio.py     backend (probe / tmdb-search / tmdb-details / export-art / run job.json)
app\tools\               shared super moflex helpers (copies; ./sync_tools.sh refreshes them from moflex-encoder)
app\templates\           Mobiclip one-job queue templates (2D, 3D)
app\encoder\             Mobiclip Multicore Encoder 2.0.5 (portable copy)
app\RunAsDate.exe        runs the encoder with a 2022 clock (the license has expired)
app\User_CTR.lic         the encoder license
app\settings.json        created by the app (TMDB token, defaults): not in git
src\                     MoflexStudio.cs + build.bat (uses the csc.exe that ships with Windows)
```

## Rebuild the exe

Run `src\build.bat` on Windows. It writes `Moflex Studio.exe` next to `app\`.
