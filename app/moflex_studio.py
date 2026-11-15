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

Job (every field optional except source):
{
 "source": "path",
 "source_type": "auto|2d|hsbs|fsbs",       video input: how the picture is laid out
 "output": "3d|2d",                        video input: what to encode; any input: the 3D flag
 "bitrate": 1400,                          video input
 "audio": {
   "main":   {"keep": true} | {"normalize": true}
           | {"file": null|"path", "stream": N, "offset": 0.0, "lang": "eng"},
   "second": {"keep": true, "label": "JPN"} | null
           | {"file": null|"path", "stream": N, "offset": 0.0, "lang": "jpn", "label": "COM"}
 },
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

def emit(_type, **kw):
    kw['type'] = _type
    sys.__stdout__.write(json.dumps(kw, ensure_ascii=False) + '\n')
    sys.__stdout__.flush()

def log(text, stage=None):
    emit('log', stage=stage, text=text)

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
def read_trailer(path):
    """-> dict(base=len of the block region, sections=[(4cc, full section bytes)], parsed views)"""
    size = os.path.getsize(path)
    t = {'base': size, 'sections': [], 'nfo': {}, 'lang_main': '', 'audio2': None, 'subs': [], 'art': False}
    with open(path, 'rb') as f:
        if size < 16:
            return t
        f.seek(size - 16)
        ft = f.read(16)
        if ft[8:] != b'CSXTRA01':
            return t
        off = struct.unpack('<Q', ft[:8])[0]
        f.seek(off)
        blob = f.read(size - 16 - off)
    t['base'] = off
    q = 0
    while q + 8 <= len(blob):
        cc, ln = struct.unpack_from('<4sI', blob, q)
        if q + 8 + ln > len(blob):
            raise RuntimeError(f'corrupt trailer section at {q}')
        t['sections'].append((cc, blob[q:q + 8 + ln]))
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
                data_len = len(body) - 12
            else:
                rate, ch = struct.unpack_from('<IH', body, 0); spp = 1024; lang = ''; data_len = len(body) - 8
            secs = data_len * 2 / max(1, ch) / max(1, rate)          # 4-bit IMA: ~2 samples per byte
            t['audio2'] = {'lang': lang, 'rate': rate, 'channels': ch, 'seconds': round(secs, 1), 'bytes': len(body)}
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
                         'sar': sar, 'fps_num': fn, 'fps_den': fd, 'fps': fn / fd, 'detected': det}
    for s in st:
        t = s.get('tags', {})
        if s['codec_type'] == 'audio':
            info['audio'].append({'index': s['index'], 'codec': s.get('codec_name'),
                                  'channels': s.get('channels'), 'lang': t.get('language', 'und'),
                                  'title': t.get('title', ''), 'default': bool(s.get('disposition', {}).get('default'))})
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
        info['moflex'] = {'is3d': is3d == '1', 'nfo': nfo, 'lang_main': t['lang_main'],
                          'audio2': t['audio2'], 'subs': t['subs'], 'art': t['art']}
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
def tmdb(path, **params):
    import urllib.request, urllib.parse
    if not S['tmdb_token']:
        raise RuntimeError('no TMDB token set')
    q = urllib.parse.urlencode(params)
    req = urllib.request.Request(f'https://api.themoviedb.org/3/{path}' + (f'?{q}' if q else ''))
    req.add_header('Authorization', f'Bearer {S["tmdb_token"]}')
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.load(r)

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
        if se:
            e = tmdb(f'tv/{tid}/season/{se[0]}/episode/{se[1]}')
            info['eptitle'] = e.get('name', '')
            info['date'] = e.get('air_date', '') or ''
            if e.get('runtime'):
                info['runtime'] = e['runtime']
            if e.get('name'):
                info['desc'] = f'S{se[0]}.E{se[1]} “{e["name"]}” - {e.get("overview", "")}'
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

def make_avi(job, info, layout, avi):
    """video only: with an audio stream the Mobiclip encoder's final write runs single-threaded at
    ~1.7x realtime (45 min for a movie); without one the whole encode takes minutes. Track 1 is
    added afterwards by assemble() (it replaced the encoder's audio with the normalised one anyway)."""
    cmd = [FFMPEG, '-hide_banner', '-nostdin', '-y', '-loglevel', 'error', '-nostats',
           '-progress', 'pipe:1', '-i', job['source']]
    cmd += ['-filter_complex', video_filter(info['video'], layout, job['output'], S['max_fps']), '-map', '[v]']
    if info['duration']:
        cmd += ['-t', f"{info['duration']:.3f}"]
    cmd += ['-an', '-sn', '-dn', '-fps_mode', 'cfr', '-c:v', 'libx264', '-preset', 'ultrafast', '-qp', '0', avi]
    ffmpeg_progress(cmd, info['duration'], 'avi')

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

