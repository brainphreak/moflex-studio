#!/usr/bin/env python3
"""moflex_studio -- the all-in-one SUPER MOFLEX builder.

Three kinds of input, one set of edits:
  video file (mkv/mp4/avi...)  -> lossless 400x240 AVI -> official Mobiclip encoder (-q, headless)
                                  -> plain moflex -> assemble
  plain moflex                 -> assemble (video untouched)
  SUPER MOFLEX                 -> assemble (video untouched; every trailer section it already has
                                  can be kept byte for byte, dropped, relabelled or replaced)

assemble = in-band audio (keep | normalise | replace) + a new CSXTRA trailer:
  NFO0 metadata, ART5 poster, LNG0 in-band language, SUB1 x up to 16, AUD1 second audio.

Commands (JSON out, for the GUI):
  probe <file>                         tracks, source type, title/year/episode, and for a moflex
                                       its trailer contents (metadata, audio, subtitles, poster)
  export-art <file.moflex> <out.png>   the poster stored in a SUPER MOFLEX, as PNG
  tmdb-search <movie|tv> <query> [year]
  tmdb-details <movie|tv> <id> [S:E]
  run <job.json>                       one job; one JSON event per line
  make SOURCE [options]                the same from command-line options, for people (make --help)
  iw3-status                           2D to 3D: is iw3 installed, its models, defaults and templates

Job (every field optional except source):
{
 "source": "path",
 "source_type": "auto|2d|hsbs|fsbs",       video input: how the picture is laid out
 "output": "3d|2d",                        video input: what to encode; any input: the 3D flag
 "bitrate": 1400,                          video input
 "audio": {
   "main":   {"keep": true} | {"normalize": true}
           | {"file": null|"path", "stream": N, "offset": 0.0, "lang": "eng"},
   "extra":  [ {"keep": 0, "label": "JPN"},                 trailer track 0 the file already has
               {"file": null|"path", "stream": N, "offset": 0.0, "lang": "jpn", "label": "COM"} ]
             audio tracks 2..8 (CLOWNSEC player only; the official player plays track 1).
             Older jobs may give "second": {...} | null instead -- read as a one-item "extra".
 },
 "versions": [ {"existing": 0, "label": "B&W"},                an alternate the SUPER MOFLEX already has
               {"source": "colour.mkv", "source_type": "auto", "label": "COLOR"},   encoded like the main
               {"source": "bw.moflex", "label": "B&W"} ],      an existing moflex (same size + 3D mode)
             alternate VIDEO versions (CLOWNSEC player: VIDEO > Version); same timeline as the main.
 "main_label": "ORIG",                     the main version's name in that menu
             Each version row may carry "default_audio": N (1 = track 1 .. 8), the track the player
             switches to with that version (VAU0); "main_default_audio" does it for the main one
             in jobs without a {"self": true} row. Default 1.
 "subtitles": [ {"existing": 0, "label": "eng"},              a track the SUPER MOFLEX already has
                {"file": "x.eng.srt", "label": "eng", "offset": 0.0},
                {"file": null|"movie.mkv", "stream": 3, "label": "dub"} ],
 "metadata": null | {"title","year","category","genres","runtime","date","desc","showdesc",
                     "eptitle","format": "native|converted", "season_episode": [S, E]},
 "art": null | "keep" | "none" | "poster.jpg" | "https://...",
 "super": true,                            false: plain moflex (in-band audio only)
 "output_dir": "", "output_name": "", "overwrite": false
}
"""
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import uuid

HERE = os.path.dirname(os.path.realpath(__file__))
IS_WIN = os.name == 'nt'
NO_WINDOW = 0x08000000 if IS_WIN else 0
IMAGE_SUBS = {'hdmv_pgs_subtitle', 'dvd_subtitle', 'dvdsub', 'dvb_subtitle', 'xsub', 'pgssub'}
MAX_SUBS = 16
MAX_SUB_BYTES = 1024 * 1024 - 4
MAX_EXTRA_AUDIO = 7                     # + the in-band track = the 8 the player's audio menu holds
MAX_VERSIONS = 3                        # alternate video versions besides the main one

# ------------------------------------------------------------------ settings
def load_settings():
    s = {
        'ffmpeg_dir': os.path.join(HERE, 'bin') if os.path.isdir(os.path.join(HERE, 'bin')) else '',
        'encoder_dir': os.path.join(HERE, 'encoder'),
        'encoder': 'mobiclip',          # the moflex encoder: 'mobiclip' (Nintendo's, Windows) or 'open'
        'open_encoder': os.path.join(HERE, 'bin', 'moflex-ffmpeg.exe' if os.name == 'nt' else 'moflex-ffmpeg'),
        'runasdate': os.path.join(HERE, 'RunAsDate.exe'),
        'runasdate_date': '14\\01\\2022 00:00:00',
        'license': os.path.join(HERE, 'User_CTR.lic'),
        'tools_dir': os.path.join(HERE, 'tools'),
        'tmdb_token': '',
        'max_fps': 30,
        'iw3_dir': '',                  # iw3 (nunif's Windows package); '' = look in the usual places
    }
    p = os.path.join(HERE, 'settings.json')
    if os.path.exists(p):
        with open(p, encoding='utf-8') as f:
            s.update(json.load(f))
    if not s['tmdb_token']:
        s['tmdb_token'] = os.environ.get('TMDB_TOKEN', '')
    tf = os.path.expanduser('~/.config/moflex-encoder/tmdb_token')
    if not s['tmdb_token'] and os.path.exists(tf):
        s['tmdb_token'] = open(tf).readline().strip()
    return s

S = load_settings()
if S['ffmpeg_dir']:
    os.environ['PATH'] = S['ffmpeg_dir'] + os.pathsep + os.environ.get('PATH', '')
os.environ['PYTHONUTF8'] = '1'
if S['tmdb_token']:
    os.environ['TMDB_TOKEN'] = S['tmdb_token']
FFMPEG, FFPROBE = 'ffmpeg', 'ffprobe'
sys.path.insert(0, S['tools_dir'])

def tools():
    """the shared super moflex helpers (imported lazily: probe/tmdb do not need them)"""
    import make_super_moflex as msm
    import moflex_addstreams as mas
    import ass_to_srt
    return msm, mas, ass_to_srt

for _st in (sys.__stdout__, sys.__stderr__):   # UTF-8 always: a pipe or file isn't on Windows by default,
    try:                                       # and a progress bar's block characters then crash a print
        _st.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

HUMAN = False   # the 'make' command: plain lines for a person instead of JSON events for the GUI

def emit(_type, **kw):
    kw['type'] = _type
    if HUMAN:
        return human(kw)
    sys.__stdout__.write(json.dumps(kw, ensure_ascii=False) + '\n')
    sys.__stdout__.flush()

_last_pct = [None, -10.0]
def human(e):
    """one event as a person reads it: stages and warnings on their own line, progress on one updating line"""
    t, out = e['type'], sys.__stdout__
    if t == 'progress':
        pct = e.get('pct') or 0
        if e.get('stage') != _last_pct[0] or pct - _last_pct[1] >= 1 or pct >= 100:
            _last_pct[0], _last_pct[1] = e.get('stage'), pct
            eta = f", about {clock(e['eta'])} left" if e.get('eta') else ''
            out.write(f"\r  {pct:5.1f}%{eta}        ")
            out.flush()
        return
    if t == 'stage':
        out.write(f"\n[{e.get('index')}/{e.get('total')}] {e.get('text')}\n")
    elif t == 'log' and str(e.get('text', '')).startswith('WARNING'):
        out.write(f"\n  {e['text']}\n")
    elif t == 'start':
        out.write(f"{os.path.basename(e.get('source', ''))}: {e.get('kind')} -> {e.get('output', '').upper()}"
                  f", {clock(e.get('duration') or 0)}\n")
    elif t == 'done':
        mb = (e.get('size') or 0) / 1048576
        out.write(f"\nDone in {clock(e.get('seconds') or 0)}: {e.get('output')} ({mb:.0f} MB)\n")
        for w in e.get('warnings') or []:
            out.write(f"  check: {w}\n")
    elif t == 'error':
        out.write(f"\nError: {e.get('message')}\n")
    out.flush()

MAKE_HELP = """moflex-studio make -- everything the window does, from the command line

  moflex-studio make SOURCE [options]
  moflex-studio probe SOURCE      the source's audio and subtitle streams (their numbers) and info
  moflex-studio run JOB.json      a job file (what the window writes; --save-job makes one)

Only SOURCE is needed: every option below has a default -- the same choices the window makes for a
newly opened file -- shown in [brackets].

VIDEO
  --3d | --2d             what to encode             [3D for a side-by-side source or with --conv3d, else 2D]
  --source-type T         auto, 2d, hsbs, fsbs       [auto: detected from the picture and file name]
  --bitrate KBPS          video bitrate              [1400, the official 3D setting]
  --encoder E             mobiclip or open           [Settings: mobiclip]
  --version SPEC          another version of the film (repeatable, up to 3)       [none]
                            FILE[::label=B&W,type=hsbs,audio=2]   (audio = the track it switches to)
  --main-label NAME       the main version's name in the Version menu             [none]

2D TO 3D (iw3)                                                                    [off]
  --conv3d [TEMPLATE]     make a 2D source 3D first: best, high, medium, low, or yours   [best]
  --model MODEL           the depth model                                         [the template's]
  --iw3 KEY=VALUE         any iw3 setting (repeatable)                            [the template's]
  --keep-sbs              keep iw3's 3D side-by-side video                        [deleted]
  --templates             list the templates, models and iw3 settings, then exit

AUDIO (track 1 plays everywhere; tracks 2-8 in the CLOWNSEC player)
  --audio N               track 1 = the source's audio stream N   [the first English one, else the default]
  --audio-file FILE[:N]   track 1 from another file                               [no]
  --audio-offset SECONDS  shift track 1                                           [0]
  --lang CODE             track 1's language                                      [the stream's, else eng]
  --normalize             (an existing moflex) fix track 1's loudness             [video sources: always]
  --add-audio SPEC        another track (repeatable, up to 7): N | FILE[:N] [::label=COM,lang=eng,offset=0.5]
                                                    [every other audio stream of the source, labelled]
  --no-extra-audio        only track 1

SUBTITLES (up to 16)
  --sub SPEC              N | FILE.srt/.ass/.mkv[:N]  [::label=eng,offset=-1.2]   (repeatable)
                          [every text subtitle in the source, and FILE.xx.srt/.ass beside it]
  --no-subs               none

INFO                                                     [title, year, season/episode: from the file name]
  --title T  --year Y  --genres G  --desc D  --runtime MIN  --date YYYY-MM-DD
  --category C            Movies, TV Shows, Shorts, Music Videos   [Movies; TV Shows for S01E02 names]
  --season S --episode E --eptitle T --show-desc D                        (a TV episode)
  --format native|converted                                               [native]
  --tmdb                  fill the info and poster in from TMDB (no token needed)  [off]
  --poster FILE|URL       a poster                     [none, or TMDB's with --tmdb]
  --no-poster             no poster even with --tmdb

OUTPUT
  -o, --output FILE       the output file              ["Title (Year) (3D) (S).moflex" beside the source]
  --out DIR  --name NAME  just the folder / just the name
  --overwrite             replace an existing file     [no: stops instead]
  --plain                 a plain moflex (no SUPER MOFLEX info, subtitles or extra audio)   [SUPER MOFLEX]
  --save-job FILE         write the job file instead of running it (edit it, then: run FILE)
  --keep-work             keep the work folder         [deleted]
  --json                  JSON events instead of plain lines

A SPEC's options follow "::", e.g.  --sub "C:\\subs\\movie.eng.srt::label=eng,offset=0.5"

Examples:
  moflex-studio make "Ace Ventura (1994).mkv" --conv3d
  moflex-studio make "Avatar (2009) Half SBS.mkv" --tmdb --no-extra-audio
  moflex-studio make "Show S01E02.mkv" --2d --audio 1 --add-audio 2::label=JPN -o "D:\\3DS\\Show S01E02.moflex"
  moflex-studio make "Film.mkv" --encoder open --bitrate 1500 --save-job film.json
"""

def _sub_label(lang, title, forced):
    t = (title or '').lower()
    if 'sdh' in t or 'hearing' in t:
        return 'sdh'
    if forced or 'forced' in t:
        return 'frc'
    if 'comment' in t:
        return 'com'
    lang = (lang or '').lower()
    return lang if re.fullmatch(r'[a-z][a-z0-9]{1,2}', lang) and lang != 'und' else 'eng'

def _sidecar_label(f):
    m = re.search(r'\.([A-Za-z][A-Za-z0-9]{1,2})\.(srt|ass|ssa)$', os.path.basename(f), re.I)
    if m:
        return m.group(1).lower()
    return 'sdh' if re.search('sdh', f, re.I) else 'frc' if re.search('forced', f, re.I) else 'eng'

def default_tracks(info, src):
    """what the window picks for a newly opened video: track 1 = the first English audio (else the
    default, else the first); every other audio stream as tracks 2-8; every text subtitle stream and
    any FILE.*.srt/.ass beside the source"""
    streams = info.get('audio') or []
    main = (next((x for x in streams if (x.get('lang') or '').startswith('en')), None)
            or next((x for x in streams if x.get('default')), None) or (streams[0] if streams else None))
    aud = {'main': {}, 'extra': []}
    if main:
        ml = main.get('lang') or ''
        aud['main'] = {'file': None, 'stream': main['index'], 'offset': 0.0,
                       'lang': (ml if ml not in ('', 'und') else 'eng')[:3]}
        for x in streams:
            if x is main or len(aud['extra']) >= 7:
                continue
            sl, t = (x.get('lang') or '').lower(), (x.get('title') or '').lower()
            lbl = 'com' if 'comment' in t else sl if re.fullmatch(r'[a-z][a-z0-9]{1,2}', sl) and sl != 'und' else f"a{len(aud['extra']) + 2}"
            aud['extra'].append({'file': None, 'stream': x['index'], 'offset': 0.0, 'lang': lbl, 'label': lbl})
    subs = []
    for x in info.get('subtitles') or []:
        if x.get('image') or len(subs) >= 16:
            continue
        subs.append({'file': None, 'stream': x['index'], 'offset': 0.0,
                     'label': _sub_label(x.get('lang'), x.get('title'), x.get('forced'))})
    base = os.path.splitext(src)[0]
    d = os.path.dirname(src) or '.'
    for f in sorted(os.listdir(d)):
        p = os.path.join(d, f)
        if len(subs) < 16 and p.lower().startswith(base.lower() + '.') and f.lower().endswith(('.srt', '.ass', '.ssa')):
            subs.append({'file': p, 'offset': 0.0, 'label': _sidecar_label(p)})
    return aud, subs

def _spec(text):
    """'FILE[:N][::k=v,k=v]' or 'N[::...]' -> (file or None, stream or None, {k: v})"""
    body, _, opts = text.partition('::')
    kv = {}
    for part in filter(None, opts.split(',')):
        k, _, v = part.partition('=')
        kv[k.strip().lower()] = v.strip()
    if re.fullmatch(r'\d+', body.strip()):
        return None, int(body), kv
    m = re.match(r'^(.*?)(?::(\d+))?$', body.strip())
    f, n = m.group(1), m.group(2)
    if not os.path.isfile(f):
        raise RuntimeError(f'no such file: {f}')
    return os.path.abspath(f), (int(n) if n else None), kv

def _val(v):
    """an iw3 setting's value from text: numbers, true/false, a list like 3,1"""
    lv = v.strip().lower()
    if lv in ('true', 'yes', 'on'):
        return True
    if lv in ('false', 'no', 'off'):
        return False
    if ',' in v or '/' in v:
        return [_val(x) for x in re.split(r'[,/]', v)]
    try:
        return int(v)
    except ValueError:
        pass
    try:
        return float(v)
    except ValueError:
        return v

