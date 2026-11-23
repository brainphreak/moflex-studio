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

MAKE_HELP = """moflex-studio make -- one video (or moflex) into a SUPER MOFLEX, from the command line

  moflex-studio make SOURCE [options]

  --3d | --2d                 what to encode (default: 3D for a 3D source or with --conv3d, else 2D)
  --source-type T             auto (default), 2d, hsbs, fsbs: how the source's picture is laid out
  --conv3d [TEMPLATE]         make a 2D source 3D with iw3 first: best (default), high, medium, low,
                              or a template of your own (see --templates)
  --keep-sbs                  keep iw3's 3D side-by-side video beside the output
  --templates                 list the 2D-to-3D templates and exit
  --title T  --year Y         the info (default: from the file name)
  --category C                Movies (default), TV Shows, Shorts, Music Videos
  --genres G  --desc D        more info
  --tmdb                      look the info and poster up on TMDB (needs a TMDB token in Studio's settings)
  --poster FILE|URL           a poster
  --bitrate KBPS              the moflex video bitrate (default 1400)
  --out DIR                   where the output goes (default: beside the source)
  --name NAME                 the output's file name (default: "Title (Year) (3D) (S).moflex")
  --plain                     a plain moflex (no SUPER MOFLEX info, subtitles or extra audio)
  --keep-work                 keep the work folder (for looking into a problem)
  --json                      JSON events instead of plain lines (what the GUI reads)

Examples:
  moflex-studio make "Ace Ventura (1994).mkv" --conv3d
  moflex-studio make "Ace Ventura (1994).mkv" --conv3d low --out D:\\3DS
  moflex-studio make "Avatar (2009) Half SBS.mkv" --3d --source-type hsbs --tmdb
"""

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
    ap.add_argument('--plain', action='store_true'); ap.add_argument('--keep-work', action='store_true')
    ap.add_argument('--json', action='store_true')
    ap.add_argument('-h', '--help', action='store_true')
    a = ap.parse_args(argv)
    HUMAN = not a.json
    if a.help or (not a.source and not a.templates):
        print(MAKE_HELP); return
    if a.templates:
        for k, v in iw3_templates().items():
            c = iw3_settings({'template': k})
            print(f"  {k:<10} {v.get('label', ''):<40} {c['model']}, depth {c['resolution']}, {c['method']}"
                  f"{'' if v.get('builtin') else '  (yours)'}")
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
    if a.conv3d:
        if a.conv3d not in iw3_templates():
            raise RuntimeError(f"no 2D-to-3D template '{a.conv3d}' (see --templates)")
        job['conv3d'] = {'enabled': True, 'template': a.conv3d, 'keep_sbs': a.keep_sbs}
        job['source_type'] = '2d' if a.source_type == 'auto' else a.source_type
    if info['kind'] == 'video':
        is3d_src = (job['source_type'] if job['source_type'] != 'auto' else info['video']['detected']) != '2d'
        job['output'] = '2d' if a.out2d else '3d' if (a.out3d or a.conv3d or is3d_src) else '2d'
    elif a.out3d or a.out2d:
        job['output'] = '3d' if a.out3d else '2d'
    if a.bitrate:
        job['bitrate'] = a.bitrate
    if a.name:
        job['output_name'] = a.name
    if a.poster:
        job['art'] = a.poster
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
    'keep_sbs': False,              # keep the 3D SBS video beside the output
}
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
    out.update({k: v for k, v in c.items() if k in IW3_DEFAULTS})
    out['template'] = c.get('template') or 'best'
    return out

IW3_MODELS = ['Distill_Any_L', 'Distill_Any_B', 'Distill_Any_S', 'VDA_L', 'VDA_B', 'VDA_S',
              'VDA_Stream_L', 'Any_V2_L', 'Any_V2_B', 'Any_V2_S', 'Any_L', 'Any_B', 'Any_S',
              'DepthPro', 'DepthPro_S', 'ZoeD_Any_N', 'ZoeD_N']

def iw3_dirs():
    """where iw3 may be: the settings' folder, the usual Documents\\iw3, Studio's own install"""
    out = []
    if S.get('iw3_dir'):
        out.append(S['iw3_dir'])
    home = os.path.expanduser('~')
    out += [os.path.join(home, 'Documents', 'iw3'),
            os.path.join(os.environ.get('LOCALAPPDATA', home), 'Moflex Studio', 'iw3')]
    return out

def iw3_find():
    """(root, python.exe) of a working iw3 install, or (None, None). root holds python\\, nunif\\, git\\"""
    for d in iw3_dirs():
        py = os.path.join(d, 'python', 'python.exe' if IS_WIN else 'python')
        if os.path.isfile(py) and os.path.isdir(os.path.join(d, 'nunif', 'iw3')):
            return d, py
    return None, None

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
    return a + ['--video-codec', 'libx264', '--crf', str(int(c.get('crf') or 20))]

def iw3_label(c):
    """how a file was converted, kept in its info (conv3d=): so a lower model is always told apart"""
    ed = c.get('edge_dilation') or [0]
    ed = '/'.join(str(x) for x in (ed if isinstance(ed, list) else [ed]))
    return (f"iw3 {c.get('template', 'custom')}: {c['model']} d{c['divergence']} c{c['convergence']}"
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
    cmd = [py, '-m', 'iw3.cli', '-i', job['source'], '-o', out, '--yes', '--max-fps', str(S['max_fps'])] + iw3_args(c)
    env = dict(os.environ, PYTHONUTF8='1')
    env['PATH'] = os.pathsep.join([os.path.join(root, 'python'), os.path.join(root, 'python', 'Scripts'),
                                   os.path.join(root, 'git', 'cmd'), env.get('PATH', '')])
    log('iw3: ' + ' '.join(cmd[3:]), 'iw3')
    p = subprocess.Popen(cmd, cwd=os.path.join(root, 'nunif'), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
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
            total = 3 + 2 * n_alt + (1 if conv else 0)
            stages = (['iw3'] if conv else []) + ['avi', 'moflex'] + ['avi', 'moflex'] * n_alt + ['super']
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
            job['_video_only'] = True                        # the encoder got no audio; assemble adds track 1
            job['_duration'] = info['duration']
            os.remove(avi)                                   # only the encoder needed it
            moflex_in, existing = enc, read_trailer(enc)
            n_stage = encode_versions(job, work, is3d, base + 2, total)
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
            shutil.move(sbs, os.path.splitext(final)[0] + '_LRF_Full_SBS.mp4')   # kept, as asked
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
        elif a[0] == 'iw3-status':    # for the 2D to 3D panel: is iw3 here, and what it offers
            root, py = iw3_find()
            print(json.dumps({'installed': bool(root), 'dir': root or '', 'looked': iw3_dirs(),
                              'models': IW3_MODELS, 'defaults': IW3_DEFAULTS, 'templates': iw3_templates()}, indent=1))
        else:
            print(__doc__); sys.exit(1)
    except SystemExit:
        raise
    except Exception as ex:
        emit('error', message=str(ex))
        sys.exit(1)

if __name__ == '__main__':
    main()
