"""Original motion design + original synthesized score. Render with local FFmpeg."""
from pathlib import Path
from functools import lru_cache
import argparse
import json
import math
import subprocess
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
W, H, FPS, DURATION = 1920, 1080, 30, 32.0
GREEN = (184, 246, 91)
WHITE = (240, 246, 240)
MUTED = (136, 154, 156)
RED = (255, 112, 107)
FONT = Path(r"C:\Windows\Fonts\msyh.ttc")
BOLD = Path(r"C:\Windows\Fonts\msyhbd.ttc")
LATIN = Path(r"C:\Windows\Fonts\bahnschrift.ttf")
ICON = Image.open(ROOT.parent / "assets" / "replaykeeper.png").convert("RGBA")
STATUS = Image.open(ROOT / "status-window.png").convert("RGBA")
STARTS = [0, 4, 8, 13, 20, 25]
ENDS = [4, 8, 13, 20, 25, 32]
CAPTIONS = (ROOT / "narration" / "script.txt").read_text(encoding="utf-8").splitlines()

def clamp(v, low=0.0, high=1.0):
    return max(low, min(high, v))

def ease(v):
    return 1 - (1 - clamp(v)) ** 3

def smooth(v):
    v = clamp(v)
    return v * v * (3 - 2 * v)

@lru_cache(maxsize=80)
def font(size, bold=False, latin=False):
    return ImageFont.truetype(str(LATIN if latin else BOLD if bold else FONT), size)

@lru_cache(maxsize=100)
def text_image(value, size, color=WHITE, bold=False, latin=False):
    f = font(size, bold, latin)
    box = f.getbbox(value)
    image = Image.new("RGBA", (max(1, box[2] - box[0] + 8), box[3] - box[1] + 8))
    ImageDraw.Draw(image).text((4 - box[0], 4 - box[1]), value, font=f, fill=color)
    return image

def put_text(canvas, value, xy, size, color=WHITE, bold=False, alpha=1, latin=False, center=False):
    layer = text_image(value, size, color, bold, latin)
    if alpha < 1:
        layer = layer.copy()
        layer.putalpha(layer.getchannel("A").point(lambda p: int(p * clamp(alpha))))
    x, y = xy
    if center:
        x -= layer.width / 2
    canvas.alpha_composite(layer, (int(x), int(y)))

@lru_cache(maxsize=72)
def icon_at(size):
    return ICON.resize((size, size), Image.Resampling.LANCZOS)

def put_icon(canvas, cx, cy, size, alpha=1):
    layer = icon_at(int(size))
    if alpha < 1:
        layer = layer.copy()
        layer.putalpha(layer.getchannel("A").point(lambda p: int(p * clamp(alpha))))
    canvas.alpha_composite(layer, (int(cx - size / 2), int(cy - size / 2)))

def make_background():
    y, x = np.mgrid[0:H, 0:W]
    g = np.exp(-(((x - 1490) / 700) ** 2 + ((y - 370) / 500) ** 2))
    g2 = np.exp(-(((x - 240) / 1000) ** 2 + ((y - 960) / 800) ** 2))
    array = np.empty((H, W, 3), np.uint8)
    for i, (base, glow, bottom) in enumerate([(9, 15, 3), (15, 24, 7), (19, 9, 9)]):
        array[:, :, i] = np.clip(base + g * glow + g2 * bottom, 0, 255)
    image = Image.fromarray(array).convert("RGBA")
    d = ImageDraw.Draw(image)
    for x0 in range(40, W, 80):
        for y0 in range(35, H, 80):
            d.ellipse((x0, y0, x0 + 2, y0 + 2), fill=(38, 56, 55))
    return image

BACKGROUND = make_background()

def chrome(canvas, t, scene):
    d = ImageDraw.Draw(canvas)
    d.line((100, 111, 1820, 111), fill=(49, 67, 64), width=1)
    d.line((100, 1008, 1820, 1008), fill=(49, 67, 64), width=1)
    put_text(canvas, "REPLAYKEEPER", (100, 60), 24, GREEN, latin=True)
    put_text(canvas, "即时回放守护", (1420, 62), 23, MUTED)
    put_text(canvas, f"0{scene + 1} / 06", (1740, 63), 22, MUTED, latin=True)
    d.line((100, 1008, 100 + int(1720 * t / DURATION), 1008), fill=GREEN, width=3)
    put_text(canvas, "WINDOWS  /  INSTANT REPLAY", (100, 1031), 18, MUTED, latin=True)
    put_text(canvas, "专心游戏，让守护留在后台", (1490, 1028), 18, MUTED)