def make_cmd(argv):
    """the 'make' command: a job from command-line options, then run it"""
    global HUMAN
    import argparse
    ap = argparse.ArgumentParser(prog='moflex-studio make', add_help=False)
    ap.add_argument('source', nargs='?')
    ap.add_argument('--3d', dest='out3d', action='store_true')
    ap.add_argument('--2d', dest='out2d', action='store_true')
    ap.add_argument('--source-type', default='auto', choices=['auto', '2d', 'hsbs', 'fsbs'])
    ap.add_argument('--conv3d', nargs='?', const='best')
    ap.add_argument('--keep-sbs', action='store_true')
    ap.add_argument('--templates', action='store_true')
    ap.add_argument('--title'); ap.add_argument('--year', type=int)
    ap.add_argument('--category', default='Movies'); ap.add_argument('--genres'); ap.add_argument('--desc')
    ap.add_argument('--tmdb', action='store_true'); ap.add_argument('--poster')
    ap.add_argument('--bitrate', type=int); ap.add_argument('--out'); ap.add_argument('--name')
    ap.add_argument('--encoder', choices=['mobiclip', 'open'])
    ap.add_argument('--plain', action='store_true'); ap.add_argument('--keep-work', action='store_true')
    ap.add_argument('--json', action='store_true')
    ap.add_argument('--model'); ap.add_argument('--iw3', action='append', default=[])
    ap.add_argument('--version', action='append', default=[]); ap.add_argument('--main-label')
    ap.add_argument('--audio', type=int); ap.add_argument('--audio-file'); ap.add_argument('--audio-offset', type=float)
    ap.add_argument('--normalize', action='store_true'); ap.add_argument('--lang')
    ap.add_argument('--add-audio', action='append', default=[]); ap.add_argument('--sub', action='append', default=[])
    ap.add_argument('--runtime', type=int); ap.add_argument('--date')
    ap.add_argument('--season', type=int); ap.add_argument('--episode', type=int); ap.add_argument('--eptitle')
    ap.add_argument('--show-desc'); ap.add_argument('--format', choices=['native', 'converted'])
    ap.add_argument('--no-poster', action='store_true'); ap.add_argument('-o', '--output')
    ap.add_argument('--overwrite', action='store_true'); ap.add_argument('--save-job')
    ap.add_argument('--no-extra-audio', action='store_true'); ap.add_argument('--no-subs', action='store_true')
    ap.add_argument('-h', '--help', action='store_true')
    a = ap.parse_args(argv)
    HUMAN = not a.json
    if a.help or (not a.source and not a.templates):
        print(MAKE_HELP); return
    if a.templates:
        print('Templates:')
        for k, v in iw3_templates().items():
            c = iw3_settings({'template': k})
            print(f"  {k:<10} {v.get('label', ''):<40} {c['model']}, depth {c['resolution']}, {c['method']}"
                  f"{'' if v.get('builtin') else '  (yours)'}")
        print('Models (--model):')
        for m in (iw3_catalog() if iw3_find()[0] else [{'model': k, 'label': v[2], 'mb': v[0], 'have': False} for k, v in IW3_MODEL_FILES.items()]):
            print(f"  {m['model']:<14} {m['label']:<48} {'downloaded' if m['have'] else str(m['mb']) + ' MB'}")
        print('iw3 settings (--iw3 KEY=VALUE): ' + ', '.join(k for k in IW3_DEFAULTS if k not in ('keep_sbs',)))
        return
    src = os.path.abspath(a.source)
    if not os.path.isfile(src):
        raise RuntimeError(f'no such file: {src}')
    info = probe(src)
    title, year, se = info['parsed']['title'], info['parsed']['year'], info['parsed']['season_episode']
    md = {'title': a.title or title, 'year': a.year or year or 0, 'category': a.category,
          'genres': a.genres or '', 'desc': a.desc or ''}
    if se and not a.title:
        md['season_episode'] = se
        md['category'] = 'TV Shows' if a.category == 'Movies' else a.category
    if a.tmdb:
        kind = 'tv' if md.get('season_episode') else 'movie'
        hits = tmdb_search(kind, md['title'], md['year'] or None)
        if hits:
            d = tmdb_details(kind, hits[0]['id'], md.get('season_episode'))
            md.update({k: v for k, v in d.items() if v})
    job = {'source': src, 'source_type': a.source_type, 'metadata': md, 'super': not a.plain,
           'output_dir': os.path.abspath(a.out) if a.out else '', 'keep_work': a.keep_work}
    if a.conv3d or a.model or a.iw3:
        tpl = a.conv3d or 'best'
        if tpl not in iw3_templates():
            raise RuntimeError(f"no 2D-to-3D template '{tpl}' (see --templates)")
        job['conv3d'] = {'enabled': True, 'template': tpl, 'keep_sbs': a.keep_sbs}
        if a.model:
            if a.model not in IW3_MODELS:
                raise RuntimeError(f'unknown depth model {a.model} (see --templates)')
            job['conv3d']['model'] = a.model
        for kv in a.iw3:
            k, _, v = kv.partition('=')
            if k.strip() not in IW3_DEFAULTS:
                raise RuntimeError(f'unknown iw3 setting {k} (see --templates)')
            job['conv3d'][k.strip()] = _val(v)
        job['source_type'] = '2d' if a.source_type == 'auto' else a.source_type
        a.conv3d = tpl
    # episode info
    if a.season is not None or a.episode is not None:
        md['season_episode'] = [a.season or 0, a.episode or 0]
        if md['category'] == 'Movies':
            md['category'] = 'TV Shows'
    for k, v in (('eptitle', a.eptitle), ('showdesc', a.show_desc), ('runtime', a.runtime), ('date', a.date),
                 ('format', a.format)):
        if v is not None:
            md[k] = v
    # audio: the window's defaults unless told otherwise
    dflt_aud, dflt_subs = default_tracks(info, src) if info['kind'] == 'video' else ({}, [])
    aud = {}
    main = {}
    if a.normalize and info['kind'] != 'video':     # a video source's track 1 is always normalised
        main['normalize'] = True
    if a.audio is not None:
        main.update({'file': None, 'stream': a.audio})
    if a.audio_file:
        f, n, _ = _spec(a.audio_file)
        main.update({'file': f, 'stream': n if n is not None else 0})
    if a.audio_offset is not None:
        main.setdefault('file', None); main['offset'] = a.audio_offset
    if a.lang:
        main['lang'] = a.lang
    if 'file' in main and 'stream' not in main:
        main['stream'] = next((x['index'] for x in info['audio'] if x.get('default')), info['audio'][0]['index'] if info['audio'] else 0)
    if main:
        if 'file' not in main and dflt_aud.get('main'):
            main = dict(dflt_aud['main'], **main)
        aud['main'] = main
    elif dflt_aud.get('main'):
        aud['main'] = dict(dflt_aud['main'])
    extra = []
    for sp in a.add_audio:
        f, n, kv = _spec(sp)
        it = {'file': f, 'stream': n if n is not None else 0, 'offset': float(kv.get('offset', 0) or 0)}
        if kv.get('lang'): it['lang'] = kv['lang']
        if kv.get('label'): it['label'] = kv['label'][:3].upper()
        extra.append(it)
    if not a.add_audio and not a.no_extra_audio:
        extra = [x for x in dflt_aud.get('extra') or []
                 if not (aud.get('main', {}).get('file') is None and x['stream'] == aud.get('main', {}).get('stream'))]
    if extra and not a.no_extra_audio:
        aud['extra'] = extra
    if aud:
        job['audio'] = aud
    # subtitles
    subs = []
    for sp in a.sub:
        f, n, kv = _spec(sp)
        it = {'file': f, 'offset': float(kv.get('offset', 0) or 0)}
        if n is not None:
            it['stream'] = n
        lab = kv.get('label')
        if not lab:
            src_s = next((x for x in info.get('subtitles') or [] if f is None and x.get('index') == n), None)
            lab = (src_s or {}).get('lang') or (re.search(r'\.([a-z]{2,3})\.[^.]+$', f or '') or [None, 'sub'])[1]
        it['label'] = str(lab)[:3].lower()
        subs.append(it)
    if not a.sub and not a.no_subs:
        subs = dflt_subs
    if subs and not a.no_subs:
        job['subtitles'] = subs
    # versions
    vers = []
    for sp in a.version:
        f, _, kv = _spec(sp)
        row = {'source': f, 'source_type': kv.get('type', 'auto'), 'label': kv.get('label', '')[:7]}
        if kv.get('audio'):
            row['default_audio'] = int(kv['audio'])
        vers.append(row)
    if vers:
        job['versions'] = [{'self': True, 'label': (a.main_label or '')[:7]}] + vers
        job['main_label'] = (a.main_label or '')[:7]
    # output
    if a.output:
        o = os.path.abspath(a.output)
        job['output_dir'], job['output_name'] = os.path.dirname(o), os.path.splitext(os.path.basename(o))[0]
    if a.overwrite:
        job['overwrite'] = True
    if a.no_poster:
        job['art'] = 'none'
    if info['kind'] == 'video':
        is3d_src = (job['source_type'] if job['source_type'] != 'auto' else info['video']['detected']) != '2d'
        job['output'] = '2d' if a.out2d else '3d' if (a.out3d or a.conv3d or is3d_src) else '2d'
    elif a.out3d or a.out2d:
        job['output'] = '3d' if a.out3d else '2d'
    if a.bitrate:
        job['bitrate'] = a.bitrate
    if a.encoder:
        job['encoder'] = a.encoder
    if a.name:
        job['output_name'] = a.name
    if a.poster:
        job['art'] = a.poster
    if a.save_job:
        with open(a.save_job, 'w', encoding='utf-8') as f:
            json.dump(job, f, indent=1, ensure_ascii=False)
        print(f'wrote {a.save_job} -- run it with: moflex-studio run "{a.save_job}"')
        return
    fd, jobfile = tempfile.mkstemp(prefix='moflex_job_', suffix='.json')
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        json.dump(job, f)
    try:
        run_job(jobfile)
    finally:
        os.remove(jobfile)

def log(text, stage=None):
    emit('log', stage=stage, text=text)

def warn(job, text):
    """something the user should look at: logged now, listed again when the job finishes"""
    log('WARNING: ' + text, 'check')
    job.setdefault('_warnings', []).append(text)

def clock(sec):
    sec = int(round(sec))
    return f'{sec // 3600}:{sec // 60 % 60:02d}:{sec % 60:02d}' if sec >= 3600 else f'{sec // 60}:{sec % 60:02d}'

def trim_wav(path, secs):
    """cut a 16-bit WAV to `secs` (in place, streamed: an hour of PCM is ~600 MB). Returns the seconds cut."""
    import wave
    with wave.open(path, 'rb') as w:
        par, n, rate = w.getparams(), w.getnframes(), w.getframerate()
        keep = int(secs * rate)
        if n <= keep:
            return 0.0
        tmp = path + '.trim'
        with wave.open(tmp, 'wb') as o:
            o.setparams(par)
            left = keep
            while left > 0:
                b = w.readframes(min(1 << 20, left))
                if not b:
                    break
                o.writeframes(b)
                left -= len(b) // (par.sampwidth * par.nchannels)
    os.replace(tmp, path)
    return (n - keep) / rate

class _LogWriter:
    """print() from the shared tools becomes 'log' events, so stdout stays one JSON object per line"""
    def __init__(self, stage): self.stage, self.buf = stage, ''
    def write(self, s):
        self.buf += s
        while '\n' in self.buf:
            line, self.buf = self.buf.split('\n', 1)
            if line.strip():
                sys.__stdout__.write(json.dumps({'type': 'log', 'stage': self.stage, 'text': line}, ensure_ascii=False) + '\n')
                sys.__stdout__.flush()
    def flush(self): pass

def quiet_tools(stage='super'):
    import contextlib
    return contextlib.redirect_stdout(_LogWriter(stage))

def run_quiet(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace',
                          creationflags=NO_WINDOW, **kw)

# ------------------------------------------------------------------ CSXTRA trailer
class Sec:
    """a big trailer section (audio, video) left in its file: copied across in chunks, never loaded.
    `patch` = (offset in the section, bytes) applied on the way out (a relabel)."""
    def __init__(self, path, off, size, head, patch=None):
        self.path, self.off, self.size, self.head, self.patch = path, off, size, head, patch
    def __len__(self):
        return self.size
    def __getitem__(self, k):            # only ever the header bytes (4cc, length, label)
        return self.head[k]
    def relabel(self, at, data):
        return Sec(self.path, self.off, self.size, self.head[:at] + data + self.head[at + len(data):], (at, data))
    def write_to(self, fo):
        with open(self.path, 'rb') as fi:
            fi.seek(self.off)
            left, first = self.size, True
            while left > 0:
                b = fi.read(min(left, 8 << 20))
                if not b:
                    raise RuntimeError('source file shrank while copying a trailer section')
                if first and self.patch:
                    at, data = self.patch
                    b = b[:at] + data + b[at + len(data):]
                first = False
                fo.write(b); left -= len(b)

class VidSec:
    """'VID1' + label + a whole block-only moflex file, streamed"""
    def __init__(self, path, label):
        self.path, self.label = path, label
        self.size = 16 + os.path.getsize(path)
    def __len__(self):
        return self.size
    def write_to(self, fo):
        fo.write(struct.pack('<4sI', b'VID1', self.size - 8) + self.label)
        with open(self.path, 'rb') as fi:
            shutil.copyfileobj(fi, fo, 8 << 20)

def load_sec(s):
    if not isinstance(s, Sec):
        return s
    with open(s.path, 'rb') as fi:
        fi.seek(s.off)
        return fi.read(s.size)

def write_sec(fo, s):
    if isinstance(s, (Sec, VidSec)):
        s.write_to(fo)
    else:
        fo.write(s)

LAZY = (b'AUD0', b'AUD1', b'VID1')

def read_trailer(path):
    """-> dict(base=len of the block region, sections=[(4cc, section bytes or Sec)], parsed views)"""
    size = os.path.getsize(path)
    t = {'base': size, 'sections': [], 'nfo': {}, 'lang_main': '', 'audio2': None, 'audios': [],
         'videos': [], 'vid_main': '', 'vaud': [], 'subs': [], 'art': False}
    with open(path, 'rb') as f:
        if size < 16:
            return t
        f.seek(size - 16)
        ft = f.read(16)
        if ft[8:] != b'CSXTRA01':
            return t
        off = struct.unpack('<Q', ft[:8])[0]
        end = size - 16
        if off > end:                                # a footer pointing past the file: damaged, or a cut copy
            raise RuntimeError('the SUPER MOFLEX trailer is damaged (it points past the end of the file)')
        t['base'] = off
        q = off
        while q + 8 <= end:
            f.seek(q)
            hd = f.read(32)
            cc, ln = struct.unpack_from('<4sI', hd, 0)
            if q + 8 + ln > end:
                raise RuntimeError(f'corrupt trailer section at {q - off}')
            if cc in LAZY:
                t['sections'].append((cc, Sec(path, q, 8 + ln, hd[:min(len(hd), 8 + ln)])))
            else:
                f.seek(q)
                t['sections'].append((cc, f.read(8 + ln)))
            q += 8 + ln
    for cc, sec in t['sections']:
        body = sec[8:]
        if cc == b'NFO0':
            for line in body.decode('utf-8', 'replace').split('\n'):
                if '=' in line:
                    k, _, v = line.partition('=')
                    t['nfo'][k.strip()] = v.strip('\r\n')
        elif cc == b'LNG0':
            t['lang_main'] = body[:4].rstrip(b'\0').decode('ascii', 'replace')
        elif cc in (b'AUD0', b'AUD1'):
            if cc == b'AUD1':
                rate, ch, spp = struct.unpack_from('<IHH', body, 0)
                lang = body[8:12].rstrip(b'\0').decode('ascii', 'replace')
                data_len = len(sec) - 8 - 12
            else:
                rate, ch = struct.unpack_from('<IH', body, 0); spp = 1024; lang = ''; data_len = len(sec) - 8 - 8
            secs = data_len * 2 / max(1, ch) / max(1, rate)          # 4-bit IMA: ~2 samples per byte
            a = {'existing': len(t['audios']), 'lang': lang, 'rate': rate, 'channels': ch,
                 'seconds': round(secs, 1), 'bytes': len(sec) - 8}
            t['audios'].append(a)
            t['audio2'] = t['audio2'] or a
        elif cc == b'VID1':
            t['videos'].append({'existing': len(t['videos']), 'label': body[:8].rstrip(b'\0').decode('utf-8', 'replace'),
                                'bytes': len(sec) - 16})
        elif cc == b'VLB0':
            t['vid_main'] = body[:8].rstrip(b'\0').decode('utf-8', 'replace')
        elif cc == b'VAU0':
            t['vaud'] = list(body[:MAX_VERSIONS + 1])
        elif cc in (b'SUB0', b'SUB1'):
            if cc == b'SUB1':
                lang, data = body[:4].rstrip(b'\0').decode('ascii', 'replace'), body[4:]
            else:
                lang, data = '', body
            fmt = 'ass' if data.lstrip()[:13].lower().startswith(b'[script info]') else 'srt'
            cues = data.count(b'\nDialogue:') if fmt == 'ass' else data.count(b' --> ')
            t['subs'].append({'existing': len(t['subs']), 'label': lang.lower(), 'format': fmt,
                              'cues': cues, 'bytes': len(data)})
        elif cc == b'ART5':
            t['art'] = True
    return t