def audio_wav(sel, default_file, work, tag):
    msm, _, _ = tools()
    f = sel.get('file') or default_file
    p = os.path.join(work, f'audio.{tag}.wav')
    return msm.extract_normalized(f, int(sel['stream']), p, tag, float(sel.get('offset') or 0))

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
    return section(b'NFO0', ('\n'.join(lines) + '\n').encode('utf-8'))

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
        wav = audio_wav(main, default_audio_file, work, 'main')
        lang_main = lang_main or 'eng'
    elif job.get('_video_only'):
        # freshly encoded from a silent source: every moflex gets an audio track, so add silence
        wav = os.path.join(work, 'audio.silent.wav')
        r = run_quiet([FFMPEG, '-y', '-v', 'error', '-f', 'lavfi', '-i', 'anullsrc=r=44100:cl=stereo',
                       '-t', f"{float(job.get('_duration') or 1):.3f}", '-c:a', 'pcm_s16le', wav])
        if r.returncode != 0:
            raise RuntimeError('could not make the silent audio track: ' + r.stderr.strip()[:300])
    if wav:
        base2 = os.path.join(work, 'base2.moflex')
        r = run_quiet([sys.executable, '-u', os.path.join(S['tools_dir'], 'moflex_addstreams.py'),
                       base, base2, '--strip-audio', '--audio', wav])
        for line in r.stdout.splitlines():
            log(line, 'super')
        if r.returncode != 0 or not os.path.exists(base2):
            raise RuntimeError('replacing the in-band audio failed: ' + (r.stdout + r.stderr)[-500:])
        os.replace(base2, base)
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

    # ---- second audio (trailer)
    second = aud.get('second', {'keep': True} if (b'AUD1' in old or b'AUD0' in old) else None)
    if second and second.get('keep'):
        s = old.get(b'AUD1') or old.get(b'AUD0')
        if s:
            if second.get('label') and s[:4] == b'AUD1':
                s = s[:16] + lang4(check_label(second['label'])) + s[20:]
            secs.append(s)
    elif second and 'stream' in second:
        emit('progress', stage='super', pct=82, note='preparing audio track 2')
        w2 = audio_wav(second, default_audio_file, work, 'second')
        secs.append(adpcm_section(w2, second.get('label') or second.get('lang') or 'alt'))

    # ---- write: blocks + sections + footer
    out = os.path.join(work, 'out.moflex')
    with open(base, 'rb') as fi, open(out, 'wb') as fo:
        shutil.copyfileobj(fi, fo, 8 << 20)
        boff = fo.tell()
        for s in secs:
            fo.write(s)
        fo.write(struct.pack('<Q8s', boff, b'CSXTRA01'))
    t = verify_trailer(out)
    kinds = {}
    for cc, _ in t['sections']:
        kinds[cc.decode()] = kinds.get(cc.decode(), 0) + 1
    log('verified trailer: ' + ', '.join(f'{k} x{v}' if v > 1 else k for k, v in kinds.items()), 'super')
    emit('progress', stage='super', pct=100)
    return out, poster_full

# ------------------------------------------------------------------ job
def run_job(jobfile):
    with open(jobfile, encoding='utf-8') as f:
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
            if is3d and layout == '2d':
                raise RuntimeError('3D output needs a side-by-side source (set the source type to HSBS or FSBS)')
            # default audio: the source's first track (normalised) in-band
            aud = job.setdefault('audio', {})
            if 'main' not in aud and info['audio']:
                aud['main'] = {'stream': info['audio'][0]['index'], 'lang': (info['audio'][0]['lang'] or 'eng')[:3]}
            stages = ['avi', 'moflex', 'super']
            emit('start', source=src, kind=kind, work=work, layout=layout, output=job['output'], stages=stages,
                 duration=info['duration'])
            emit('stage', stage='avi', index=1, total=3, text='Converting to lossless AVI')
            avi = os.path.join(work, 'moflex_job.avi')
            make_avi(job, info, layout, avi)
            emit('stage', stage='moflex', index=2, total=3,
                 text=f"Encoding {job['output'].upper()} moflex ({job.get('bitrate') or 1400} kbps, 2 passes)")
            enc = make_moflex(job, avi, work)
            job['_video_only'] = True                        # the encoder got no audio; assemble adds track 1
            job['_duration'] = info['duration']
            os.remove(avi)                                   # only the encoder needed it
            moflex_in, existing = enc, read_trailer(enc)
            n_stage = 3
        else:
            is3d = (job.get('output') or ('3d' if info['moflex']['is3d'] else '2d')).lower() == '3d'
            stages = ['super']
            emit('start', source=src, kind=kind, work=work, output='3d' if is3d else '2d', stages=stages,
                 duration=info['duration'])
            moflex_in, existing = src, read_trailer(src)
            n_stage = 1
        emit('stage', stage='super', index=n_stage, total=n_stage,
             text='Building the SUPER MOFLEX' if want_super else 'Finishing the moflex')
        with quiet_tools('super'):
            out, poster_full = assemble(job, moflex_in, existing, work, src, is3d)

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
        emit('done', output=final, size=os.path.getsize(final), seconds=round(time.time() - t0, 1))
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
        else:
            print(__doc__); sys.exit(1)
    except SystemExit:
        raise
    except Exception as ex:
        emit('error', message=str(ex))
        sys.exit(1)

if __name__ == '__main__':
    main()