def chip(canvas, label, box, color=GREEN, active=True):
    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle(box, radius=18, fill=(26, 37, 37), outline=(65, 86, 75) if active else (44, 54, 57), width=1)
    x, y, x2, y2 = box
    d.ellipse((x + 26, y + (y2-y)/2 - 5, x + 36, y + (y2-y)/2 + 5), fill=color)
    put_text(canvas, label, (x + 54, y + 19), 25, color)

def crosshair(canvas, cx, cy, r, color=GREEN, angle=0):
    d = ImageDraw.Draw(canvas)
    d.arc((cx-r, cy-r, cx+r, cy+r), start=angle, end=angle+255, fill=color, width=3)
    d.arc((cx-r-30, cy-r-30, cx+r+30, cy+r+30), start=-angle+140, end=-angle+270, fill=(72, 109, 82), width=2)
    for a in (0, 90, 180, 270):
        rad = math.radians(a)
        p = (cx + math.cos(rad)*(r-20), cy + math.sin(rad)*(r-20))
        q = (cx + math.cos(rad)*(r+15), cy + math.sin(rad)*(r+15))
        d.line((*p, *q), fill=color, width=3)
    d.ellipse((cx-5, cy-5, cx+5, cy+5), fill=color)

def timeline(canvas, x, y, width, u, interrupted=False):
    d = ImageDraw.Draw(canvas)
    d.line((x, y, x+width, y), fill=(56, 73, 71), width=2)
    end = .51 if interrupted else clamp(u)
    d.line((x, y, x+width*end, y), fill=RED if interrupted else GREEN, width=4)
    for k in range(29):
        xx = x+width*k/28
        amp = 12 + 25 * abs(math.sin(k*1.77))
        color = (97, 138, 105) if k/28 <= end else (49, 62, 64)
        d.line((xx, y-amp, xx, y+amp), fill=color, width=4)
    d.rounded_rectangle((x+width*end-8, y-58, x+width*end+8, y+58), radius=6, fill=RED if interrupted else GREEN)