def section(cc, body):
    return struct.pack('<4sI', cc, len(body)) + body

def label8(text):
    """a video version's name: up to 7 bytes of UTF-8, NUL-padded to 8"""
    b = (text or '').strip().encode('utf-8')[:7]
    while b:
        try:
            b.decode('utf-8'); break
        except UnicodeDecodeError:
            b = b[:-1]
    return b.ljust(8, b'\0')

def lang4(code):
    return (code or 'und').upper().encode('ascii', 'replace')[:3].ljust(4, b'\0')

def verify_trailer(path):
    t = read_trailer(path)
    data_len = os.path.getsize(path)
    walked = t['base'] + sum(len(s) for _, s in t['sections']) + 16
    if t['sections'] and walked != data_len:
        raise RuntimeError('trailer walk did not land on the footer')
    return t

# ------------------------------------------------------------------ probe
SBS_NAME = re.compile(r'sbs|side.?by.?side', re.I)

def parse_name(fn):
    b = re.sub(r'\.[A-Za-z0-9]{2,6}$', '', os.path.basename(fn))
    ep = re.search(r'[. _-]S(\d+)E(\d+)', b, re.I) or re.search(r'[. _-](\d{1,2})x(\d{2})[. _-]', b)
    se = [int(ep.group(1)), int(ep.group(2))] if ep else None
    head = b[:ep.start()] if ep else b
    ym = re.search(r'[(. _\[](19\d\d|20\d\d)[). _\]]', head + ' ')
    year = int(ym.group(1)) if ym else 0
    if ym:
        head = head[:ym.start()]
    title = re.sub(r'[._]+', ' ', head).strip(' -([')
    return title, year, se

def is_moflex(path):
    if path.lower().endswith('.moflex'):
        return True
    with open(path, 'rb') as f:
        return f.read(2) == b'\x4c\x32'

def stream_secs(s):
    """a stream's own length in seconds (MP4/AVI: duration; MKV: the DURATION tag), or None"""
    try:
        if s.get('duration') not in (None, 'N/A'):
            return float(s['duration'])
    except ValueError:
        pass
    t = (s.get('tags') or {})
    d = t.get('DURATION') or t.get('duration')
    m = re.match(r'^(\d+):(\d\d):(\d\d(?:\.\d+)?)', d or '')
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3)) if m else None

def probe(path):
    r = run_quiet([FFPROBE, '-v', 'error', '-print_format', 'json', '-show_format', '-show_streams', path])
    if r.returncode != 0:
        raise RuntimeError(f'cannot read {path}: {r.stderr.strip()[:300]}')
    j = json.loads(r.stdout)
    st = j.get('streams', [])
    v = next((s for s in st if s['codec_type'] == 'video'
              and not s.get('disposition', {}).get('attached_pic')), None)
    info = {'path': path, 'kind': 'video', 'duration': float(j.get('format', {}).get('duration') or 0),
            'video': None, 'audio': [], 'subtitles': []}
    if v:
        sar = 1.0
        m = re.match(r'^(\d+):(\d+)$', v.get('sample_aspect_ratio') or '')
        if m and int(m.group(1)) and int(m.group(2)):
            sar = int(m.group(1)) / int(m.group(2))
        fr = v.get('avg_frame_rate') if v.get('avg_frame_rate') not in (None, '0/0') else v.get('r_frame_rate')
        fn, fd = 24000, 1001
        m = re.match(r'^(\d+)/(\d+)$', fr or '')
        if m and int(m.group(2)):
            fn, fd = int(m.group(1)), int(m.group(2))
        w, h = int(v['width']), int(v['height'])
        dar = w * sar / max(1, h)
        det = 'fsbs' if dar >= 2.5 else ('hsbs' if SBS_NAME.search(os.path.basename(path)) else '2d')
        info['video'] = {'index': v['index'], 'codec': v.get('codec_name'), 'width': w, 'height': h,
                         'sar': sar, 'fps_num': fn, 'fps_den': fd, 'fps': fn / fd, 'detected': det,
                         'dur': stream_secs(v)}
    for s in st:
        t = s.get('tags', {})
        if s['codec_type'] == 'audio':
            info['audio'].append({'index': s['index'], 'codec': s.get('codec_name'),
                                  'channels': s.get('channels'), 'lang': t.get('language', 'und'),
                                  'title': t.get('title', ''), 'default': bool(s.get('disposition', {}).get('default')),
                                  'dur': stream_secs(s)})
        elif s['codec_type'] == 'subtitle':
            info['subtitles'].append({'index': s['index'], 'codec': s.get('codec_name'),
                                      'lang': t.get('language', 'und'), 'title': t.get('title', ''),
                                      'image': s.get('codec_name') in IMAGE_SUBS,
                                      'forced': bool(s.get('disposition', {}).get('forced'))})
    title, year, se = parse_name(path)
    info['parsed'] = {'title': title, 'year': year, 'season_episode': se}
    if is_moflex(path):
        t = read_trailer(path)
        info['kind'] = 'super' if t['sections'] else 'moflex'
        info['video'] = info['video'] or {}
        info['video']['detected'] = 'moflex'
        nfo = t['nfo']
        is3d = nfo.get('is3d')
        if is3d is None:
            low = os.path.basename(path).lower()
            is3d = '1' if ('(3d)' in low or 'sbs' in low or 'lrf' in low or '_3d' in low) else '0'
        info['meta_from_file'] = nfo_meta(nfo, path)
        info['moflex'] = {'is3d': is3d == '1', 'nfo': nfo, 'lang_main': t['lang_main'],
                          'audio2': t['audio2'], 'audios': t['audios'], 'videos': t['videos'],
                          'vid_main': t['vid_main'], 'vaud': t['vaud'], 'subs': t['subs'], 'art': t['art']}
        if nfo.get('title'):
            info['parsed']['title'] = nfo['title']
        if nfo.get('year', '').isdigit():
            info['parsed']['year'] = int(nfo['year'])
    return info

def export_art(path, out_png):
    t = read_trailer(path)
    sec = next((s for cc, s in t['sections'] if cc == b'ART5'), None)
    if not sec:
        raise RuntimeError('no poster in this file')
    w, h = struct.unpack_from('<HH', sec, 8)
    raw = os.path.join(tempfile.mkdtemp(), 'art.raw')
    with open(raw, 'wb') as f:
        f.write(sec[12:12 + w * h * 2])
    r = run_quiet([FFMPEG, '-y', '-v', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb565le', '-s', f'{w}x{h}',
                   '-i', raw, out_png])
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip())
    return out_png

# ------------------------------------------------------------------ TMDB
TMDB_PROXY = 'https://clownsec.com/bigtop/tmdb.php'   # holds a TMDB key: no token of your own needed

def tmdb(path, **params):
    import urllib.request, urllib.parse
    if not S['tmdb_token']:   # through clownsec.com (the lookups Big Top uses; same answers as TMDB's)
        return tmdb_proxy(path, params)
    q = urllib.parse.urlencode(params)
    req = urllib.request.Request(f'https://api.themoviedb.org/3/{path}' + (f'?{q}' if q else ''))
    req.add_header('Authorization', f'Bearer {S["tmdb_token"]}')
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.load(r)

def tmdb_proxy(path, params):
    """one TMDB lookup through clownsec.com's proxy (see its tmdb.php for what it answers)"""
    import urllib.request, urllib.parse
    f = re.match(r'^find/(tt\d{5,10})$', path)
    if f:
        req = urllib.request.Request(TMDB_PROXY + '?' + urllib.parse.urlencode({'q': 'find', 'imdb': f.group(1)}),
                                     headers={'User-Agent': 'moflex-studio'})
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.load(r)
    m = re.match(r'^(search/movie|search/tv|movie/(\d+)|tv/(\d+)(?:/season/(\d+)(?:/episode/(\d+))?)?)$', path)
    if not m:
        raise RuntimeError(f'TMDB lookup {path} needs a TMDB token of your own (Settings)')
    episode = None
    if path.startswith('search/'):
        q = {'q': 'search_movie' if path == 'search/movie' else 'search_tv', 'query': params.get('query', '')}
        y = params.get('year') or params.get('first_air_date_year')
        if y:
            q['year'] = y
    elif m.group(2):
        q = {'q': 'movie', 'id': m.group(2)}
    elif m.group(4) is not None:
        q = {'q': 'tv_season', 'id': m.group(3), 'season': m.group(4)}
        episode = m.group(5)
    else:
        q = {'q': 'tv', 'id': m.group(3)}
    req = urllib.request.Request(TMDB_PROXY + '?' + urllib.parse.urlencode(q),
                                 headers={'User-Agent': 'moflex-studio'})
    with urllib.request.urlopen(req, timeout=30) as r:
        d = json.load(r)
    if d.get('not_found'):
        raise RuntimeError('not found on TMDB')
    if episode is not None:   # the proxy answers a whole season: the episode from it
        e = next((x for x in d.get('episodes') or [] if str(x.get('episode_number')) == episode), None)
        if not e:
            raise RuntimeError('episode not found on TMDB')
        return e
    return d

def tmdb_search(kind, query, year=None):
    imdb = re.search(r'\btt\d{5,10}\b', query or '')
    if imdb:   # an IMDb id (or an imdb.com link): TMDB finds it directly
        r = tmdb(f'find/{imdb.group(0)}', external_source='imdb_id')
        out = []
        for k, x in [('tv', x) for x in r.get('tv_results') or []] + [('movie', x) for x in r.get('movie_results') or []]:
            if kind == 'tv' and k != 'tv' or kind != 'tv' and k == 'tv':
                continue
            out.append({'id': x['id'], 'title': x.get('name') or x.get('title'),
                        'year': (x.get('first_air_date') or x.get('release_date') or '')[:4],
                        'overview': x.get('overview', ''), 'poster': x.get('poster_path')})
        if out:
            return out
        if r.get('tv_results') or r.get('movie_results'):
            raise RuntimeError(f"{imdb.group(0)} is a {'TV show' if r.get('tv_results') else 'movie'} on TMDB:"
                               f" switch Movie/TV and search again")
        raise RuntimeError(f'{imdb.group(0)} is not on TMDB')
    if kind == 'tv':
        r = tmdb('search/tv', query=query, **({'first_air_date_year': year} if year else {}))
        return [{'id': x['id'], 'title': x.get('name'), 'year': (x.get('first_air_date') or '')[:4],
                 'overview': x.get('overview', ''), 'poster': x.get('poster_path')} for x in r['results'][:20]]
    r = tmdb('search/movie', query=query, **({'year': year} if year else {}))
    return [{'id': x['id'], 'title': x.get('title'), 'year': (x.get('release_date') or '')[:4],
             'overview': x.get('overview', ''), 'poster': x.get('poster_path')} for x in r['results'][:20]]

def tmdb_details(kind, tid, se=None):
    if kind == 'tv':
        d = tmdb(f'tv/{tid}')
        info = {'title': d['name'], 'year': int((d.get('first_air_date') or '0')[:4] or 0),
                'category': 'TV Shows', 'genres': ', '.join(g['name'] for g in d['genres']),
                'runtime': (d.get('episode_run_time') or [23])[0], 'date': '',
                'showdesc': d.get('overview', ''), 'desc': d.get('overview', ''), 'eptitle': '',
                'season_episode': se,
                'poster_url': f"https://image.tmdb.org/t/p/w500{d['poster_path']}" if d.get('poster_path') else ''}
        if se and se[1] > 0:                     # TMDB has season 0 (specials) but no episode 0
            try:
                e = tmdb(f'tv/{tid}/season/{se[0]}/episode/{se[1]}')
            except Exception:
                e = {}                               # not on TMDB: keep the show's info
            info['eptitle'] = e.get('name', '')
            info['date'] = e.get('air_date', '') or ''
            if e.get('runtime'):
                info['runtime'] = e['runtime']
            if e.get('name'):
                info['desc'] = f'S{int(se[0]):02d}e{int(se[1]):02d} “{e["name"]}” - {e.get("overview", "")}'
        return info
    d = tmdb(f'movie/{tid}')
    return {'title': d['title'], 'year': int((d.get('release_date') or '0')[:4] or 0),
            'category': 'Movies', 'genres': ', '.join(g['name'] for g in d['genres']),
            'runtime': d.get('runtime') or 0, 'date': d.get('release_date', '') or '',
            'desc': d.get('overview', ''), 'showdesc': '', 'eptitle': '', 'season_episode': None,
            'poster_url': f"https://image.tmdb.org/t/p/w500{d['poster_path']}" if d.get('poster_path') else ''}

# ------------------------------------------------------------------ stage 1: lossless AVI
def video_filter(vinfo, layout, out_mode, max_fps):
    k = 2 if layout == 'hsbs' else 1
    eye = (f'scale=w=iw*sar*{k}:h=ih:flags=lanczos,setsar=1,'
           'scale=400:240:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,'
           'pad=400:240:(ow-iw)/2:(oh-ih)/2:black,setsar=1')
    pre = f"fps={vinfo['fps_num']}/{vinfo['fps_den'] * 2}," if vinfo['fps'] > max_fps + 0.5 else ''
    if out_mode == '3d':
        return (f'[0:v:0]{pre}split[a][b];[a]crop=iw/2:ih:0:0,{eye}[l];'
                f'[b]crop=iw/2:ih:iw/2:0,{eye}[r];[l][r]hstack,format=yuv420p[v]')
    if layout == '2d':
        return f'[0:v:0]{pre}{eye},format=yuv420p[v]'
    return f'[0:v:0]{pre}crop=iw/2:ih:0:0,{eye},format=yuv420p[v]'   # 2D from SBS: left eye

def ffmpeg_progress(cmd, duration, stage):
    import threading
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                         encoding='utf-8', errors='replace', creationflags=NO_WINDOW)
    t0 = time.time(); last = 0; errs = []
    th = threading.Thread(target=lambda: errs.extend(p.stderr.readlines()), daemon=True)
    th.start()
    for line in p.stdout:
        m = re.match(r'out_time_us=(\d+)', line)
        if m and time.time() - last > 0.4:
            last = time.time()
            t = int(m.group(1)) / 1e6
            pct = min(100.0, t / max(duration, 0.1) * 100)
            el = time.time() - t0
            emit('progress', stage=stage, pct=round(pct, 1), elapsed=round(el, 1),
                 eta=round(el / pct * (100 - pct), 1) if pct > 0.5 else None,
                 speed=round(t / el, 1) if el > 0 else None)
    p.wait(); th.join(2)
    if p.returncode != 0:
        raise RuntimeError(f'{stage} failed: ' + ''.join(errs)[-600:].strip())

def check_source_lengths(job, info):
    """warn before encoding when the source's video stream is shorter than the audio going with it
    (e.g. a 2D->3D conversion that stopped early: the original audio is all there, the picture isn't)"""
    vd = (info.get('video') or {}).get('dur')
    main = (job.get('audio') or {}).get('main') or {}
    if not vd or 'stream' not in main or main.get('file'):
        return
    a = next((x for x in info['audio'] if x['index'] == int(main['stream'])), None)
    ad = a and a.get('dur')
    if ad and ad - vd > 2:
        warn(job, f'the source\'s video ({clock(vd)}) is {clock(ad - vd)} shorter than its audio ({clock(ad)})'
                  ' -- the picture may be cut short; the audio will be trimmed to match')
        job['_short_warned'] = True

def avi_secs(avi):
    """the lossless AVI's video length (what the encoder will get), or None"""
    r = run_quiet([FFPROBE, '-v', 'error', '-select_streams', 'v:0', '-show_entries',
                   'stream=duration,nb_frames,avg_frame_rate', '-of', 'json', avi])
    try:
        st = json.loads(r.stdout)['streams'][0]
        d = stream_secs(st)
        if d:
            return d
        n, (fn, fd) = int(st['nb_frames']), map(int, st['avg_frame_rate'].split('/'))
        return n * fd / fn
    except Exception:
        return None

def make_avi(job, info, layout, avi, src=None):
    """video only: with an audio stream the Mobiclip encoder's final write runs single-threaded at
    ~1.7x realtime (45 min for a movie); without one the whole encode takes minutes. Track 1 is
    added afterwards by assemble() (it replaced the encoder's audio with the normalised one anyway)."""
    cmd = [FFMPEG, '-hide_banner', '-nostdin', '-y', '-loglevel', 'error', '-nostats',
           '-progress', 'pipe:1', '-i', src or job['source']]
    cmd += ['-filter_complex', video_filter(info['video'], layout, job['output'], S['max_fps']), '-map', '[v]']
    if info['duration']:
        cmd += ['-t', f"{info['duration']:.3f}"]
    cmd += ['-an', '-sn', '-dn', '-fps_mode', 'cfr', '-c:v', 'libx264', '-preset', 'ultrafast', '-qp', '0', avi]
    ffmpeg_progress(cmd, info['duration'], 'avi')

# ------------------------------------------------------------------ stage 0: 2D -> 3D with iw3
# A 2D source made into 3D by iw3 (github.com/nagadomi/nunif, MIT): on the full-size picture, into a
# full side-by-side video the rest of the pipeline takes as any FSBS source. Audio and subtitles
# still come from the original file. Not real time (an RTX 4090: ~0.6x for 1080p with Distill_Any_L).

# the defaults: the settings tuned for the 3DS by hand in iw3's GUI (a lot of trial and error went
# into these -- see the 2D-to-3D notes), with Distill-Any-Depth Large
IW3_DEFAULTS = {
    'model': 'Distill_Any_L',
    'divergence': 1.7,              # "3D Strength"
    'convergence': 0.65,            # "Convergence Plane"
    'convergence_mode': 'sod_v1',   # ... automatic, by the salient object
    'ipd_offset': 0,                # "Your Own Size"
    'synthetic_view': 'both',
    'method': 'mlbw_l2s',
    'stereo_width': 0,              # "Stereo Processing Width" (0 = Default)
    'resolution': 512,              # "Depth Resolution"
    'limit_resolution': True,       # ... "Limit to source"
    'foreground_scale': 0,
    'edge_dilation': [3, 1],        # "Edge Fix"
    'depth_aa': False,
    'ema': True,                    # "Flicker Reduction"
    'ema_decay': 0.99,
    'ema_buffer': 30,
    'scene_detect': False,          # "Scene Boundary Detection"
    'preserve_border': True,        # "Preserve Screen Border"
    'crf': 20,                      # the intermediate SBS video (shrunk to 400x240 later anyway)
    # the intermediate video's encoding and the processing -- speed and the kept SBS file's quality,
    # not the depth (so they don't mark a template "(changed)")
    'preset': 'medium',             # x264 preset: ultrafast .. placebo
    'tune': '',                     # '', film, animation, grain, stillimage, psnr, fastdecode, zerolatency
    'pix_fmt': 'yuv420p',
    'colorspace': 'auto',
    'hwaccel': '',                  # video decoding on the graphics card: '', cuda, d3d11va, d3d12va, dxva2, amf
    'batch_size': 2,                # frames per depth batch (ignored with low_vram)
    'max_workers': 0,               # inference worker threads (0 = off)
    'fp16': True,                   # half precision (off = --disable-amp)
    'tta': False,                   # flip augmentation on the depth model (slower, slightly steadier)
    'low_vram': False,
    'compile': False,               # torch.compile
    'cuda_stream': False,
    'keep_sbs': False,              # keep the 3D SBS video beside the output
}
IW3_OUTPUT_KEYS = ('crf', 'preset', 'tune', 'pix_fmt', 'colorspace', 'hwaccel', 'batch_size', 'max_workers',
                   'fp16', 'low_vram', 'compile', 'cuda_stream', 'keep_sbs')
# Templates: the easy choice. Each is IW3_DEFAULTS with a few changes; the name goes into the file's
# conv3d= label. The user's own are kept in iw3_templates.json beside this file ({name: {label, ...}}).
IW3_TEMPLATES = {
    'best':   {'label': 'Best quality (top graphics card)'},
    'high':   {'label': 'High quality', 'model': 'Distill_Any_B'},
    'medium': {'label': 'Medium quality', 'model': 'Distill_Any_S'},
    'low':    {'label': 'Low quality (low-end graphics card)', 'model': 'Distill_Any_S', 'resolution': 392,
               'method': 'row_flow_v3', 'convergence_mode': 'constant'},
}
IW3_TEMPLATES_FILE = os.path.join(HERE, 'iw3_templates.json')

def iw3_templates():
    """the built-in templates, then the user's own (which may replace a built-in one by name)"""
    t = {k: dict(v, builtin=True) for k, v in IW3_TEMPLATES.items()}
    try:
        with open(IW3_TEMPLATES_FILE, encoding='utf-8-sig') as f:
            for k, v in json.load(f).items():
                t[str(k)] = dict(v, builtin=False)
    except (OSError, ValueError):
        pass
    return t

def iw3_settings(c):
    """a job's conv3d: its template (default 'best') with any settings it gives on top"""
    c = c or {}
    tpl = iw3_templates().get(c.get('template') or 'best') or {}
    out = dict(IW3_DEFAULTS)
    out.update({k: v for k, v in tpl.items() if k in IW3_DEFAULTS})
    base = dict(out)
    out.update({k: v for k, v in c.items() if k in IW3_DEFAULTS})
    out['template'] = c.get('template') or 'best'
    if any(out[k] != base[k] for k in IW3_DEFAULTS if k not in IW3_OUTPUT_KEYS):
        out['template'] += ' (changed)'      # the file's conv3d= label must not claim the template as is
    return out

def iw3_save_template(name, settings):
    """keep the user's own template (settings: IW3_DEFAULTS' keys, plus a 'label')"""
    name = re.sub(r'[^A-Za-z0-9 _-]', '', str(name)).strip()[:32]
    if not name:
        raise RuntimeError('a template needs a name')
    if name in IW3_TEMPLATES:
        raise RuntimeError(f"'{name}' is a built-in template: choose another name")
    try:
        with open(IW3_TEMPLATES_FILE, encoding='utf-8-sig') as f:
            mine = json.load(f)
    except (OSError, ValueError):
        mine = {}
    t = {k: v for k, v in settings.items() if k in IW3_DEFAULTS}
    t['label'] = str(settings.get('label') or name)[:60]
    mine[name] = t
    with open(IW3_TEMPLATES_FILE, 'w', encoding='utf-8') as f:
        json.dump(mine, f, indent=1)
    return name

def iw3_delete_template(name):
    try:
        with open(IW3_TEMPLATES_FILE, encoding='utf-8-sig') as f:
            mine = json.load(f)
    except (OSError, ValueError):
        mine = {}
    mine.pop(name, None)
    with open(IW3_TEMPLATES_FILE, 'w', encoding='utf-8') as f:
        json.dump(mine, f, indent=1)

# iw3, installed by Studio itself into app\\iw3 -- all of it inside Studio's folder, and only what
# Studio uses: a private Python 3.12, nunif's code (no Git), PyTorch with graphics-card support, iw3's
# packages (not its GUI), and the models the default template needs. iw3's own installer also fetches
# every depth model and waifu2x's (~11 GB) -- here any other model downloads when a job first uses it.
IW3_PY_URL = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip'
IW3_GETPIP_URL = 'https://bootstrap.pypa.io/get-pip.py'
IW3_SRC_URL = 'https://codeload.github.com/nagadomi/nunif/zip/refs/heads/master'
IW3_HOME = os.path.join(HERE, 'iw3')

def _fetch(url, dest, what):
    import urllib.request
    req = urllib.request.Request(url, headers={'User-Agent': 'moflex-studio'})
    with urllib.request.urlopen(req, timeout=120) as r, open(dest, 'wb') as o:
        total = int(r.headers.get('Content-Length') or 0); done = 0; last = 0
        while True:
            b = r.read(1 << 20)
            if not b:
                break
            o.write(b); done += len(b)
            if total and time.time() - last > 0.5:
                last = time.time()
                emit('progress', stage='iw3-install', pct=round(done / total * 100, 1), text=what)

def _run_logged(cmd, cwd, env, what):
    p = subprocess.Popen(cmd, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         creationflags=NO_WINDOW)
    tail = []
    for raw in p.stdout:
        line = raw.decode('utf-8', 'replace').strip()
        if line:
            tail = (tail + [line])[-8:]
            log(line[:300], 'iw3-install')
    if p.wait() != 0:
        raise RuntimeError(f'{what} failed: ' + ' | '.join(tail[-3:])[-500:])

def iw3_env(py):
    """iw3's environment: its Python first on PATH; temp files and caches inside Studio's folder"""
    env = dict(os.environ, PYTHONUTF8='1', PIP_NO_CACHE_DIR='1', PIP_DISABLE_PIP_VERSION_CHECK='1',
               HF_HOME=os.path.join(IW3_HOME, 'cache', 'hf'), XDG_CACHE_HOME=os.path.join(IW3_HOME, 'cache'),
               TMP=os.path.join(IW3_HOME, 'tmp'), TEMP=os.path.join(IW3_HOME, 'tmp'))
    pd = os.path.dirname(py)
    extra = [pd, os.path.join(pd, 'Scripts'), os.path.join(os.path.dirname(pd), 'git', 'cmd')]
    env['PATH'] = os.pathsep.join(extra + [env.get('PATH', '')])
    return env

# the depth models worth offering up front, with their download size (MB) and checkpoint file(s)
# THE list of depth models Studio offers -- in Settings' model choice, Models... > Download and the
# install alike -- in this order; each one Studio can download itself
IW3_MODEL_FILES = {
    'Distill_Any_L': (1279, ['distill_any_depth_vitl.safetensors'], 'Distill-Any-Depth Large (Best quality)'),
    'Distill_Any_B': (372, ['distill_any_depth_vitb.safetensors'], 'Distill-Any-Depth Base (High quality)'),
    'Distill_Any_S': (95, ['distill_any_depth_vits.safetensors'], 'Distill-Any-Depth Small (Medium / Low quality)'),
    'VDA_L': (1467, ['video_depth_anything_vitl.pth'], 'Video Depth Anything Large (non-commercial)'),
    'VDA_B': (437, ['video_depth_anything_vitb.pth'], 'Video Depth Anything Base (non-commercial)'),
    'VDA_S': (111, ['video_depth_anything_vits.pth'], 'Video Depth Anything Small'),
    'Any_V2_L': (1279, ['depth_anything_v2_vitl.pth'], 'Depth Anything V2 Large (non-commercial)'),
    'Any_V2_B': (372, ['depth_anything_v2_vitb.pth'], 'Depth Anything V2 Base (non-commercial)'),
    'Any_V2_S': (95, ['depth_anything_v2_vits.pth'], 'Depth Anything V2 Small'),
}
# where they come from: iw3 itself fetches only some (it wants Distill Base/Large placed by hand), so
# Studio downloads these to the names iw3 looks for
_HF = 'https://huggingface.co/'
IW3_MODEL_URLS = {
    'Distill_Any_L': _HF + 'xingyang1/Distill-Any-Depth/resolve/main/large/model.safetensors',
    'Distill_Any_B': _HF + 'xingyang1/Distill-Any-Depth/resolve/main/base/model.safetensors',
    'Distill_Any_S': _HF + 'xingyang1/Distill-Any-Depth/resolve/main/small/model.safetensors',
    'VDA_L': _HF + 'depth-anything/Video-Depth-Anything-Large/resolve/main/video_depth_anything_vitl.pth',
    'VDA_B': _HF + 'depth-anything/Video-Depth-Anything-Base/resolve/main/video_depth_anything_vitb.pth',
    'VDA_S': _HF + 'depth-anything/Video-Depth-Anything-Small/resolve/main/video_depth_anything_vits.pth',
    'Any_V2_L': _HF + 'depth-anything/Depth-Anything-V2-Large/resolve/main/depth_anything_v2_vitl.pth',
    'Any_V2_B': _HF + 'depth-anything/Depth-Anything-V2-Base/resolve/main/depth_anything_v2_vitb.pth',
    'Any_V2_S': _HF + 'depth-anything/Depth-Anything-V2-Small/resolve/main/depth_anything_v2_vits.pth',
}

def iw3_catalog():
    """the offered models, in order, each with whether it's downloaded: [{model, label, mb, have}]"""
    have = {m['file'] for m in iw3_models()} if iw3_find()[0] else set()
    return [{'model': m, 'label': lab, 'mb': mb, 'have': any(f in have for f in fs)}
            for m, (mb, fs, lab) in IW3_MODEL_FILES.items()]

def iw3_ensure_model(m, nunif=None):
    """the model's checkpoint where iw3 wants it, downloaded first if it isn't there (with progress)"""
    if m not in IW3_MODEL_URLS:
        return            # iw3 fetches the others itself (or says how)
    nunif = nunif or iw3_find()[0]
    d = iw3_checkpoints(nunif)
    dest = os.path.join(d, IW3_MODEL_FILES[m][1][0])
    if os.path.isfile(dest) and os.path.getsize(dest) > 1 << 20:
        return
    os.makedirs(d, exist_ok=True)
    mb = IW3_MODEL_FILES[m][0]
    log(f'downloading {m} ({mb} MB)', 'iw3')
    part = dest + '.part'
    _fetch(IW3_MODEL_URLS[m], part, f'Downloading {m}')
    os.replace(part, dest)

def iw3_checkpoints(nunif=None):
    nunif = nunif or iw3_find()[0]
    return os.path.join(nunif, 'iw3', 'pretrained_models', 'hub', 'checkpoints') if nunif else None

def iw3_models(nunif=None):
    """the models iw3 has downloaded: [{file, mb, model, label}] (model '' = one of iw3's own small ones)"""
    d = iw3_checkpoints(nunif)
    out = []
    if d and os.path.isdir(d):
        names = {f: (m, lab) for m, (_, fs, lab) in IW3_MODEL_FILES.items() for f in fs}
        for f in sorted(os.listdir(d)):
            p = os.path.join(d, f)
            if os.path.isfile(p):
                m, lab = names.get(f, ('', ''))
                out.append({'file': f, 'mb': round(os.path.getsize(p) / 1048576), 'model': m, 'label': lab or f})
    return out

def iw3_delete_model(fname):
    d = iw3_checkpoints()
    if not d or os.path.basename(fname) != fname or not os.path.isfile(os.path.join(d, fname)):
        raise RuntimeError('no such model file')
    if fname.startswith('iw3_'):
        raise RuntimeError("that's one of iw3's own small models (it needs them)")
    os.remove(os.path.join(d, fname))

def iw3_fetch_models(models, index=1, total=1, where=None):
    """download depth models now, by converting one second of a test picture with each (this also
    proves iw3 works end to end, and fetches iw3's own small models the first time)"""
    nunif, py = where or iw3_find()
    if not nunif:
        raise RuntimeError("iw3 isn't installed")
    env = iw3_env(py)
    tmp = env['TMP']
    os.makedirs(tmp, exist_ok=True)
    clip = os.path.join(tmp, 'test.mp4')
    subprocess.run([FFMPEG, '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i',
                    'testsrc2=size=640x360:rate=24:duration=1', '-pix_fmt', 'yuv420p', clip],
                   check=True, creationflags=NO_WINDOW)
    for m in models:
        if m not in IW3_MODELS:
            raise RuntimeError(f'unknown depth model {m}')
        mb = IW3_MODEL_FILES.get(m, (0,))[0]
        emit('stage', stage='iw3-install', index=index, total=total,
             text=f"Downloading {m}" + (f" (about {mb / 1024:.1f} GB)" if mb >= 1000 else f" (about {mb} MB)" if mb else '')
             + ' and testing iw3')
        iw3_ensure_model(m, nunif)
        c = dict(iw3_settings({'template': 'best'}), model=m)
        _run_logged([py, '-m', 'iw3.cli', '-i', clip, '-o', os.path.join(tmp, 'test_sbs.mp4'), '--yes']
                    + iw3_args(c), nunif, env, f'Testing iw3 with {m}')
    shutil.rmtree(tmp, ignore_errors=True)