def scene_art(index, u, t):
    canvas = Image.new("RGBA", (W, H))
    d = ImageDraw.Draw(canvas)
    enter = ease(u / .7)
    shift = int(44 * (1-enter))
    if index == 0:
        put_text(canvas, "精彩的一刻", (130, 262+shift), 108, WHITE, True, enter)
        put_text(canvas, "不该错过。", (130, 394+shift), 108, GREEN, True, enter)
        put_text(canvas, "FOR THE MOMENTS WORTH KEEPING", (135, 571), 26, MUTED, alpha=enter, latin=True)
        crosshair(canvas, 1450, 432, int(174+12*math.sin(u)), angle=u*25)
        for k in range(7):
            yy = 702-k*36
            span = 180+k*40
            d.line((1450-span, yy, 1450, yy-120, 1450+span, yy), fill=(27+k*5, 54+k*5, 40+k*3), width=2)
        timeline(canvas, 140, 780, 1640, .12+.86*clamp(u/3.6))
        put_text(canvas, "THE HIGHLIGHT", (1320, 730), 21, GREEN, latin=True)
    elif index == 1:
        put_text(canvas, "回放，可能悄悄关闭。", (130, 228+shift), 89, WHITE, True, enter)
        put_text(canvas, "精彩还在继续，录制状态却变了。", (135, 365), 35, MUTED, alpha=enter)
        closed = u > 1.25
        color = RED if closed else GREEN
        d.rounded_rectangle((130, 503, 1790, 720), radius=30, fill=(22, 31, 35), outline=(65, 75, 73), width=1)
        put_text(canvas, "即时回放", (190, 552), 46, WHITE, True)
        chip(canvas, "已关闭" if closed else "已开启", (1435, 563, 1710, 638), color)
        timeline(canvas, 580, 609, 740, min(.97,u/1.6), interrupted=closed)
        put_text(canvas, "状态变化 · 动画示意", (137, 786), 22, MUTED)
    elif index == 2:
        size = int(390 * (.88+.12*enter) + 3*math.sin(u*1.5))
        put_icon(canvas, 535, 485+3*math.sin(u*1.2), size, enter)
        r = int(255+7*math.sin(u*1.8))
        d.arc((535-r, 485-r, 535+r, 485+r), start=u*25, end=u*25+120, fill=(81, 119, 59), width=2)
        put_text(canvas, "ReplayKeeper", (855, 331+shift), 105, WHITE, alpha=enter, latin=True)
        put_text(canvas, "即时回放守护", (859, 474+shift), 74, GREEN, True, enter)
        put_text(canvas, "让回放状态，有人替你留意。", (862, 613), 34, MUTED, alpha=enter)
        chip(canvas, "低调运行 · 持续关注", (859, 722, 1435, 800))
    elif index == 3:
        put_text(canvas, "意外关闭？尝试自动恢复。", (130, 215+shift), 81, WHITE, True, enter)
        put_text(canvas, "先确认，再开启；恢复后再次复查。", (136, 339), 34, MUTED, alpha=enter)
        labels = ["后台检测", "确认关闭", "尝试开启"]
        subs = ["每 3 秒检查", "连续确认状态", "发送已配置快捷键"]
        for k in range(3):
            x = 130+k*560
            active = u > .65+k*.85
            d.rounded_rectangle((x, 491, x+510, 699), radius=28, fill=(24, 37, 35) if active else (22, 30, 34), outline=GREEN if active else (56, 68, 68), width=2 if active else 1)
            put_text(canvas, f"0{k+1}", (x+35, 520), 30, GREEN if active else MUTED, latin=True)
            put_text(canvas, labels[k], (x+35, 563), 43, WHITE, True)
            put_text(canvas, subs[k], (x+35, 628), 25, MUTED)
            if k<2:
                d.line((x+523, 593, x+548, 593), fill=GREEN if active else MUTED, width=3)
                d.line((x+540, 586, x+548, 593, x+540, 600), fill=GREEN if active else MUTED, width=3)
        enabled = u > 4.2
        chip(canvas, "状态复查：已开启" if enabled else "正在复查状态…", (635, 765, 1290, 841), GREEN if enabled else MUTED)
        if enabled:
            radius=7+8*(.5+.5*math.sin(u*3))
            d.ellipse((666-radius,803-radius,666+radius,803+radius),outline=GREEN,width=1)
        put_text(canvas, "流程示意 · 已压缩等待时间；恢复失败时会间隔重试", (960, 898), 22, MUTED, center=True)
    elif index == 4:
        put_text(canvas, "登录自启动", (130, 277+shift), 83, WHITE, True, enter)
        put_text(canvas, "安静常驻托盘。", (130, 390+shift), 83, GREEN, True, enter)
        put_text(canvas, "不弹主窗口，想看状态时再打开。", (136, 533), 31, MUTED, alpha=enter)
        chip(canvas, "支持暂停与退出", (133, 660, 690, 738))
        image = STATUS.resize((722, 474), Image.Resampling.LANCZOS)
        xx, yy = 1005+int(25*(1-enter)), 292+int(3*math.sin(u*1.3))
        d.rounded_rectangle((xx-16, yy-16, xx+738, yy+490), radius=26, fill=(19, 28, 31), outline=(75, 97, 83), width=2)
        canvas.alpha_composite(image, (xx, yy))
        put_text(canvas, "程序真实界面 · 当前状态快照", (1008, 807), 22, MUTED)
        d.rounded_rectangle((1010, 862, 1730, 931), radius=17, fill=(24, 35, 36))
        put_icon(canvas, 1650, 896, 46)
        put_text(canvas, "运行于 Windows 通知区域", (1045, 880), 23, MUTED)
    else:
        put_icon(canvas, 960, 300, int(232+6*math.sin(u*.9)), enter)
        put_text(canvas, "ReplayKeeper", (960, 468+shift), 105, WHITE, alpha=enter, latin=True, center=True)
        put_text(canvas, "你专心游戏，它守护回放。", (960, 623+shift), 67, GREEN, True, enter, center=True)
        for k,label in enumerate(["后台检测", "尝试恢复", "登录自启动"]):
            chip(canvas, label, (421+k*372, 784, 755+k*372, 856))
        put_text(canvas, "守护即时回放开关；实际录制受 NVIDIA 功能与环境限制。", (960, 934), 21, MUTED, center=True)
    return canvas

def frame(t):
    canvas = BACKGROUND.copy()
    ambient=ImageDraw.Draw(canvas)
    for k in range(16):
        x=120+(k*197+t*(4+k%3))%1680
        y=165+(k*83+t*(2+k%4))%725
        ambient.ellipse((x,y,x+2,y+2),fill=(61,88,61))
    index = max(i for i,s in enumerate(STARTS) if t>=s)
    local = t-STARTS[index]
    art = scene_art(index,local,t)
    # Continuous crossfades, without hard cuts or an unreadable last-frame flash.
    if local < .28 and index > 0:
        prev = scene_art(index-1, t-STARTS[index-1], t)
        canvas.alpha_composite(prev)
        art.putalpha(art.getchannel("A").point(lambda p:int(p*smooth(local/.28))))
    canvas.alpha_composite(art)
    chrome(canvas,t,index)
    # Subtitle timing follows the actual spoken clip duration, written during audio assembly.
    for k,(start,duration) in enumerate(NARRATION_TIMES):
        if start <= t < start+duration:
            put_text(canvas,CAPTIONS[k],(960,960),25,WHITE,center=True)
            break
    fade = min(clamp(t/.42),clamp((DURATION-t)/.65))
    if fade < 1:
        black=Image.new("RGBA",(W,H),(0,0,0,int((1-fade)*255)))
        canvas.alpha_composite(black)
    return canvas.convert("RGB")

NARRATION_TIMES=[]