def iw3_install(models=('Distill_Any_L',)):
    """iw3 into app\\iw3 (see above), with the chosen models, its progress as events"""
    import zipfile
    if not IS_WIN:
        raise RuntimeError('iw3 is installed this way on Windows only')
    root = IW3_HOME
    tmp = os.path.join(root, 'tmp')
    os.makedirs(tmp, exist_ok=True)
    py = os.path.join(root, 'python', 'python.exe')
    env = iw3_env(py)
    total = 4 + max(1, len(models))
    # 1. Python 3.12 (embeddable), set up to find nunif and installed packages
    emit('stage', stage='iw3-install', index=1, total=total, text='Downloading Python 3.12 for iw3')
    if not os.path.isfile(py) or not os.path.isfile(os.path.join(root, 'python', 'Scripts', 'pip.exe')):
        shutil.rmtree(os.path.join(root, 'python'), ignore_errors=True)
        z = os.path.join(tmp, 'python.zip')
        _fetch(IW3_PY_URL, z, 'Python')
        with zipfile.ZipFile(z) as zf:
            zf.extractall(os.path.join(root, 'python'))
        pth = os.path.join(root, 'python', 'python312._pth')
        with open(pth, 'a', encoding='utf-8') as f:
            f.write('\n..\\nunif\nimport site\n')
        gp = os.path.join(tmp, 'get-pip.py')
        _fetch(IW3_GETPIP_URL, gp, 'pip')
        _run_logged([py, gp, '--no-warn-script-location'], root, env, 'Installing pip')
    # 2. nunif's code (iw3 is part of it), without Git
    emit('stage', stage='iw3-install', index=2, total=total, text='Downloading iw3 (nunif)')
    z = os.path.join(tmp, 'nunif.zip')
    _fetch(IW3_SRC_URL, z, 'iw3')
    new = os.path.join(root, 'nunif.new')
    shutil.rmtree(new, ignore_errors=True)
    with zipfile.ZipFile(z) as zf:
        top = zf.namelist()[0].split('/')[0]
        zf.extractall(new)
    old = os.path.join(root, 'nunif')
    keep = os.path.join(old, 'iw3', 'pretrained_models')   # an update keeps the models already fetched
    if os.path.isdir(keep):
        dst = os.path.join(new, top, 'iw3', 'pretrained_models')
        shutil.rmtree(dst, ignore_errors=True)
        shutil.move(keep, dst)
    shutil.rmtree(old, ignore_errors=True)
    shutil.move(os.path.join(new, top), old)
    shutil.rmtree(new, ignore_errors=True)
    # 3. PyTorch with graphics-card support (most of the download), then iw3's packages (not its GUI's)
    emit('stage', stage='iw3-install', index=3, total=total,
         text='Installing PyTorch with graphics-card support (about 3 GB) -- this takes a while')
    _run_logged([py, '-m', 'pip', 'install', '--upgrade', 'pip'], old, env, 'Updating pip')
    _run_logged([py, '-m', 'pip', 'install', '--upgrade', '-r', os.path.join(old, 'requirements-torch.txt')],
                old, env, 'Installing PyTorch')
    emit('stage', stage='iw3-install', index=4, total=total, text="Installing iw3's packages")
    _run_logged([py, '-m', 'pip', 'install', '--upgrade', '-r', os.path.join(old, 'requirements.txt')],
                old, env, "Installing iw3's packages")
    # 4. the chosen depth models (none: each downloads when a job first uses it)
    shutil.rmtree(tmp, ignore_errors=True)
    if models:
        iw3_fetch_models(list(models), 5, total, where=(old, py))
    if not iw3_layout(root):
        raise RuntimeError("iw3's install didn't finish")
    emit('done', output=root)

IW3_MODELS = ['Distill_Any_L', 'Distill_Any_B', 'Distill_Any_S', 'VDA_L', 'VDA_B', 'VDA_S',
              'VDA_Stream_L', 'Any_V2_L', 'Any_V2_B', 'Any_V2_S', 'Any_L', 'Any_B', 'Any_S',
              'DepthPro', 'DepthPro_S', 'ZoeD_Any_N', 'ZoeD_N']

def iw3_dirs():
    """where iw3 may be, in order: the folder set in Settings, Studio's own (Install iw3), then the usual
    places people put it (iw3's Windows package, or a nunif checkout)"""
    out = []
    if S.get('iw3_dir'):
        out.append(S['iw3_dir'])
    out.append(IW3_HOME)
    home = os.path.expanduser('~')
    bases = [home] + [os.path.join(home, x) for x in ('Documents', 'Desktop', 'Downloads', 'Programs')]
    if IS_WIN:
        bases += [d + ':\\' for d in 'CDEF' if os.path.isdir(d + ':\\')]
    for b in bases:
        try:
            names = sorted(os.listdir(b))
        except OSError:
            continue
        for n in names:
            if n.lower().startswith(('iw3', 'nunif')) and os.path.isdir(os.path.join(b, n)):
                out.append(os.path.join(b, n))
    return out

def _has_torch(py):
    try:
        r = subprocess.run([py, '-c', 'import importlib.util as u; print(bool(u.find_spec("torch")))'],
                           capture_output=True, text=True, timeout=20, creationflags=NO_WINDOW)
        return r.stdout.strip() == 'True'
    except Exception:
        return False

def iw3_layout(d):
    """(nunif folder, python) for an iw3 install at d, or None. d may be iw3's Windows package (python\\ +
    nunif\\), a nunif checkout (iw3\\ inside) with its own virtual environment or a Python beside it, or
    a folder holding such a checkout. With no Python of its own, the one set in Settings or on PATH, if
    it has PyTorch."""
    if not d or not os.path.isdir(d):
        return None
    exe = 'python.exe' if IS_WIN else 'python'
    nunif = d if os.path.isfile(os.path.join(d, 'iw3', 'cli.py')) else os.path.join(d, 'nunif')
    if not os.path.isfile(os.path.join(nunif, 'iw3', 'cli.py')):
        return None
    pys = [os.path.join(os.path.dirname(nunif), 'python', exe)]
    for v in ('.venv', 'venv', 'env'):
        pys.append(os.path.join(nunif, v, 'Scripts' if IS_WIN else 'bin', exe))
    for py in pys:
        if os.path.isfile(py):
            return nunif, py
    for py in [S.get('iw3_python') or '', shutil.which('python') or '', shutil.which('python3') or '']:
        if py and os.path.isfile(py) and _has_torch(py):
            return nunif, py
    return None

_iw3_cache = []
def iw3_find():
    """(nunif folder, python) of the iw3 to use, or (None, None)"""
    if _iw3_cache:
        return _iw3_cache[0]
    for d in iw3_dirs():
        r = iw3_layout(d)
        if r:
            _iw3_cache.append(r)
            return r
    return None, None

def iw3_check(d):
    """is there a working iw3 at d: its Python, PyTorch and whether it sees a graphics card"""
    r = iw3_layout(d)
    if not r:
        return {'ok': False, 'message': "No iw3 there: pick iw3's folder (with python and nunif inside) or nunif's own folder."}
    nunif, py = r
    try:
        p = subprocess.run([py, '-c', 'import torch, iw3; print(torch.__version__, torch.cuda.is_available())'],
                           cwd=nunif, capture_output=True, text=True, timeout=180, env=iw3_env(py), creationflags=NO_WINDOW)
    except Exception as ex:
        return {'ok': False, 'message': f'its Python did not run: {ex}'}
    if p.returncode != 0:
        return {'ok': False, 'message': 'iw3 is there but does not start: ' + (p.stderr.strip().splitlines() or ['?'])[-1][-300:]}
    ver, cuda = (p.stdout.split() + ['?', 'False'])[:2]
    return {'ok': True, 'nunif': nunif, 'python': py, 'torch': ver, 'gpu': cuda == 'True',
            'models': [m['label'] for m in iw3_models(nunif) if not m['file'].startswith('iw3_')]}

# checkpoint file names iw3 looks for, and how other downloads name the same weights
def _model_name_for(path):
    b = os.path.basename(path)
    known = {f for _, fs, _ in IW3_MODEL_FILES.values() for f in fs}
    if b in known or re.match(r'^(depth_anything|video_depth_anything|distill_any_depth|ZoeD|depth_pro|iw3_)', b):
        return b
    low = path.replace('\\', '/').lower()
    m = re.search(r'distill-any-depth/(small|base|large)/model\.safetensors$', low)
    if m:
        return {'small': 'distill_any_depth_vits.safetensors', 'base': 'distill_any_depth_vitb.safetensors',
                'large': 'distill_any_depth_vitl.safetensors'}[m.group(1)]
    return None

def iw3_import_models(src):
    """the depth models found under src (another iw3, a download folder) into this iw3's models: as
    links where possible (the same drive: no extra space), else copies. Events, like run."""
    d = iw3_checkpoints()
    if not d:
        raise RuntimeError("iw3 isn't installed")
    os.makedirs(d, exist_ok=True)
    found = []
    base_depth = src.rstrip('\\/').count(os.sep)
    for dp, dn, fn in os.walk(src):
        if dp.count(os.sep) - base_depth >= 8:
            dn[:] = []
        for f in fn:
            if f.endswith(('.pth', '.pt', '.safetensors')):
                p = os.path.join(dp, f)
                name = _model_name_for(p)
                if name and os.path.getsize(p) > 100000:
                    found.append((p, name))
    linked = copied = 0
    for n, (p, name) in enumerate(found, 1):
        dest = os.path.join(d, name)
        if os.path.exists(dest) or os.path.normcase(os.path.abspath(p)) == os.path.normcase(os.path.abspath(dest)):
            continue
        emit('stage', stage='iw3-import', index=n, total=len(found), text=f'Adding {name}')
        try:
            os.link(p, dest); linked += 1
        except OSError:
            shutil.copyfile(p, dest + '.part'); os.replace(dest + '.part', dest); copied += 1
    emit('done', output=d, linked=linked, copied=copied, found=len(found))

def iw3_args(c):
    """iw3's command-line options for a 2D-to-3D setting set (IW3_DEFAULTS' keys)"""
    a = ['--depth-model', c['model'], '--divergence', str(c['divergence']), '--convergence', str(c['convergence']),
         '--convergence-mode', c['convergence_mode'], '--ipd-offset', str(int(c['ipd_offset'])),
         '--synthetic-view', c['synthetic_view'], '--method', c['method'],
         '--foreground-scale', str(c['foreground_scale'])]
    if int(c.get('stereo_width') or 0) > 0:
        a += ['--stereo-width', str(int(c['stereo_width']))]
    if int(c.get('resolution') or 0) > 0:
        a += ['--resolution', str(int(c['resolution']))]
        if c.get('limit_resolution'):
            a.append('--limit-resolution')
    ed = c.get('edge_dilation') or [0]
    a += ['--edge-dilation'] + [str(int(x)) for x in (ed if isinstance(ed, list) else [ed])]
    if c.get('depth_aa'):
        a.append('--depth-aa')
    if c.get('ema'):
        a += ['--ema-normalize', '--ema-decay', str(c['ema_decay']), '--ema-buffer', str(int(c['ema_buffer']))]
    if c.get('scene_detect'):
        a.append('--scene-detect')
    if c.get('preserve_border'):
        a.append('--preserve-screen-border')
    if c.get('tta'):
        a.append('--tta')
    crf = c.get('crf')
    crf = 20 if crf is None or crf == '' else int(crf)     # 0 is a real choice (lossless), not "unset"
    a += ['--video-codec', 'libx264', '--crf', str(crf), '--preset', str(c.get('preset') or 'medium'),
          '--pix-fmt', str(c.get('pix_fmt') or 'yuv420p'), '--colorspace', str(c.get('colorspace') or 'auto')]
    if c.get('tune'):
        a += ['--tune', str(c['tune'])]
    if c.get('hwaccel'):
        a += ['--hwaccel', str(c['hwaccel'])]
    if int(c.get('batch_size') or 2) != 2:
        a += ['--batch-size', str(int(c['batch_size']))]
    if int(c.get('max_workers') or 0) > 0:
        a += ['--max-workers', str(int(c['max_workers']))]
    if c.get('fp16') is False:
        a.append('--disable-amp')
    for k, flag in (('low_vram', '--low-vram'), ('compile', '--compile'), ('cuda_stream', '--cuda-stream')):
        if c.get(k):
            a.append(flag)
    return a

def iw3_label(c):
    """how a file was converted, kept in its info (conv3d=): so a lower model is always told apart"""
    ed = c.get('edge_dilation') or [0]
    ed = '/'.join(str(x) for x in (ed if isinstance(ed, list) else [ed]))
    return (f"iw3 {c.get('template', 'custom')}: {c['model']}{' tta' if c.get('tta') else ''} d{c['divergence']} c{c['convergence']}"
            f"{' ' + c['convergence_mode'] if c['convergence_mode'] != 'constant' else ''} {c['method']}"
            f" r{c.get('resolution') or 'def'} e{ed}{' ema' + str(c['ema_decay']) if c.get('ema') else ''}")

def iw3_convert(job, info, out):
    """the source's picture made 3D by iw3, into out (full side-by-side), with progress"""
    root, py = iw3_find()
    if not root:
        raise RuntimeError('iw3 isn\'t installed: Settings > 2D to 3D > Install iw3 (or set its folder)')
    c = iw3_settings(job.get('conv3d'))
    if c['model'] not in IW3_MODELS:
        raise RuntimeError(f"unknown depth model {c['model']}")
    iw3_ensure_model(c['model'], root)   # a model not downloaded yet: now, before iw3 asks for it
    cmd = [py, '-m', 'iw3.cli', '-i', job['source'], '-o', out, '--yes', '--max-fps', str(S['max_fps'])] + iw3_args(c)
    env = iw3_env(py)
    os.makedirs(env['TMP'], exist_ok=True)
    log('iw3: ' + ' '.join(cmd[3:]), 'iw3')
    p = subprocess.Popen(cmd, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         env=env, creationflags=NO_WINDOW)
    t0 = time.time(); last = 0; buf = b''; tail = []
    while True:
        ch = p.stdout.read(256)
        if not ch:
            break
        buf += ch
        parts = re.split(rb'[\r\n]', buf)
        buf = parts.pop()
        for raw in parts:
            line = raw.decode('utf-8', 'replace').strip()
            if not line:
                continue
            tail = (tail + [line])[-12:]
            m = re.search(r'(\d+)/(\d+) \[', line)      # tqdm: "name:  42%|####| 600/1446 [00:37<..."
            if m and time.time() - last > 0.5 and int(m.group(2)) > 0:
                last = time.time()
                done, tot = int(m.group(1)), int(m.group(2))
                pct = min(100.0, done / tot * 100)
                el = time.time() - t0
                emit('progress', stage='iw3', pct=round(pct, 1), elapsed=round(el, 1),
                     eta=round(el / pct * (100 - pct), 1) if pct > 0.5 else None,
                     speed=round(done / max(el, 0.1) / max(info['video']['fps'], 1), 2))
    p.wait()
    if p.returncode != 0 or not os.path.isfile(out):
        raise RuntimeError('iw3 failed: ' + ' | '.join(tail[-5:])[-600:])
    return iw3_label(c)

# ------------------------------------------------------------------ stage 2: Mobiclip encoder
def encoder_running():
    if not IS_WIN:
        return False
    r = run_quiet(['tasklist', '/FI', 'IMAGENAME eq MobiclipMulticoreEncoder.exe', '/FO', 'CSV', '/NH'])
    return 'MobiclipMulticoreEncoder' in r.stdout

def make_moflex(job, avi, work):
    if not IS_WIN:
        raise RuntimeError('the Mobiclip encoder only runs on Windows')
    exe = os.path.join(S['encoder_dir'], 'MobiclipMulticoreEncoder.exe')
    if not os.path.exists(exe):
        raise RuntimeError(f'encoder not found: {exe}')
    if encoder_running():
        raise RuntimeError('the Mobiclip encoder is already open -- close it first')
    data = os.path.join(os.environ['APPDATA'], 'Mobiclip', 'Mobiclip Encoding Tool')
    os.makedirs(data, exist_ok=True)
    if not os.path.exists(os.path.join(data, 'User_CTR.lic')) and os.path.exists(S['license']):
        shutil.copy(S['license'], data)
    outdir = os.path.join(work, 'enc'); os.makedirs(outdir, exist_ok=True)
    js = lambda s: json.dumps(s)[1:-1]
    with open(os.path.join(HERE, 'templates', f"template_{job['output']}.queue"), encoding='utf-8') as f:
        q = f.read()
    q = (q.replace('__INPUT_FILE__', js(avi)).replace('__INPUT_DIR__', js(os.path.dirname(avi)))
          .replace('__INPUT_NAME__', js(os.path.basename(avi))).replace('__OUTPUT_DIR__', js(outdir))
          .replace('__BITRATE__', str(int(job.get('bitrate') or 1400))))
    passes = []
    for i in range(8):
        n = f'{uuid.uuid4()}.pass'; passes.append(n); q = q.replace(f'__PASS{i}__', n)
    qf = os.path.join(work, 'job.queue')
    with open(qf, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write(q)
    started = time.time()
    if os.path.exists(S['runasdate']):
        d, t = S['runasdate_date'].split(' ', 1)
        cmd = [S['runasdate'], '/movetime', d, t, exe, '-q', qf]
    else:
        cmd = [exe, '-q', qf]
    si = subprocess.STARTUPINFO(); si.dwFlags |= 1; si.wShowWindow = 0      # SW_HIDE
    subprocess.Popen(cmd, cwd=work, startupinfo=si, creationflags=NO_WINDOW)
    seen = False; t0 = time.time(); polls = 0
    while True:
        polls += 1                     # counts working seconds, so a Pause in the GUI is not mistaken for a hang
        if encoder_running():
            seen = True
        elif seen or polls > 45:
            break
        out = [x for x in os.listdir(outdir) if x.lower().endswith('.moflex')]
        emit('progress', stage='moflex', pct=None, elapsed=round(time.time() - t0, 1),
             note='writing' if out else ('encoding' if seen else 'starting'))
        time.sleep(1)
    for n in passes:
        try: os.remove(os.path.join(data, n))
        except OSError: pass
    for n in os.listdir(data):
        p = os.path.join(data, n)
        if os.path.splitext(n)[1].lower() in ('.index', '.info', '.pass', '.tmp') and os.path.getmtime(p) >= started - 2:
            try: os.remove(p)
            except OSError: pass
    out = [os.path.join(outdir, x) for x in os.listdir(outdir) if x.lower().endswith('.moflex')]
    if not seen:
        raise RuntimeError('the encoder did not start')
    if not out or os.path.getsize(out[0]) == 0:
        raise RuntimeError('the encoder finished without producing a moflex')
    return out[0]

# ------------------------------------------------------------------ stage 2b: the open encoder
# Mobipeg-MOFLEX (Oldhimaster1, GPL-2.0) with the clownsec patches -- the encoder Big Top streams with,
# here for files: it reads the source itself (no lossless AVI), runs on Windows, macOS and Linux, and
# needs no licence. Its settings are Big Top's (tuned by eye against the official encoder on an Old
# 3DS), with the slower, finer motion search a file can afford. It picks a quantizer per frame from
# the frame's brightness and detail rather than hitting a bitrate exactly; the bitrate setting moves
# that quantizer (1400-1500 kbps is the official 3D class).
OPEN_ENV = ['MOBI_8X8=1', 'MOBI_8X8_MS=1', 'MOBI_V11_MV_REFINE=1', 'MOBI_V11_MV_PENALTY=0', 'MOBI_SKIP=0',
            'MOBI_CSPLIT=1', 'MOBI_DZ=3', 'MOBI_INTER4_DZ=1', 'MOBI_P_INTRA_BIAS=200', 'MOBI_SUBME=9',
            'MOBI_AQ=1']

def open_aq_offset(kbps, is3d):
    """the per-frame quantizer's offset for a bitrate, each step ~8.5% fewer bits. Calibrated on a
    1-minute film clip against the official encoder at 1400 kbps (same file size): 3D at offset 6.0,
    2D (one eye, one encoder) at 0.0"""
    import math
    return round((6.0 if is3d else 0.0) + math.log(1400 / max(200, kbps)) / math.log(1.087), 2)

def open_encoder_path():
    p = S.get('open_encoder') or ''
    if p and os.path.isfile(p):
        return p
    w = shutil.which('moflex-ffmpeg')
    if w:
        return w
    raise RuntimeError('the open moflex encoder (moflex-ffmpeg) is not installed: Settings > Tools')

def make_open_moflex(job, info, layout, work, src=None, stage='moflex'):
    """the source straight into a video-only moflex with the open encoder (with progress)"""
    v = info['video']
    is3d = job['output'] == '3d'
    num, den = v.get('fps_num') or 24000, v.get('fps_den') or 1001
    pre = []
    if num / den > S['max_fps'] + 0.5:
        den *= 2
    pre.append(f'fps={num}/{den}')
    # one eye fitted into 400x240, black bars, the picture on an 8-pixel grid (an edge inside a block
    # would ring into the bar; the official encoder does the same)
    def eye(k):
        return (f'scale=w=iw*sar*{k}:h=ih:flags=lanczos,setsar=1,'
                'scale=400:240:force_original_aspect_ratio=decrease:force_divisible_by=8:flags=lanczos,'
                'pad=400:240:floor((ow-iw)/16)*8:floor((oh-ih)/16)*8:black,setsar=1,format=yuv420p')
    k = 2 if layout == 'hsbs' else 1
    if is3d:
        tb = f'settb=expr=1/(2*({num}/{den}))'
        fc = (f"[0:v:0]{','.join(pre)},split=2[l0][r0];[l0]crop=iw/2:ih:0:0,{eye(k)},{tb},setpts=2*N[l];"
              f"[r0]crop=iw/2:ih:iw/2:0,{eye(k)},{tb},setpts=2*N+1[r];[l][r]interleave=nb_inputs=2[v]")
        lay = ['-r', f'{2 * num}/{den}', '-mo_layout', '0', '-mo_interleave_input_double_rate', '1']
    else:
        crop = '' if layout == '2d' else 'crop=iw/2:ih:0:0,'
        fc = f"[0:v:0]{','.join(pre)},{crop}{eye(k)}[v]"
        lay = ['-r', f'{num}/{den}', '-mo_layout', '6']
    out = os.path.join(work, 'open.moflex')
    kbps = int(job.get('bitrate') or 1400)
    cmd = [open_encoder_path(), '-hide_banner', '-nostdin', '-y', '-v', 'error', '-nostats', '-progress', 'pipe:1',
           '-i', src or job['source'], '-filter_complex', fc, '-map', '[v]'] + lay + [
           '-an', '-sn', '-dn', '-c:v', 'mobiclip', '-mobiclip', '1', '-qp', '16', '-mobi_qyx', '0',
           '-preset', 'medium', '-me_method', 'hex', '-g', '240', '-keyint_min', '48', '-f', 'moflex', out]
    env = OPEN_ENV + [f'MOBI_AQ_OFFSET={open_aq_offset(kbps, is3d)}'] + (['MOBI_DUAL_EYES=1'] if is3d else [])
    old = {k2: os.environ.get(k2) for k2 in (e.split('=')[0] for e in env)}
    os.environ.update(dict(e.split('=', 1) for e in env))
    try:
        ffmpeg_progress(cmd, info['duration'], stage)
    finally:
        for k2, v2 in old.items():
            if v2 is None:
                os.environ.pop(k2, None)
            else:
                os.environ[k2] = v2
    if not os.path.isfile(out) or os.path.getsize(out) == 0:
        raise RuntimeError('the open encoder finished without producing a moflex')
    if is3d:
        log(f'paired the eyes\' timestamps: {pair_3d_timestamps(out)} pictures', stage)
    return out

def pair_3d_timestamps(path):
    """a 3D moflex's eyes as pairs: each right picture gets its left picture's timestamp, as the
    official encoder writes them (L,R share one). The open encoder stamps every picture on its own at
    twice the frame rate, and players pair the eyes by timestamp -- so one eye showed a frame off.
    Done in place, bit for bit (the same width); returns the number of pictures changed."""
    def rvarb(d, i):
        v = d[i]; i += 1
        if not (v & 0x80): return v, i
        val = (v & 0x7f) << 7; v = d[i]; i += 1
        if not (v & 0x80): return val | v, i
        val = (((v & 0x7f) | val) << 7); v = d[i]; i += 1
        if not (v & 0x80): return val | v, i
        val = (((v & 0x7f) | val) << 7) | d[i]; i += 1
        return val, i
    class BR:
        def __init__(s_, d, byte): s_.d = d; s_.start = byte; s_.bp = 0
        def pop(s_):
            b = s_.d[s_.start + (s_.bp >> 3)]; bit = (b >> (7 - (s_.bp & 7))) & 1; s_.bp += 1; return bit
        def popn(s_, n):
            v = 0
            for _ in range(n): v = (v << 1) | s_.pop()
            return v
        def poplen(s_):
            n = 1
            while s_.pop() == 0: n += 1
            return n
        def after(s_): return s_.start + ((s_.bp + 7) >> 3)
    d = bytearray(open(path, 'rb').read())
    n = len(d); pos = 0; size = None; vidx = None; fc = 0; even = None; patches = []
    while pos + 2 <= n:
        if d[pos] == 0x4C and d[pos + 1] == 0x32:
            size = int.from_bytes(d[pos + 12:pos + 14], 'big') + 1
            i = pos + 14
            while True:
                typ, i = rvarb(d, i); _, i = rvarb(d, i)
                if typ == 0: break
                if typ in (1, 3):
                    if vidx is None: vidx = d[i]
                    i += 10 + (3 if typ == 3 else 2)
                elif typ == 2: i += 6
                elif typ == 4: i += 2
                else: raise RuntimeError('unexpected moflex stream header')
            flags = d[i]; i += 1
        else:
            flags = d[pos]; i = pos + 1
        if flags & 2: i += 2
        if size is None: raise RuntimeError('not a moflex')
        ci, end = i, pos + size
        if end > n: break
        while ci < end and d[ci] != 0:
            br = BR(d, ci)
            si = br.popn(br.poplen()); endframe = br.pop(); ef = None
            if endframe:
                br.popn(br.poplen()); br.pop(); b2 = br.poplen()
                ef = (ci * 8 + br.bp, b2 * 2 + 26); ef = ef + (br.popn(ef[1]),)
            pkt = br.popn(13) + 1
            ci = br.after() + pkt
            if endframe and si == vidx:
                if fc % 2 == 0: even = ef[2]
                elif ef[2] != even and even < (1 << ef[1]): patches.append((ef[0], ef[1], even))
                fc += 1
        pos += size
    for bit, w, val in patches:
        for k in range(w):
            b = (val >> (w - 1 - k)) & 1
            bi, sh = (bit + k) >> 3, 7 - ((bit + k) & 7)
            d[bi] = (d[bi] & ~(1 << sh)) | (b << sh)
    if patches:
        with open(path, 'wb') as f:
            f.write(d)
    return len(patches)

def use_open(job):
    return (job.get('encoder') or S.get('encoder') or 'mobiclip') == 'open'

# ------------------------------------------------------------------ stage 3: assemble
def check_label(code):
    code = (code or '').strip().lower()
    if not re.fullmatch(r'[a-z][a-z0-9]{1,2}', code):
        raise RuntimeError(f'subtitle/audio label must be 2-3 characters starting with a letter, got {code!r}')
    return code

def subtitle_from_stream(path, idx, codec, work, n, offset):
    msm, mas, ass_to_srt = tools()
    if codec in IMAGE_SUBS:
        raise RuntimeError(f'subtitle stream {idx} is image-based (PGS/VobSub); it needs OCR first')
    if codec in ('ass', 'ssa'):
        a = os.path.join(work, f'sub{n}.raw.ass')
        r = run_quiet([FFMPEG, '-y', '-v', 'error', '-i', path, '-map', f'0:{idx}', '-c:s', 'copy', a])
        if r.returncode != 0:
            raise RuntimeError(f'could not extract subtitle stream {idx}: {r.stderr.strip()[:300]}')
        p = os.path.join(work, f'sub{n}.ass')
        if ass_to_srt.write_ass(a, p) == 0:
            raise RuntimeError(f'subtitle stream {idx} has no usable dialogue')
    else:
        p = os.path.join(work, f'sub{n}.srt')
        r = run_quiet([FFMPEG, '-y', '-v', 'error', '-i', path, '-map', f'0:{idx}', p])
        if r.returncode != 0:
            raise RuntimeError(f'could not extract subtitle stream {idx}: {r.stderr.strip()[:300]}')
        msm.scrub(p)
    return shift_sub(p, offset, work, n)

def subtitle_from_file(path, work, n, offset):
    msm, mas, ass_to_srt = tools()
    if re.search(r'\.(ass|ssa)$', path, re.I):
        p = os.path.join(work, f'sub{n}.ass')
        if ass_to_srt.write_ass(path, p) == 0:
            raise RuntimeError(f'{os.path.basename(path)} has no usable dialogue')
    else:
        p = os.path.join(work, f'sub{n}.srt')
        shutil.copyfile(path, p)
        msm.scrub(p)
    return shift_sub(p, offset, work, n)

def shift_sub(p, offset, work, n):
    if not offset:
        return p
    msm, _, _ = tools()
    if p.endswith('.ass'):
        return msm.shift_ass(p, os.path.join(work, f'sub{n}.shift.ass'), float(offset))
    return msm.shift_srt(p, os.path.join(work, f'sub{n}.shift.srt'), float(offset))

def audio_wav(sel, default_file, work, tag, job=None):
    msm, _, _ = tools()
    f = sel.get('file') or default_file
    p = os.path.join(work, f'audio.{tag}.wav')
    p = msm.extract_normalized(f, int(sel['stream']), p, tag, float(sel.get('offset') or 0))
    vd = (job or {}).get('_video_dur')
    if vd:
        # audio must not outlast the picture: the encoder writes the leftover as blocks with no
        # sync markers, which players can't time (length 0:00, seeking broken in older players)
        cut = trim_wav(p, vd)
        if cut > 0.5:
            warn(job, f'audio track "{tag}" ran {clock(cut)} past the end of the video -- trimmed to {clock(vd)}')
    return p

def load_wav(path):
    """16-bit PCM WAV -> (array('h'), channels, rate); read in chunks (a movie is ~400M samples)"""
    import array, wave
    with wave.open(path, 'rb') as w:
        if w.getsampwidth() != 2:
            raise RuntimeError('need 16-bit PCM')
        chn, rate = w.getnchannels(), w.getframerate()
        pcm = array.array('h')
        left = w.getnframes()
        while left > 0:
            b = w.readframes(min(1 << 20, left))
            if not b:
                break
            pcm.frombytes(b)
            left -= len(b) // (2 * chn)
    if sys.byteorder == 'big':
        pcm.byteswap()
    return pcm, chn, rate

def adpcm_section(wav, label):
    import array
    _, mas, _ = tools()
    pcm, chn, rate = load_wav(wav)
    total = len(pcm) // chn
    pad = (-total) % mas.PKT_SAMPLES
    pcm.extend(array.array('h', bytes(2 * pad * chn)))
    apkts = mas.adpcm_packets(pcm, chn, rate)
    blob = b''.join(pl for _, pl in apkts)
    return struct.pack('<4sIIHH4s', b'AUD1', 12 + len(blob), rate, chn, mas.PKT_SAMPLES, lang4(label)) + blob

def art_section(path_or_url, work):
    src = path_or_url
    if src.startswith('http'):
        import urllib.request
        src = os.path.join(work, 'poster_dl.jpg')
        urllib.request.urlretrieve(path_or_url, src)
    raw = os.path.join(work, 'art.raw')
    r = run_quiet([FFMPEG, '-y', '-v', 'error', '-i', src, '-vf', 'scale=132:188', '-pix_fmt', 'rgb565le',
                   '-f', 'rawvideo', raw])
    if r.returncode != 0:
        raise RuntimeError(f'could not read the poster image: {r.stderr.strip()[:300]}')
    data = open(raw, 'rb').read()
    if len(data) != 132 * 188 * 2:
        raise RuntimeError('poster conversion produced the wrong size')
    return struct.pack('<4sIHH', b'ART5', 4 + len(data), 132, 188) + data, src

def nfo_section(md, is3d):
    msm, _, _ = tools()
    f = msm.fold_meta
    lines = [f"title={f(str(md.get('title') or ''))}", f"year={int(md.get('year') or 0)}",
             f"category={md.get('category') or ('TV Shows' if md.get('season_episode') else 'Movies')}",
             f"genres={f(str(md.get('genres') or ''))}", f"runtime={int(md.get('runtime') or 0)}",
             f"date={md.get('date') or ''}", f"is3d={1 if is3d else 0}",
             f"format={(md.get('format') or 'converted').lower()}", f"desc={f(str(md.get('desc') or ''))}"]
    if md.get('showdesc'):
        lines.append(f"showdesc={f(str(md['showdesc']))}")
    # what the video IS, kept in the file so nothing has to guess it from the file name:
    # kind = movie | episode | short | musicvideo; for an episode, title= stays the SHOW's name and
    # season/episode/eptitle say which one (season 0 = the show's specials, episode 0 = a season's special)
    kind = md_kind(md)
    lines.append(f"kind={kind}")
    se = md.get('season_episode')
    if kind == 'episode' and se and len(se) == 2:
        lines.append(f"season={int(se[0])}")
        lines.append(f"episode={int(se[1])}")
        if md.get('eptitle'):
            lines.append(f"eptitle={f(str(md['eptitle']))}")
    if md.get('tmdb_id'):
        lines.append(f"tmdb={md['tmdb_id']}")
    if md.get('conv3d'):                 # made 3D from 2D: by what, with which settings
        lines.append(f"conv3d={f(str(md['conv3d']))}")
    if md.get('encoder'):                # which moflex encoder made the video, and how
        lines.append(f"encoder={f(str(md['encoder']))}")
    return section(b'NFO0', ('\n'.join(lines) + '\n').encode('utf-8'))

KINDS = ('movie', 'episode', 'short', 'musicvideo')
KIND_SHELF = {'movie': 'Movies', 'episode': 'TV Shows', 'short': 'Shorts', 'musicvideo': 'Music Videos'}

def md_kind(md):
    """the kind of video a job's metadata describes: its own 'kind', else an episode when it has a
    season/episode, else what the shelf implies (Shorts, Music Videos), else a movie"""
    k = str(md.get('kind') or '').lower()
    if k in KINDS:
        return k
    se = md.get('season_episode')
    if se and len(se) == 2:
        return 'episode'
    cat = str(md.get('category') or '').lower()
    return 'short' if cat == 'shorts' else 'musicvideo' if cat == 'music videos' else 'movie'

def nfo_meta(nfo, path):
    """embedded info -> the season/episode/eptitle/kind a job uses: the file's own fields first,
    the file name only for what the file does not say (files made before these fields existed)"""
    _, _, se = parse_name(path)
    out = {}
    try:
        if nfo.get('season', '') != '' and nfo.get('episode', '') != '':
            se = [int(nfo['season']), int(nfo['episode'])]
    except ValueError:
        pass
    out['season_episode'] = se
    ep = nfo.get('eptitle', '')
    if not ep and se:
        m = re.search(r'S\d+e\d+ - (.+)$', os.path.splitext(os.path.basename(path))[0], re.I)
        ep = m.group(1) if m else ''
    out['eptitle'] = ep
    out['kind'] = md_kind({'kind': nfo.get('kind'), 'season_episode': se, 'category': nfo.get('category')})
    out['tmdb_id'] = nfo.get('tmdb', '')
    return out

FAT32_MAX = 4 * 1024 ** 3 - 1          # the 3DS SD card is FAT32: no file can reach 4 GiB
ADPCM_BPS = 44100 / 1024 * (8 + 1024)   # bytes/s of one 44.1 kHz stereo IMA-ADPCM track (4-byte state per channel + 1024 samples)

def estimate_size(job, info):
    """rough output size in bytes, BEFORE any work: video at its bitrate (+3% container), each audio track as ADPCM"""
    dur = info.get('duration') or 0
    aud = job.get('audio') or {}
    if info['kind'] == 'video':
        size = (job.get('bitrate') or 1400) * 1000 / 8 * dur * 1.03 + ADPCM_BPS * dur   # track 1 (even silent is ADPCM)
    else:
        size = os.path.getsize(info['path'])            # blocks + any trailer it has now (upper bound)
    for it in extra_audio(job, None):
        if it and 'keep' not in it:
            size += ADPCM_BPS * dur
    for v in job.get('versions') or []:
        if v.get('source'):
            if is_moflex(v['source']):
                size += os.path.getsize(v['source'])
            else:
                size += (job.get('bitrate') or 1400) * 1000 / 8 * dur * 1.03 + ADPCM_BPS * dur
    return int(size)

def extra_audio(job, existing):
    """the job's trailer audio list (new "extra", or the older single "second"); None = keep what the file has"""
    aud = job.get('audio') or {}
    if 'extra' in aud:
        return [x for x in (aud['extra'] or []) if x]
    if 'second' in aud:
        sec = aud['second']
        if not sec:
            return []
        return [dict(sec, keep=0) if sec.get('keep') is True else sec]
    if existing is None:
        return []
    return [{'keep': i} for i in range(len(existing['audios']))]

def check_fat32(size, what):
    if size > FAT32_MAX:
        raise RuntimeError(f'{what} is {size / 1024 ** 3:.2f} GB -- over the 4 GB a 3DS (FAT32) SD card can hold. '
                           'Lower the bitrate on the Video tab, or drop track 2.')

def fat_clean(name):
    for bad, rep in (('/', ' - '), (':', ' -'), ('*', ''), ('?', ''), ('"', "'"), ('<', ''), ('>', ''), ('|', ''), ('\\', ' ')):
        name = name.replace(bad, rep)
    return re.sub(r'\s+', ' ', name).strip()

def library_name(md, is3d):
    """the catalog convention the old builder used: 'Title (YEAR) (3D) (S).moflex', TV with ' - S01e01 - Ep'"""
    tag3d = ' (3D)' if is3d else ''
    se = md.get('season_episode')
    if se:
        prefix = f"{md['title']} ({md.get('year') or 0}){tag3d} (S) - S{int(se[0]):02d}e{int(se[1]):02d}"
        ep = md.get('eptitle') or ''
        room = 130 - len(prefix) - 3 - 7
        if ep and len(ep) > room > 0:
            ep = (ep[:room].rsplit(' ', 1)[0].rstrip(' -!?,') or ep[:room]) + '…'
        base = prefix + (f' - {ep}' if ep else '')
    else:
        base = f"{md['title']} ({md.get('year') or 0}){tag3d} (S)"
    return fat_clean(base) + '.moflex'

def reaudio(moflex, wav, work):
    """replace a block-only moflex's in-band audio with `wav` (in place)"""
    tmp = os.path.join(work, 'reaudio.moflex')
    r = run_quiet([sys.executable, '-u', os.path.join(S['tools_dir'], 'moflex_addstreams.py'),
                   moflex, tmp, '--strip-audio', '--audio', wav])
    for line in r.stdout.splitlines():
        log(line, 'super')
    if r.returncode != 0 or not os.path.exists(tmp):
        raise RuntimeError('replacing the in-band audio failed: ' + (r.stdout + r.stderr)[-500:])
    os.replace(tmp, moflex)

def version_order(job, old_vids, old_vaud=(), existing=None):
    """the job's versions in output order: [{'self': True}...] marks this file's own video. Jobs from
    before the main could move list only the alternates (the file's own video first, main_label)."""
    vs = job.get('versions')
    old_vaud = list(old_vaud)
    old_labels = [v['label'] for v in (existing or {}).get('videos', [])]
    if vs is None:                                       # not specified: keep what the file has, names included
        vs = [{'existing': i, 'label': old_labels[i] if i < len(old_labels) else '',
               'default_audio': old_vaud[i + 1] if i + 1 < len(old_vaud) else 1} for i in range(len(old_vids))]
        job.setdefault('main_default_audio', old_vaud[0] if old_vaud else 1)
        job.setdefault('main_label', (existing or {}).get('vid_main') or '')
    vs = [dict(v, _n=n) for n, v in enumerate(vs)]       # _n: the key encode_versions used
    if not any(v.get('self') for v in vs):
        vs = [{'self': True, 'label': job.get('main_label') or '', 'default_audio': job.get('main_default_audio') or 1}] + vs
    return vs

def blocks_only(src, dst):
    """copy a moflex's block region (no CSXTRA trailer) -> dst"""
    t = read_trailer(src)
    with open(src, 'rb') as fi, open(dst, 'wb') as fo:
        left = t['base']
        while left > 0:
            b = fi.read(min(left, 8 << 20))
            if not b:
                raise RuntimeError('the moflex ended early (truncated file?)')
            fo.write(b); left -= len(b)
    return dst

def track1_wav(base, work):
    """the main version's in-band audio as 16-bit WAV (decoded, not re-normalised): what an
    alternate version carries in-band, so switching versions keeps the same track 1"""
    p = os.path.join(work, 'audio.track1.wav')
    if not os.path.exists(p):
        r = run_quiet([FFMPEG, '-y', '-v', 'error', '-i', base, '-map', '0:a:0', '-ar', '44100', '-ac', '2',
                       '-c:a', 'pcm_s16le', p])
        if r.returncode != 0:
            raise RuntimeError('could not read the main version\'s audio for the alternate versions: ' + r.stderr.strip()[:300])
    return p

def video_shape(path):
    pr = probe(path)
    v = pr['video'] or {}
    return v.get('width'), v.get('height'), pr['duration']

def assemble(job, moflex_in, existing, work, default_audio_file, is3d):
    """moflex_in: a plain moflex or SUPER MOFLEX. existing: read_trailer(moflex_in). Returns out path in work."""
    msm, mas, _ = tools()
    aud = job.get('audio') or {}
    want_super = job.get('super', True)

    # ---- block region only (addstreams wants no trailer)
    base = os.path.join(work, 'base.moflex')
    with open(moflex_in, 'rb') as fi, open(base, 'wb') as fo:
        left = existing['base']
        while left > 0:
            b = fi.read(min(left, 8 << 20)); fo.write(b); left -= len(b)

    # ---- in-band audio
    main = aud.get('main') or {'keep': True}
    lang_main = (main.get('lang') or existing.get('lang_main') or '').lower()
    wav = None
    if main.get('normalize'):
        pr = probe(base)
        if not pr['audio']:
            raise RuntimeError('this moflex has no in-band audio to normalise')
        emit('progress', stage='super', pct=5, note='normalising the in-band audio')
        wav = msm.extract_normalized(base, pr['audio'][0]['index'], os.path.join(work, 'audio.main.wav'), 'main')
    elif 'stream' in main:
        emit('progress', stage='super', pct=5, note='preparing audio track 1')
        wav = audio_wav(main, default_audio_file, work, 'main', job)
        lang_main = lang_main or 'eng'
    elif job.get('_video_only'):
        # freshly encoded from a silent source: every moflex gets an audio track, so add silence
        wav = os.path.join(work, 'audio.silent.wav')
        r = run_quiet([FFMPEG, '-y', '-v', 'error', '-f', 'lavfi', '-i', 'anullsrc=r=44100:cl=stereo',
                       '-t', f"{float(job.get('_video_dur') or job.get('_duration') or 1):.3f}", '-c:a', 'pcm_s16le', wav])
        if r.returncode != 0:
            raise RuntimeError('could not make the silent audio track: ' + r.stderr.strip()[:300])
    if wav:
        reaudio(base, wav, work)
    if not want_super:
        return base, None

    old = {cc: s for cc, s in existing['sections']}
    old_subs = [s for cc, s in existing['sections'] if cc in (b'SUB0', b'SUB1')]
    secs = []
    poster_full = None

    # ---- metadata
    md = job.get('metadata')
    if md:
        # how the video was made (conv3d=, encoder=) belongs to the video: an edit of the info keeps it
        # unless this run made the video anew
        for k in ('conv3d', 'encoder'):
            if not md.get(k) and (existing.get('nfo') or {}).get(k):
                md[k] = existing['nfo'][k]
        secs.append(nfo_section(md, is3d))
    elif b'NFO0' in old:
        secs.append(old[b'NFO0'])

    # ---- poster
    art = job.get('art')
    if art in (None, 'keep'):
        if md and md.get('poster') and art is None:
            art = md['poster']
        elif md and md.get('poster_url') and art is None and b'ART5' not in old:
            art = md['poster_url']
    if art and art not in ('keep', 'none'):
        emit('progress', stage='super', pct=40, note='poster')
        s, poster_full = art_section(art, work)
        secs.append(s)
    elif art != 'none' and b'ART5' in old:
        secs.append(old[b'ART5'])

    # ---- in-band language
    if lang_main:
        secs.append(section(b'LNG0', lang4(lang_main)))

    # ---- subtitles (in the order given; the first is the default)
    items = job.get('subtitles')
    if items is None:                                   # not specified: keep what the file has
        items = [{'existing': i} for i in range(len(old_subs))]
    if len(items) > MAX_SUBS:
        raise RuntimeError(f'{len(items)} subtitle tracks -- the player shows at most {MAX_SUBS}')
    for n, it in enumerate(items):
        emit('progress', stage='super', pct=50 + 30 * n // max(1, len(items)), note=f'subtitle {n + 1} of {len(items)}')
        if 'existing' in it:
            s = old_subs[int(it['existing'])]
            body = s[8:]
            if s[:4] == b'SUB0':
                body = lang4(it.get('label') or 'eng') + body
            elif it.get('label'):
                body = lang4(check_label(it['label'])) + body[4:]
            secs.append(section(b'SUB1', body))
            continue
        off = it.get('offset') or 0
        if 'stream' in it:
            f = it.get('file') or job['source']
            codec = next((x['codec'] for x in probe(f)['subtitles'] if x['index'] == int(it['stream'])), '')
            p = subtitle_from_stream(f, int(it['stream']), codec, work, n, off)
        else:
            p = subtitle_from_file(it['file'], work, n, off)
        data = open(p, 'rb').read()
        if len(data) > MAX_SUB_BYTES:
            raise RuntimeError(f'subtitle {n + 1} is {len(data):,} bytes -- over the 1 MiB the player accepts')
        label = it.get('label')
        if not label and 'file' in it and it.get('file'):
            m = re.search(r'\.([a-z][a-z0-9]{1,2})\.(srt|ass|ssa)$', it['file'], re.I)
            label = m.group(1) if m else None
        secs.append(section(b'SUB1', lang4(check_label(label or 'eng')) + data))

    # ---- audio tracks 2..8 (trailer)
    old_auds = [s for cc, s in existing['sections'] if cc in (b'AUD0', b'AUD1')]
    extras = extra_audio(job, existing)
    if len(extras) > MAX_EXTRA_AUDIO:
        raise RuntimeError(f'{len(extras) + 1} audio tracks -- the player holds at most {MAX_EXTRA_AUDIO + 1}')
    for n, it in enumerate(extras):
        if 'keep' in it:
            k = int(it['keep'])
            if k >= len(old_auds):
                raise RuntimeError(f'the file has no audio track {k + 2} to keep')
            s = old_auds[k]
            if it.get('label') and s[:4] == b'AUD1':
                s = s.relabel(16, lang4(check_label(it['label'])))
            elif s[:4] == b'AUD0':                      # oldest form has no label: read it whole once
                s = section(b'AUD1', s[8:16] + lang4(check_label(it.get('label') or 'alt')) + load_sec(s)[16:])
            secs.append(s)
        else:
            emit('progress', stage='super', pct=82, note=f'preparing audio track {n + 2}')
            w2 = audio_wav(it, default_audio_file, work, f'extra{n}', job)
            secs.append(adpcm_section(w2, it.get('label') or it.get('lang') or 'alt'))
            os.remove(w2)                               # an hour of PCM is ~600 MB: one at a time

    # ---- video versions: the first is the main (the file's own blocks), the rest go in the trailer
    old_vids = [s for cc, s in existing['sections'] if cc == b'VID1']
    order = version_order(job, old_vids, existing.get('vaud') or (), existing)
    n_alt = len(order) - 1
    if n_alt > MAX_VERSIONS:
        raise RuntimeError(f'{len(order)} video versions -- the player holds at most {MAX_VERSIONS + 1}')
    main_blocks = base
    if n_alt > 0:
        t1 = None
        def track1():                                   # what every version carries in-band
            nonlocal t1
            t1 = t1 or wav or track1_wav(base, work)    # base = this file's own video, track 1 final
            return t1
        built = []                                      # (label8, block-only moflex path or Sec)
        for n, v in enumerate(order):
            emit('progress', stage='super', pct=88 + 10 * n // len(order), note=f'video version {n + 1}')
            label = label8(v.get('label') or ('' if n == 0 else f'V{n + 1}'))
            if v.get('self'):
                built.append((label, base)); continue
            if 'existing' in v and not v.get('source'):
                k = int(v['existing'])
                if k >= len(old_vids):
                    raise RuntimeError(f'the file has no video version {k + 2} to keep')
                if not wav and n > 0:                    # unchanged and still in the trailer: copy as it is
                    built.append((label, old_vids[k].relabel(8, label))); continue
                alt = os.path.join(work, f'ver{n}.moflex')
                with open(alt, 'wb') as fo:
                    s = old_vids[k]
                    with open(s.path, 'rb') as fi:
                        fi.seek(s.off + 16); left = s.size - 16
                        while left > 0:
                            b = fi.read(min(left, 8 << 20)); fo.write(b); left -= len(b)
                if not wav:                              # promoted to main, track 1 unchanged: it already has it
                    built.append((label, alt)); continue
            else:
                src_v = (job.get('_alt_moflex') or {}).get(str(v.get('_n', n))) or v['source']
                alt = blocks_only(src_v, os.path.join(work, f'ver{n}.moflex'))
            reaudio(alt, track1(), work)
            built.append((label, alt))
        # every version: same size as the main one, and (warned) the same length
        mpath = built[0][1] if isinstance(built[0][1], str) else None
        mw, mh, mdur = video_shape(mpath or base)
        for n, (label, pth) in enumerate(built[1:], 2):
            if not isinstance(pth, str):
                continue                                 # a kept trailer version was checked when it was made
            aw, ah, adur = video_shape(pth)
            if (aw, ah) != (mw, mh):
                raise RuntimeError(f'video version {n} is {aw}x{ah} but the main one is {mw}x{mh}: '
                                   'all must be encoded the same way (same 2D/3D output)')
            if mdur and adur and abs(mdur - adur) > 2:
                log(f'warning: video version {n} runs {adur:.1f} s, the main one {mdur:.1f} s -- '
                    'the player switches at the same time, so they should share one timeline', 'super')
        main_blocks = built[0][1]
        for label, pth in built[1:]:
            secs.append(VidSec(pth, label) if isinstance(pth, str) else pth)
        secs.append(section(b'VLB0', built[0][0]))
        # each version's default audio track (1..total), main first: the player switches to it with the version
        n_tracks = 1 + len(extras)
        vaud = bytes(max(1, min(n_tracks, int(v.get('default_audio') or 1))) for v in order)
        secs.append(section(b'VAU0', vaud))
        log('default audio per version: ' + ', '.join(f'{(lab.rstrip(bytes(1)).decode("utf-8", "replace") or "main")}=track {d}'
                                                     for (lab, _), d in zip(built, vaud)), 'super')
        if main_blocks != base:
            log(f'main version: {built[0][0].rstrip(bytes(1)).decode("utf-8", "replace") or "(unnamed)"}', 'super')

    # ---- write: blocks + sections + footer
    out = os.path.join(work, 'out.moflex')
    with open(main_blocks, 'rb') as fi, open(out, 'wb') as fo:
        shutil.copyfileobj(fi, fo, 8 << 20)
        boff = fo.tell()
        for s in secs:
            write_sec(fo, s)
        fo.write(struct.pack('<Q8s', boff, b'CSXTRA01'))
    t = verify_trailer(out)
    kinds = {}
    for cc, _ in t['sections']:
        kinds[cc.decode()] = kinds.get(cc.decode(), 0) + 1
    log('verified trailer: ' + ', '.join(f'{k} x{v}' if v > 1 else k for k, v in kinds.items()), 'super')
    emit('progress', stage='super', pct=100)
    return out, poster_full

# ------------------------------------------------------------------ job
def versions_to_encode(job):
    return [(n, v) for n, v in enumerate(job.get('versions') or []) if v.get('source') and not is_moflex(v['source'])]

def encode_versions(job, work, is3d, first_stage, total):
    """alternate versions given as video files: encoded exactly like the main one (same output, bitrate)"""
    out, i = {}, first_stage
    # list rows are numbered as the UI shows them: with this file's own row in the list, row 1 is n=0
    off = 1 if any(v.get('self') for v in job.get('versions') or []) else 2
    for n, v in versions_to_encode(job):
        vinfo = probe(v['source'])
        if not vinfo['video']:
            raise RuntimeError(f'video version {n + off}: no video stream in {os.path.basename(v["source"])}')
        layout = (v.get('source_type') or 'auto').lower()
        if layout == 'auto':
            layout = vinfo['video']['detected']
        if is3d and layout == '2d':
            raise RuntimeError(f'video version {n + off} is 2D but the output is 3D (set its layout to HSBS or FSBS)')
        vj = dict(job, source=v['source'], output='3d' if is3d else '2d')
        wd = os.path.join(work, f'version{n}'); os.makedirs(wd, exist_ok=True)
        if use_open(job):
            emit('stage', stage='moflex', index=i, total=total,
                 text=f"Version {n + off}: encoding {vj['output'].upper()} moflex (open encoder, ~{job.get('bitrate') or 1400} kbps)")
            out[str(n)] = make_open_moflex(vj, vinfo, layout, wd)
            i += 1
            continue
        emit('stage', stage='avi', index=i, total=total, text=f'Version {n + off}: converting to lossless AVI')
        avi = os.path.join(wd, 'moflex_job.avi')
        make_avi(vj, vinfo, layout, avi)
        emit('stage', stage='moflex', index=i + 1, total=total,
             text=f"Version {n + off}: encoding {vj['output'].upper()} moflex ({job.get('bitrate') or 1400} kbps, 2 passes)")
        out[str(n)] = make_moflex(vj, avi, wd)
        os.remove(avi)
        i += 2
    job['_alt_moflex'] = out
    return i

def run_job(jobfile):
    with open(jobfile, encoding='utf-8-sig') as f:   # -sig: a job file saved with a BOM reads too
        job = json.load(f)
    t0 = time.time()
    src = job['source']
    info = probe(src)
    kind = info['kind']
    want_super = job.get('super', True)
    src_name = os.path.splitext(os.path.basename(src))[0]
    outdir = job.get('output_dir') or os.path.dirname(os.path.abspath(src))
    os.makedirs(outdir, exist_ok=True)
    work = tempfile.mkdtemp(prefix='moflex_studio_')
    try:
        if kind == 'video':
            if not info['video']:
                raise RuntimeError('no video stream in the source')
            job['output'] = (job.get('output') or '3d').lower()
            is3d = job['output'] == '3d'
            layout = (job.get('source_type') or 'auto').lower()
            if layout == 'auto':
                layout = info['video']['detected']
            conv = is3d and layout == '2d' and bool((job.get('conv3d') or {}).get('enabled'))
            if is3d and layout == '2d' and not conv:
                raise RuntimeError('3D output needs a side-by-side source (set the source type to HSBS or FSBS),'
                                   ' or turn on 2D to 3D (iw3)')
            # default audio: the source's first track (normalised) in-band
            aud = job.setdefault('audio', {})
            if 'main' not in aud and info['audio']:
                aud['main'] = {'stream': info['audio'][0]['index'], 'lang': (info['audio'][0]['lang'] or 'eng')[:3]}
            check_fat32(estimate_size(job, info), 'The output would be about')
            check_source_lengths(job, info)
            n_alt = len(versions_to_encode(job))
            per = ['moflex'] if use_open(job) else ['avi', 'moflex']     # the open encoder reads the source itself
            total = 1 + len(per) * (1 + n_alt) + (1 if conv else 0)
            stages = (['iw3'] if conv else []) + per + per * n_alt + ['super']
            emit('start', source=src, kind=kind, work=work, layout=layout, output=job['output'], stages=stages,
                 duration=info['duration'])
            pic, vinfo, base = None, info, 1
            if conv:   # 2D -> 3D first: iw3 makes the full side-by-side picture the rest works from
                c = iw3_settings(job.get('conv3d'))
                emit('stage', stage='iw3', index=1, total=total,
                     text=f"Converting 2D to 3D (iw3, {c['template']}: {c['model']})")
                pic = os.path.join(work, 'iw3_LRF_Full_SBS.mp4')
                label = iw3_convert(job, info, pic)
                job['_conv3d'] = label
                if job.get('metadata'):          # recorded in the file's info (conv3d=) with the rest of it
                    job['metadata']['conv3d'] = label
                vinfo = dict(info, video=probe(pic)['video'])
                layout, base = 'fsbs', 2
                job['_sbs'] = pic
            if use_open(job):
                emit('stage', stage='moflex', index=base, total=total,
                     text=f"Encoding {job['output'].upper()} moflex (open encoder, ~{job.get('bitrate') or 1400} kbps)")
                enc = make_open_moflex(job, vinfo, layout, work, src=pic)
                nxt = base + 1
            else:
                emit('stage', stage='avi', index=base, total=total, text='Converting to lossless AVI')
                avi = os.path.join(work, 'moflex_job.avi')
                make_avi(job, vinfo, layout, avi, src=pic)
                job['_video_dur'] = avi_secs(avi)
                if job['_video_dur'] and info['duration'] - job['_video_dur'] > 2 and not job.get('_short_warned'):
                    warn(job, f"the converted video is {clock(job['_video_dur'])} but the source runs {clock(info['duration'])}"
                              ' -- the picture may be cut short')
                emit('stage', stage='moflex', index=base + 1, total=total,
                     text=f"Encoding {job['output'].upper()} moflex ({job.get('bitrate') or 1400} kbps, 2 passes)")
                enc = make_moflex(job, avi, work)
                os.remove(avi)                               # only the encoder needed it
                nxt = base + 2
            job['_video_only'] = True                        # the encoder got no audio; assemble adds track 1
            job['_duration'] = info['duration']
            kb = int(job.get('bitrate') or 1400)
            job['_encoder'] = (f"open moflex-ffmpeg aq{open_aq_offset(kb, is3d)} ~{kb}k" if use_open(job)
                               else f"mobiclip {kb}k 2-pass")
            if job.get('metadata'):
                job['metadata']['encoder'] = job['_encoder']
            moflex_in, existing = enc, read_trailer(enc)
            n_stage = encode_versions(job, work, is3d, nxt, total)
        else:
            is3d = (job.get('output') or ('3d' if info['moflex']['is3d'] else '2d')).lower() == '3d'
            n_alt = len(versions_to_encode(job))
            stages = ['avi', 'moflex'] * n_alt + ['super']
            emit('start', source=src, kind=kind, work=work, output='3d' if is3d else '2d', stages=stages,
                 duration=info['duration'])
            check_fat32(estimate_size(job, info), 'The output would be about')
            moflex_in, existing = src, read_trailer(src)
            n_stage = encode_versions(job, work, is3d, 1, 1 + 2 * n_alt)
        emit('stage', stage='super', index=n_stage, total=n_stage,
             text='Building the SUPER MOFLEX' if want_super else 'Finishing the moflex')
        with quiet_tools('super'):
            out, poster_full = assemble(job, moflex_in, existing, work, src, is3d)
        check_fat32(os.path.getsize(out), 'The finished moflex')

        md = job.get('metadata')
        if job.get('output_name'):
            name = fat_clean(job['output_name'])
            name += '' if name.lower().endswith('.moflex') else '.moflex'
        elif md and md.get('title') and want_super:
            name = library_name(md, is3d)
        elif kind == 'video':
            name = src_name + ('_3D' if is3d else '_2D') + '.moflex'
        else:
            name = os.path.basename(src)
        final = os.path.join(outdir, name)
        if os.path.abspath(final) == os.path.abspath(src) and not job.get('overwrite'):
            final = os.path.join(outdir, os.path.splitext(name)[0] + ' (edited).moflex')
        tmp_final = final + '.part'
        shutil.move(out, tmp_final)
        os.replace(tmp_final, final)
        if poster_full and os.path.exists(poster_full):
            shutil.copyfile(poster_full, os.path.splitext(final)[0] + '.jpg')
        sbs = job.get('_sbs')
        if sbs and os.path.exists(sbs) and iw3_settings(job.get('conv3d')).get('keep_sbs'):
            # kept, as asked: beside the moflex, named the way iw3 names it -- the SOURCE's name +
            # _LRF_Full_SBS ("Full SBS" also tells Studio and Big Top its layout, if it's used again)
            keep = os.path.join(os.path.dirname(final),
                                os.path.splitext(os.path.basename(job['source']))[0] + '_LRF_Full_SBS.mp4')
            if os.path.exists(keep) and not job.get('overwrite'):
                stem, n = keep[:-4], 2
                while os.path.exists(f'{stem} ({n}).mp4'):
                    n += 1
                keep = f'{stem} ({n}).mp4'
            shutil.move(sbs, keep)
            log(f'kept the 3D side-by-side video: {keep}', 'super')
        emit('done', output=final, size=os.path.getsize(final), seconds=round(time.time() - t0, 1),
             warnings=job.get('_warnings') or [])
    finally:
        if not job.get('keep_work'):
            shutil.rmtree(work, ignore_errors=True)

def main():
    a = sys.argv[1:]
    if not a:
        print(__doc__); sys.exit(1)
    try:
        if a[0] == 'probe':
            print(json.dumps(probe(a[1]), ensure_ascii=False, indent=1))
        elif a[0] == 'export-art':
            print(json.dumps({'png': export_art(a[1], a[2])}))
        elif a[0] == 'tmdb-search':
            print(json.dumps(tmdb_search(a[1], a[2], a[3] if len(a) > 3 else None), ensure_ascii=False, indent=1))
        elif a[0] == 'tmdb-details':
            se = [int(x) for x in a[3].split(':')] if len(a) > 3 else None
            print(json.dumps(tmdb_details(a[1], a[2], se), ensure_ascii=False, indent=1))
        elif a[0] == 'run':
            run_job(a[1])
        elif a[0] == 'make':          # the command line for people (moflex-studio.cmd make ...)
            make_cmd(a[1:])
        elif a[0] == 'iw3-save-template':   # iw3-save-template NAME '{"label":..,"model":..}'
            print(json.dumps({'name': iw3_save_template(a[1], json.loads(a[2]))}))
        elif a[0] == 'iw3-delete-template':
            iw3_delete_template(a[1]); print(json.dumps({'ok': True}))
        elif a[0] == 'iw3-install':          # iw3-install [MODEL ...] (default Distill_Any_L; "none"); events
            iw3_install([] if a[1:] == ['none'] else a[1:] or ['Distill_Any_L'])
        elif a[0] == 'iw3-fetch':            # iw3-fetch MODEL ...: download models now; events
            iw3_fetch_models(a[1:]); emit('done', output='')
        elif a[0] == 'pair-3d':              # pair-3d FILE.moflex: fix an open-encoder 3D file's eye pairing
            print(json.dumps({'changed': pair_3d_timestamps(a[1])}))
        elif a[0] == 'iw3-check':            # iw3-check DIR: a usable iw3 there?
            print(json.dumps(iw3_check(a[1]), indent=1))
        elif a[0] == 'iw3-import-models':    # iw3-import-models DIR: link/copy models found there; events
            iw3_import_models(a[1])
        elif a[0] == 'iw3-models':           # the downloaded models, with sizes
            print(json.dumps({'models': iw3_models(), 'offer': {m: {'mb': v[0], 'label': v[2]} for m, v in IW3_MODEL_FILES.items()}}, indent=1))
        elif a[0] == 'iw3-delete-model':
            iw3_delete_model(a[1]); print(json.dumps({'ok': True}))
        elif a[0] == 'iw3-status':    # for the 2D to 3D panel: is iw3 here, and what it offers
            root, py = iw3_find()
            print(json.dumps({'installed': bool(root), 'dir': root or '', 'looked': iw3_dirs(),
                              'models': IW3_MODELS, 'catalog': iw3_catalog(),
                              'defaults': IW3_DEFAULTS, 'templates': iw3_templates()}, indent=1))
        else:
            print(__doc__); sys.exit(1)
    except SystemExit:
        raise
    except Exception as ex:
        emit('error', message=str(ex))
        sys.exit(1)

if __name__ == '__main__':
    main()