def build_audio():
    sr=48000
    n=int(DURATION*sr)
    music=np.zeros((n,2),dtype=np.float64)
    rng=np.random.default_rng(73)
    # D minor electronic motif, entirely synthesized here (no borrowed recording).
    beat=60/96
    roots=[73.416,65.406,58.270,65.406]
    for k in range(int(DURATION/beat)+1):
        start=int(k*beat*sr)
        if start>=n: break
        length=min(int(.5*sr),n-start)
        a=np.arange(length)/sr
        kick=np.sin(2*np.pi*(43*a+(110-43)*.025*(1-np.exp(-a/.025))))*np.exp(-a/0.095)*.22
        bassfreq=roots[(k//8)%4]
        bass=(np.sin(2*np.pi*bassfreq*a)+.28*np.sin(4*np.pi*bassfreq*a))*np.exp(-a/0.22)*.10
        music[start:start+length]+=np.column_stack((kick+bass,kick+bass))
        if k%2==1:
            snap=rng.normal(0,1,length)*np.exp(-a/.035)*.042
            music[start:start+length]+=np.column_stack((snap,snap))
        arp_start=int((k+.5)*beat*sr)
        if arp_start<n:
            length=min(int(.44*sr),n-arp_start)
            a=np.arange(length)/sr
            freq=bassfreq*[4,6,8,6][k%4]
            arp=(np.sin(2*np.pi*freq*a)+.22*np.sin(2*np.pi*2*freq*a))*np.exp(-a/.11)*.046
            pan=.3 if k%2 else .7
            music[arp_start:arp_start+length]+=np.column_stack((arp*(1-pan),arp*pan))
    timeline=np.arange(n)/sr
    for freq,amp in [(146.832,.016),(174.614,.012),(220.0,.012)]:
        music+=np.column_stack((np.sin(2*np.pi*freq*timeline),np.sin(2*np.pi*(freq+.13)*timeline)))*amp
    music*=np.minimum(1,timeline/1.6)[:,None]*np.minimum(1,(DURATION-timeline)/2.4)[:,None]
    voice=np.zeros(n)
    for i in range(6):
        path=ROOT/'narration'/f'{i:02d}.mp3'
        audio=np.frombuffer(subprocess.check_output(['ffmpeg','-v','error','-i',str(path),'-f','f32le','-ar',str(sr),'-ac','1','-']),dtype='<f4').copy()
        # Remove only service silence, not spoken sounds.
        active=np.flatnonzero(np.abs(audio)>.003)
        if len(active): audio=audio[max(0,active[0]-int(.08*sr)):min(len(audio),active[-1]+int(.16*sr))]
        duration=len(audio)/sr
        slot=ENDS[i]-STARTS[i]
        if duration>slot-.85: raise ValueError(f'Voice {i} too long ({duration:.2f}s) for its scene')
        start=STARTS[i]+(.8 if i==5 else .6)
        offset=int(start*sr)
        voice[offset:offset+len(audio)]+=audio*.86
        NARRATION_TIMES.append((start,duration))
        a=max(0,offset-int(.25*sr));b=min(n,offset+len(audio)+int(.25*sr))
        music[a:b]*=.36
    # Original transition accents and a soft green confirmation chime.
    for start in [4,8,13,20,25]:
        off=int(start*sr);length=min(int(.32*sr),n-off)
        a=np.arange(length)/sr
        sweep=rng.normal(0,1,length)*np.sin(np.pi*a/.32)**2*.028
        music[off:off+length]+=np.column_stack((sweep,sweep))
    off=int(17.2*sr);a=np.arange(int(.65*sr))/sr
    chime=(np.sin(2*np.pi*880*a)+.3*np.sin(2*np.pi*1320*a))*np.exp(-a/.15)*.043
    music[off:off+len(a)]+=np.column_stack((chime,chime))
    mixed=music+voice[:,None]
    peak=np.max(np.abs(mixed))
    if peak>.92: mixed*=.92/peak
    with wave.open(str(ROOT/'soundtrack.wav'),'wb') as wav:
        wav.setnchannels(2);wav.setsampwidth(2);wav.setframerate(sr)
        wav.writeframes((np.clip(mixed,-1,1)*32767).astype('<i2').tobytes())
    (ROOT/'narration-timing.json').write_text(json.dumps(NARRATION_TIMES,indent=2),encoding='utf-8')
    # Portable sidecar subtitles in addition to burned-in captions.
    def tc(t):
        ms=int(round(t*1000));return f'{ms//3600000:02d}:{ms//60000%60:02d}:{ms//1000%60:02d},{ms%1000:03d}'
    srt='\n\n'.join(f'{i+1}\n{tc(start)} --> {tc(start+duration)}\n{CAPTIONS[i]}' for i,(start,duration) in enumerate(NARRATION_TIMES))
    (ROOT/'ReplayKeeper-promo.srt').write_text(srt+'\n',encoding='utf-8-sig')
    print('Audio ready; voice timings:',NARRATION_TIMES,flush=True)

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--preview',action='store_true');args=parser.parse_args()
    build_audio()
    for i,t in enumerate([2.3,6.5,10.7,18,22.8,29]):
        frame(t).save(ROOT/f'preview-{i+1:02d}.jpg',quality=94)
    frame(29).save(ROOT/'poster.jpg',quality=97)
    # Fit six complete frames in a 2x3 layout.
    sheet=Image.new('RGB',(1280,1080),(8,14,17))
    for i in range(6):
        thumb=Image.open(ROOT/f'preview-{i+1:02d}.jpg').resize((640,360),Image.Resampling.LANCZOS)
        sheet.paste(thumb,((i%2)*640,(i//2)*360))
    sheet.save(ROOT/'contact-sheet.jpg',quality=94)
    if args.preview:return
    output=ROOT/'ReplayKeeper-promo.mp4'
    command=['ffmpeg','-y','-hide_banner','-loglevel','warning','-f','rawvideo','-pix_fmt','rgb24','-s',f'{W}x{H}','-r',str(FPS),'-i','-','-i',str(ROOT/'soundtrack.wav'),'-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-af','loudnorm=I=-16:TP=-2.5:LRA=9','-ar','48000','-c:a','aac','-b:a','192k','-t',str(DURATION),'-movflags','+faststart',str(output)]
    process=subprocess.Popen(command,stdin=subprocess.PIPE,stderr=open(ROOT/'encode.log','wb'))
    try:
        for i in range(int(DURATION*FPS)):
            process.stdin.write(frame(i/FPS).tobytes())
            if i%90==0:print(f'Render {i}/{int(DURATION*FPS)} frames',flush=True)
        process.stdin.close()
        if process.wait()!=0:raise RuntimeError('FFmpeg failed; see encode.log')
    except BaseException:
        process.kill();process.wait();raise
    print('Rendered:',output,flush=True)

if __name__=='__main__':main()
